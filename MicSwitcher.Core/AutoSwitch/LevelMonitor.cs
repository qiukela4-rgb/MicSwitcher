using System.Collections.Concurrent;
using MicSwitcher.Core.Audio;

namespace MicSwitcher.Core.AutoSwitch;

/// <summary>自动切换结果通知的事件参数。</summary>
public sealed class AutoSwitchEventArgs : EventArgs
{
    public required AudioDeviceInfo From { get; init; }
    public required AudioDeviceInfo To { get; init; }
    public float WinningLevel { get; init; }
}

/// <summary>
/// 电平探测自动切换引擎。
/// 周期性采样所有麦克风峰值，维护滑动窗口平均电平，
/// 把"窗口内持续有声且最强"的麦克风自动设为默认。
/// </summary>
public sealed class LevelMonitor : IDisposable
{
    private readonly AudioDeviceService _audio;
    private readonly DevicePeakReader _peakReader = new();   // 自己开捕获流实测电平（虚拟声卡也能读到）
    private readonly System.Timers.Timer _timer;
    private readonly ConcurrentDictionary<string, Queue<float>> _windows = new();
    private readonly object _switchLock = new();

    // ---- 可调参数（运行时可改，见 Configure） ----
    private int _windowSamples;            // 滑动窗口样本数
    private float _silenceThreshold;       // 低于此峰值视为静音
    private int _debounceSamples;          // 新设备需连续达标样本数才切换（防抖）
    private HashSet<string> _excluded = new();   // 被排除的设备，不参与自动切换
    private List<string> _priority = new();      // 设备优先级顺序（默认设备失效时的兜底）
    private bool _preferPhysical = true;         // 都没声音时优先占住真实设备
    private int _sampleSeq;                      // 采样计数（用于周期性输出电平跟踪）

    private string? _currentWinnerId;
    private string? _pendingId;            // 防抖计数目标
    private int _pendingCount;
    private bool _enabled;
    private bool _disposed;

    /// <summary>成功自动切换默认麦克风时触发（UI 线程外，需自行调度）。</summary>
    public event EventHandler<AutoSwitchEventArgs>? Switched;

    /// <summary>采样发生异常时触发（如设备全部消失）。</summary>
    public event EventHandler<Exception>? Error;

    /// <summary>周期性电平跟踪输出（每约 6 秒一次），用于排查“为什么不切换”。</summary>
    public Action<string>? Trace { get; set; }

    public LevelMonitor(
        AudioDeviceService audio,
        int intervalMs = 300,
        int windowSamples = 6,        // 6 × 300ms ≈ 1.8s 窗口
        float silenceThreshold = 0.01f, // 峰值 < 1% 视为静音
        int debounceSamples = 3)      // 连续 3 次采样都达标才切
    {
        _audio = audio;
        _windowSamples = Math.Max(1, windowSamples);
        _silenceThreshold = silenceThreshold;
        _debounceSamples = Math.Max(1, debounceSamples);

        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => SafeSample();
    }

    /// <summary>是否正在运行自动模式。</summary>
    public bool IsRunning => _enabled;

    /// <summary>切默认设备时是否同步「通信设备」角色。</summary>
    public bool SyncCommunicationRole { get; set; } = true;

    /// <summary>运行时调整灵敏度参数与排除列表（立即生效，不需要重启）。</summary>
    public void Configure(int? intervalMs = null, float? silenceThreshold = null,
        int? debounceSamples = null, IEnumerable<string>? excludedDevices = null,
        IEnumerable<string>? priorityOrder = null, bool? preferPhysicalDevices = null)
    {
        if (priorityOrder is not null) _priority = priorityOrder.ToList();
        if (preferPhysicalDevices is { } pf) _preferPhysical = pf;
        if (silenceThreshold is { } t) _silenceThreshold = Math.Clamp(t, 0.0005f, 0.5f);
        if (debounceSamples is { } d)
        {
            _debounceSamples = Math.Max(1, d);
            _pendingId = null;
            _pendingCount = 0;
        }
        if (excludedDevices is not null) _excluded = excludedDevices.ToHashSet();
        if (intervalMs is { } iv && iv > 0) _timer.Interval = Math.Max(50, iv);
    }

    /// <summary>启动自动模式。</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LevelMonitor));
        _enabled = true;
        _windows.Clear();
        _currentWinnerId = _audio.GetDefaultDeviceId(AudioFlow.Capture);
        _pendingId = null;
        _pendingCount = 0;
        _timer.Start();
    }

    /// <summary>停止自动模式（保留当前默认设备不动）。</summary>
    public void Stop()
    {
        _enabled = false;
        _timer.Stop();
        _windows.Clear();
        _pendingId = null;
        _pendingCount = 0;
        _peakReader.Sync(Array.Empty<string>());   // 关掉所有实测通道
    }

    private void SafeSample()
    {
        if (!_enabled) return;
        try
        {
            Sample();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
    }

    private void Sample()
    {
        var devices = _audio.EnumerateDevices(AudioFlow.Capture)
            .Where(d => !_excluded.Contains(d.Id))
            .ToList();
        if (devices.Count == 0) return;

        // 用实测峰值替换系统峰值表（系统表对虚拟声卡恒为 0）
        _peakReader.Sync(devices.Select(d => d.Id));
        devices = devices.Select(d =>
        {
            float measured = _peakReader.GetPeak(d.Id);
            return measured < 0f ? d : d with { PeakValue = measured };
        }).ToList();

        // ---- 先处理「当前默认设备已经不存在」的情况 ----
        // 否则会死锁：默认设备被禁用后，剩下的麦克风不是默认设备，
        // 电平可能一直读 0，于是永远等不到“够响”而不会切换，结果整机没输入。
        var defaultId = _audio.GetDefaultDeviceId(AudioFlow.Capture);
        bool defaultGone = string.IsNullOrEmpty(defaultId) || devices.All(d => d.Id != defaultId);
        if (defaultGone)
        {
            RecoverFromMissingDefault(devices);
            return;
        }

        // 更新每个设备的滑动窗口，并求窗口平均电平
        var averages = new Dictionary<string, float>();
        foreach (var dev in devices)
        {
            var q = _windows.GetOrAdd(dev.Id, _ => new Queue<float>());
            lock (q)
            {
                q.Enqueue(dev.PeakValue);
                while (q.Count > _windowSamples) q.Dequeue();
                averages[dev.Id] = q.Average();
            }
        }

        // 找窗口平均电平最高的设备
        var best = averages.OrderByDescending(kv => kv.Value).First();
        float bestAvg = best.Value;
        float currentAvg = _currentWinnerId is not null && averages.TryGetValue(_currentWinnerId, out var ca) ? ca : 0f;

        bool bestAudible = bestAvg >= _silenceThreshold;
        // 相对判据：明显比当前设备响，也算“这边有人在说话”
        // 明显更响（虚拟声卡电平常偏小，所以偏移量取很小，同时用 1.5 倍相对差防抖）
        bool clearlyLouder = bestAvg >= _silenceThreshold * 0.5f && bestAvg >= currentAvg * 1.5f + 0.0005f;

        if (++_sampleSeq % 20 == 0)
        {
            Trace?.Invoke("电平跟踪: " + string.Join(", ",
                devices.Select(d => $"{d.FriendlyName}={averages.GetValueOrDefault(d.Id):0.0000}"))
                + $" | 阈值={_silenceThreshold:0.0000} 最响={best.Key[..Math.Min(24, best.Key.Length)]}"
                + $" 当前={(_currentWinnerId is null ? "null" : _currentWinnerId[..Math.Min(24, _currentWinnerId.Length)])}");
        }

        lock (_switchLock)
        {
            // 没人说话：重置防抖；如果现在占的是虚拟声卡，就换到真实设备上待命
            if (!bestAudible && !clearlyLouder)
            {
                _pendingId = null;
                _pendingCount = 0;
                TryPreferPhysical(devices);
                return;
            }

            // 最强的还是当前赢家：重置防抖
            if (best.Key == _currentWinnerId)
            {
                _pendingId = null;
                _pendingCount = 0;
                return;
            }

            // 出现了新的候选，做防抖计数
            if (best.Key != _pendingId)
            {
                _pendingId = best.Key;
                _pendingCount = 1;
                return;
            }

            _pendingCount++;
            if (_pendingCount >= _debounceSamples)
            {
                // 防抖通过，执行切换
                var to = devices.First(d => d.Id == best.Key);
                var from = devices.FirstOrDefault(d => d.Id == _currentWinnerId);
                try
                {
                    _audio.SetDefaultDevice(best.Key, SyncCommunicationRole);
                    _currentWinnerId = best.Key;
                    _pendingId = null;
                    _pendingCount = 0;

                    if (from is not null)
                    {
                        Switched?.Invoke(this, new AutoSwitchEventArgs
                        {
                            From = from,
                            To = to,
                            WinningLevel = best.Value,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, ex);
                }
            }
        }
    }

    /// <summary>
    /// 都没声音时，如果当前默认是「虚拟声卡」而又有真实设备可用，
    /// 就把默认切到真实设备上待命（虚拟声卡常静音，不该一直占着）。
    /// </summary>
    private void TryPreferPhysical(IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (!_preferPhysical) return;

        var current = devices.FirstOrDefault(d => d.Id == _currentWinnerId);
        if (current is null) return;
        if (!VirtualDeviceDetector.IsVirtual(current)) return;   // 已经是真实设备，不动

        var physical = BestPhysical(devices);
        if (physical is null || physical.Id == current.Id) return;

        try
        {
            _audio.SetDefaultDevice(physical.Id, SyncCommunicationRole);
            _currentWinnerId = physical.Id;

            Switched?.Invoke(this, new AutoSwitchEventArgs
            {
                From = current,
                To = physical,
                WinningLevel = 0f,
            });
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
    }

    /// <summary>按优先级挑一个真实（非虚拟）设备；没有就返回 null。</summary>
    private AudioDeviceInfo? BestPhysical(IReadOnlyList<AudioDeviceInfo> devices)
        => devices
            .Where(d => !VirtualDeviceDetector.IsVirtual(d))
            .OrderBy(d =>
            {
                int i = _priority.IndexOf(d.Id);
                return i < 0 ? int.MaxValue : i;
            })
            .ThenByDescending(d => d.PeakValue)
            .FirstOrDefault();

    /// <summary>默认设备消失时，立刻切到优先级最高的可用设备。</summary>
    private void RecoverFromMissingDefault(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var target = (_preferPhysical ? BestPhysical(devices) : null)
            ?? devices.OrderBy(d =>
            {
                int i = _priority.IndexOf(d.Id);
                return i < 0 ? int.MaxValue : i;
            }).First();

        lock (_switchLock)
        {
            try
            {
                // 注意：即使 _currentWinnerId 已经是它，也要真正去设一次默认设备，
                // 否则「默认设备被外部改成已失效的那台」这种情况永远修不回来。
                _audio.SetDefaultDevice(target.Id, SyncCommunicationRole);

                bool changed = target.Id != _currentWinnerId;
                _currentWinnerId = target.Id;
                _pendingId = null;
                _pendingCount = 0;

                if (changed)
                {
                    Switched?.Invoke(this, new AutoSwitchEventArgs
                    {
                        From = target,   // 原默认设备已不可用，用目标设备占位，UI 据此显示“已恢复”
                        To = target,
                        WinningLevel = 0f,
                    });
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
        }
    }

    public void Dispose()
    {
        _peakReader.Dispose();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _windows.Clear();
    }
}
