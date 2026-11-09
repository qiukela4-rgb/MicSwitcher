using System.Runtime.InteropServices;
using MicSwitcher.Core.Audio.Native;
using NAudio.CoreAudioApi;

namespace MicSwitcher.Core.Audio;

/// <summary>
/// 音频设备服务：枚举端点、读取状态、设置静音、切换默认设备、读取电平。
/// 线程安全：每个方法内部自建 MMDeviceEnumerator（COM 对象不宜跨线程缓存共享）。
/// </summary>
public sealed class AudioDeviceService
{
    private static DataFlow ToDataFlow(AudioFlow flow) =>
        flow == AudioFlow.Capture ? DataFlow.Capture : DataFlow.Render;

    private static AudioFlow ToAudioFlow(DataFlow flow) =>
        flow == DataFlow.Capture ? AudioFlow.Capture : AudioFlow.Render;

    /// <summary>枚举指定方向的所有活动端点。</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dataFlow = ToDataFlow(flow);
        var defaultId = GetDefaultId(enumerator, dataFlow);

        var result = new List<AudioDeviceInfo>();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(dataFlow, NAudio.CoreAudioApi.DeviceState.Active))
        {
            result.Add(Snapshot(dev, flow, defaultId));
        }
        return result;
    }

    /// <summary>取当前默认设备 ID，没有则返回 null。</summary>
    public string? GetDefaultDeviceId(AudioFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return GetDefaultId(enumerator, ToDataFlow(flow));
    }

    /// <summary>取当前默认设备的快照。</summary>
    public AudioDeviceInfo? GetDefaultDevice(AudioFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dataFlow = ToDataFlow(flow);
        foreach (var dev in enumerator.EnumerateAudioEndPoints(dataFlow, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var id = dev.ID;
            if (id == GetDefaultId(enumerator, dataFlow))
                return Snapshot(dev, flow, id);
        }
        return null;
    }

    /// <summary>
    /// 设置默认设备。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="syncCommunicationRole">
    /// true（默认）：同时设置 控制台/多媒体/通信 三个角色，所有应用（含通话软件）跟随。
    /// false：只设 控制台 + 多媒体，不动「通信设备」角色。
    /// </param>
    public void SetDefaultDevice(string deviceId, bool syncCommunicationRole = true)
    {
        if (syncCommunicationRole)
        {
            PolicyConfig.SetDefaultEndpoint(deviceId); // 三个角色全设
        }
        else
        {
            PolicyConfig.SetDefaultEndpoint(deviceId, Native.ERole.eConsole);
            PolicyConfig.SetDefaultEndpoint(deviceId, Native.ERole.eMultimedia);
        }
    }

    /// <summary>设置/取消某设备静音。</summary>
    public void SetMute(string deviceId, bool muted)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDevice(deviceId);
        using var session = dev.AudioEndpointVolume;
        session.Mute = muted;
    }

    /// <summary>切换某设备静音状态，返回切换后的状态。</summary>
    public bool ToggleMute(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDevice(deviceId);
        using var volume = dev.AudioEndpointVolume;
        volume.Mute = !volume.Mute;
        return volume.Mute;
    }

    /// <summary>读取单个设备当前峰值电平（0.0~1.0，对麦克风即输入音量）。</summary>
    public float ReadPeak(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dev = enumerator.GetDevice(deviceId);
        return dev.AudioMeterInformation.MasterPeakValue;
    }

    /// <summary>批量读取多个设备的峰值电平（用于电平探测）。</summary>
    public Dictionary<string, float> ReadPeaks(IEnumerable<string> deviceIds)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new Dictionary<string, float>();
        foreach (var id in deviceIds)
        {
            try
            {
                var dev = enumerator.GetDevice(id);
                result[id] = dev.AudioMeterInformation.MasterPeakValue;
            }
            catch (COMException)
            {
                result[id] = 0f; // 设备刚被拔掉等瞬时错误，按静音处理
            }
        }
        return result;
    }

    private static string? GetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)?.ID;
        }
        catch (COMException)
        {
            return null; // 没有任何活动设备时抛异常，这里当作"无默认"
        }
    }

    private static AudioDeviceInfo Snapshot(MMDevice dev, AudioFlow flow, string? defaultId)
    {
        float peak;
        bool muted;
        try { peak = dev.AudioMeterInformation.MasterPeakValue; }
        catch (COMException) { peak = 0f; }
        try { muted = dev.AudioEndpointVolume.Mute; }
        catch (COMException) { muted = false; }

        return new AudioDeviceInfo(
            Id: dev.ID,
            FriendlyName: dev.FriendlyName,
            Flow: flow,
            IsDefault: dev.ID == defaultId,
            IsMuted: muted,
            PeakValue: peak,
            State: DeviceState.Active);
    }
}
