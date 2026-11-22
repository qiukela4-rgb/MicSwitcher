using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MicSwitcher.Core.Config;
using MicSwitcher.Core.Hotkeys;
using Application = System.Windows.Application;

namespace MicSwitcher.App;

internal sealed class HotkeyService : IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly Action _onMute;
    private readonly Action<bool> _onCycle;
    private readonly Action<string, string> _showBalloon;

    private readonly Dictionary<string, bool> _status = new();
    private HwndSource? _hotkeySource;
    private HwndSourceHook? _wndProcHook;
    private int _createThreadId;
    private string _hotkeySignature = "";

    internal HotkeyService(
        Func<AppConfig> config,
        Action onMute,
        Action<bool> onCycle,
        Action<string, string> showBalloon)
    {
        _config = config;
        _onMute = onMute;
        _onCycle = onCycle;
        _showBalloon = showBalloon;
    }

    public IReadOnlyDictionary<string, bool> Status => _status;

    /// <summary>按配置注册全局热键（静音 / 下一个 / 上一个）。</summary>
    public void Register()
    {
        if (Application.Current?.Dispatcher is null || !Application.Current.Dispatcher.CheckAccess())
        {
            DiagnosticsService.Log("热键：Register 在非 UI 线程被调用，已忽略");
            return;
        }

        var config = _config();
        _hotkeySignature = BuildSignature(config);
        _status.Clear();

        if (!config.EnableHotkeys) { DiagnosticsService.Log("热键：配置里已关闭，跳过注册"); return; }

        try
        {
            _createThreadId = Environment.CurrentManagedThreadId;
            _hotkeySource = new HwndSource(new HwndSourceParameters("MicSwitcherHotkeyWnd")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
            });
            _wndProcHook = HotkeyWndProc;
            _hotkeySource.AddHook(_wndProcHook);

            bool m = TryRegister(NativeMethods.HotkeyIdMute, "Mute", config.HotkeyMute, "静音", config.HotkeyMuteEnabled);
            bool n = TryRegister(NativeMethods.HotkeyIdNext, "Next", config.HotkeyNext, "下一个", config.HotkeyNextEnabled);
            bool p = TryRegister(NativeMethods.HotkeyIdPrev, "Prev", config.HotkeyPrev, "上一个", config.HotkeyPrevEnabled);
            DiagnosticsService.Log($"热键注册: 静音={m} 下一个={n} 上一个={p}");

            // 有失败的热键 → 托盘提示一次（不影响程序其余功能）
            var failed = _status.Where(kv => !kv.Value).Select(kv => kv.Key switch
            {
                "Mute" => $"静音 {config.HotkeyMute}",
                "Next" => $"下一个 {config.HotkeyNext}",
                _ => $"上一个 {config.HotkeyPrev}",
            }).ToList();

            if (failed.Count > 0)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    _showBalloon("热键被占用",
                        string.Join("、", failed) + " 无法注册，请到「设置 → 全局快捷键」里更换。")),
                    DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("热键注册异常: " + ex.Message);
        }
    }

    /// <summary>配置里的热键字段变化时重新注册（设置窗口改完热键后调用）。</summary>
    public void ReloadIfChanged(AppConfig config)
    {
        string signature = BuildSignature(config);
        if (signature == _hotkeySignature) return;
        _hotkeySignature = signature;
        Reload();
    }

    private static string BuildSignature(AppConfig config)
        => $"{config.EnableHotkeys}|{config.HotkeyMute}|{config.HotkeyNext}|{config.HotkeyPrev}|{config.HotkeyMuteEnabled}{config.HotkeyNextEnabled}{config.HotkeyPrevEnabled}";

    private void Reload()
    {
        Unregister();
        Register();
    }

    private bool TryRegister(int id, string key, string? text, string label, bool enabled = true)
    {
        if (!enabled)
        {
            _status[key] = true;   // 未启用视为“无冲突”
            DiagnosticsService.Log($"热键「{label}」未启用，跳过注册");
            return true;
        }

        bool ok = false;

        if (_hotkeySource is not null)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture, out string? error))
            {
                DiagnosticsService.Log($"热键「{label}」无效（{text}）：{error}");
            }
            else
            {
                ok = NativeMethods.RegisterHotKey(_hotkeySource.Handle, id, gesture.RegisterModifiers, gesture.VirtualKey);
                if (!ok) DiagnosticsService.Log($"热键「{label}」({gesture}) 注册失败，可能被其他程序占用");
            }
        }

        _status[key] = ok;
        return ok;
    }

    public void Unregister()
    {
        if (_hotkeySource is null) return;

        if (Environment.CurrentManagedThreadId != _createThreadId)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted)
                {
                    DiagnosticsService.Log("热键：Dispatcher 已关闭，无法在 UI 线程注销");
                    return;
                }
                dispatcher.Invoke(UnregisterCore);
            }
            catch (Exception ex)
            {
                DiagnosticsService.Log("热键：调度注销失败 " + ex.Message);
            }
            return;
        }

        UnregisterCore();
    }

    private void UnregisterCore()
    {
        try
        {
            if (_hotkeySource is null) return;
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, NativeMethods.HotkeyIdMute);
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, NativeMethods.HotkeyIdNext);
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, NativeMethods.HotkeyIdPrev);
            _hotkeySource.Dispose();
            _hotkeySource = null;
            _wndProcHook = null;
        }
        catch { /* 忽略 */ }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case NativeMethods.HotkeyIdMute: _onMute(); handled = true; break;
            case NativeMethods.HotkeyIdNext: _onCycle(true); handled = true; break;
            case NativeMethods.HotkeyIdPrev: _onCycle(false); handled = true; break;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}