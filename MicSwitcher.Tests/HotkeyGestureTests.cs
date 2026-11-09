using MicSwitcher.Core.Hotkeys;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>热键文本解析/格式化的单元测试。</summary>
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, (uint)'M')]
    [InlineData("ctrl+shift+n", HotkeyGesture.ModControl | HotkeyGesture.ModShift, (uint)'N')]
    [InlineData("Alt+F9", HotkeyGesture.ModAlt, 0x78u)]
    [InlineData("Win+Alt+1", HotkeyGesture.ModWin | HotkeyGesture.ModAlt, (uint)'1')]
    public void 能解析合法热键(string text, uint expectedMods, uint expectedVk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g, out var err), err);
        Assert.Equal(expectedMods, g.Modifiers);
        Assert.Equal(expectedVk, g.VirtualKey);
    }

    [Theory]
    [InlineData("M")]            // 缺修饰键
    [InlineData("Ctrl+")]        // 缺主键
    [InlineData("Ctrl+Alt+QQ")]  // 无法识别的键
    [InlineData("")]
    [InlineData(null)]
    public void 拒绝非法热键(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void 解析后再格式化应保持一致()
    {
        foreach (var preset in HotkeyGesture.Presets)
        {
            Assert.True(HotkeyGesture.TryParse(preset, out var g, out var err), $"{preset}: {err}");
            Assert.Equal(preset, g.ToString());
        }
    }

    [Fact]
    public void 注册修饰符包含NOREPEAT()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+M", out var g, out _));
        Assert.Equal(g.Modifiers | HotkeyGesture.ModNoRepeat, g.RegisterModifiers);
    }
}
