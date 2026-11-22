using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using MicSwitcher.Core.Config;
using Application = System.Windows.Application;

namespace MicSwitcher.App;

internal sealed class TrayIconHost
{
    private readonly Func<AppConfig> _config;
    private readonly Action _showSettings;

    private TaskbarIcon _tray = null!;
    private System.Drawing.Icon? _trayIconKeepAlive;
    private bool _trayIconIsMuted;
    private ContextMenu? _currentMenu;

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

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _tray.Visibility = _config().ShowTrayIcon ? Visibility.Visible : Visibility.Hidden;
                    DiagnosticsService.Log($"延迟刷新后: IsCreated={_tray.IsCreated} Visibility={_tray.Visibility}");
                }
                catch (Exception ex) { DiagnosticsService.Log("延迟刷新异常: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("BuildTrayIcon 异常: " + ex);
        }
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
            _trayIconKeepAlive = icon;
            _tray.Icon = icon;
        }
        catch (Exception ex) { DiagnosticsService.Log("切换托盘图标失败: " + ex.Message); }
    }

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

    public void SetContextMenu(ContextMenu menu)
    {
        _tray.ContextMenu = menu;
        _currentMenu = menu;
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

    public void ShowNotification(string title, string text)
    {
        if (_tray is null) return;
        _tray.ShowNotification(title, text);
    }

    public void Dispose()
    {
        if (_currentMenu is { } menu) menu.IsOpen = false;
        _tray?.Dispose();
        _tray = null!;
        _currentMenu = null;
    }
}