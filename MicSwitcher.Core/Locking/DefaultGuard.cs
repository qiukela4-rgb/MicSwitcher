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

        lock (_lock)
        {
            _lockedDeviceId = deviceId;
            _lockedDeviceName = _audio.EnumerateDevices(AudioFlow.Capture)
                .FirstOrDefault(d => d.Id == deviceId)?.FriendlyName ?? "(未知设备)";
            _enabled = true;

            // 立刻把默认设为锁定的设备
            _audio.SetDefaultDevice(deviceId);
            _pollTimer.Start();
        }
    }

    /// <summary>停止守护（保留当前默认设备不动）。</summary>
    public void StopGuarding()
    {
        lock (_lock)
        {
            _enabled = false;
            _lockedDeviceId = null;
            _lockedDeviceName = null;
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
        lock (_lock)
        {
            if (!_enabled || _lockedDeviceId is null) return;
            lockedId = _lockedDeviceId;
        }

        var current = _audio.GetDefaultDeviceId(AudioFlow.Capture);
        if (current is null || current == lockedId) return;

        // 默认被改了 → 改回锁定的设备
        string? intruder = _audio.EnumerateDevices(AudioFlow.Capture)
            .FirstOrDefault(d => d.Id == current)?.FriendlyName;

        _audio.SetDefaultDevice(lockedId);

        var lockedInfo = _audio.EnumerateDevices(AudioFlow.Capture)
            .FirstOrDefault(d => d.Id == lockedId);
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
