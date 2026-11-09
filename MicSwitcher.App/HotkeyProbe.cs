using System.Runtime.InteropServices;
using System.Windows.Interop;
using MicSwitcher.Core.Hotkeys;

namespace MicSwitcher.App;

/// <summary>
/// 热键占用检测：用一个独立的隐藏窗口去「试注册」再立刻注销，
/// 从而在不影响现有热键的前提下判断某个组合键是否能被注册。
/// </summary>
internal static class HotkeyProbe
{
    private const int ProbeId = 0x7FF0;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private static readonly object Gate = new();
    private static HwndSource? _source;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static IntPtr EnsureHandle()
    {
        lock (Gate)
        {
            _source ??= new HwndSource(new HwndSourceParameters("MicSwitcherHotkeyProbe")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
            });
            return _source.Handle;
        }
    }

    /// <summary>
    /// 检查组合键是否可用。返回 null 表示可用，否则返回给用户看的原因。
    /// </summary>
    public static string? Check(string? gestureText)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture, out string? parseError))
            return parseError ?? "热键格式不正确";

        IntPtr h = EnsureHandle();

        if (RegisterHotKey(h, ProbeId, gesture.RegisterModifiers, gesture.VirtualKey))
        {
            UnregisterHotKey(h, ProbeId);
            return null;
        }

        int err = Marshal.GetLastWin32Error();
        return err == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "这个组合键已经被其他程序（或本程序的另一个动作）占用了"
            : $"系统拒绝了该组合键（错误码 {err}）";
    }
}
