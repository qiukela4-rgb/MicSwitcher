using MicSwitcher.Core.AutoSwitch;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 默认设备失效时的恢复目标选择。
/// 这段逻辑是「默认麦被禁用后整机没输入」那个死锁的解法，选不出人就会卡死，故逐条锁行为。
/// </summary>
public class RecoveryPolicyTests
{
    private static RecoveryCandidate C(string id, float peak, bool isVirtual = false)
        => new(id, peak, isVirtual);

    private static readonly string[] NoPriority = Array.Empty<string>();

    // ---------- 空 ----------

    [Fact]
    public void 没有候选时返回空()
    {
        Assert.Null(RecoveryPolicy.SelectPhysicalBest(Array.Empty<RecoveryCandidate>(), NoPriority));
        Assert.Null(RecoveryPolicy.SelectRecoveryTarget(Array.Empty<RecoveryCandidate>(), NoPriority, true));
    }

    // ---------- 真实设备优先 ----------

    [Fact]
    public void 挑真实设备时跳过虚拟声卡()
    {
        var list = new[] { C("V", 0.9f, isVirtual: true), C("USB", 0.1f) };

        Assert.Equal("USB", RecoveryPolicy.SelectPhysicalBest(list, NoPriority));
    }

    [Fact]
    public void 全是虚拟声卡时没有真实设备可选()
    {
        var list = new[] { C("V1", 0.5f, isVirtual: true), C("V2", 0.3f, isVirtual: true) };

        Assert.Null(RecoveryPolicy.SelectPhysicalBest(list, NoPriority));
    }

    [Fact]
    public void 全是虚拟声卡时恢复目标退回按优先级取()
    {
        var list = new[] { C("V1", 0.5f, isVirtual: true), C("V2", 0.3f, isVirtual: true) };

        // 不能返回 null —— 否则默认设备死了就永远没人接管
        Assert.Equal("V1", RecoveryPolicy.SelectRecoveryTarget(list, NoPriority, preferPhysical: true));
    }

    // ---------- 优先级 ----------

    [Fact]
    public void 优先级高于电平()
    {
        // B 更响，但 A 在优先级列表里更靠前
        var list = new[] { C("A", 0.1f), C("B", 0.9f) };

        Assert.Equal("A", RecoveryPolicy.SelectPhysicalBest(list, new[] { "A", "B" }));
    }

    [Fact]
    public void 不在优先级列表的设备排最后()
    {
        var list = new[] { C("UNKNOWN", 0.99f), C("LISTED", 0.01f) };

        Assert.Equal("LISTED", RecoveryPolicy.SelectPhysicalBest(list, new[] { "LISTED" }));
    }

    [Fact]
    public void 同优先级时取电平更高的()
    {
        // 两台都不在优先级列表里 → 名次相同 → 比电平
        var list = new[] { C("A", 0.2f), C("B", 0.8f) };

        Assert.Equal("B", RecoveryPolicy.SelectPhysicalBest(list, NoPriority));
    }

    [Fact]
    public void 同优先级同电平时取靠前的()
    {
        var list = new[] { C("first", 0.5f), C("second", 0.5f) };

        Assert.Equal("first", RecoveryPolicy.SelectPhysicalBest(list, NoPriority));
    }

    // ---------- 恢复目标的两条路径 ----------

    [Fact]
    public void 开优先真实设备时即使虚拟声卡优先级更高也不选它()
    {
        var list = new[] { C("V", 0.9f, isVirtual: true), C("USB", 0.1f) };

        Assert.Equal("USB", RecoveryPolicy.SelectRecoveryTarget(list, new[] { "V", "USB" }, preferPhysical: true));
    }

    [Fact]
    public void 关掉优先真实设备时按优先级可能落到虚拟声卡()
    {
        var list = new[] { C("V", 0.1f, isVirtual: true), C("USB", 0.9f) };

        // 用户显式关掉「优先真实设备」，就尊重优先级顺序
        Assert.Equal("V", RecoveryPolicy.SelectRecoveryTarget(list, new[] { "V", "USB" }, preferPhysical: false));
    }

    [Fact]
    public void 退化路径只按优先级不看电平()
    {
        // 关掉 preferPhysical 且都不在优先级列表 → 取第一个，而不是电平最高的
        var list = new[] { C("quiet", 0.01f), C("loud", 0.99f) };

        Assert.Equal("quiet", RecoveryPolicy.SelectRecoveryTarget(list, NoPriority, preferPhysical: false));
    }

    [Fact]
    public void 恢复目标不会返回列表外的设备()
    {
        var list = new[] { C("A", 0.1f) };

        // 优先级里写了不存在或已拔掉的设备 id，不能把目标指到它们身上
        var target = RecoveryPolicy.SelectRecoveryTarget(list, new[] { "拔掉的设备", "A" }, preferPhysical: true);

        Assert.Equal("A", target);
    }
}
