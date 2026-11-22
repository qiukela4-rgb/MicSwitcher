using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace MicSwitcher.App;

internal static class DiagnosticsService
{
    private static bool _diagInitialized;

    /// <summary>启动时检测进程完整性级别；Low/Untrusted 时提示托盘可能不显示。</summary>
    internal static void CheckIntegrityLevel()
    {
        // ---- 完整性级别检测：进程若是 Low 完整性，托盘图标会被 Windows UIPI 拦掉 ----
        // 典型场景：程序放在被强制打了 Low 标签的目录（如某些沙箱工作区）里运行。
        try
        {
            bool skip = Environment.GetEnvironmentVariable("MICSWITCHER_NO_INTEGRITY_WARN") == "1";
            string level = GetIntegrityLevel(Process.GetCurrentProcess().Handle);
            if (!skip && (level.StartsWith("Low") || level.StartsWith("Untrusted")))
            {
                MessageBox.Show(
                    $"检测到 MicSwitcher 运行在「低完整性级别」（{level}）。\n\n" +
                    "此时 Windows 会阻止本程序向任务栏注册图标，托盘图标不会显示，\n" +
                    "开机自启写注册表也会失败。\n\n" +
                    "解决办法：把程序复制到一个普通目录（如桌面）后再运行。",
                    "MicSwitcher - 完整性级别提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch { /* 检测失败不影响运行 */ }
    }

    /// <summary>记录会话/窗口站/桌面/任务栏窗口等关键信息，用于定位 TryCreate 失败。</summary>
    internal static void LogEnvironment()
    {
        try
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            Log($"SessionId={sessionId}");
            Log("本进程完整性级别=" + GetIntegrityLevel(Process.GetCurrentProcess().Handle));

            IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            Log("Shell_TrayWnd=" + (trayWnd != IntPtr.Zero ? "找到" : "未找到"));
            if (trayWnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(trayWnd, out uint trayPid);
                try
                {
                    using var tp = Process.GetProcessById((int)trayPid);
                    Log($"任务栏进程={tp.ProcessName}({trayPid}) 完整性=" + GetIntegrityLevel(tp.Handle));
                }
                catch { }
            }
        }
        catch (Exception ex) { Log("环境探测异常: " + ex.Message); }
    }

    /// <summary>读取进程令牌的完整性级别（Low/Medium/High）。</summary>
    internal static string GetIntegrityLevel(IntPtr processHandle)
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, 0x0008, out IntPtr tok)) return "未知";
            try
            {
                NativeMethods.GetTokenInformation(tok, 25, IntPtr.Zero, 0, out uint len);
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)len);
                try
                {
                    if (NativeMethods.GetTokenInformation(tok, 25, buf, len, out _))
                    {
                        var label = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.TokenMandatoryLabel>(buf);
                        IntPtr sub = NativeMethods.GetSidSubAuthority(label.Label.Sid, 0);
                        uint rid = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(sub);
                        return rid switch
                        {
                            0x0000 => "Untrusted",
                            0x1000 => "Low（低，无法向任务栏注册图标）",
                            0x2000 => "Medium（中，正常）",
                            0x2100 => "MediumPlus",
                            0x3000 => "High（高/管理员）",
                            0x4000 => "System",
                            _ => "0x" + rid.ToString("X")
                        };
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            finally { NativeMethods.CloseHandle(tok); }
        }
        catch { }
        return "未知";
    }

    /// <summary>托盘诊断日志：写入 exe 同目录 tray-diag.log（每次启动覆盖），用于排查图标不显示。</summary>
    internal static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-diag.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (!_diagInitialized)
            {
                _diagInitialized = true;
                File.WriteAllText(path, line);
            }
            else
            {
                File.AppendAllText(path, line);
            }
        }
        catch { /* 日志失败不影响运行 */ }
    }
}