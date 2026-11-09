namespace MicSwitcher.Core.Hotkeys;

/// <summary>
/// 全局热键的组合键：修饰键 + 虚拟键码。
/// 文本格式如 "Ctrl+Alt+M"，用于存进 settings.json 和界面展示。
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    // 与 RegisterHotKey 的 fsModifiers 保持一致
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>注册时额外加上，避免长按连发（RegisterHotKey 的 MOD_NOREPEAT）。</summary>
    public const uint ModNoRepeat = 0x4000;

    public bool IsValid => VirtualKey != 0 && Modifiers != 0;

    /// <summary>给 RegisterHotKey 用的最终修饰符。</summary>
    public uint RegisterModifiers => Modifiers | ModNoRepeat;

    /// <summary>预设热键（界面下拉里直接选）。</summary>
    public static IReadOnlyList<string> Presets { get; } = new[]
    {
        "Ctrl+Alt+M",
        "Ctrl+Alt+N",
        "Ctrl+Alt+P",
        "Ctrl+Shift+M",
        "Ctrl+Alt+1",
        "Ctrl+Alt+2",
        "Ctrl+Alt+F9",
        "Ctrl+Alt+F10",
    };

    private static readonly Dictionary<string, uint> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["`"] = 0xC0,
        ["-"] = 0xBD,
        ["="] = 0xBB,
        ["["] = 0xDB,
        ["]"] = 0xDD,
        [";"] = 0xBA,
        ["'"] = 0xDE,
        [","] = 0xBC,
        ["."] = 0xBE,
        ["/"] = 0xBF,
    };

    /// <summary>把虚拟键码映射回名字。</summary>
    public static string KeyName(uint vk)
    {
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);        // F1..F12
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();        // A..Z
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();        // 0..9
        foreach (var kv in KeyNames)
            if (kv.Value == vk) return kv.Key;
        return "0x" + vk.ToString("X2");
    }

    /// <summary>解析 "Ctrl+Alt+M" 这类文本。</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        uint modifiers = 0;
        uint vk = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win":
                case "windows": modifiers |= ModWin; continue;
            }

            if (vk != 0)
            {
                error = "只能包含一个主键";
                return false;
            }
            if (!TryParseKey(token, out vk))
            {
                error = $"无法识别的按键：{token}";
                return false;
            }
        }

        if (modifiers == 0)
        {
            error = "至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }
        if (vk == 0)
        {
            error = "缺少主键（如 M、F9）";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, vk);
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }

        if ((token.StartsWith('F') || token.StartsWith('f')) &&
            int.TryParse(token.AsSpan(1), out int fn) && fn >= 1 && fn <= 12)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        if (KeyNames.TryGetValue(token, out vk)) return true;

        return false;
    }

    /// <summary>输出 "Ctrl+Alt+M"。</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}
