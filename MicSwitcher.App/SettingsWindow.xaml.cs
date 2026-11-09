using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MicSwitcher.Core.Audio;
using MicSwitcher.Core.Config;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;

namespace MicSwitcher.App;

/// <summary>
/// 主窗口：左侧「手动 / 自动」两个互斥拨动开关切换模式，
/// 右侧是麦克风设备卡片（支持上下箭头与拖动排序），左下角进入详细设置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly AppConfig _config;
    private bool _realExit;     // 托盘「退出」时置 true，允许窗口真正关闭
    private bool _syncing;      // 程序化改开关状态时置 true，避免事件互相触发
    private string _lastSignature = "";   // 上次渲染的设备快照，用于跳过无变化的刷新
    private PreferencesWindow? _preferences;

    // ---- 长按拖拽排序的状态 ----
    private readonly System.Windows.Threading.DispatcherTimer _longPressTimer =
        new() { Interval = TimeSpan.FromMilliseconds(220) };

    private AudioDeviceInfo? _pressDevice;
    private ListBoxItem? _pressItem;
    private Point _pressPoint;
    private Point _grabOffset;
    private FrameworkElement? _ghost;
    private Canvas? _ghostHost;
    private Popup? _ghostPopup;
    private int _dragOriginIndex = -1;
    private bool _pressFromHandle;
    private bool _swapping;
    private bool _committing;
    private Point _lastIndicatorPoint = new(double.NaN, double.NaN);
    private Border? _insertLine;
    private int _insertIndex = -1;
    private bool _isDragging;

    /// <summary>设置发生改变时通知托盘刷新菜单。</summary>
    public event EventHandler? Changed;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        _config = AppConfig.Load();
        _services.Config = _config;
        _services.ApplyStrategy(_config.AutoStrategy);

        InitializeComponent();

        InitMode();
        InitDragSort();

        Refresh(force: true);
        Loaded += (_, _) => Refresh(force: true);
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(force: true); };

        // 轮询设备变化：窗口不可见时跳过，且只在数据真的变了才重建列表
        var refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        refreshTimer.Tick += (_, _) => Refresh(force: false);
        refreshTimer.Start();
    }

    // ================= 初始化 =================

    private void InitMode()
    {
        _syncing = true;

        ModeManualToggle.IsChecked = _services.Mode != WorkMode.Auto;
        ModeAutoToggle.IsChecked = _services.Mode == WorkMode.Auto;
        LockToggle.IsChecked = _services.Mode == WorkMode.Locked;

        StrategyByLevel.IsChecked = _config.AutoStrategy == AutoStrategy.ByLevel;
        StrategyByNewDevice.IsChecked = _config.AutoStrategy == AutoStrategy.ByNewDevice;

        _syncing = false;

        ModeManualToggle.Checked += ModeManual_Changed;
        ModeManualToggle.Unchecked += ModeManual_Changed;
        ModeAutoToggle.Checked += ModeAuto_Changed;
        ModeAutoToggle.Unchecked += ModeAuto_Changed;
        LockToggle.Checked += Lock_Changed;
        LockToggle.Unchecked += Lock_Changed;
        StrategyByLevel.Checked += Strategy_Changed;
        StrategyByNewDevice.Checked += Strategy_Changed;
    }

    // ================= 刷新（带节流） =================

    /// <summary>
    /// 刷新界面。<paramref name="force"/>=false 时，若设备快照没变就直接返回，
    /// 避免每 2 秒重建列表导致的闪烁和卡顿。
    /// </summary>
    private void Refresh(bool force)
    {
        if (!force && !IsVisible) return;

        var ordered = _services.OrderedDevices();

        string signature = string.Join("|", ordered.Select(d =>
            $"{d.Id}:{(d.IsDefault ? 1 : 0)}{(d.IsMuted ? 1 : 0)}"))
            + $"/{_services.Mode}/{_services.AutoStrategy}";

        bool dataChanged = signature != _lastSignature;

        if (dataChanged)
        {
            _lastSignature = signature;

            var selectedId = (DeviceList.SelectedItem as AudioDeviceInfo)?.Id;
            DeviceList.ItemsSource = ordered;
            if (selectedId is not null)
                DeviceList.SelectedItem = ordered.FirstOrDefault(d => d.Id == selectedId);
        }

        // 状态栏文本很便宜，每次都刷
        var current = ordered.FirstOrDefault(d => d.IsDefault);
        string modeText = _services.Mode switch
        {
            WorkMode.Locked => "🔒 锁定",
            WorkMode.Auto => _services.AutoStrategy == AutoStrategy.ByLevel
                ? "🤖 自动 · 按声音大小"
                : "🤖 自动 · 按新设备",
            _ => "✋ 手动",
        };
        StatusText.Text = (current is null ? "没有检测到麦克风" : $"当前默认：{current.FriendlyName}")
                          + $"　|　模式：{modeText}";

        SyncModeUi();
    }

    /// <summary>把左栏开关与策略单选同步成当前状态（不触发事件）。</summary>
    private void SyncModeUi()
    {
        _syncing = true;

        ModeManualToggle.IsChecked = _services.Mode != WorkMode.Auto;
        ModeAutoToggle.IsChecked = _services.Mode == WorkMode.Auto;
        LockToggle.IsChecked = _services.Mode == WorkMode.Locked;

        StrategyByLevel.IsChecked = _services.AutoStrategy == AutoStrategy.ByLevel;
        StrategyByNewDevice.IsChecked = _services.AutoStrategy == AutoStrategy.ByNewDevice;

        bool autoEnabled = _services.Mode == WorkMode.Auto;
        StrategyByLevel.IsEnabled = autoEnabled;
        StrategyByNewDevice.IsEnabled = autoEnabled;

        _syncing = false;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private AudioDeviceInfo? Selected => DeviceList.SelectedItem as AudioDeviceInfo;

    // ================= 模式切换 =================

    private void ModeManual_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;

        _services.Config = _config;
        _services.SetManual();

        Refresh(force: true);
        NotifyChanged();
    }

    private void ModeAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;

        _services.Config = _config;
        if (ModeAutoToggle.IsChecked == true)
            _services.SetAuto(_config.AutoStrategy);
        else
            _services.SetManual();   // 我不允许「两个都关」，退回手动

        Refresh(force: true);
        NotifyChanged();
    }

    private void Strategy_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;

        var strategy = StrategyByNewDevice.IsChecked == true
            ? AutoStrategy.ByNewDevice
            : AutoStrategy.ByLevel;

        _config.AutoStrategy = strategy;
        _config.Save();
        _services.Config = _config;
        _services.ApplyStrategy(strategy);

        if (_services.Mode == WorkMode.Auto)
            _services.SetAuto(strategy);

        Refresh(force: true);
        NotifyChanged();
    }

    private void Lock_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;

        _services.Config = _config;
        if (LockToggle.IsChecked == true)
        {
            if (_services.Audio.GetDefaultDeviceId(AudioFlow.Capture) is null)
            {
                MessageBox.Show("没有可锁定的麦克风。", "MicSwitcher");
                Refresh(force: true);
                return;
            }
            _services.LockCurrent();
        }
        else
        {
            _services.Unlock();
        }

        Refresh(force: true);
        NotifyChanged();
    }

    // ================= 设备操作 =================

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        try
        {
            _services.Config = _config;
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(Selected.Id);
            Refresh(force: true); NotifyChanged();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "设为默认失败"); }
    }

    private void ToggleMute_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        try
        {
            _services.Audio.ToggleMute(Selected.Id);
            Refresh(force: true); NotifyChanged();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "静音失败"); }
    }

    private void Cycle_Click(object sender, RoutedEventArgs e)
    {
        var devices = _services.Audio.EnumerateDevices(AudioFlow.Capture);
        if (devices.Count < 2) { MessageBox.Show("至少需要两个麦克风。"); return; }
        try
        {
            string? cur = _services.Audio.GetDefaultDeviceId(AudioFlow.Capture);
            int idx = devices.ToList().FindIndex(d => d.Id == cur);
            var next = devices[(idx + 1) % devices.Count];
            _services.Config = _config;
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(next.Id);
            Refresh(force: true); NotifyChanged();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "切换失败"); }
    }

    private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击卡片空白处才切默认，避免双击箭头按钮误触
        if (FindAncestor<System.Windows.Controls.Button>((DependencyObject)e.OriginalSource) is not null) return;
        SetDefault_Click(sender, e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(force: true);

    // ================= 排序：上下箭头 =================

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AudioDeviceInfo dev) MoveBy(dev.Id, -1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AudioDeviceInfo dev) MoveBy(dev.Id, +1);
    }

    /// <summary>
    /// 上移/下移一位：两个卡片像黑板上下两半一样对着推——
    /// 被移动的那张滑向目标位置，另一张朝反方向让位，动画结束后才真正落定顺序。
    /// </summary>
    private void MoveBy(string deviceId, int delta)
    {
        if (_swapping || _isDragging) return;

        var order = _services.OrderedDevices().Select(d => d.Id).ToList();
        int idx = order.IndexOf(deviceId);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= order.Count) return;

        // 找不到可视容器就直接改数据（例如列表还没排版）
        if (ContainerFromIndex(idx) is not ListBoxItem fromItem ||
            ContainerFromIndex(target) is not ListBoxItem toItem)
        {
            (order[idx], order[target]) = (order[target], order[idx]);
            ApplyOrder(order);
            return;
        }

        double fromTop = fromItem.TransformToVisual(DragLayer).Transform(new Point(0, 0)).Y;
        double toTop = toItem.TransformToVisual(DragLayer).Transform(new Point(0, 0)).Y;
        double dy = toTop - fromTop;         // 被移动卡片要走的距离（负数=上移）

        var fromTf = EnsureTranslate(fromItem);
        var toTf = EnsureTranslate(toItem);

        var ease = new System.Windows.Media.Animation.CubicEase
        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(210);

        var moveFrom = new System.Windows.Media.Animation.DoubleAnimation(0, dy, dur) { EasingFunction = ease };
        var moveTo = new System.Windows.Media.Animation.DoubleAnimation(0, -dy, dur) { EasingFunction = ease };

        _swapping = true;

        moveFrom.Completed += (_, _) =>
        {
            // 动画走完再改数据；重建列表后 transform 自然被丢弃
            fromTf.BeginAnimation(TranslateTransform.YProperty, null);
            toTf.BeginAnimation(TranslateTransform.YProperty, null);
            fromItem.RenderTransform = Transform.Identity;
            toItem.RenderTransform = Transform.Identity;

            var now = _services.OrderedDevices().Select(d => d.Id).ToList();
            int a = now.IndexOf(deviceId);
            int b = a + delta;
            if (a >= 0 && b >= 0 && b < now.Count)
                (now[a], now[b]) = (now[b], now[a]);

            _swapping = false;
            ApplyOrder(now);
            DeviceList.SelectedItem = _services.OrderedDevices().FirstOrDefault(d => d.Id == deviceId);
        };

        // 让被让位的那张先动起来，形成“对推”的机械感
        toTf.BeginAnimation(TranslateTransform.YProperty, moveTo);
        fromTf.BeginAnimation(TranslateTransform.YProperty, moveFrom);
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform tf) return tf;
        tf = new TranslateTransform();
        element.RenderTransform = tf;
        return tf;
    }

    private void ApplyOrder(List<string> order)
    {
        _config.DevicePriority = order;
        _config.Save();
        _services.Config = _config;
        Refresh(force: true);
        NotifyChanged();
    }

    // ================= 排序：长按拖拽（幽灵卡片 + 镶嵌指示） =================

    private void InitDragSort()
    {
        _longPressTimer.Tick += LongPress_Tick;

        DeviceList.PreviewMouseLeftButtonDown += DeviceList_PreviewMouseLeftButtonDown;
        DeviceList.LostMouseCapture += DeviceList_LostMouseCapture;
        PreviewMouseMove += Window_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Window_PreviewMouseLeftButtonUp;
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    /// <summary>丢失鼠标捕获时结束拖拽，避免幽灵卡在屏幕上。</summary>
    private void DeviceList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging) CommitDrag();
    }

    private void DeviceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点箭头按钮不算拖拽
        if (FindAncestor<System.Windows.Controls.Button>((DependencyObject)e.OriginalSource) is not null) return;

        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item?.DataContext is not AudioDeviceInfo dev) return;

        _pressDevice = dev;
        _pressItem = item;
        _pressPoint = e.GetPosition(this);

        // 从 ⠿ 手柄按下 → 快得多就进入拖拽；按卡片其他地方 → 稍长，避免误触
        _pressFromHandle = FindAncestorWithTag((DependencyObject)e.OriginalSource, "dragHandle") is not null;
        _longPressTimer.Interval = TimeSpan.FromMilliseconds(_pressFromHandle ? 90 : 160);
        _longPressTimer.Start();
    }

    private void LongPress_Tick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();

        if (_pressDevice is null || _pressItem is null) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { ResetPress(); return; }

        // 从手柄拖：允许已经开始移动；从卡片拖：挪开太多就当作普通点击
        if (!_pressFromHandle)
        {
            var pos = Mouse.GetPosition(this);
            if (Math.Abs(pos.X - _pressPoint.X) > 8 || Math.Abs(pos.Y - _pressPoint.Y) > 8)
            {
                ResetPress();
                return;
            }
        }

        BeginDrag();
    }

    private void BeginDrag()
    {
        if (_pressItem is null || _pressDevice is null) return;

        var item = _pressItem;
        if (item.ActualWidth <= 0 || item.ActualHeight <= 0) { ResetPress(); return; }

        _isDragging = true;
        _dragOriginIndex = DeviceList.Items.IndexOf(_pressDevice);
        _insertIndex = _dragOriginIndex;

        double w = item.ActualWidth;
        double h = item.ActualHeight;

        // 幽灵卡片：1:1 画面 + 圆角裁剪 + 柔和投影 + 半透明
        var brush = new VisualBrush(item)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
        };

        _ghost = new Border
        {
            Width = w,
            Height = h,
            Background = brush,
            CornerRadius = new CornerRadius(9),
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), 9, 9),
            Opacity = 0.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.22,
                Color = Colors.Black,
            },
        };

        // 用一个铺满虚拟屏幕的透明弹层承载幽灵（只创建一次，之后复用）。
        // 拖拽过程中只做渲染位移，不再搬动系统窗口，也不会反复创建窗口把资源耗光。
        EnsureGhostInfrastructure();
        _ghostHost!.Children.Clear();
        _ghostHost.Children.Add(_ghost);
        _ghostPopup!.IsOpen = true;

        // 插入提示线：绿色圆头横条 + 光晕
        _insertLine = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x4F)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.85,
                Color = Color.FromRgb(0x2E, 0xA0, 0x4F),
            },
        };
        DragLayer.Children.Add(_insertLine);

        _grabOffset = Mouse.GetPosition(item);
        _lastIndicatorPoint = new Point(double.NaN, double.NaN);
        MoveGhost(Mouse.GetPosition(this));

        item.Opacity = 0.25;
        Mouse.Capture(DeviceList, CaptureMode.SubTree);
    }

    /// <summary>幽灵弹层只建一次，反复拖拽都复用它，避免反复创建全屏窗口导致卡死。</summary>
    private void EnsureGhostInfrastructure()
    {
        if (_ghostPopup is not null) return;

        _ghostHost = new Canvas { Background = null, IsHitTestVisible = false };
        _ghostPopup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            StaysOpen = true,
            Placement = PlacementMode.Absolute,
            HorizontalOffset = SystemParameters.VirtualScreenLeft,
            VerticalOffset = SystemParameters.VirtualScreenTop,
            Width = Math.Max(1, SystemParameters.VirtualScreenWidth),
            Height = Math.Max(1, SystemParameters.VirtualScreenHeight),
            Child = _ghostHost,
            IsOpen = false,
        };
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        // 兜底：按键已经松开却没收到 Up 时，这里直接收尾，避免幽灵一直跟着鼠标
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            CommitDrag();
            return;
        }

        MoveGhost(e.GetPosition(this));

        // 指示线只在移动超过 1px 时重算，减少无谓的布局换算
        var p = e.GetPosition(DragLayer);
        if (double.IsNaN(_lastIndicatorPoint.Y) ||
            Math.Abs(p.Y - _lastIndicatorPoint.Y) >= 1 ||
            Math.Abs(p.X - _lastIndicatorPoint.X) >= 1)
        {
            _lastIndicatorPoint = p;
            UpdateInsertIndicator(p);
        }
    }

    /// <summary>把窗口坐标换算成幽灵画布坐标（虚拟屏幕原点为 0）。</summary>
    private Point WindowPointToGhostHost(Point windowPoint)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Point screenPx = PointToScreen(windowPoint);
        double x = screenPx.X / dpi.DpiScaleX - SystemParameters.VirtualScreenLeft;
        double y = screenPx.Y / dpi.DpiScaleY - SystemParameters.VirtualScreenTop;
        return new Point(x, y);
    }

    private void MoveGhost(Point posInWindow)
    {
        if (_ghost is null) return;

        var p = WindowPointToGhostHost(posInWindow);
        Canvas.SetLeft(_ghost, p.X - _grabOffset.X);
        Canvas.SetTop(_ghost, p.Y - _grabOffset.Y);
    }

    /// <summary>算出插入位置并画出指示线；位置变化时给指示线一个轻柔的弹入。</summary>
    private void UpdateInsertIndicator(Point posInLayer)
    {
        if (_insertLine is null) return;

        int index = ComputeInsertIndex(posInLayer);

        if (index != _insertIndex)
        {
            _insertIndex = index;
            AnimateInsertLine();
        }

        double y = SlotY(index);
        if (double.IsNaN(y))
        {
            _insertLine.Visibility = Visibility.Collapsed;
            return;
        }

        _insertLine.Visibility = Visibility.Visible;
        _insertLine.Width = Math.Max(0, DragLayer.ActualWidth - 4);
        Canvas.SetLeft(_insertLine, 2);
        Canvas.SetTop(_insertLine, y - 1.5);
    }

    private void AnimateInsertLine()
    {
        if (_insertLine?.RenderTransform is not ScaleTransform scale) return;

        var grow = new System.Windows.Media.Animation.DoubleAnimation(
            0.45, 1.0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new System.Windows.Media.Animation.BackEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                Amplitude = 0.5,
            },
        };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var fade = new System.Windows.Media.Animation.DoubleAnimation(
            0.45, 1.0, TimeSpan.FromMilliseconds(150));
        _insertLine.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>鼠标所在位置对应插入到第几个槽位（0..Count）。</summary>
    private int ComputeInsertIndex(Point posInLayer)
    {
        int count = DeviceList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (ContainerFromIndex(i) is not ListBoxItem c) continue;
            var top = c.TransformToVisual(DragLayer).Transform(new Point(0, 0)).Y;
            if (posInLayer.Y < top + c.ActualHeight / 2) return i;
        }
        return count;
    }

    /// <summary>第 index 个插入槽位的 Y 坐标（插到末尾时取最后一行底部）。</summary>
    private double SlotY(int index)
    {
        int count = DeviceList.Items.Count;
        if (count == 0) return double.NaN;

        if (index < count && ContainerFromIndex(index) is ListBoxItem it)
            return it.TransformToVisual(DragLayer).Transform(new Point(0, 0)).Y - 4;

        if (ContainerFromIndex(count - 1) is ListBoxItem last)
            return last.TransformToVisual(DragLayer).Transform(new Point(0, 0)).Y + last.ActualHeight + 2;

        return double.NaN;
    }

    private ListBoxItem? ContainerFromIndex(int i)
        => DeviceList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _longPressTimer.Stop();

        if (!_isDragging)
        {
            ResetPress();
            return;
        }

        CommitDrag();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isDragging || e.Key != Key.Escape) return;
        e.Handled = true;
        CancelDrag();
    }

    /// <summary>
    /// 松手：幽灵卡片像被弹力绳拉回一样落位（ElasticEase），落位后才写入优先级；
    /// 原地放下则直接弹回原位、不改顺序。
    /// </summary>
    private void CommitDrag()
    {
        if (!_isDragging || _committing) return;
        _committing = true;

        var dragged = _pressDevice;
        if (dragged is null) { CancelDrag(); return; }

        int origin = _dragOriginIndex;
        int insert = _insertIndex < 0 ? origin : _insertIndex;
        if (insert > origin) insert--;            // 移除自身后索引前移
        int maxIndex = Math.Max(0, DeviceList.Items.Count - 1);
        insert = Math.Clamp(insert, 0, maxIndex);

        bool positionChanged = insert != origin;

        // 目标：变了 → 落到新槽位；没变 → 弹回原位
        double targetYInLayer = positionChanged
            ? SlotY(Math.Min(insert, DeviceList.Items.Count))
            : SlotY(origin);
        if (double.IsNaN(targetYInLayer) || _ghost is null)
        {
            _committing = false;
            EndDragVisuals();
            return;
        }

        var slotInWindow = DragLayer.TransformToAncestor(this)
            .Transform(new Point(0, targetYInLayer));
        var slotInHost = WindowPointToGhostHost(slotInWindow);

        double curLeft = Canvas.GetLeft(_ghost);
        double curTop = Canvas.GetTop(_ghost);
        if (double.IsNaN(curLeft)) curLeft = 0;
        if (double.IsNaN(curTop)) curTop = 0;

        var ease = new System.Windows.Media.Animation.ElasticEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            Oscillations = 1,
            Springiness = 6,
        };

        var animX = new System.Windows.Media.Animation.DoubleAnimation(
            curLeft, slotInHost.X, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
        var animY = new System.Windows.Media.Animation.DoubleAnimation(
            curTop, slotInHost.Y - 2, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };

        animY.Completed += (_, _) =>
        {
            _committing = false;
            EndDragVisuals();

            if (positionChanged)
            {
                var order = _services.OrderedDevices().Select(d => d.Id).ToList();
                if (order.Remove(dragged.Id))
                {
                    order.Insert(Math.Clamp(insert, 0, order.Count), dragged.Id);
                    ApplyOrder(order);
                }
                DeviceList.SelectedItem = _services.OrderedDevices()
                    .FirstOrDefault(d => d.Id == dragged.Id);
            }
        };

        if (_insertLine is not null) _insertLine.Visibility = Visibility.Collapsed;
        _ghost.BeginAnimation(Canvas.LeftProperty, animX);
        _ghost.BeginAnimation(Canvas.TopProperty, animY);
    }

    private void CancelDrag()
    {
        if (_committing) return;
        EndDragVisuals();
        Refresh(force: true);
    }

    /// <summary>清掉幽灵、指示线，恢复被拖行的透明度与鼠标捕获。</summary>
    private void EndDragVisuals()
    {
        _isDragging = false;
        _committing = false;
        _insertIndex = -1;
        _dragOriginIndex = -1;

        if (_ghost is not null)
        {
            _ghost.BeginAnimation(Canvas.LeftProperty, null);
            _ghost.BeginAnimation(Canvas.TopProperty, null);
        }
        _ghost = null;

        // 弹层保留复用，只清内容并收起
        _ghostHost?.Children.Clear();
        if (_ghostPopup is not null) _ghostPopup.IsOpen = false;

        if (_insertLine is not null) DragLayer.Children.Remove(_insertLine);
        _insertLine = null;

        if (_pressItem is not null) _pressItem.Opacity = 1;

        if (Mouse.Captured is not null && Mouse.Captured == DeviceList) Mouse.Capture(null);
        ResetPress();
    }

    private void ResetPress()
    {
        _pressDevice = null;
        _pressItem = null;
    }

    private static DependencyObject? FindAncestorWithTag(DependencyObject? current, object tag)
    {
        while (current is not null)
        {
            if (current is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ================= 详细设置 =================

    private void OpenPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (_preferences is null)
        {
            _preferences = new PreferencesWindow(_config, _services) { Owner = this };
            _preferences.Changed += (_, _) =>
            {
                _services.Config = _config;
                _services.ApplyStrategy(_config.AutoStrategy);
                Refresh(force: true);
                NotifyChanged();
            };
            _preferences.Closed += (_, _) => _preferences = null;
        }

        _preferences.Show();
        _preferences.Activate();
    }

    // ================= 关闭处理 =================

    /// <summary>托盘「退出」时调用：放行窗口关闭。</summary>
    public void AllowClose() => _realExit = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_config.MinimizeToTrayOnClose && !_realExit)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
