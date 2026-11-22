using MicSwitcher.Core.Audio;

namespace MicSwitcher.Core.AutoSwitch;

/// <summary>新麦克风接入事件。</summary>
public sealed class DeviceArrivedEventArgs : EventArgs
{
    public required AudioDeviceInfo Device { get; init; }
}

/// <summary>
/// 监听「新的录音设备变为可用」。
/// 用周期轮询对比活动设备集合实现，不依赖 COM 回调线程，行为可预测。
/// 说明：只对「本轮之前没见过、现在出现」的设备触发一次；设备拔掉后再次插入还会再触发。
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly AudioDeviceService _audio;
    private readonly System.Timers.Timer _timer;
    private readonly object _gate = new();

    private HashSet<string> _known = new();
    private bool _running;
    private bool _disposed;

    /// <summary>检测到新麦克风接入。</summary>
    public event EventHandler<DeviceArrivedEventArgs>? DeviceArrived;

    /// <summary>轮询出错。</summary>
    public event EventHandler<Exception>? Error;

    public DeviceWatcher(AudioDeviceService audio, int intervalMs = 1500)
    {
        _audio = audio;
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => SafePoll();
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>开始监听。以「当前已有的设备」为基线，之后只对新出现的触发。</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeviceWatcher));

        // 枚举放在锁外：COM 调用可能因音频服务忙而阻塞，持锁会把 Stop 一起卡住
        var baseline = _audio.EnumerateDevices(AudioFlow.Capture).Select(d => d.Id).ToHashSet();

        lock (_gate)
        {
            _known = baseline;
            _running = true;
        }
        _timer.Start();
    }

    /// <summary>停止监听。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Stop();
        }
    }

    private void SafePoll()
    {
        try { Poll(); }
        catch (Exception ex) { Error?.Invoke(this, ex); }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (!_running) return;
        }

        // 枚举在锁外
        var devices = _audio.EnumerateDevices(AudioFlow.Capture);
        var ids = devices.Select(d => d.Id).ToList();

        List<string> arrivedIds;
        lock (_gate)
        {
            if (!_running) return;   // 枚举期间可能已 Stop

            var result = ArrivalPolicy.Diff(_known, ids);
            arrivedIds = result.Arrived.ToList();
            _known = result.NextBaseline.ToHashSet();
        }

        if (arrivedIds.Count == 0) return;

        var byId = devices.ToDictionary(d => d.Id);
        foreach (var id in arrivedIds)
        {
            if (byId.TryGetValue(id, out var dev))
                DeviceArrived?.Invoke(this, new DeviceArrivedEventArgs { Device = dev });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        lock (_gate) { _running = false; }   // 释放之后 IsRunning 不该还是 true
    }
}
