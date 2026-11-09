using Microsoft.Win32;

namespace MicSwitcher.Core.Startup;

/// <summary>
/// 开机自启辅助：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册。
/// 只影响当前用户，无需管理员权限。
/// Enable/Disable 返回 null 表示成功，否则返回具体失败原因（含路径/异常详情，便于诊断）。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MicSwitcher";

    /// <summary>是否已注册开机自启（读取失败按 false 处理）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用开机自启。返回 null=成功，否则=失败原因。</summary>
    public static string? Enable()
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "MicSwitcher.App.exe");

            if (!File.Exists(exePath))
                return $"找不到程序文件：{exePath}";

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>禁用开机自启。返回 null=成功，否则=失败原因。</summary>
    public static string? Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>切换状态，返回切换后是否启用。</summary>
    public static bool Toggle()
    {
        if (IsEnabled()) { Disable(); return false; }
        return Enable() is null;
    }
}