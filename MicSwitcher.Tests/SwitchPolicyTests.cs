using MicSwitcher.Core.AutoSwitch;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 自动切换判定表的单元测试。
///
/// 这段规则原先埋在采样回调里，必须先枚举真实音频设备才能跑，因此改动只能靠真机试。
/// 抽成纯函数后，下面每条都对应一个真实故障场景（尤其虚拟声卡与防抖）。
/// </summary>
public class SwitchPolicyTests
{
    private const float Threshold = 0.004f;   // 与 AppConfig 默认值一致（0.4%）
    private const int Debounce = 3;

    private static DeviceLevel L(string id, float avg) => new(id, avg);

    private static LevelDecision Eval(
        DeviceLevel[] levels,
        string? current = null,
        string? pending = null,
        int pendingCount = 0,
        float threshold = Threshold,
        int debounce = Debounce)
        => SwitchPolicy.Evaluate(levels, current, pending, pendingCount, threshold, debounce);

    // ---------- 空与静音 ----------

    [Fact]
    public void 没有设备时不动作()
    {
        var d = Eval(Array.Empty<DeviceLevel>());

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.NoDevices, d.Reason);
    }

    [Fact]
    public void 全部低于阈值且没有明显更响时不切换()
    {
        // 两台电平相同 → 相对判据也不成立（没有 1.5 倍优势）
        var d = Eval(new[] { L("A", 0.002f), L("B", 0.002f) }, current: "A");

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.Silent, d.Reason);
        Assert.Null(d.PendingId);
        Assert.Equal(0, d.PendingCount);
    }

    [Fact]
    public void 没声音时清空已有防抖计数()
    {
        var d = Eval(new[] { L("A", 0.001f), L("B", 0.001f) }, current: "A", pending: "B", pendingCount: 2);

        Assert.Equal(DecisionReason.Silent, d.Reason);
        Assert.Null(d.PendingId);
        Assert.Equal(0, d.PendingCount);
    }

    // ---------- 当前赢家 ----------

    [Fact]
    public void 最响的就是当前设备时不切换()
    {
        var d = Eval(new[] { L("A", 0.01f), L("B", 0.001f) }, current: "A");

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.SameAsCurrent, d.Reason);
        Assert.Equal("A", d.BestId);
    }

    // ---------- 防抖 ----------

    [Fact]
    public void 新候选首次出现只记候选不切换()
    {
        var d = Eval(new[] { L("A", 0.001f), L("B", 0.01f) }, current: "A");

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.NewCandidate, d.Reason);
        Assert.Equal("B", d.BestId);
        Assert.Equal("B", d.PendingId);
        Assert.Equal(1, d.PendingCount);
    }

    [Fact]
    public void 候选未达防抖次数时继续等待()
    {
        var d = Eval(new[] { L("A", 0.001f), L("B", 0.01f) }, current: "A", pending: "B", pendingCount: 1);

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.DebounceWaiting, d.Reason);
        Assert.Equal("B", d.PendingId);
        Assert.Equal(2, d.PendingCount);
    }

    [Fact]
    public void 候选达到防抖次数时执行切换()
    {
        var d = Eval(new[] { L("A", 0.001f), L("B", 0.01f) }, current: "A", pending: "B", pendingCount: 2);

        Assert.Equal(SwitchAction.Switch, d.Action);
        Assert.Equal(DecisionReason.DebouncePassed, d.Reason);
        Assert.Equal("B", d.BestId);
        Assert.Null(d.PendingId);
        Assert.Equal(0, d.PendingCount);
    }

    [Fact]
    public void 候选中途换人时计数重新开始()
    {
        // B 正在计数，但 C 突然更响 → 不能沿用 B 的计数
        var d = Eval(
            new[] { L("A", 0.001f), L("B", 0.01f), L("C", 0.02f) },
            current: "A", pending: "B", pendingCount: 2);

        Assert.Equal(SwitchAction.None, d.Action);
        Assert.Equal(DecisionReason.NewCandidate, d.Reason);
        Assert.Equal("C", d.PendingId);
        Assert.Equal(1, d.PendingCount);
    }

    [Fact]
    public void 防抖次数为一也需要下一轮才切换()
    {
        // 首次出现只记候选，下一轮才累计到 1 → 与原实现保持一致
        var first = Eval(new[] { L("A", 0.001f), L("B", 0.01f) }, current: "A", debounce: 1);
        Assert.Equal(DecisionReason.NewCandidate, first.Reason);

        var second = Eval(new[] { L("A", 0.001f), L("B", 0.01f) },
            current: "A", pending: "B", pendingCount: first.PendingCount, debounce: 1);
        Assert.Equal(SwitchAction.Switch, second.Action);
    }

    // ---------- 相对判据（虚拟声卡场景） ----------

    [Fact]
    public void 绝对电平未达阈值但明显更响时也视为有人说话()
    {
        // 虚拟声卡电平常偏小：0.0022 低于阈值 0.004，但比当前 0.001 高出一倍多
        var d = Eval(new[] { L("A", 0.001f), L("CABLE", 0.0022f) }, current: "A");

        Assert.Equal(DecisionReason.NewCandidate, d.Reason);
        Assert.Equal("CABLE", d.BestId);
    }

    [Fact]
    public void 只高一点点不算明显更响()
    {
        // 0.0022 相对当前 0.0015 只有 1.47 倍，未到 1.5 倍门槛
        var d = Eval(new[] { L("A", 0.0015f), L("CABLE", 0.0022f) }, current: "A");

        Assert.Equal(DecisionReason.Silent, d.Reason);
    }

    [Fact]
    public void 当前设备未知时相对判据以零为基准()
    {
        // currentWinnerId 不在设备列表里（例如刚被拔掉），currentAvg 记为 0
        var d = Eval(new[] { L("A", 0.0025f) }, current: "已消失的设备");

        Assert.Equal(DecisionReason.NewCandidate, d.Reason);
        Assert.Equal("A", d.BestId);
    }

    // ---------- 确定性与边界 ----------

    [Fact]
    public void 电平相同时取靠前的设备()
    {
        // 输入顺序即优先级顺序；平局必须稳定，否则托盘菜单与切换目标会跳变
        var d = Eval(new[] { L("first", 0.01f), L("second", 0.01f) }, current: null);

        Assert.Equal("first", d.BestId);
    }

    [Fact]
    public void 恰好等于阈值时算有声()
    {
        var d = Eval(new[] { L("A", Threshold) }, current: null);

        Assert.Equal(DecisionReason.NewCandidate, d.Reason);
    }

    [Fact]
    public void 判定不修改传入的防抖参数()
    {
        // 纯函数：同样输入连算两次结果必须一致
        var levels = new[] { L("A", 0.001f), L("B", 0.01f) };

        var first = Eval(levels, current: "A", pending: "B", pendingCount: 1);
        var second = Eval(levels, current: "A", pending: "B", pendingCount: 1);

        Assert.Equal(first, second);
    }
}
