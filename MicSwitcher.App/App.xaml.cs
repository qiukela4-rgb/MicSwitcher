using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using H.NotifyIcon;
using MicSwitcher.Core.Audio;
using MicSwitcher.Core.AutoSwitch;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Hotkeys;
using MicSwitcher.Core.Startup;
using MessageBox = System.Windows.MessageBox;

namespace MicSwitcher.App;

/// <summary>
/// 托盘主程序：Hardcodet TaskbarIcon 常驻 + 右键菜单 + 单实例守护。
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showSettingsEvent;

    private AppServices _services = null!;
    private bool _initialized;
    private AppConfig _config = new();
    private TaskbarIcon _tray = null!;
    private SettingsWindow? _settingsWindow;
    private static System.Drawing.Icon? _trayIconKeepAlive;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 完整性级别检测：进程若是 Low 完整性，托盘图标会被 Windows UIPI 拦掉 ----
        // 典型场景：程序放在被强制打了 Low 标签的目录（如某些沙箱工作区）里运行。
        try
        {
            bool skip = Environment.GetEnvironmentVariable("MICSWITCHER_NO_INTEGRITY_WARN") == "1";
            string level = GetIntegrityLevel(System.Diagnostics.Process.GetCurrentProcess().Handle);
            if (!skip && (level.StartsWith("Low") || level.StartsWith("Untrusted")))
            {
                MessageBox.Show(
                    $"检测到 MicSwitcher 运行在「低完整性级别」（{level}）。\n\n" +
                    "此时 Windows 会阻止本程序向任务栏注册图标，托盘图标不会显示，\n" +
                    "开机自启写注册表也会失败。\n\n" +
                    "解决办法：把程序复制到一个普通目录（如桌面）后再运行。",
                    "MicSwitcher - 完整性级别提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch { /* 检测失败不影响运行 */ }

        // ---- 单实例：已经有实例在跑 → 直接把它的窗口叫出来，然后自己退出 ----
        // 注意：不要用 "Global\" 前缀——创建/打开全局对象需要 SeCreateGlobalPrivilege，
        // 普通权限进程会被拒绝（Access denied），导致唤醒失败。用会话内名字即可（本来就是按用户会话隔离）。
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\MicSwitcher_SingleInstance", out createdNew);
        }
        catch (Exception ex)
        {
            DiagLog("单实例：创建互斥量失败，按独立实例继续运行 " + ex.Message);
            createdNew = true;
        }

        if (!createdNew)
        {
            // 双保险：先发事件让已有实例自己弹窗口；不行就直接找到它的窗口置前
            if (!SignalExistingInstance() && !ActivateExistingWindow())
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
        try
        {
            _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\MicSwitcher_ShowSettings");
            StartShowSettingsListener();
        }
        catch (Exception ex)
        {
            DiagLog("单实例：创建唤醒事件失败，双击将依赖窗口激活兜底 " + ex.Message);
            _showSettingsEvent = null;
        }

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

        DiagLog($"启动延迟：{delaySeconds} 秒后再初始化");
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
        _services = new AppServices { Config = _config };
        _initialized = true;
        _services.ApplyStrategy(_config.AutoStrategy);
        _services.ApplyConfigToEngines();
        _services.AutoSwitched += OnAutoSwitched;
        _services.Error += (_, ex) => ShowBalloon("自动切换出错", ex.Message);
        _services.LevelMonitor.Trace = DiagLog;

        BuildTrayIcon();
        RefreshTrayMenu();
        RegisterHotkeys();

        // 最小化启动：命令行 --minimized（开机自启）或配置里的「启动时最小化到托盘」都算
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)
                         || _config.StartMinimized;

        if (!minimized || _pendingShowSettings)
            ShowSettings();
    }

    // ---------- 托盘 ----------
    private void BuildTrayIcon()
    {
        DiagLog("=== BuildTrayIcon 开始 ===");
        try
        {
            LogEnvironment();

            var icon = LoadTrayIcon(false);
            _trayIconKeepAlive = icon;   // 保持引用，避免图标句柄被回收导致托盘显示空白
            DiagLog($"图标: {(icon is null ? "null" : icon.Width + "x" + icon.Height)}");

            _tray = new TaskbarIcon
            {
                Icon = icon,
                ToolTipText = "MicSwitcher - 麦克风切换",
                Visibility = _config.ShowTrayIcon ? Visibility.Visible : Visibility.Hidden,
            };
            _tray.TrayMouseDoubleClick += (_, _) => ShowSettings();

            DiagLog($"创建后: IsCreated={_tray.IsCreated} Visibility={_tray.Visibility}");

            if (!_tray.IsCreated)
            {
                try { _tray.ForceCreate(); DiagLog($"ForceCreate 后: IsCreated={_tray.IsCreated}"); }
                catch (Exception ex) { DiagLog("ForceCreate 失败: " + ex.Message); }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _tray.Visibility = _config.ShowTrayIcon ? Visibility.Visible : Visibility.Hidden;
                    DiagLog($"延迟刷新后: IsCreated={_tray.IsCreated} Visibility={_tray.Visibility}");
                }
                catch (Exception ex) { DiagLog("延迟刷新异常: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            DiagLog("BuildTrayIcon 异常: " + ex);
        }
    }

    /// <summary>记录会话/窗口站/桌面/任务栏窗口等关键信息，用于定位 TryCreate 失败。</summary>
    private static void LogEnvironment()
    {
        try
        {
            int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            DiagLog($"SessionId={sessionId}");
            DiagLog("本进程完整性级别=" + GetIntegrityLevel(System.Diagnostics.Process.GetCurrentProcess().Handle));

            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            DiagLog("Shell_TrayWnd=" + (trayWnd != IntPtr.Zero ? "找到" : "未找到"));
            if (trayWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(trayWnd, out uint trayPid);
                try
                {
                    using var tp = System.Diagnostics.Process.GetProcessById((int)trayPid);
                    DiagLog($"任务栏进程={tp.ProcessName}({trayPid}) 完整性=" + GetIntegrityLevel(tp.Handle));
                }
                catch { }
            }
        }
        catch (Exception ex) { DiagLog("环境探测异常: " + ex.Message); }
    }

    /// <summary>读取进程令牌的完整性级别（Low/Medium/High）。</summary>
    private static string GetIntegrityLevel(IntPtr processHandle)
    {
        try
        {
            if (!OpenProcessToken(processHandle, 0x0008, out IntPtr tok)) return "未知";
            try
            {
                GetTokenInformation(tok, 25, IntPtr.Zero, 0, out uint len);
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)len);
                try
                {
                    if (GetTokenInformation(tok, 25, buf, len, out _))
                    {
                        var label = System.Runtime.InteropServices.Marshal.PtrToStructure<TokenMandatoryLabel>(buf);
                        IntPtr sub = GetSidSubAuthority(label.Label.Sid, 0);
                        uint rid = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(sub);
                        return rid switch
                        {
                            0x0000 => "Untrusted",
                            0x1000 => "Low（低，无法向任务栏注册图标）",
                            0x2000 => "Medium（中，正常）",
                            0x2100 => "MediumPlus",
                            0x3000 => "High（高/管理员）",
                            0x4000 => "System",
                            _ => "0x" + rid.ToString("X")
                        };
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        catch { }
        return "未知";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TokenMandatoryLabel { public SidAndAttributes Label; }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, uint len, out uint retLen);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>托盘诊断日志：写入 exe 同目录 tray-diag.log（每次启动覆盖），用于排查图标不显示。</summary>
    private static bool _diagInitialized;

    private static void DiagLog(string msg)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-diag.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (!_diagInitialized)
            {
                _diagInitialized = true;
                File.WriteAllText(path, line);
            }
            else
            {
                File.AppendAllText(path, line);
            }
        }
        catch { /* 日志失败不影响运行 */ }
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
        catch (Exception ex) { DiagLog("加载托盘图标失败: " + ex.Message); }
        return CreateTrayIcon();
    }

    /// <summary>按当前静音状态切换托盘图标（可在设置里关掉这个行为）。</summary>
    private void UpdateTrayIcon(bool isMuted)
    {
        if (_tray is null) return;

        bool wantMuted = _config.TrayIconShowsMute && isMuted;
        if (wantMuted == _trayIconIsMuted && _trayIconKeepAlive is not null) return;

        try
        {
            _trayIconIsMuted = wantMuted;
            var icon = LoadTrayIcon(wantMuted);
            _trayIconKeepAlive = icon;
            _tray.Icon = icon;
        }
        catch (Exception ex) { DiagLog("切换托盘图标失败: " + ex.Message); }
    }

    private static bool _trayIconIsMuted;

    /// <summary>GDI+ 自绘托盘图标（绿色圆底+白色麦克风）；任何绘制失败都回退系统图标，绝不抛异常。</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        // 优先加载随程序发布的真实 .ico 文件（多尺寸，兼容性最好）。
        // 运行时现画的 HICON 在托盘里可能显示为空白，故只作为兜底。
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                DiagLog("图标来源: " + icoPath);
                return new System.Drawing.Icon(icoPath);
            }
            DiagLog("图标文件不存在，回退自绘: " + icoPath);
        }
        catch (Exception ex)
        {
            DiagLog("加载 app.ico 失败: " + ex.Message);
        }

        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                using (var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 46, 160, 67)))
                {
                    g.FillEllipse(bg, 1, 1, 30, 30);
                }
                using (var mic = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                {
                    g.FillEllipse(mic, 12, 8, 8, 8);
                    g.FillRectangle(mic, 14, 8, 4, 9);
                    g.FillRectangle(mic, 9, 20, 14, 3);
                }
            }

            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    /// <summary>根据当前设备与模式重建右键菜单（一级菜单 + 设备/模式子菜单）。</summary>
    private void RefreshTrayMenu()
    {
        var menu = new ContextMenu();

        // ---- 当前麦克风（只读标题） ----
        var current = _services.Audio.GetDefaultDevice(AudioFlow.Capture);
        menu.Items.Add(new MenuItem
        {
            Header = current is null ? "没有检测到麦克风" : $"🎤 {current.FriendlyName}",
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        // ---- 切换麦克风（子菜单） ----
        var devicesMenu = new MenuItem { Header = "切换麦克风" };
        var devices = _services.OrderedDevices(includeExcluded: false);
        foreach (var dev in devices)
        {
            var item = new MenuItem
            {
                Header = dev.IsMuted ? "🔇 " + dev.FriendlyName : dev.FriendlyName,
                IsCheckable = true,
                IsChecked = dev.IsDefault,
            };
            string id = dev.Id;
            item.Click += (_, _) => SafeSwitchTo(id);
            devicesMenu.Items.Add(item);
        }
        if (devices.Count == 0)
            devicesMenu.Items.Add(new MenuItem { Header = "（无可用麦克风）", IsEnabled = false });
        menu.Items.Add(devicesMenu);

        var nextItem = new MenuItem { Header = "下一个麦克风" };
        nextItem.Click += (_, _) => SafeCycle();
        menu.Items.Add(nextItem);

        // ---- 静音 ----
        bool muted = current?.IsMuted ?? false;
        var muteItem = new MenuItem { Header = muted ? "🔇 取消静音" : "🎤 静音当前麦" };
        muteItem.Click += (_, _) => SafeToggleMute();
        menu.Items.Add(muteItem);

        menu.Items.Add(new Separator());

        // ---- 模式（子菜单） ----
        var modeMenu = new MenuItem { Header = "模式" };

        bool isManual = _services.Mode == WorkMode.Manual;
        bool isLocked = _services.Mode == WorkMode.Locked;
        bool isAutoLevel = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByLevel;
        bool isAutoNewDevice = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByNewDevice;

        AddRadialItem(modeMenu, "✋ 手动", isManual, () => SetManualMode());
        AddRadialItem(modeMenu, "🎚 自动 · 按声音大小", isAutoLevel, () => SetAutoMode(AutoStrategy.ByLevel));
        AddRadialItem(modeMenu, "🔌 自动 · 按新设备", isAutoNewDevice, () => SetAutoMode(AutoStrategy.ByNewDevice));
        modeMenu.Items.Add(new Separator());
        AddRadialItem(modeMenu, "🔒 锁定当前麦", isLocked, () => SetLockedMode());
        menu.Items.Add(modeMenu);

        // ---- 开机自启 ----
        var autoStartItem = new MenuItem
        {
            Header = "🚀 开机自启",
            IsCheckable = true,
            IsChecked = AutoStart.IsEnabled(),
        };
        autoStartItem.Click += (_, _) =>
        {
            bool now = AutoStart.Toggle();
            RefreshTrayMenu();
            ShowBalloon("开机自启", now ? "已启用" : "已关闭");
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "⚙ 设置…" };
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        var exitItem = new MenuItem { Header = "❌ 退出" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        _tray.ContextMenu = menu;
        UpdateTrayIcon(current?.IsMuted ?? false);
    }

    /// <summary>往子菜单里加一个互斥（单选式）菜单项。</summary>
    private static void AddRadialItem(MenuItem parent, string text, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        parent.Items.Add(item);
    }

    // ---------- 模式切换 ----------

    private void SetManualMode()
    {
        _services.Config = _config;
        _services.SetManual();
        RefreshTrayMenu();
        ShowBalloon("模式：手动", "不再自动切换，仅响应手动命令。");
    }

    private void SetAutoMode(AutoStrategy strategy)
    {
        _services.Config = _config;
        _services.SetAuto(strategy);
        RefreshTrayMenu();
        ShowBalloon("模式：自动",
            strategy == AutoStrategy.ByLevel
                ? "正在监听各麦克风声音，自动切到正在说话的那个。"
                : "正在监听新麦克风接入，插上就切过去。");
    }

    private void SetLockedMode()
    {
        _services.Config = _config;
        _services.LockCurrent();
        RefreshTrayMenu();
        var locked = _services.Audio.GetDefaultDevice(AudioFlow.Capture);
        ShowBalloon("模式：锁定", $"已锁定 {(locked?.FriendlyName ?? "当前麦")}，外部改动会被还原。");
    }

    // ---------- 安全命令封装 ----------
    private void SafeSwitchTo(string deviceId)
    {
        try
        {
            _services.Config = _config;
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(deviceId);
            RefreshTrayMenu();
        }
        catch (Exception ex) { ShowBalloon("切换失败", ex.Message); }
    }

    private void SafeCycle(bool forward = true)
    {
        try
        {
            // 轮流切换固定用物理顺序，避免优先级排序让「下一个」看起来原地打转
            var devices = _services.Audio.EnumerateDevices(AudioFlow.Capture);
            if (devices.Count < 2)
            {
                ShowBalloon("下一个麦克风", "至少需要两个麦克风才能轮流切换。");
                return;
            }
            string? currentId = _services.Audio.GetDefaultDeviceId(AudioFlow.Capture);
            int idx = devices.ToList().FindIndex(d => d.Id == currentId);
            int step = forward ? 1 : -1;
            int nextIdx = ((idx < 0 ? 0 : idx) + step + devices.Count * 2) % devices.Count;
            var next = devices[nextIdx];
            _services.Config = _config;
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(next.Id);
            RefreshTrayMenu();
            ShowBalloon("已切换麦克风", next.FriendlyName);
        }
        catch (Exception ex) { ShowBalloon("切换失败", ex.Message); }
    }

    private void SafeToggleMute()
    {
        try
        {
            var current = _services.Audio.GetDefaultDeviceId(AudioFlow.Capture);
            if (current is null) return;
            bool nowMuted = _services.Audio.ToggleMute(current);
            RefreshTrayMenu();
            ShowBalloon(nowMuted ? "已静音" : "已取消静音", "");
        }
        catch (Exception ex) { ShowBalloon("静音失败", ex.Message); }
    }

    // ---------- 自动切换提示 ----------
    private void OnAutoSwitched(object? sender, AutoSwitchEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshTrayMenu();
            if (!_config.NotifyOnAutoSwitch) return;

            if (e.From.Id == e.To.Id)
                ShowBalloon("麦克风已恢复", $"原默认麦不可用，已切到 {e.To.FriendlyName}");
            else if (e.WinningLevel <= 0f)
                ShowBalloon("已切到真实麦克风", $"{e.From.FriendlyName} → {e.To.FriendlyName}");
            else
                ShowBalloon("自动切换麦克风", $"{e.From.FriendlyName} → {e.To.FriendlyName}");
        });
    }

    // ---------- 全局热键 ----------
    private HwndSource? _hotkeySource;

    private const int WM_HOTKEY = 0x0312;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyIdMute = 1;
    private const int HotkeyIdNext = 2;
    private const int HotkeyIdPrev = 3;

    /// <summary>按配置注册全局热键（静音 / 下一个 / 上一个）。</summary>
    private void RegisterHotkeys()
    {
        _hotkeySignature = $"{_config.EnableHotkeys}|{_config.HotkeyMute}|{_config.HotkeyNext}|{_config.HotkeyPrev}|{_config.HotkeyMuteEnabled}{_config.HotkeyNextEnabled}{_config.HotkeyPrevEnabled}";
        HotkeyStatus.Clear();

        if (!_config.EnableHotkeys) { DiagLog("热键：配置里已关闭，跳过注册"); return; }

        try
        {
            _hotkeySource = new HwndSource(new HwndSourceParameters("MicSwitcherHotkeyWnd")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
            });
            _hotkeySource.AddHook(HotkeyWndProc);

            bool m = TryRegister(HotkeyIdMute, "Mute", _config.HotkeyMute, "静音", _config.HotkeyMuteEnabled);
            bool n = TryRegister(HotkeyIdNext, "Next", _config.HotkeyNext, "下一个", _config.HotkeyNextEnabled);
            bool p = TryRegister(HotkeyIdPrev, "Prev", _config.HotkeyPrev, "上一个", _config.HotkeyPrevEnabled);
            DiagLog($"热键注册: 静音={m} 下一个={n} 上一个={p}");

            // 有失败的热键 → 托盘提示一次（不影响程序其余功能）
            var failed = HotkeyStatus.Where(kv => !kv.Value).Select(kv => kv.Key switch
            {
                "Mute" => $"静音 {_config.HotkeyMute}",
                "Next" => $"下一个 {_config.HotkeyNext}",
                _ => $"上一个 {_config.HotkeyPrev}",
            }).ToList();

            if (failed.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    ShowBalloon("热键被占用",
                        string.Join("、", failed) + " 无法注册，请到「设置 → 全局快捷键」里更换。")),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            DiagLog("热键注册异常: " + ex.Message);
        }
    }

    /// <summary>各热键最近一次注册结果（供设置窗口显示冲突状态）。</summary>
    internal static readonly Dictionary<string, bool> HotkeyStatus = new();

    private bool TryRegister(int id, string key, string? text, string label, bool enabled = true)
    {
        if (!enabled)
        {
            HotkeyStatus[key] = true;   // 未启用视为“无冲突”
            DiagLog($"热键「{label}」未启用，跳过注册");
            return true;
        }

        bool ok = false;

        if (_hotkeySource is not null)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture, out string? error))
            {
                DiagLog($"热键「{label}」无效（{text}）：{error}");
            }
            else
            {
                ok = RegisterHotKey(_hotkeySource.Handle, id, gesture.RegisterModifiers, gesture.VirtualKey);
                if (!ok) DiagLog($"热键「{label}」({gesture}) 注册失败，可能被其他程序占用");
            }
        }

        HotkeyStatus[key] = ok;
        return ok;
    }

    /// <summary>重新注册热键（设置窗口改完热键后调用）。</summary>
    private void ReloadHotkeys()
    {
        UnregisterHotkeys();
        RegisterHotkeys();
    }

    private void UnregisterHotkeys()
    {
        try
        {
            if (_hotkeySource is null) return;
            UnregisterHotKey(_hotkeySource.Handle, HotkeyIdMute);
            UnregisterHotKey(_hotkeySource.Handle, HotkeyIdNext);
            UnregisterHotKey(_hotkeySource.Handle, HotkeyIdPrev);
            _hotkeySource.Dispose();
            _hotkeySource = null;
        }
        catch { /* 忽略 */ }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyIdMute: SafeToggleMute(); handled = true; break;
            case HotkeyIdNext: SafeCycle(true); handled = true; break;
            case HotkeyIdPrev: SafeCycle(false); handled = true; break;
        }
        return IntPtr.Zero;
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
            if (_tray is not null)
                _tray.Visibility = _config.ShowTrayIcon ? Visibility.Visible : Visibility.Hidden;
        }
        catch (Exception ex) { DiagLog("应用托盘可见性失败: " + ex.Message); }

        // 热键配置变了才重新注册，避免每次拨开关都短暂注销
        string signature = $"{_config.EnableHotkeys}|{_config.HotkeyMute}|{_config.HotkeyNext}|{_config.HotkeyPrev}|{_config.HotkeyMuteEnabled}{_config.HotkeyNextEnabled}{_config.HotkeyPrevEnabled}";
        if (signature != _hotkeySignature)
        {
            _hotkeySignature = signature;
            ReloadHotkeys();
        }

        RefreshTrayMenu();
    }

    private string _hotkeySignature = "";

    /// <summary>副实例请求已运行的实例显示设置窗口。</summary>
    private static bool SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\MicSwitcher_ShowSettings", out var ev))
            {
                using (ev)
                {
                    ev.Set();
                    DiagLog("单实例：已通知已有实例显示窗口");
                    return true;
                }
            }
            DiagLog("单实例：未找到唤醒事件（对端可能是旧版本）");
        }
        catch (Exception ex) { DiagLog("单实例：发事件失败 " + ex.Message); }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// 兜底：直接找到已有实例的主窗口并置前（窗口可能是隐藏到托盘的，也能唤回来）。
    /// 这样即使对方是旧版本、没有唤醒事件，双击也能打开程序。
    /// </summary>
    private static bool ActivateExistingWindow()
    {
        // 新旧版本的窗口标题都试一遍
        foreach (var title in new[] { "MicSwitcher", "MicSwitcher 设置" })
        {
            IntPtr h = FindWindow(null, title);
            if (h == IntPtr.Zero) continue;

            try
            {
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                else ShowWindow(h, SW_SHOW);
                SetForegroundWindow(h);
                DiagLog($"单实例：已直接唤起窗口「{title}」");
                return true;
            }
            catch (Exception ex) { DiagLog("单实例：唤起窗口失败 " + ex.Message); }
        }

        DiagLog("单实例：没找到已有窗口");
        return false;
    }

    private void StartShowSettingsListener()
    {
        var ev = _showSettingsEvent;
        if (ev is null) return;

        var th = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    ev.WaitOne();
                    DiagLog("单实例：收到唤醒信号，弹出主窗口");
                    Dispatcher.BeginInvoke(new Action(ShowSettings));
                }
                catch { return; }
            }
        })
        { IsBackground = true, Name = "MicSwitcher-ShowSettings" };
        th.Start();
    }

    // ---------- 退出 ----------
    private void ExitApp()
    {
        _settingsWindow?.AllowClose(); // 放行设置窗口的关闭拦截
        UnregisterHotkeys();
        _tray.Dispose();
        _tray = null!;
        _services.Dispose();
        Shutdown();
    }

    private void ShowBalloon(string title, string text)
    {
        if (_tray is null) return;
        _tray.ShowNotification(title, text);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 不主动 ReleaseMutex：进程退出时内核会自动释放命名互斥量，
        // 且 ReleaseMutex 只能在持有它的线程调用（OnExit 可能在其他线程，会抛异常）。
        UnregisterHotkeys();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}