using MicSwitcher.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 峰值解码的纯函数测试：只喂字节缓冲，不依赖任何音频设备，CI 也能跑。
/// 覆盖自动切换最容易失效的一环——采样格式判错会把「有声」算成 0，从而永远不切换。
/// </summary>
public class DevicePeakReaderTests
{
    // ---------- float32 ----------

    [Fact]
    public void 浮点静音应算作零()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buf = FloatBuffer(0f, 0f, 0f, 0f);

        Assert.Equal(0f, DevicePeakReader.ComputePeak(buf, buf.Length, fmt), 4);
    }

    [Fact]
    public void 浮点半幅应算作一半()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buf = FloatBuffer(0.5f, -0.5f);

        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(buf, buf.Length, fmt), 3);
    }

    [Fact]
    public void 浮点超过满幅应截断到一()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buf = FloatBuffer(2.5f);

        Assert.Equal(1f, DevicePeakReader.ComputePeak(buf, buf.Length, fmt), 4);
    }

    // ---------- 16 bit ----------

    [Fact]
    public void 十六位满幅与半幅()
    {
        var fmt = new WaveFormat(48000, 16, 1);

        var full = Pcm16(short.MaxValue);
        Assert.Equal(1f, DevicePeakReader.ComputePeak(full, full.Length, fmt), 3);

        var half = Pcm16(16384);
        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(half, half.Length, fmt), 3);
    }

    [Fact]
    public void 十六位负半幅应取绝对值()
    {
        var fmt = new WaveFormat(48000, 16, 1);
        var buf = Pcm16(-16384);

        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(buf, buf.Length, fmt), 3);
    }

    // ---------- 24 bit ----------

    [Fact]
    public void 二十四位半幅应算作一半()
    {
        var fmt = new WaveFormat(48000, 24, 1);

        var positive = Pcm24(0x400000);
        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(positive, positive.Length, fmt), 3);

        var negative = Pcm24(-0x400000);
        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(negative, negative.Length, fmt), 3);
    }

    [Fact]
    public void 二十四位满幅不越界()
    {
        var fmt = new WaveFormat(48000, 24, 1);
        var buf = Pcm24(-0x800000);

        float peak = DevicePeakReader.ComputePeak(buf, buf.Length, fmt);

        Assert.InRange(peak, 0.99f, 1f);
    }

    // ---------- 32 bit 整数 ----------

    [Fact]
    public void 三十二位整数半幅应算作一半()
    {
        var fmt = new WaveFormat(48000, 32, 1);
        var buf = Pcm32(0x40000000);

        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(buf, buf.Length, fmt), 3);
    }

    [Fact]
    public void 三十二位整数最小值不应溢出()
    {
        var fmt = new WaveFormat(48000, 32, 1);
        var buf = Pcm32(int.MinValue);

        float peak = DevicePeakReader.ComputePeak(buf, buf.Length, fmt);

        Assert.InRange(peak, 0.99f, 1f);
    }

    // ---------- 8 bit ----------

    [Fact]
    public void 八位以一百二十八为静音中点()
    {
        var fmt = new WaveFormat(8000, 8, 1);

        var silent = new byte[] { 128, 128 };
        Assert.Equal(0f, DevicePeakReader.ComputePeak(silent, silent.Length, fmt), 4);

        var loud = new byte[] { 255 };
        Assert.InRange(DevicePeakReader.ComputePeak(loud, loud.Length, fmt), 0.98f, 1f);
    }

    // ---------- 格式识别 ----------

    [Fact]
    public void 扩展格式应归一化成标准格式()
    {
        // 共享模式下 WASAPI 常返回 WAVE_FORMAT_EXTENSIBLE，Encoding 不是 Pcm/IeeeFloat，
        // 不归一化会导致所有分支都命中不了、峰值恒为 0。
        var ext = new WaveFormatExtensible(48000, 16, 1);

        var normalized = DevicePeakReader.Normalize(ext);

        Assert.NotNull(normalized);
        Assert.Equal(WaveFormatEncoding.Pcm, normalized!.Encoding);
        Assert.Equal(16, normalized.BitsPerSample);
        Assert.True(DevicePeakReader.IsSupported(ext));
    }

    [Fact]
    public void 扩展格式的缓冲也能算峰值()
    {
        var ext = new WaveFormatExtensible(48000, 16, 1);
        var buf = Pcm16(16384);

        Assert.Equal(0.5f, DevicePeakReader.ComputePeak(buf, buf.Length, ext), 3);
    }

    [Theory]
    [InlineData(12)]   // 不常见的位深，不支持
    [InlineData(20)]
    public void 不支持的位深应被判定为不支持(int bits)
    {
        var fmt = new WaveFormat(48000, bits, 1);

        Assert.False(DevicePeakReader.IsSupported(fmt));
    }

    [Fact]
    public void 空缓冲应返回零不抛异常()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var buf = Array.Empty<byte>();

        Assert.Equal(0f, DevicePeakReader.ComputePeak(buf, 0, fmt));
    }

    // ---------- 构造工具 ----------

    private static byte[] FloatBuffer(params float[] samples)
    {
        var buf = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(buf, i * 4);
        return buf;
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var buf = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(buf, i * 2);
        return buf;
    }

    private static byte[] Pcm24(params int[] samples)
    {
        var buf = new byte[samples.Length * 3];
        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i];
            buf[i * 3] = (byte)(v & 0xFF);
            buf[i * 3 + 1] = (byte)((v >> 8) & 0xFF);
            buf[i * 3 + 2] = (byte)((v >> 16) & 0xFF);
        }
        return buf;
    }

    private static byte[] Pcm32(params int[] samples)
    {
        var buf = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(buf, i * 4);
        return buf;
    }
}
