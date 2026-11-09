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

        lock (_gate)
        {
            _known = _audio.EnumerateDevices(AudioFlow.Capture).Select(d => d.Id).ToHashSet();
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
        List<AudioDeviceInfo> arrived;

        lock (_gate)
        {
            if (!_running) return;

            var devices = _audio.EnumerateDevices(AudioFlow.Capture);
            var ids = devices.Select(d => d.Id).ToHashSet();

            arrived = devices.Where(d => !_known.Contains(d.Id)).ToList();
            _known = ids; // 拔掉的设备从基线移除，再插回来算新设备
        }

        foreach (var dev in arrived)
            DeviceArrived?.Invoke(this, new DeviceArrivedEventArgs { Device = dev });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
