using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MicSwitcher.App;

/// <summary>
/// 监听 explorer（任务栏进程）重启时广播的 TaskbarCreated 消息。
///
/// explorer 重启会把所有程序的托盘图标清掉，而 H.NotifyIcon 未必能自己恢复。
/// 结果是「进程还活着、托盘图标却消失」——用户会以为程序退出后台了。
/// 收到广播后调用回调重建图标。
/// </summary>
internal sealed class TaskbarRestartWatcher : IDisposable
{
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private readonly Action _onRestart;
    private bool _disposed;

    internal TaskbarRestartWatcher(Action onRestart)
    {
        _onRestart = onRestart;

        // 建一个零尺寸的隐藏窗口接收广播消息。必须在 UI 线程创建（有 Dispatcher）。
        _source = new HwndSource(new HwndSourceParameters("MicSwitcherTaskbarWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTaskbarCreated)
        {
            DiagnosticsService.Log("任务栏重建：收到 TaskbarCreated 广播，准备恢复托盘图标");
            try { _onRestart(); }
            catch (Exception ex) { DiagnosticsService.Log("任务栏重建：恢复回调失败 " + ex.Message); }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
