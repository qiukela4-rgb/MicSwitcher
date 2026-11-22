using System;
using System.Windows.Controls;
using MicSwitcher.Core.Audio;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Startup;

namespace MicSwitcher.App;

internal sealed class TrayMenuBuilder
{
    private readonly TrayIconHost _host;
    private readonly AppServices _services;
    private readonly Action<string> _switchTo;
    private readonly Action _cycle;
    private readonly Action _toggleMute;
    private readonly Action _setManual;
    private readonly Action<AutoStrategy> _setAuto;
    private readonly Action _setLocked;
    private readonly Action _showSettings;
    private readonly Action _exitApp;
    private readonly Action<string, string> _showBalloon;

    internal TrayMenuBuilder(
        TrayIconHost host,
        AppServices services,
        Action<string> switchTo,
        Action cycle,
        Action toggleMute,
        Action setManual,
        Action<AutoStrategy> setAuto,
        Action setLocked,
        Action showSettings,
        Action exitApp,
        Action<string, string> showBalloon)
    {
        _host = host;
        _services = services;
        _switchTo = switchTo;
        _cycle = cycle;
        _toggleMute = toggleMute;
        _setManual = setManual;
        _setAuto = setAuto;
        _setLocked = setLocked;
        _showSettings = showSettings;
        _exitApp = exitApp;
        _showBalloon = showBalloon;
    }

    /// <summary>根据当前设备与模式重建右键菜单（一级菜单 + 设备/模式子菜单）。</summary>
    public void Refresh()
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
            item.Click += (_, _) => _switchTo(id);
            devicesMenu.Items.Add(item);
        }
        if (devices.Count == 0)
            devicesMenu.Items.Add(new MenuItem { Header = "（无可用麦克风）", IsEnabled = false });
        menu.Items.Add(devicesMenu);

        var nextItem = new MenuItem { Header = "下一个麦克风" };
        nextItem.Click += (_, _) => _cycle();
        menu.Items.Add(nextItem);

        // ---- 静音 ----
        bool muted = current?.IsMuted ?? false;
        var muteItem = new MenuItem { Header = muted ? "🔇 取消静音" : "🎤 静音当前麦" };
        muteItem.Click += (_, _) => _toggleMute();
        menu.Items.Add(muteItem);

        menu.Items.Add(new Separator());

        // ---- 模式（子菜单） ----
        var modeMenu = new MenuItem { Header = "模式" };

        bool isManual = _services.Mode == WorkMode.Manual;
        bool isLocked = _services.Mode == WorkMode.Locked;
        bool isAutoLevel = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByLevel;
        bool isAutoNewDevice = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByNewDevice;

        AddRadialItem(modeMenu, "✋ 手动", isManual, () => _setManual());
        AddRadialItem(modeMenu, "🎚 自动 · 按声音大小", isAutoLevel, () => _setAuto(AutoStrategy.ByLevel));
        AddRadialItem(modeMenu, "🔌 自动 · 按新设备", isAutoNewDevice, () => _setAuto(AutoStrategy.ByNewDevice));
        modeMenu.Items.Add(new Separator());
        AddRadialItem(modeMenu, "🔒 锁定当前麦", isLocked, () => _setLocked());
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
            Refresh();
            _showBalloon("开机自启", now ? "已启用" : "已关闭");
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "⚙ 设置…" };
        settingsItem.Click += (_, _) => _showSettings();
        menu.Items.Add(settingsItem);

        var exitItem = new MenuItem { Header = "❌ 退出" };
        exitItem.Click += (_, _) => _exitApp();
        menu.Items.Add(exitItem);

        _host.SetContextMenu(menu);
        _host.UpdateTrayIcon(current?.IsMuted ?? false);
    }

    /// <summary>往子菜单里加一个互斥（单选式）菜单项。</summary>
    private static void AddRadialItem(MenuItem parent, string text, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        parent.Items.Add(item);
    }
}