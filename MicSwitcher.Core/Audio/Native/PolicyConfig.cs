using System;
using System.Runtime.InteropServices;

namespace MicSwitcher.Core.Audio.Native;

/// <summary>音频端点角色，对应 Windows ERole。</summary>
public enum ERole : uint
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

/// <summary>数据流方向。</summary>
public enum EDataFlow : uint
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

/// <summary>设备状态掩码。</summary>
[Flags]
public enum DeviceState : uint
{
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
    All = 0xF,
}

/// <summary>
/// 非公开 COM 接口 IPolicyConfig，用于设置系统默认音频端点。
/// GUID: F8679F50-850A-41CF-9C72-430F290290C8（Win7+ 稳定）。
/// 这是 SoundSwitch 等工具采用的同一机制。
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
}

/// <summary>
/// PolicyConfigClient 的 COM 类 GUID（870af99c-171d-4f9e-af0d-e63df40c2bc9）。
/// </summary>
[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient { }

/// <summary>
/// 封装 IPolicyConfig，提供设置默认音频端点的静态方法。
/// </summary>
public static class PolicyConfig
{
    /// <summary>
    /// 将指定设备设为某角色的默认端点。
    /// </summary>
    /// <param name="deviceId">端点的 ID（来自 IMMDevice.GetId，形如 "{0.0.1.00000000}.{guid}"）。</param>
    /// <param name="role">目标角色。若为 null 则对全部三个角色都设置。</param>
    public static void SetDefaultEndpoint(string deviceId, ERole? role = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("设备 ID 不能为空", nameof(deviceId));

        var client = (IPolicyConfig)(object)new PolicyConfigClient();
        if (client is null)
            throw new InvalidOperationException("无法创建 PolicyConfigClient COM 对象");

        if (role.HasValue)
        {
            Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(deviceId, role.Value));
        }
        else
        {
            // 设全部角色，确保所有应用都跟随
            Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(deviceId, ERole.eConsole));
            Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
            Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(deviceId, ERole.eCommunications));
        }
    }
}
