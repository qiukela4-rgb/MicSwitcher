using System;
using System.Threading;
using Application = System.Windows.Application;

namespace MicSwitcher.App;

internal sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\MicSwitcher_SingleInstance";
    private const string ShowSettingsEventName = @"Local\MicSwitcher_ShowSettings";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsEvent;

    public bool TryAcquire()
    {
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("单实例：创建互斥量失败，按独立实例继续运行 " + ex.Message);
            createdNew = true;
        }
        return createdNew;
    }

    public bool TryWakeExisting()
        => SignalExistingInstance() || ActivateExistingWindow();

    /// <summary>副实例请求已运行的实例显示设置窗口。</summary>
    public bool SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var ev))
            {
                using (ev)
                {
                    ev.Set();
                    DiagnosticsService.Log("单实例：已通知已有实例显示窗口");
                    return true;
                }
            }
            DiagnosticsService.Log("单实例：未找到唤醒事件（对端可能是旧版本）");
        }
        catch (Exception ex) { DiagnosticsService.Log("单实例：发事件失败 " + ex.Message); }
        return false;
    }

    /// <summary>
    /// 兜底：直接找到已有实例的主窗口并置前（窗口可能是隐藏到托盘的，也能唤回来）。
    /// 这样即使对方是旧版本、没有唤醒事件，双击也能打开程序。
    /// </summary>
    public bool ActivateExistingWindow()
    {
        // 新旧版本的窗口标题都试一遍
        foreach (var title in new[] { "MicSwitcher", "MicSwitcher 设置" })
        {
            IntPtr h = NativeMethods.FindWindow(null, title);
            if (h == IntPtr.Zero) continue;

            try
            {
                if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                else NativeMethods.ShowWindow(h, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(h);
                DiagnosticsService.Log($"单实例：已直接唤起窗口「{title}」");
                return true;
            }
            catch (Exception ex) { DiagnosticsService.Log("单实例：唤起窗口失败 " + ex.Message); }
        }

        DiagnosticsService.Log("单实例：没找到已有窗口");
        return false;
    }

    public void StartListening(Action showSettings)
    {
        try
        {
            _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
            StartShowSettingsListener(showSettings);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("单实例：创建唤醒事件失败，双击将依赖窗口激活兜底 " + ex.Message);
            _showSettingsEvent = null;
        }
    }

    private void StartShowSettingsListener(Action showSettings)
    {
        var ev = _showSettingsEvent;
        if (ev is null) return;

        var th = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    ev.WaitOne();
                    DiagnosticsService.Log("单实例：收到唤醒信号，弹出主窗口");
                    Application.Current?.Dispatcher.BeginInvoke(new Action(showSettings));
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { return; }
            }
        })
        { IsBackground = true, Name = "MicSwitcher-ShowSettings" };
        th.Start();
    }

    public void Dispose()
    {
        // 不主动 ReleaseMutex：进程退出时内核会自动释放命名互斥量，
        // 且 ReleaseMutex 只能在持有它的线程调用（OnExit 可能在其他线程，会抛异常）。
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }
}