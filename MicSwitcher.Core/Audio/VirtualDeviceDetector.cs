namespace MicSwitcher.Core.Audio;

/// <summary>
/// 判断一个录音设备是不是「虚拟声卡」（Mic You / VB-Cable / Voicemeeter 之类）。
/// 用设备名和 Id 里的关键字做启发式识别，识别不出来就当作真实设备。
/// </summary>
public static class VirtualDeviceDetector
{
    private static readonly string[] Keywords =
    {
        "virtual", "虚拟",
        "vb-audio", "vbaudio", "vb cable", "cable output", "cable input", "cable_out", "cable_in",
        "voicemeeter", "voicemeter",
        "mic you", "micyou", "mic-you",
        "nvidia broadcast", "rtx voice",
        "steam streaming",
        "obs virtual", "obs-camera",
        "soundwire", "loopback", "blackhole",
        "stereo mix", "立体声混音",
    };

    /// <summary>设备名或 Id 命中虚拟声卡关键字 → true。</summary>
    public static bool IsVirtual(string? friendlyName, string? deviceId)
    {
        string text = $"{friendlyName} {deviceId}".ToLowerInvariant();
        foreach (var kw in Keywords)
            if (text.Contains(kw, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsVirtual(AudioDeviceInfo device)
        => IsVirtual(device.FriendlyName, device.Id);
}
