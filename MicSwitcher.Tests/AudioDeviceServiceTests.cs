using System.Threading;
using MicSwitcher.Core.Audio;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 音频服务的集成测试（需在真实 Windows 会话里跑，CI 无音频设备时部分断言会跳过）。
/// COM/MMDevice API 要求 STA 线程，故用 STA helper 包裹。
/// </summary>
public class AudioDeviceServiceTests
{
    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (error is not null) throw error;
        return result!;
    }

    [Fact]
    public void EnumerateCaptureDevices_DoesNotThrow()
    {
        var devices = RunSta(() =>
        {
            var svc = new AudioDeviceService();
            return svc.EnumerateDevices(AudioFlow.Capture);
        });
        Assert.NotNull(devices);
        // 有音频设备时至少一个麦克风；CI 无设备则为空，不强制
    }

    [Fact]
    public void GetDefaultDeviceId_ReturnsOrEmpty()
    {
        var id = RunSta(() =>
        {
            var svc = new AudioDeviceService();
            return svc.GetDefaultDeviceId(AudioFlow.Capture);
        });
        // 无设备时可能为 null；有设备时应非空
        Assert.True(id is null || !string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public void EnumerateRenderDevices_DoesNotThrow()
    {
        var devices = RunSta(() =>
        {
            var svc = new AudioDeviceService();
            return svc.EnumerateDevices(AudioFlow.Render);
        });
        Assert.NotNull(devices);
    }
}
