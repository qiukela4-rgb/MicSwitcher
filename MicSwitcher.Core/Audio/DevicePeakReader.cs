using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicSwitcher.Core.Audio;

/// <summary>
/// 每设备的实时电平读取器。
///
/// 为什么不用 <c>IAudioMeterInformation.MasterPeakValue</c>：
/// 它只反映「当前有没有客户端在使用这个端点」，虚拟声卡（CABLE / Voicemeeter）
/// 在没有程序采集时会一直报 0，导致自动切换永远看不到它的声音。
/// 这里改为对每个设备开一条共享模式的 <see cref="WasapiCapture"/>，直接从音频缓冲算峰值。
/// </summary>
public sealed class DevicePeakReader : IDisposable
{
    /// <summary>
    /// 通道启动失败后的重试间隔。
    /// 设备在程序启动瞬间可能正被别的程序独占、或音频服务尚未就绪，
    /// 这类失败是可恢复的；不重试会让该设备在本次运行期间永久读不到电平。
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 同一设备最多尝试启动多少次。
    /// 设备被永久占用时无限重试只会反复创建/销毁采集流，白白增加线程与退出负担。
    /// 10 次 × 5 秒 ≈ 50 秒，足够覆盖开机时音频服务尚未就绪的情况。
    /// </summary>
    private const int MaxAttempts = 10;

    private sealed class Channel
    {
        public WasapiCapture? Capture;
        public float Peak;
        public bool Failed;

        /// <summary>失败是否值得重试。音频格式无法解码属于永久失败，重试没有意义。</summary>
        public bool Retryable = true;

        /// <summary>已尝试启动的次数。</summary>
        public int Attempts;

        public DateTime LastAttemptUtc;
    }

    private readonly Dictionary<string, Channel> _channels = new();
    private readonly object _gate = new();
    private volatile bool _disposed;

    /// <summary>保证 ids 里每个设备都有一条捕获通道；不在列表里的会被关掉。</summary>
    public void Sync(IEnumerable<string> deviceIds)
    {
        if (_disposed) return;

        var wanted = deviceIds.ToHashSet();
        var toStop = new List<Channel>();
        var toStart = new Dictionary<string, int>();   // 设备 id → 已尝试次数

        lock (_gate)
        {
            // 关掉不再需要的（只改字典，真正停止放到锁外）
            foreach (var id in _channels.Keys.ToList())
            {
                if (wanted.Contains(id)) continue;
                toStop.Add(_channels[id]);
                _channels.Remove(id);
            }

            // 打开新出现的；之前打不开的隔 RetryInterval 重试一次
            foreach (var id in wanted)
            {
                if (_channels.TryGetValue(id, out var existing))
                {
                    if (existing.Failed && existing.Retryable && existing.Attempts < MaxAttempts
                        && DateTime.UtcNow - existing.LastAttemptUtc >= RetryInterval)
                    {
                        toStop.Add(existing);
                        _channels.Remove(id);
                        toStart[id] = existing.Attempts;   // 沿用次数，避免无限重试
                    }
                    continue;
                }

                toStart[id] = 0;
            }
        }

        // 停止/启动都可能阻塞：StopRecording 要等采集线程收尾，而采集回调在
        // DataAvailable 里要拿 _gate。持锁做这些操作就会两边互等 —— 退出时表现为
        // 界面卡死、进程退不掉。必须在锁外执行。
        foreach (var ch in toStop) StopChannel(ch);

        foreach (var (id, attempts) in toStart)
        {
            if (_disposed) return;

            var ch = StartChannel(id, attempts);

            bool keep;
            lock (_gate)
            {
                keep = !_disposed;
                if (keep) _channels[id] = ch;
            }

            if (!keep) StopChannel(ch);
        }
    }

    /// <summary>取某设备最近一次实测峰值；没有通道或打不开则返回 -1（调用方自行回退）。</summary>
    public float GetPeak(string deviceId)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(deviceId, out var ch)) return -1f;
            if (ch.Failed) return -1f;

            // 上一次的峰值做一点衰减，避免一帧就掉到 0
            ch.Peak *= 0.75f;
            float peak = ch.Peak;
            ch.Peak = peak;
            return peak;
        }
    }

    private Channel StartChannel(string deviceId, int previousAttempts = 0)
    {
        var channel = new Channel
        {
            LastAttemptUtc = DateTime.UtcNow,
            Attempts = previousAttempts + 1,
        };
        WasapiCapture? capture = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);

            capture = new WasapiCapture(device);
            var format = capture.WaveFormat;

            if (!IsSupported(format))
            {
                // 解码不了时不能返回 0 —— 那会被上层当成「没人说话」。
                // 标记为永久失败，让上层回退到系统峰值表。
                channel.Failed = true;
                channel.Retryable = false;
                return channel;
            }

            capture.DataAvailable += (_, e) =>
            {
                if (_disposed) return;
                float p = ComputePeak(e.Buffer, e.BytesRecorded, format);
                lock (_gate)
                {
                    if (p > channel.Peak) channel.Peak = p;
                }
            };
            capture.RecordingStopped += (_, _) => { };

            capture.StartRecording();
            channel.Capture = capture;
            capture = null;   // 所有权交给 channel，finally 不再释放
        }
        catch
        {
            channel.Failed = true;   // 打不开就回退到系统峰值表，稍后重试
        }
        finally
        {
            // 启动失败时必须释放：否则每 5 秒重试一次就泄漏一条采集流，
            // 线程数会随之上涨，退出时也更难停下来。
            capture?.Dispose();
        }

        return channel;
    }

    private void StopChannel(Channel channel)
    {
        try
        {
            channel.Capture?.StopRecording();
            channel.Capture?.Dispose();
        }
        catch { /* 忽略 */ }
        channel.Capture = null;
    }

    /// <summary>
    /// 共享模式下 <see cref="WasapiCapture.WaveFormat"/> 常常是 WAVE_FORMAT_EXTENSIBLE，
    /// 此时 <c>Encoding</c> 是 Extensible 而不是 IeeeFloat/Pcm，直接拿 Encoding 比较会漏判。
    /// 这里先归一化成标准格式。
    /// </summary>
    internal static WaveFormat? Normalize(WaveFormat format)
    {
        if (format is not WaveFormatExtensible ext) return format;
        try { return ext.ToStandardWaveFormat(); }
        catch { return null; }
    }

    /// <summary>该格式能否算峰值。不能算时应回退系统峰值表，而不是当作静音。</summary>
    internal static bool IsSupported(WaveFormat format)
    {
        var f = Normalize(format);
        if (f is null) return false;

        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return f.BitsPerSample == 32;
        if (f.Encoding == WaveFormatEncoding.Pcm) return f.BitsPerSample is 8 or 16 or 24 or 32;
        return false;
    }

    /// <summary>从 PCM 缓冲算峰值（0~1）。</summary>
    internal static float ComputePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0) return 0f;

        var f = Normalize(format);
        if (f is null) return 0f;

        if (f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32)
        {
            float max = 0f;
            for (int i = 0; i + 3 < bytesRecorded; i += 4)
            {
                float v = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (v > max) max = v;
            }
            return Math.Min(1f, max);
        }

        if (f.Encoding != WaveFormatEncoding.Pcm) return 0f;

        switch (f.BitsPerSample)
        {
            case 8:
            {
                // 8-bit PCM 是无符号的，静音中点是 128
                int max = 0;
                for (int i = 0; i < bytesRecorded; i++)
                {
                    int v = Math.Abs(buffer[i] - 128);
                    if (v > max) max = v;
                }
                return Math.Min(1f, max / 128f);
            }

            case 16:
            {
                int max = 0;
                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    int v = Math.Abs((int)BitConverter.ToInt16(buffer, i));
                    if (v > max) max = v;
                }
                return Math.Min(1f, max / 32768f);
            }

            case 24:
            {
                // 小端 3 字节有符号，最高字节做符号扩展
                int max = 0;
                for (int i = 0; i + 2 < bytesRecorded; i += 3)
                {
                    int v = buffer[i] | (buffer[i + 1] << 8) | ((sbyte)buffer[i + 2] << 16);
                    int abs = Math.Abs(v);
                    if (abs > max) max = abs;
                }
                return Math.Min(1f, max / 8388608f);
            }

            case 32:
            {
                // 用 long 承接，避免 Math.Abs(int.MinValue) 溢出
                long max = 0;
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    long v = Math.Abs((long)BitConverter.ToInt32(buffer, i));
                    if (v > max) max = v;
                }
                return Math.Min(1f, max / 2147483648f);
            }

            default:
                return 0f;
        }
    }

    public void Dispose()
    {
        List<Channel> toStop;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            toStop = _channels.Values.ToList();
            _channels.Clear();
        }

        // 同 Sync：停止采集必须在锁外，否则会和 DataAvailable 回调互相等待。
        foreach (var ch in toStop) StopChannel(ch);
    }
}
