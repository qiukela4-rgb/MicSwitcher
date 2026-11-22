using MicSwitcher.Core.AutoSwitch;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 「新设备接入」的比对规则。
/// 它决定自动切换会不会误触发——尤其是枚举瞬时为空时的处理。
/// </summary>
public class ArrivalPolicyTests
{
    private static IReadOnlySet<string> Set(params string[] ids) => ids.ToHashSet();

    private static ArrivalResult Diff(IReadOnlySet<string> baseline, params string[] current)
        => ArrivalPolicy.Diff(baseline, current);

    [Fact]
    public void 设备没变化时不产生新到事件()
    {
        var r = Diff(Set("A", "B"), "A", "B");

        Assert.Empty(r.Arrived);
        Assert.True(r.NextBaseline.SetEquals(new[] { "A", "B" }));
    }

    [Fact]
    public void 多出的设备算新接入()
    {
        var r = Diff(Set("A"), "A", "B");

        Assert.Equal(new[] { "B" }, r.Arrived);
        Assert.True(r.NextBaseline.SetEquals(new[] { "A", "B" }));
    }

    [Fact]
    public void 首次没有基线时所有设备都算新()
    {
        var r = Diff(Set(), "A", "B");

        Assert.Equal(new[] { "A", "B" }, r.Arrived);
    }

    [Fact]
    public void 拔掉的设备从基线移除()
    {
        var r = Diff(Set("A", "B"), "A");

        Assert.Empty(r.Arrived);                 // 拔掉不是「新接入」，不该触发切换
        Assert.True(r.NextBaseline.SetEquals(new[] { "A" }));
    }

    [Fact]
    public void 拔掉后再插回算新接入()
    {
        var first = Diff(Set("A", "B"), "A");   // B 被拔掉
        var second = Diff(first.NextBaseline, "A", "B");   // B 插回来

        Assert.Equal(new[] { "B" }, second.Arrived);
    }

    [Fact]
    public void 枚举瞬时为空时保留基线且不触发()
    {
        // 音频服务抖动／驱动初始化时枚举会返回空。
        // 若把空当成「设备全拔了」清空基线，下一轮恢复时所有设备都会被当成刚插上。
        var r = Diff(Set("A", "B"));

        Assert.Empty(r.Arrived);
        Assert.True(r.NextBaseline.SetEquals(new[] { "A", "B" }));
    }

    [Fact]
    public void 空基线加空枚举不抛异常()
    {
        var r = Diff(Set());

        Assert.Empty(r.Arrived);
        Assert.Empty(r.NextBaseline);
    }

    [Fact]
    public void 新到设备保持枚举顺序()
    {
        var r = Diff(Set(), "C", "B", "A");

        Assert.Equal(new[] { "C", "B", "A" }, r.Arrived);
    }

    [Fact]
    public void 只报本轮新出现的那些()
    {
        var r = Diff(Set("A", "C"), "A", "B", "C", "D");

        Assert.Equal(new[] { "B", "D" }, r.Arrived);
    }
}
