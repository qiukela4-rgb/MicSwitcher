using MicSwitcher.Core.Audio;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 虚拟声卡识别。它决定了「都没声音时该不该让位给真实设备」，
/// 识别错会把真麦当成虚拟设备而反复让位，或把虚拟声卡当真麦一直占着。
/// </summary>
public class VirtualDeviceDetectorTests
{
    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", null)]
    [InlineData("麦克风 (VB-Audio Virtual Cable)", null)]
    [InlineData("Voicemeeter Out B1", null)]
    [InlineData("VoiceMeeter Input", null)]
    [InlineData("Mic You", null)]
    [InlineData("NVIDIA Broadcast", null)]
    [InlineData("Stereo Mix (Realtek)", null)]
    [InlineData("立体声混音 (Realtek Audio)", null)]
    [InlineData("虚拟麦克风", null)]
    public void 常见虚拟声卡应被识别(string name, string? id)
    {
        Assert.True(VirtualDeviceDetector.IsVirtual(name, id), $"{name} 应判定为虚拟设备");
    }

    [Theory]
    [InlineData("麦克风 (USB2.0 Device)", null)]
    [InlineData("Microphone Array (Intel Smart Sound)", null)]
    [InlineData("耳机麦克风 (Realtek High Definition Audio)", null)]
    [InlineData("DJI Mic 2", null)]
    public void 真实麦克风不应被误判(string name, string? id)
    {
        Assert.False(VirtualDeviceDetector.IsVirtual(name, id), $"{name} 不应判定为虚拟设备");
    }

    [Fact]
    public void 名称干净但设备id含关键字也能识别()
    {
        // 有些驱动把产品名写在 id 里、友好名却是通用的
        Assert.True(VirtualDeviceDetector.IsVirtual("麦克风", "SWD\\MMDEVAPI\\{vb-audio-cable}"));
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.True(VirtualDeviceDetector.IsVirtual("CABLE OUTPUT", null));
        Assert.True(VirtualDeviceDetector.IsVirtual("cable output", null));
    }

    [Fact]
    public void 空名称空id不抛异常()
    {
        Assert.False(VirtualDeviceDetector.IsVirtual(null, null));
        Assert.False(VirtualDeviceDetector.IsVirtual("", ""));
    }
}
