namespace MicSwitcher.Core.Audio;

/// <summary>音频端点方向。</summary>
public enum AudioFlow
{
    /// <summary>录音设备（麦克风）。</summary>
    Capture,
    /// <summary>播放设备（扬声器/耳机）。</summary>
    Render,
}

/// <summary>设备状态掩码（透传 Windows DEVICE_STATE）。</summary>
[Flags]
public enum DeviceState
{
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
    All = 0xF,
}

/// <summary>一个音频端点设备的快照信息。</summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    AudioFlow Flow,
    bool IsDefault,
    bool IsMuted,
    float PeakValue,
    DeviceState State);
