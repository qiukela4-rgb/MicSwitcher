using MicSwitcher.Core.Audio;
using MicSwitcher.Core.AutoSwitch;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Locking;

namespace MicSwitcher.App;

/// <summary>
/// 应用级服务容器：持有音频服务、电平引擎、设备监听、锁定守护，
/// 并管理互斥的工作模式（手动 / 锁定 / 自动，自动再分「按声音」「按新设备」）。
/// </summary>
public sealed class AppServices : IDisposable
{
    public AudioDeviceService Audio { get; } = new();
    public LevelMonitor LevelMonitor { get; }
    public DeviceWatcher Watcher { get; }
    public DefaultGuard Guard { get; }

    /// <summary>当前配置（切设备时要读「通信角色同步」等开关）。</summary>
    public AppConfig Config { get; set; } = AppConfig.Load();

    /// <summary>当前工作模式。</summary>
    public WorkMode Mode { get; private set; } = WorkMode.Manual;

    /// <summary>当前自动策略。</summary>
    public AutoStrategy AutoStrategy { get; private set; } = AutoStrategy.ByLevel;

    /// <summary>自动切换发生（两种策略共用）。</summary>
    public event EventHandler<AutoSwitchEventArgs>? AutoSwitched;

    /// <summary>引擎出错。</summary>
    public event EventHandler<Exception>? Error;

    public AppServices()
    {
        LevelMonitor = new LevelMonitor(Audio);
        Watcher = new DeviceWatcher(Audio);
        Guard = new DefaultGuard(Audio);

        LevelMonitor.Switched += (_, e) => AutoSwitched?.Invoke(this, e);
        LevelMonitor.Error += (_, ex) => Error?.Invoke(this, ex);
        Watcher.DeviceArrived += OnDeviceArrived;
        Watcher.Error += (_, ex) => Error?.Invoke(this, ex);
        Guard.Error += (_, ex) => Error?.Invoke(this, ex);
    }

    // ---------- 切换设备（统一走这里，保证角色同步开关生效） ----------

    /// <summary>把某设备设为默认麦克风。</summary>
    public void SwitchTo(string deviceId)
        => Audio.SetDefaultDevice(deviceId, Config.SyncCommunicationRole);

    /// <summary>按优先级顺序排好的麦克风列表（默认包含被排除的设备，方便界面上管理）。</summary>
    /// <param name="includeExcluded">false 时过滤掉配置里排除的设备（托盘菜单与自动切换用）。</param>
    public IReadOnlyList<AudioDeviceInfo> OrderedDevices(bool includeExcluded = true)
        => DeviceListOrdering.Build(Audio.EnumerateDevices(AudioFlow.Capture), Config, includeExcluded);

    /// <summary>把配置里的灵敏度/排除列表/角色同步推给引擎（改完设置立即生效）。</summary>
    public void ApplyConfigToEngines()
    {
        LevelMonitor.SyncCommunicationRole = Config.SyncCommunicationRole;
        LevelMonitor.Configure(
            intervalMs: Config.SampleIntervalMs,
            silenceThreshold: Config.SilenceThreshold,
            debounceSamples: Config.DebounceSamples,
            excludedDevices: Config.ExcludedDevices,
            priorityOrder: Config.DevicePriority,
            preferPhysicalDevices: Config.PreferPhysicalDevices);
    }

    // ---------- 模式 ----------

    // 恢复上次模式时不写盘，避免每次启动都重写一次 settings.json
    private bool _suppressModePersist;

    /// <summary>把当前模式写回配置，下次启动（含开机自启）恢复。</summary>
    private void PersistMode()
    {
        if (_suppressModePersist) return;
        Config.Mode = Mode.ToString();
        Config.Save();
    }

    /// <summary>按配置恢复上次的工作模式；在引擎配置推完之后调用。</summary>
    public void RestoreMode()
    {
        _suppressModePersist = true;
        try
        {
            switch (WorkModeParser.Parse(Config.Mode))
            {
                case WorkMode.Auto:
                    SetAuto(Config.AutoStrategy);
                    break;
                case WorkMode.Locked:
                    LockCurrent();
                    break;
                default:
                    SetManual();
                    break;
            }
        }
        finally { _suppressModePersist = false; }
    }

    /// <summary>进入手动模式：停止自动和守护，保留当前默认设备。</summary>
    public void SetManual()
    {
        LevelMonitor.Stop();
        Watcher.Stop();
        Guard.StopGuarding();
        Mode = WorkMode.Manual;
        PersistMode();
    }

    /// <summary>同步「当前自动策略」到界面状态，不启动/停止引擎（用于启动时按配置回填）。</summary>
    public void ApplyStrategy(AutoStrategy strategy) => AutoStrategy = strategy;

    /// <summary>进入自动模式，按指定策略工作。</summary>
    public void SetAuto(AutoStrategy strategy)
    {
        Guard.StopGuarding();

        LevelMonitor.Stop();
        Watcher.Stop();

        AutoStrategy = strategy;
        ApplyConfigToEngines();

        if (strategy == AutoStrategy.ByLevel)
            LevelMonitor.Start();
        else
            Watcher.Start();

        Mode = WorkMode.Auto;
        PersistMode();
    }

    /// <summary>锁定当前默认麦克风。</summary>
    public void LockCurrent()
    {
        LevelMonitor.Stop();
        Watcher.Stop();
        var current = Audio.GetDefaultDeviceId(AudioFlow.Capture);
        if (current is null) return;
        Guard.StartGuarding(current);
        Mode = WorkMode.Locked;
        PersistMode();
    }

    /// <summary>解除锁定，回到手动模式。</summary>
    public void Unlock()
    {
        Guard.StopGuarding();
        Mode = WorkMode.Manual;
        PersistMode();
    }

    // ---------- 「按新设备」策略 ----------

    private void OnDeviceArrived(object? sender, DeviceArrivedEventArgs e)
    {
        try
        {
            if (Mode != WorkMode.Auto || AutoStrategy != AutoStrategy.ByNewDevice) return;

            var from = Audio.GetDefaultDevice(AudioFlow.Capture);
            if (from is not null && from.Id == e.Device.Id) return; // 已经是它了

            SwitchTo(e.Device.Id);

            if (from is not null)
            {
                AutoSwitched?.Invoke(this, new AutoSwitchEventArgs
                {
                    From = from,
                    To = e.Device,
                    WinningLevel = 0f,
                });
            }
        }
        catch (Exception ex) { Error?.Invoke(this, ex); }
    }

    public void Dispose()
    {
        LevelMonitor.Dispose();
        Watcher.Dispose();
        Guard.Dispose();
    }
}
