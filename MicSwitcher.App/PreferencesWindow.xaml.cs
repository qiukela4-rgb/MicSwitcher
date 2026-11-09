using System.ComponentModel;
using System.Threading.Tasks;
using System.Diagnostics;
using MicSwitcher.Core.Updates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicSwitcher.Core.Audio;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Hotkeys;
using MicSwitcher.Core.Startup;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MicSwitcher.App;

/// <summary>
/// 详细设置窗口：左侧分类导航 + 右侧可折叠分组。
/// 启动 / 窗口与托盘 / 通知 / 自动模式灵敏度 / 快捷键 / 设备管理 / 关于。
/// </summary>
public partial class PreferencesWindow : Window
{
    private readonly AppConfig _config;
    private readonly AppServices _services;
    private bool _loaded;

    private enum RecordTarget { None, Mute, Next, Prev }
    private RecordTarget _recording = RecordTarget.None;
    private bool _updatingHotkeyBoxes;
    private bool _syncingSliders;

    /// <summary>任一设置发生变化（主程序据此立即应用）。</summary>
    public event EventHandler? Changed;

    public PreferencesWindow(AppConfig config, AppServices services)
    {
        _config = config;
        _services = services;
        InitializeComponent();

        InitControls();
        BuildDeviceRows();

        _loaded = true;
        PreviewKeyDown += OnPreviewKeyDown;

        NavList.SelectedIndex = 0;
    }

    // ================= 初始化 =================

    private void InitControls()
    {
        _syncingSliders = true;

        AutoStartToggle.IsChecked = AutoStart.IsEnabled();
        StartMinimizedToggle.IsChecked = _config.StartMinimized;
        DelayAutostartToggle.IsChecked = _config.DelayedAutostart;
        DelaySecondsSlider.Value = Math.Clamp(_config.DelayedAutostartSeconds, 1, 60);
        DelaySecondsText.Text = $"{(int)DelaySecondsSlider.Value} 秒";

        CloseToTrayToggle.IsChecked = _config.MinimizeToTrayOnClose;
        ShowTrayIconToggle.IsChecked = _config.ShowTrayIcon;
        TrayMuteIconToggle.IsChecked = _config.TrayIconShowsMute;
        SyncCommToggle.IsChecked = _config.SyncCommunicationRole;

        NotifyToggle.IsChecked = _config.NotifyOnAutoSwitch;

        ThresholdSlider.Value = Math.Clamp((double)_config.SilenceThreshold * 100, 0.5, 50);
        ThresholdText.Text = $"{ThresholdSlider.Value:0.0}%";
        DebounceSlider.Value = Math.Clamp(_config.DebounceSamples, 1, 10);
        DebounceText.Text = $"{(int)DebounceSlider.Value} 次";
        IntervalSlider.Value = Math.Clamp(_config.SampleIntervalMs, 100, 1000);
        IntervalText.Text = $"{(int)IntervalSlider.Value} ms";
        PreferPhysicalToggle.IsChecked = _config.PreferPhysicalDevices;

        HotkeyEnableToggle.IsChecked = _config.EnableHotkeys;
        MuteEnabledToggle.IsChecked = _config.HotkeyMuteEnabled;
        NextEnabledToggle.IsChecked = _config.HotkeyNextEnabled;
        PrevEnabledToggle.IsChecked = _config.HotkeyPrevEnabled;

        FillHotkeyBox(HotkeyMuteBox, _config.HotkeyMute);
        FillHotkeyBox(HotkeyNextBox, _config.HotkeyNext);
        FillHotkeyBox(HotkeyPrevBox, _config.HotkeyPrev);

        _syncingSliders = false;

        InitAbout();
        RefreshHotkeyStatus();
    }

    private static void FillHotkeyBox(System.Windows.Controls.ComboBox box, string current)
    {
        box.Items.Clear();
        foreach (var preset in HotkeyGesture.Presets)
            box.Items.Add(preset);
        if (!box.Items.Contains(current))
            box.Items.Add(current);
        box.SelectedItem = current;
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void Save()
    {
        _config.Save();
        NotifyChanged();
    }

    // ================= 左侧导航：展开对应分组 =================

    private (string Tag, Expander Sec)[] Sections() => new[]
    {
        ("start", SecStart),
        ("window", SecWindow),
        ("notify", SecNotify),
        ("auto", SecAuto),
        ("hotkey", SecHotkey),
        ("device", SecDevice),
        ("about", SecAbout),
    };

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;

        foreach (var (t, sec) in Sections())
            sec.IsExpanded = t == tag;

        foreach (var (t, sec) in Sections())
        {
            if (t != tag) continue;
            sec.BringIntoView();
            break;
        }
    }

    // ================= 启动 =================

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        bool want = AutoStartToggle.IsChecked == true;
        string? err = want ? AutoStart.Enable() : AutoStart.Disable();

        if (err is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AutoStartToggle.Checked -= AutoStart_Changed;
                AutoStartToggle.Unchecked -= AutoStart_Changed;
                AutoStartToggle.IsChecked = !want;
                AutoStartToggle.Checked += AutoStart_Changed;
                AutoStartToggle.Unchecked += AutoStart_Changed;
                UpdateLayout();

                MessageBox.Show(
                    $"开机自启设置失败：\n{err}\n\n" +
                    "如果是「拒绝访问」，通常是程序运行在低完整性目录，或被杀软拦截。",
                    "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            }), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        NotifyChanged();
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.StartMinimized = StartMinimizedToggle.IsChecked == true;
        Save();
    }

    private void DelayAutostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.DelayedAutostart = DelayAutostartToggle.IsChecked == true;
        Save();
    }

    private void DelaySeconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelaySecondsText is null) return;
        DelaySecondsText.Text = $"{(int)e.NewValue} 秒";
        if (!_loaded || _syncingSliders) return;

        _config.DelayedAutostartSeconds = (int)e.NewValue;
        Save();
    }

    // ================= 窗口与托盘 =================

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.MinimizeToTrayOnClose = CloseToTrayToggle.IsChecked == true;
        Save();
    }

    private void ShowTrayIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.ShowTrayIcon = ShowTrayIconToggle.IsChecked == true;
        Save();

        if (!_config.ShowTrayIcon && !_config.MinimizeToTrayOnClose)
        {
            MessageBox.Show(
                "托盘图标已隐藏，且「关闭时最小化到托盘」是关的。\n\n" +
                "这样关掉窗口后程序会直接退出。若只想在后台跑，\n" +
                "建议同时打开「关闭时最小化到托盘」。",
                "MicSwitcher - 提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void TrayMuteIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.TrayIconShowsMute = TrayMuteIconToggle.IsChecked == true;
        Save();
    }

    private void SyncComm_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.SyncCommunicationRole = SyncCommToggle.IsChecked == true;
        Save();
    }

    // ================= 通知 =================

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.NotifyOnAutoSwitch = NotifyToggle.IsChecked == true;
        Save();
    }

    // ================= 自动模式灵敏度 =================

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText is null) return;
        ThresholdText.Text = $"{e.NewValue:0.0}%";
        if (!_loaded || _syncingSliders) return;

        _config.SilenceThreshold = (float)(e.NewValue / 100.0);
        Save();
    }

    private void Debounce_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DebounceText is null) return;
        DebounceText.Text = $"{(int)e.NewValue} 次";
        if (!_loaded || _syncingSliders) return;

        _config.DebounceSamples = (int)e.NewValue;
        Save();
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalText is null) return;
        IntervalText.Text = $"{(int)e.NewValue} ms";
        if (!_loaded || _syncingSliders) return;

        _config.SampleIntervalMs = (int)e.NewValue;
        Save();
    }

    private void PreferPhysical_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.PreferPhysicalDevices = PreferPhysicalToggle.IsChecked == true;
        Save();
    }

    // ================= 快捷键：总开关与单动作开关 =================

    private void HotkeyEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.EnableHotkeys = HotkeyEnableToggle.IsChecked == true;
        Save();
        RefreshHotkeyStatus();
    }

    private void MuteEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.HotkeyMuteEnabled = MuteEnabledToggle.IsChecked == true;
        Save();
        RefreshHotkeyStatus();
    }

    private void NextEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.HotkeyNextEnabled = NextEnabledToggle.IsChecked == true;
        Save();
        RefreshHotkeyStatus();
    }

    private void PrevEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.HotkeyPrevEnabled = PrevEnabledToggle.IsChecked == true;
        Save();
        RefreshHotkeyStatus();
    }

    // ================= 快捷键：下拉选择 =================

    private void HotkeyMute_Changed(object sender, SelectionChangedEventArgs e)
        => OnHotkeySelected(HotkeyMuteBox, "Mute", "静音", v => _config.HotkeyMute = v, () => _config.HotkeyMute);

    private void HotkeyNext_Changed(object sender, SelectionChangedEventArgs e)
        => OnHotkeySelected(HotkeyNextBox, "Next", "下一个", v => _config.HotkeyNext = v, () => _config.HotkeyNext);

    private void HotkeyPrev_Changed(object sender, SelectionChangedEventArgs e)
        => OnHotkeySelected(HotkeyPrevBox, "Prev", "上一个", v => _config.HotkeyPrev = v, () => _config.HotkeyPrev);

    private void OnHotkeySelected(
        System.Windows.Controls.ComboBox box,
        string statusKey,
        string label,
        Action<string> apply,
        Func<string> current)
    {
        if (!_loaded || _updatingHotkeyBoxes) return;
        if (box.SelectedItem is not string chosen) return;
        if (chosen == current()) return;

        string? conflict = HotkeyProbe.Check(chosen);
        if (conflict is not null)
        {
            MessageBox.Show(
                $"「{label}」不能用 {chosen}：\n\n{conflict}\n\n请换一个组合键。",
                "MicSwitcher - 热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);

            _updatingHotkeyBoxes = true;
            try { box.SelectedItem = current(); }
            finally { _updatingHotkeyBoxes = false; }
            return;
        }

        apply(chosen);
        Save();
        ReportIfThisFailed(statusKey, label, chosen);
        RefreshHotkeyStatus();
    }

    private void ReportIfThisFailed(string statusKey, string label, string value)
    {
        if (App.HotkeyStatus.TryGetValue(statusKey, out bool ok) && !ok)
        {
            MessageBox.Show(
                $"「{label}」的 {value} 检测时可用，但注册仍然失败，\n" +
                "可能刚刚被别的程序抢占了。请换一个组合键。",
                "MicSwitcher - 热键", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshHotkeyStatus()
    {
        if (HotkeyStatusText is null) return;

        if (!_config.EnableHotkeys)
        {
            HotkeyStatusText.Text = "全局热键当前已关闭。";
            return;
        }

        var items = new (string Key, string Label, string Value, bool Enabled)[]
        {
            ("Mute", "静音", _config.HotkeyMute, _config.HotkeyMuteEnabled),
            ("Next", "下一个", _config.HotkeyNext, _config.HotkeyNextEnabled),
            ("Prev", "上一个", _config.HotkeyPrev, _config.HotkeyPrevEnabled),
        };

        var bad = items
            .Where(x => x.Enabled && App.HotkeyStatus.TryGetValue(x.Key, out bool ok) && !ok)
            .Select(x => $"{x.Label}（{x.Value}）")
            .ToList();

        var enabledValues = items.Where(x => x.Enabled).Select(x => x.Value).ToList();
        var dup = enabledValues.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1)
                               .Select(g => g.Key)
                               .ToList();

        if (bad.Count == 0 && dup.Count == 0)
        {
            var off = items.Where(x => !x.Enabled).Select(x => x.Label).ToList();
            HotkeyStatusText.Text = off.Count == 0
                ? "✅ 三个热键都已生效。"
                : $"✅ 已生效；未启用：{string.Join("、", off)}";
            return;
        }

        var parts = new List<string>();
        if (bad.Count > 0) parts.Add("⚠ 注册失败（可能被其他程序占用）：" + string.Join("、", bad));
        if (dup.Count > 0) parts.Add("⚠ 存在重复的组合键：" + string.Join("、", dup));
        HotkeyStatusText.Text = string.Join("　", parts);
    }

    // ================= 快捷键：录制 =================

    private void RecordMute_Click(object sender, RoutedEventArgs e) => BeginRecord(RecordTarget.Mute);

    private void RecordNext_Click(object sender, RoutedEventArgs e) => BeginRecord(RecordTarget.Next);

    private void RecordPrev_Click(object sender, RoutedEventArgs e) => BeginRecord(RecordTarget.Prev);

    private void BeginRecord(RecordTarget target)
    {
        _recording = target;
        RecordHint.Text = "请按下组合键（必须含 Ctrl / Alt / Shift / Win）…　Esc 取消";
        Focus();
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == RecordTarget.None) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            EndRecord(null);
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        uint modifiers = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) modifiers |= HotkeyGesture.ModControl;
        if ((m & ModifierKeys.Alt) != 0) modifiers |= HotkeyGesture.ModAlt;
        if ((m & ModifierKeys.Shift) != 0) modifiers |= HotkeyGesture.ModShift;
        if ((m & ModifierKeys.Windows) != 0) modifiers |= HotkeyGesture.ModWin;

        if (modifiers == 0)
        {
            RecordHint.Text = "必须包含至少一个修饰键（Ctrl / Alt / Shift / Win）。";
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0) return;

        EndRecord(new HotkeyGesture(modifiers, vk).ToString());
    }

    private void EndRecord(string? gestureText)
    {
        var target = _recording;
        _recording = RecordTarget.None;
        RecordHint.Text = "";

        if (gestureText is null) return;

        if (IsDuplicate(gestureText, target))
        {
            MessageBox.Show($"「{gestureText}」已经被另一个动作占用了。", "MicSwitcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? conflict = HotkeyProbe.Check(gestureText);
        if (conflict is not null)
        {
            MessageBox.Show($"这个组合键不能用：\n\n{conflict}\n\n请换一个。",
                "MicSwitcher - 热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string statusKey;
        switch (target)
        {
            case RecordTarget.Mute:
                _config.HotkeyMute = gestureText;
                FillHotkeyBox(HotkeyMuteBox, gestureText);
                statusKey = "Mute";
                break;
            case RecordTarget.Next:
                _config.HotkeyNext = gestureText;
                FillHotkeyBox(HotkeyNextBox, gestureText);
                statusKey = "Next";
                break;
            case RecordTarget.Prev:
                _config.HotkeyPrev = gestureText;
                FillHotkeyBox(HotkeyPrevBox, gestureText);
                statusKey = "Prev";
                break;
            default:
                return;
        }

        Save();
        ReportIfThisFailed(statusKey, statusKey switch
        {
            "Mute" => "静音",
            "Next" => "下一个",
            _ => "上一个",
        }, gestureText);
        RefreshHotkeyStatus();
    }

    private bool IsDuplicate(string gesture, RecordTarget target)
    {
        if (target != RecordTarget.Mute && _config.HotkeyMuteEnabled &&
            string.Equals(gesture, _config.HotkeyMute, StringComparison.OrdinalIgnoreCase)) return true;
        if (target != RecordTarget.Next && _config.HotkeyNextEnabled &&
            string.Equals(gesture, _config.HotkeyNext, StringComparison.OrdinalIgnoreCase)) return true;
        if (target != RecordTarget.Prev && _config.HotkeyPrevEnabled &&
            string.Equals(gesture, _config.HotkeyPrev, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ================= 设备管理 =================

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => BuildDeviceRows();

    private void BuildDeviceRows()
    {
        if (DeviceAdminPanel is null) return;
        DeviceAdminPanel.Children.Clear();

        IReadOnlyList<AudioDeviceInfo> devices;
        try { devices = _services.OrderedDevices(includeExcluded: true); }
        catch (Exception ex)
        {
            DeviceAdminPanel.Children.Add(new TextBlock
            {
                Text = "读取设备失败：" + ex.Message,
                Foreground = System.Windows.Media.Brushes.OrangeRed,
            });
            return;
        }

        if (devices.Count == 0)
        {
            DeviceAdminPanel.Children.Add(new TextBlock { Text = "没有检测到麦克风。" });
            return;
        }

        foreach (var dev in devices)
        {
            string id = dev.Id;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = dev.FriendlyName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = id,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var alias = new System.Windows.Controls.TextBox
            {
                Text = _config.DeviceAliases.TryGetValue(id, out var a) ? a : "",
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(8, 0, 8, 0),
                ToolTip = "起个短名字，例如「桌面麦」；留空则用原名",
            };
            alias.LostKeyboardFocus += (_, _) =>
            {
                string text = alias.Text.Trim();
                if (text.Length == 0) _config.DeviceAliases.Remove(id);
                else _config.DeviceAliases[id] = text;
                Save();
                BuildDeviceRows();
            };
            Grid.SetColumn(alias, 1);
            row.Children.Add(alias);

            var exclude = new System.Windows.Controls.CheckBox
            {
                Content = "排除",
                IsChecked = _config.ExcludedDevices.Contains(id),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "排除后不参与自动切换，也不出现在托盘菜单",
            };
            exclude.Checked += (_, _) => SetExcluded(id, true);
            exclude.Unchecked += (_, _) => SetExcluded(id, false);
            Grid.SetColumn(exclude, 2);
            row.Children.Add(exclude);

            DeviceAdminPanel.Children.Add(row);
        }
    }

    private void SetExcluded(string deviceId, bool excluded)
    {
        if (excluded)
        {
            if (!_config.ExcludedDevices.Contains(deviceId))
                _config.ExcludedDevices.Add(deviceId);
        }
        else
        {
            if (!_config.ExcludedDevices.Remove(deviceId)) return;
        }
        Save();
    }

    // ================= 关于 =================

    private void InitAbout()
    {
        AboutNameText.Text = AppMetadata.AppName;
        AboutVersionText.Text = $"版本 v{AppMetadata.Version}";
        AboutDescText.Text = AppMetadata.AppDescription;
        AboutAuthorText.Text = $"作者：{AppMetadata.Author}";
        AboutCopyrightText.Text = AppMetadata.Copyright;

        // 图标（从发布出来的 Assets/app.ico 读，读不到就不显示）
        try
        {
            string ico = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(ico))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(ico, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                AboutIcon.Source = bitmap;
            }
        }
        catch { /* 图标缺失不影响功能 */ }

        if (string.IsNullOrWhiteSpace(AppMetadata.DonateUrl))
            DonateButton.ToolTip = "作者还没有设置打赏链接";
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";

        _ = CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            var result = await UpdateChecker.CheckAsync(
                AppMetadata.GitHubOwner, AppMetadata.GitHubRepo, AppMetadata.CurrentVersion);

            if (result is null)
            {
                UpdateStatusText.Text = $"没查到更新信息（仓库可能还没有发布 Release，或网络不通）。\n当前 v{AppMetadata.Version}。";
                return;
            }

            if (result.HasUpdate)
            {
                UpdateStatusText.Text = $"发现新版本 {result.LatestTag}（当前 v{AppMetadata.Version}）";

                var go = MessageBox.Show(
                    $"发现新版本 {result.LatestTag}\n当前 v{AppMetadata.Version}\n\n要打开下载页面吗？",
                    "MicSwitcher - 有更新", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (go == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                    OpenUrl(result.ReleaseUrl);
            }
            else
            {
                UpdateStatusText.Text = $"已是最新版本 v{AppMetadata.Version}（远端 {result.LatestTag}）";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(AppMetadata.RepoUrl);

    private void OpenIssues_Click(object sender, RoutedEventArgs e) => OpenUrl(AppMetadata.IssuesUrl);

    private void OpenStar_Click(object sender, RoutedEventArgs e) => OpenUrl(AppMetadata.StarUrl);

    private void OpenDonate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppMetadata.DonateUrl))
        {
            MessageBox.Show(
                "作者还没有设置打赏链接。\n\n如果你觉得这个工具好用，去 GitHub 点个 Star 就是最好的支持啦～",
                "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenUrl(AppMetadata.StarUrl);
            return;
        }

        OpenUrl(AppMetadata.DonateUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开链接失败：" + ex.Message, "MicSwitcher");
        }
    }

    // ================= 关闭 =================

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
