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
    private sealed class Channel
    {
        public WasapiCapture? Capture;
        public float Peak;
        public bool Failed;
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

            // 打开新出现的
            foreach (var id in wanted)
            {
                if (_channels.ContainsKey(id)) continue;
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
        var channel = new Channel();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);

            var capture = new WasapiCapture(device);
            var format = capture.WaveFormat;

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
            channel.Failed = true;   // 打不开就回退到系统峰值表
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

    /// <summary>从 PCM 缓冲算峰值（0~1）。</summary>
    private static float ComputePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0) return 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            float max = 0f;
            for (int i = 0; i + 3 < bytesRecorded; i += 4)
            {
                float v = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (v > max) max = v;
            }
            return Math.Min(1f, max);
        }

        if (format.BitsPerSample == 16)
        {
            int max = 0;
            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                int v = Math.Abs((int)BitConverter.ToInt16(buffer, i));
                if (v > max) max = v;
            }
            return Math.Min(1f, max / 32768f);
        }

        return 0f;
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
