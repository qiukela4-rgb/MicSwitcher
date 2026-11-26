using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using MicSwitcher.Core.Config;
using Application = System.Windows.Application;

namespace MicSwitcher.App;

internal sealed class TrayIconHost : IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly Action _showSettings;

    private TaskbarIcon _tray = null!;
    private System.Drawing.Icon? _trayIconKeepAlive;
    private bool _trayIconIsMuted;
    private ContextMenu? _currentMenu;

    // explorer 重启检测：TaskbarCreated 广播 + 定时核对任务栏进程双保险
    private TaskbarRestartWatcher? _restartWatcher;
    private DispatcherTimer? _trayWatchdog;
    private int _trayOwnerPid;
    private bool _disposed;

    internal TrayIconHost(Func<AppConfig> config, Action showSettings)
    {
        _config = config;
        _showSettings = showSettings;
    }

    public void Create()
    {
        DiagnosticsService.Log("=== BuildTrayIcon 开始 ===");
        try
        {
            DiagnosticsService.LogEnvironment();

            var icon = LoadTrayIcon(false);
            _trayIconKeepAlive = icon;   // 保持引用，避免图标句柄被回收导致托盘显示空白
            DiagnosticsService.Log($"图标: {(icon is null ? "null" : icon.Width + "x" + icon.Height)}");

            BuildTaskbarIcon(icon);

            // 任务栏重建的消息监听（explorer 重启时 Windows 广播 TaskbarCreated）
            try
            {
                _restartWatcher = new TaskbarRestartWatcher(() =>
                    Application.Current?.Dispatcher.BeginInvoke(new Action(RebuildAfterTaskbarRestart)));
                DiagnosticsService.Log("任务栏重建监听：已注册");
            }
            catch (Exception ex) { DiagnosticsService.Log("任务栏重建监听失败: " + ex.Message); }

            // 兜底：有些环境不广播 TaskbarCreated，改为定时核对任务栏进程 ID
            _trayOwnerPid = GetTrayOwnerPid();
            _trayWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _trayWatchdog.Tick += (_, _) => CheckTrayAlive();
            _trayWatchdog.Start();

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _tray.Visibility = _config().ShowTrayIcon ? Visibility.Visible : Visibility.Hidden;
                    DiagnosticsService.Log($"延迟刷新后: IsCreated={_tray.IsCreated} Visibility={_tray.Visibility}");
                }
                catch (Exception ex) { DiagnosticsService.Log("延迟刷新异常: " + ex.Message); }
            }), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("BuildTrayIcon 异常: " + ex);
        }
    }

    /// <summary>按当前图标/菜单/可见性建出托盘图标对象。</summary>
    private void BuildTaskbarIcon(System.Drawing.Icon? icon)
    {
        icon ??= System.Drawing.SystemIcons.Application;
        _tray = new TaskbarIcon
        {
            Icon = icon,
            ToolTipText = "MicSwitcher - 麦克风切换",
            Visibility = _config().ShowTrayIcon ? Visibility.Visible : Visibility.Hidden,
        };
        _tray.TrayMouseDoubleClick += (_, _) => _showSettings();

        DiagnosticsService.Log($"创建后: IsCreated={_tray.IsCreated} Visibility={_tray.Visibility}");

        if (!_tray.IsCreated)
        {
            try { _tray.ForceCreate(); DiagnosticsService.Log($"ForceCreate 后: IsCreated={_tray.IsCreated}"); }
            catch (Exception ex) { DiagnosticsService.Log("ForceCreate 失败: " + ex.Message); }
        }

        if (_currentMenu is not null) _tray.ContextMenu = _currentMenu;
    }

    /// <summary>explorer 重启后重建：丢弃旧对象重新建，避免 H.NotifyIcon 内部状态还以为图标在。</summary>
    private void RebuildAfterTaskbarRestart()
    {
        if (_disposed) return;
        DiagnosticsService.Log("任务栏重建：重建托盘图标");
        try
        {
            var old = _tray;
            _tray = null!;
            try { old?.Dispose(); } catch (Exception ex) { DiagnosticsService.Log("任务栏重建：释放旧图标失败 " + ex.Message); }

            var icon = LoadTrayIcon(_trayIconIsMuted);
            _trayIconKeepAlive = icon;
            BuildTaskbarIcon(icon);

            _trayOwnerPid = GetTrayOwnerPid();
            DiagnosticsService.Log($"任务栏重建：完成 IsCreated={_tray.IsCreated} 任务栏进程={_trayOwnerPid}");
        }
        catch (Exception ex) { DiagnosticsService.Log("任务栏重建：重建失败 " + ex); }
    }

    private void CheckTrayAlive()
    {
        if (_disposed) return;
        try
        {
            int pid = GetTrayOwnerPid();
            if (pid != _trayOwnerPid)
            {
                DiagnosticsService.Log($"任务栏进程变化 {_trayOwnerPid} → {pid}，恢复托盘图标");
                RebuildAfterTaskbarRestart();
                return;
            }
            // 任务栏还在但图标没创建成功，也补一次
            if (_tray is not null && !_tray.IsCreated)
            {
                DiagnosticsService.Log("托盘图标未创建，补建");
                RebuildAfterTaskbarRestart();
            }
        }
        catch (Exception ex) { DiagnosticsService.Log("托盘自检异常: " + ex.Message); }
    }

    /// <summary>任务栏窗口所属进程 ID（explorer 重启后变化），0 表示任务栏还没起来。</summary>
    private static int GetTrayOwnerPid()
    {
        IntPtr hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>加载托盘图标：优先用随程序发布的 app.ico / app-muted.ico。</summary>
    private static System.Drawing.Icon LoadTrayIcon(bool muted)
    {
        try
        {
            string file = muted ? "app-muted.ico" : "app.ico";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", file);
            if (File.Exists(path)) return new System.Drawing.Icon(path);
        }
        catch (Exception ex) { DiagnosticsService.Log("加载托盘图标失败: " + ex.Message); }
        return CreateTrayIcon();
    }

    /// <summary>按当前静音状态切换托盘图标（可在设置里关掉这个行为）。</summary>
    public void UpdateTrayIcon(bool isMuted)
    {
        if (_tray is null) return;

        bool wantMuted = _config().TrayIconShowsMute && isMuted;
        if (wantMuted == _trayIconIsMuted && _trayIconKeepAlive is not null) return;

        try
        {
            _trayIconIsMuted = wantMuted;
            var icon = LoadTrayIcon(wantMuted);
            var old = _trayIconKeepAlive;
            _trayIconKeepAlive = icon;
            _tray.Icon = icon;
            // 旧图标句柄不再有人引用，及时释放，避免频繁静音切换累积 GDI 句柄
            old?.Dispose();
        }
        catch (Exception ex) { DiagnosticsService.Log("切换托盘图标失败: " + ex.Message); }
    }

    /// <summary>GDI+ 自绘托盘图标；任何绘制失败都回退系统图标，绝不抛异常。</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                DiagnosticsService.Log("图标来源: " + icoPath);
                return new System.Drawing.Icon(icoPath);
            }
            DiagnosticsService.Log("图标文件不存在，回退自绘: " + icoPath);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("加载 app.ico 失败: " + ex.Message);
        }

        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 46, 160, 67));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var mic = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                g.FillEllipse(mic, 12, 8, 8, 8);
                g.FillRectangle(mic, 14, 8, 4, 9);
                g.FillRectangle(mic, 9, 20, 14, 3);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void SetContextMenu(ContextMenu menu)
    {
        _currentMenu = menu;
        if (_tray is null) return;
        _tray.ContextMenu = menu;
    }

    public void CloseMenu()
    {
        if (_currentMenu is { } menu) menu.IsOpen = false;
    }

    public void SetVisible(bool visible)
    {
        if (_tray is null) return;
        _tray.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>气泡通知：加节流，避免抢设备时反复弹窗堆积窗口句柄。</summary>
    private DateTime _lastNotification;
    private string _lastNotificationTitle = "";

    public void ShowNotification(string title, string text)
    {
        if (_tray is null) return;
        var now = DateTime.Now;
        // 同一标题 2 秒内不重复弹；不同标题至少间隔 400 ms
        bool same = title == _lastNotificationTitle;
        var minGap = same ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(400);
        if (now - _lastNotification < minGap) return;

        _lastNotification = now;
        _lastNotificationTitle = title;
        try { _tray.ShowNotification(title, text); }
        catch (Exception ex) { DiagnosticsService.Log("气泡通知失败: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayWatchdog?.Stop();
        _trayWatchdog = null;
        _restartWatcher?.Dispose();
        _restartWatcher = null;
        if (_currentMenu is { } menu) menu.IsOpen = false;
        _tray?.Dispose();
        _tray = null!;
        _currentMenu = null;
        _trayIconKeepAlive?.Dispose();
        _trayIconKeepAlive = null;
    }
}
