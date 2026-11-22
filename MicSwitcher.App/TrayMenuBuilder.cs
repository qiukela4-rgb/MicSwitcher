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
    private readonly TrayCommands _commands;
    private readonly Action _showSettings;
    private readonly Action _exitApp;

    internal TrayMenuBuilder(
        TrayIconHost host,
        AppServices services,
        TrayCommands commands,
        Action showSettings,
        Action exitApp)
    {
        _host = host;
        _services = services;
        _commands = commands;
        _showSettings = showSettings;
        _exitApp = exitApp;
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
            item.Click += (_, _) => _commands.SwitchTo(id);
            devicesMenu.Items.Add(item);
        }
        if (devices.Count == 0)
            devicesMenu.Items.Add(new MenuItem { Header = "（无可用麦克风）", IsEnabled = false });
        menu.Items.Add(devicesMenu);

        var nextItem = new MenuItem { Header = "下一个麦克风" };
        nextItem.Click += (_, _) => _commands.Cycle();
        menu.Items.Add(nextItem);

        // ---- 静音 ----
        bool muted = current?.IsMuted ?? false;
        var muteItem = new MenuItem { Header = muted ? "🔇 取消静音" : "🎤 静音当前麦" };
        muteItem.Click += (_, _) => _commands.ToggleMute();
        menu.Items.Add(muteItem);

        menu.Items.Add(new Separator());

        // ---- 模式（子菜单） ----
        var modeMenu = new MenuItem { Header = "模式" };

        bool isManual = _services.Mode == WorkMode.Manual;
        bool isLocked = _services.Mode == WorkMode.Locked;
        bool isAutoLevel = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByLevel;
        bool isAutoNewDevice = _services.Mode == WorkMode.Auto && _services.AutoStrategy == AutoStrategy.ByNewDevice;

        AddRadialItem(modeMenu, "✋ 手动", isManual, () => _commands.SetManual());
        AddRadialItem(modeMenu, "🎚 自动 · 按声音大小", isAutoLevel, () => _commands.SetAuto(AutoStrategy.ByLevel));
        AddRadialItem(modeMenu, "🔌 自动 · 按新设备", isAutoNewDevice, () => _commands.SetAuto(AutoStrategy.ByNewDevice));
        modeMenu.Items.Add(new Separator());
        AddRadialItem(modeMenu, "🔒 锁定当前麦", isLocked, () => _commands.SetLocked());
        menu.Items.Add(modeMenu);

        // ---- 开机自启 ----
        var autoStartItem = new MenuItem
        {
            Header = "🚀 开机自启",
            IsCheckable = true,
            IsChecked = AutoStart.IsEnabled(),
        };
        autoStartItem.Click += (_, _) => _commands.ToggleAutoStart();
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