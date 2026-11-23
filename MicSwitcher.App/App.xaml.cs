using System.Windows;
using MicSwitcher.Core.Config;
using MessageBox = System.Windows.MessageBox;

namespace MicSwitcher.App;

/// <summary>
/// 托盘主程序：Hardcodet TaskbarIcon 常驻 + 右键菜单 + 单实例守护。
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;

    private AppServices _services = null!;
    private bool _initialized;
    private AppConfig _config = new();
    private TrayIconHost? _trayHost;
    private TrayMenuBuilder? _trayMenu;
    private SettingsWindow? _settingsWindow;
    private static HotkeyService? _hotkeyService;
    private static readonly IReadOnlyDictionary<string, bool> EmptyStatus = new Dictionary<string, bool>();

    /// <summary>各热键最近一次注册结果（供设置窗口显示冲突状态）。</summary>
    public static IReadOnlyDictionary<string, bool> HotkeyStatus
        => _hotkeyService?.Status ?? EmptyStatus;

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticsService.CheckIntegrityLevel();

        // ---- 单实例：已经有实例在跑 → 直接把它的窗口叫出来，然后自己退出 ----
        // 注意：不要用 "Global\" 前缀——创建/打开全局对象需要 SeCreateGlobalPrivilege，
        // 普通权限进程会被拒绝（Access denied），导致唤醒失败。用会话内名字即可（本来就是按用户会话隔离）。
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            // 双保险：先发事件让已有实例自己弹窗口；不行就直接找到它的窗口置前
            if (!_singleInstance.TryWakeExisting())
            {
                MessageBox.Show(
                    "MicSwitcher 已经在运行了，但没能把它唤到前台。\n" +
                    "请看任务栏右下角的托盘图标（可能在 ^ 展开区）。",
                    "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        // 副实例用它来请求「显示设置窗口」
        _singleInstance.StartListening(ShowSettings);

        base.OnStartup(e);

        _config = AppConfig.Load();

        // 「开机自启延迟启动」：等音频服务/设备就绪再初始化，避开开机瞬间枚举不到麦克风
        bool fromAutostart = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        int delaySeconds = fromAutostart && _config.DelayedAutostart
            ? Math.Clamp(_config.DelayedAutostartSeconds, 1, 120)
            : 0;

        if (delaySeconds <= 0)
        {
            InitializeApp(e);
            return;
        }

        DiagnosticsService.Log($"启动延迟：{delaySeconds} 秒后再初始化");
        _startupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(delaySeconds),
        };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer?.Stop();
            _startupTimer = null;
            InitializeApp(e);
        };
        _startupTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _startupTimer;
    private bool _pendingShowSettings;

    /// <summary>真正把托盘、服务、热键拉起来的初始化（可能被延迟调用）。</summary>
    private void InitializeApp(StartupEventArgs e)
    {
        var initWatch = System.Diagnostics.Stopwatch.StartNew();
        double sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        DiagnosticsService.Log($"初始化：开始（距进程创建 {sinceStart:0} ms）");

        _services = new AppServices { Config = _config };
        _initialized = true;
        _services.ApplyConfigToEngines();
        _services.RestoreMode();   // 恢复上次的手动 / 自动 / 锁定，含开机自启
        DiagnosticsService.Log($"初始化：服务就绪 {initWatch.ElapsedMilliseconds} ms，模式={_services.Mode}");

        var trayHost = new TrayIconHost(() => _config, ShowSettings);
        _trayHost = trayHost;
        var trayCommands = new TrayCommands(_services, () => _config, trayHost);
        var trayMenu = new TrayMenuBuilder(trayHost, _services, trayCommands, ShowSettings, ExitApp);
        trayCommands.AttachMenu(trayMenu);
        _trayMenu = trayMenu;

        _services.AutoSwitched += trayCommands.OnAutoSwitched;
        _services.Error += trayCommands.OnServiceError;
        _services.LevelMonitor.Trace = DiagnosticsService.Log;

        trayHost.Create();
        trayMenu.Refresh();
        _hotkeyService = new HotkeyService(() => _config, trayCommands.ToggleMute, forward => trayCommands.Cycle(forward), trayHost.ShowNotification);
        _hotkeyService.Register();
        DiagnosticsService.Log($"初始化：托盘与热键就绪 {initWatch.ElapsedMilliseconds} ms");

        // 最小化启动：命令行 --minimized（开机自启）或配置里的「启动时最小化到托盘」都算
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)
                         || _config.StartMinimized;

        if (!minimized || _pendingShowSettings)
            ShowSettings();
    }

    // ---------- 设置窗口 ----------
    private void ShowSettings()
    {
        // 还在延迟启动阶段 → 记下来，初始化完再弹
        if (!_initialized)
        {
            _pendingShowSettings = true;
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_services);
            _settingsWindow.Changed += (_, _) => ApplyConfig(notify: true);
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        _settingsWindow.Show();
        _settingsWindow.Activate();

        // 从托盘/后台唤回时，用 topmost 闪一下把焦点抢过来
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    /// <summary>把设置窗口里改动过的配置应用到运行中的程序（托盘可见性、热键等）。</summary>
    private void ApplyConfig(bool notify)
    {
        _config = AppConfig.Load();
        if (!_initialized) return;
        _services.Config = _config;
        _services.ApplyStrategy(_config.AutoStrategy);
        _services.ApplyConfigToEngines();

        try
        {
            _trayHost?.SetVisible(_config.ShowTrayIcon);
        }
        catch (Exception ex) { DiagnosticsService.Log("应用托盘可见性失败: " + ex.Message); }

        // 热键配置变了才重新注册，避免每次拨开关都短暂注销
        _hotkeyService?.ReloadIfChanged(_config);

        _trayMenu?.Refresh();
    }

// ---------- 退出 ----------
    private void ExitApp()
    {
        DiagnosticsService.Log("退出：开始");

        // 先收起菜单：退出过程中 UI 线程若被占住，残留的菜单窗口会一直挂在屏幕上，
        // 看起来就像程序没反应
        try
        {
            _trayHost?.CloseMenu();
            _settingsWindow?.AllowClose(); // 放行设置窗口的关闭拦截
            _hotkeyService?.Unregister();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("退出：界面清理失败 " + ex.Message);
        }

        // 释放服务可能阻塞（要等音频采集流停下来）。放到后台线程并限时等待：
        // 无论设备驱动是否响应，都要走到 Shutdown，不能让进程挂在退出上。
        try
        {
            if (_initialized)
            {
                var dispose = System.Threading.Tasks.Task.Run(() =>
                {
                    try { _services.Dispose(); }
                    catch (Exception ex) { DiagnosticsService.Log("退出：释放服务失败 " + ex.Message); }
                });

                if (dispose.Wait(TimeSpan.FromSeconds(3)))
                    DiagnosticsService.Log("退出：服务已释放");
                else
                    DiagnosticsService.Log("退出：服务释放超时（采集流未在 3 秒内停止），继续退出");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("退出：释放服务异常 " + ex.Message);
        }

        try
        {
            _trayHost?.Dispose();
            _trayHost = null;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("退出：销毁托盘失败 " + ex.Message);
        }

        DiagnosticsService.Log("退出：调用 Shutdown");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticsService.Log("退出：OnExit");
        _hotkeyService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}