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

    private sealed class Channel
    {
        public WasapiCapture? Capture;
        public float Peak;
        public bool Failed;

        /// <summary>失败是否值得重试。音频格式无法解码属于永久失败，重试没有意义。</summary>
        public bool Retryable = true;

        public DateTime LastAttemptUtc;
    }

    private readonly Dictionary<string, Channel> _channels = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>保证 ids 里每个设备都有一条捕获通道；不在列表里的会被关掉。</summary>
    public void Sync(IEnumerable<string> deviceIds)
    {
        if (_disposed) return;

        var wanted = deviceIds.ToHashSet();

        lock (_gate)
        {
            // 关掉不再需要的
            foreach (var id in _channels.Keys.ToList())
            {
                if (wanted.Contains(id)) continue;
                StopChannel(_channels[id]);
                _channels.Remove(id);
            }

            // 打开新出现的；之前打不开的隔 RetryInterval 重试一次
            foreach (var id in wanted)
            {
                if (_channels.TryGetValue(id, out var existing))
                {
                    if (existing.Failed && existing.Retryable
                        && DateTime.UtcNow - existing.LastAttemptUtc >= RetryInterval)
                    {
                        StopChannel(existing);
                        _channels[id] = StartChannel(id);
                    }
                    continue;
                }

                _channels[id] = StartChannel(id);
            }
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

    private Channel StartChannel(string deviceId)
    {
        var channel = new Channel { LastAttemptUtc = DateTime.UtcNow };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);

            var capture = new WasapiCapture(device);
            var format = capture.WaveFormat;

            if (!IsSupported(format))
            {
                // 解码不了时不能返回 0 —— 那会被上层当成「没人说话」。
                // 标记为永久失败，让上层回退到系统峰值表。
                capture.Dispose();
                channel.Failed = true;
                channel.Retryable = false;
                return channel;
            }

            capture.DataAvailable += (_, e) =>
            {
                float p = ComputePeak(e.Buffer, e.BytesRecorded, format);
                lock (_gate)
                {
                    if (p > channel.Peak) channel.Peak = p;
                }
            };
            capture.RecordingStopped += (_, _) => { };

            capture.StartRecording();
            channel.Capture = capture;
        }
        catch
        {
            channel.Failed = true;   // 打不开就回退到系统峰值表，稍后重试
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
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var ch in _channels.Values) StopChannel(ch);
            _channels.Clear();
        }
    }
}
