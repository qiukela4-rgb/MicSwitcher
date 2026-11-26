using System.Runtime.InteropServices;
using MicSwitcher.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using DeviceState = NAudio.CoreAudioApi.DeviceState;

namespace MicSwitcher.Core.Locking;

/// <summary>守护触发事件：默认设备被外部改动并已还原。</summary>
public sealed class GuardTriggeredEventArgs : EventArgs
{
    public required AudioDeviceInfo LockedDevice { get; init; }
    public string? IntruderName { get; init; } // 试图抢默认的设备名
}

/// <summary>外部抢占的节流规则：避免两个软件每秒互抢默认设备。</summary>
public static class GuardSnoozePolicy
{
    /// <summary>两次还原间隔小于这个时长，算作一次连续抢占。</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(1500);

    /// <summary>连续抢占达到这个次数，判定外部软件持续接管，开始让路。</summary>
    public const int BurstThreshold = 2;

    /// <summary>让路时长。</summary>
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 判定本次抢占是否应触发让路，并返回新的连击计数。
    /// 调用方负责在让路结束后把计数清零。
    /// </summary>
    public static bool ShouldSnooze(TimeSpan sinceLastRestore, int priorBurstCount, out int newBurstCount)
    {
        if (sinceLastRestore < BurstWindow)
        {
            newBurstCount = priorBurstCount + 1;
            return newBurstCount >= BurstThreshold;
        }
        newBurstCount = 0;
        return false;
    }
}

/// <summary>
/// 默认设备锁定守护。
/// 锁定某个录音设备后，一旦检测到默认设备被改成别的，立即改回。
/// 监听 IMMNotificationClient 的默认设备变更回调 + 周期轮询兜底。
/// </summary>
public sealed class DefaultGuard : IDisposable, IMMNotificationClient
{
    private readonly AudioDeviceService _audio;
    private readonly System.Timers.Timer _pollTimer;
    private readonly object _lock = new();

    private string? _lockedDeviceId;
    private string? _lockedDeviceName;
    private bool _enabled;
    private bool _disposed;
    private MMDeviceEnumerator? _enumerator; // 保持引用，防止 COM 回调被回收

    // 外部软件（语音输入 / 虚拟声卡）持续抢默认设备时临时让路，
    // 避免两个程序每秒互抢：用户听不到声音，还会在短时间内堆积大量 COM 调用与通知。
    private DateTime _lastRestore = DateTime.MinValue;
    private int _restoreBurstCount;
    private DateTime _snoozeUntil = DateTime.MinValue;

    /// <summary>是否因外部持续抢占而暂时让路（不再抢回）。</summary>
    public bool IsSnoozed => DateTime.Now < _snoozeUntil;

    /// <summary>守护因外部改动而还原时触发。</summary>
    public event EventHandler<GuardTriggeredEventArgs>? GuardTriggered;

    /// <summary>守护执行出错时触发。</summary>
    public event EventHandler<Exception>? Error;

    public DefaultGuard(AudioDeviceService audio, int pollIntervalMs = 1000)
    {
        _audio = audio;
        _pollTimer = new System.Timers.Timer(pollIntervalMs) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => SafeEnforce();

        // 注册设备变更通知（默认设备变化会回调 OnDefaultDeviceChanged）
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (COMException)
        {
            _enumerator = null; // 注册失败则仅靠轮询兜底
        }
    }

    /// <summary>当前锁定的设备 ID，未锁定为 null。</summary>
    public string? LockedDeviceId => _lockedDeviceId;

    /// <summary>是否处于守护状态。</summary>
    public bool IsGuarding => _enabled && _lockedDeviceId is not null;

    /// <summary>
    /// 开始守护指定录音设备。
    /// </summary>
    public void StartGuarding(string deviceId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DefaultGuard));
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("设备 ID 不能为空", nameof(deviceId));

        // 先把要用的 COM 结果取好，再进锁。SetDefaultDevice 会触发
        // OnDefaultDeviceChanged → Enforce → 同一把 _lock，持锁调用会自等。
        var name = _audio.EnumerateDevices(AudioFlow.Capture)
            .FirstOrDefault(d => d.Id == deviceId)?.FriendlyName ?? "(未知设备)";

        lock (_lock)
        {
            _lockedDeviceId = deviceId;
            _lockedDeviceName = name;
            _enabled = true;
            _pollTimer.Start();
        }

        // 锁外设默认设备
        _audio.SetDefaultDevice(deviceId);
    }

    /// <summary>停止守护（保留当前默认设备不动）。</summary>
    public void StopGuarding()
    {
        lock (_lock)
        {
            _enabled = false;
            _lockedDeviceId = null;
            _lockedDeviceName = null;
            _restoreBurstCount = 0;
            _snoozeUntil = DateTime.MinValue;
            _pollTimer.Stop();
        }
    }

    private void SafeEnforce()
    {
        try { Enforce(); }
        catch (Exception ex) { Error?.Invoke(this, ex); }
    }

    /// <summary>检查当前默认是否为锁定设备，不是则改回。</summary>
    private void Enforce()
    {
        string? lockedId;
        string? lockedName;
        lock (_lock)
        {
            if (!_enabled || _lockedDeviceId is null) return;
            lockedId = _lockedDeviceId;
            lockedName = _lockedDeviceName;
        }

        var devices = _audio.EnumerateDevices(AudioFlow.Capture);

        // 锁定的设备已经不在（被拔掉 / 被禁用）：守不住了。
        // 不解锁的话，轮询会每秒尝试设一个不存在的设备、每秒抛一次异常，
        // 用户还会以为一直是锁着的。
        // 注意 devices 为空可能只是瞬时枚举失败（音频服务重启中），此时不能误解锁。
        if (devices.Count > 0 && devices.All(d => d.Id != lockedId))
        {
            StopGuarding();
            Error?.Invoke(this, new InvalidOperationException(
                $"锁定的麦克风「{lockedName ?? lockedId}」已不可用，已自动解除锁定。"));
            return;
        }

        var current = _audio.GetDefaultDeviceId(AudioFlow.Capture);
        if (current is null || current == lockedId) return;

        // 让路期内不抢回：语音输入这类软件接管默认设备时，硬抢只会让它收不到声音
        if (IsSnoozed) return;

        // 高频抢占检测：判定外部软件持续接管时让路，避免两边互抢
        var now = DateTime.Now;
        if (GuardSnoozePolicy.ShouldSnooze(now - _lastRestore, _restoreBurstCount, out int burst))
        {
            _snoozeUntil = now.Add(GuardSnoozePolicy.SnoozeDuration);
            _restoreBurstCount = 0;
            Error?.Invoke(this, new InvalidOperationException(
                $"检测到其他软件持续切换麦克风，已临时让路 {(int)GuardSnoozePolicy.SnoozeDuration.TotalSeconds} 秒。若在用语音输入，建议切到手动模式。"));
            return;
        }
        _restoreBurstCount = burst;
        _lastRestore = now;

        // 默认被改了 → 改回锁定的设备
        string? intruder = devices.FirstOrDefault(d => d.Id == current)?.FriendlyName;

        _audio.SetDefaultDevice(lockedId);

        var lockedInfo = devices.FirstOrDefault(d => d.Id == lockedId);
        if (lockedInfo is not null)
        {
            GuardTriggered?.Invoke(this, new GuardTriggeredEventArgs
            {
                LockedDevice = lockedInfo,
                IntruderName = intruder,
            });
        }
    }

    // ---- IMMNotificationClient 回调 ----
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId)
    {
        if (flow != DataFlow.Capture) return; // 只关心录音设备
        // 默认设备变了：立即触发一次守护检查
        SafeEnforce();
    }

    public void OnDeviceAdded(string pwstrDeviceName) { }
    public void OnDeviceRemoved(string pwstrDeviceName) { }
    public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState newState) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch { /* 忽略注销异常 */ }
            _enumerator.Dispose();
            _enumerator = null;
        }
    }
}
