using System;
using System.Linq;
using MicSwitcher.Core.Audio;
using MicSwitcher.Core.AutoSwitch;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Startup;
using Application = System.Windows.Application;

namespace MicSwitcher.App;

internal sealed class TrayCommands
{
    private readonly AppServices _services;
    private readonly Func<AppConfig> _config;
    private readonly TrayIconHost _host;
    private TrayMenuBuilder? _menu;

    internal TrayCommands(AppServices services, Func<AppConfig> config, TrayIconHost host)
    {
        _services = services;
        _config = config;
        _host = host;
    }

    public void AttachMenu(TrayMenuBuilder menu) => _menu = menu;

    public void SetManual()
    {
        _services.Config = _config();
        _services.SetManual();
        _menu?.Refresh();
        _host.ShowNotification("模式：手动", "不再自动切换，仅响应手动命令。");
    }

    public void SetAuto(AutoStrategy strategy)
    {
        _services.Config = _config();
        _services.SetAuto(strategy);
        _menu?.Refresh();
        _host.ShowNotification("模式：自动",
            strategy == AutoStrategy.ByLevel
                ? "正在监听各麦克风声音，自动切到正在说话的那个。"
                : "正在监听新麦克风接入，插上就切过去。");
    }

    public void SetLocked()
    {
        _services.Config = _config();
        _services.LockCurrent();
        _menu?.Refresh();
        var locked = _services.Audio.GetDefaultDevice(AudioFlow.Capture);
        _host.ShowNotification("模式：锁定", $"已锁定 {(locked?.FriendlyName ?? "当前麦")}，外部改动会被还原。");
    }

    public void SwitchTo(string deviceId)
    {
        try
        {
            _services.Config = _config();
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(deviceId);
            _menu?.Refresh();
        }
        catch (Exception ex) { _host.ShowNotification("切换失败", ex.Message); }
    }

    public void Cycle(bool forward = true)
    {
        try
        {
            // 轮流切换固定用物理顺序，避免优先级排序让「下一个」看起来原地打转
            var devices = _services.Audio.EnumerateDevices(AudioFlow.Capture);
            if (devices.Count < 2)
            {
                _host.ShowNotification("下一个麦克风", "至少需要两个麦克风才能轮流切换。");
                return;
            }
            string? currentId = _services.Audio.GetDefaultDeviceId(AudioFlow.Capture);
            int idx = devices.ToList().FindIndex(d => d.Id == currentId);
            int step = forward ? 1 : -1;
            int nextIdx = ((idx < 0 ? 0 : idx) + step + devices.Count * 2) % devices.Count;
            var next = devices[nextIdx];
            _services.Config = _config();
            if (_services.Mode != WorkMode.Manual) _services.SetManual();
            _services.SwitchTo(next.Id);
            _menu?.Refresh();
            _host.ShowNotification("已切换麦克风", next.FriendlyName);
        }
        catch (Exception ex) { _host.ShowNotification("切换失败", ex.Message); }
    }

    public void ToggleMute()
    {
        try
        {
            var current = _services.Audio.GetDefaultDeviceId(AudioFlow.Capture);
            if (current is null) return;
            bool nowMuted = _services.Audio.ToggleMute(current);
            _menu?.Refresh();
            _host.ShowNotification(nowMuted ? "已静音" : "已取消静音", "");
        }
        catch (Exception ex) { _host.ShowNotification("静音失败", ex.Message); }
    }

    public void ToggleAutoStart()
    {
        bool now = AutoStart.Toggle();
        _menu?.Refresh();
        _host.ShowNotification("开机自启", now ? "已启用" : "已关闭");
    }

    public void OnAutoSwitched(object? sender, AutoSwitchEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _menu?.Refresh();
            if (!_config().NotifyOnAutoSwitch) return;

            if (e.From.Id == e.To.Id)
                _host.ShowNotification("麦克风已恢复", $"原默认麦不可用，已切到 {e.To.FriendlyName}");
            else if (e.WinningLevel <= 0f)
                _host.ShowNotification("已切到真实麦克风", $"{e.From.FriendlyName} → {e.To.FriendlyName}");
            else
                _host.ShowNotification("自动切换麦克风", $"{e.From.FriendlyName} → {e.To.FriendlyName}");
        });
    }

    public void OnServiceError(object? sender, Exception ex)
    {
        _host.ShowNotification("麦克风提示", ex.Message);
    }
}