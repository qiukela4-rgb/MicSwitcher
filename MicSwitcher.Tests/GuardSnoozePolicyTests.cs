using MicSwitcher.Core.Locking;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 外部软件抢默认设备时的让路规则：
/// 语音输入 / 虚拟声卡会反复改默认麦，硬抢会让对方收不到声音，
/// 还可能因为高频 COM 调用与通知把程序拖崩。
/// </summary>
public class GuardSnoozePolicyTests
{
    [Fact]
    public void 间隔够久时算作独立抢占不累计()
    {
        bool snooze = GuardSnoozePolicy.ShouldSnooze(
            GuardSnoozePolicy.BurstWindow, priorBurstCount: 1, out int burst);

        Assert.False(snooze);
        Assert.Equal(0, burst);
    }

    [Fact]
    public void 窗口内的第二次抢占触发让路()
    {
        bool snooze = GuardSnoozePolicy.ShouldSnooze(
            TimeSpan.FromMilliseconds(300), priorBurstCount: 1, out int burst);

        Assert.True(snooze);
        Assert.Equal(2, burst);
    }

    [Fact]
    public void 窗口内第一次抢占只累计不让路()
    {
        bool snooze = GuardSnoozePolicy.ShouldSnooze(
            TimeSpan.FromMilliseconds(300), priorBurstCount: 0, out int burst);

        Assert.False(snooze);
        Assert.Equal(1, burst);
    }

    [Fact]
    public void 恰好在窗口边界上不累计()
    {
        bool snooze = GuardSnoozePolicy.ShouldSnooze(
            GuardSnoozePolicy.BurstWindow, priorBurstCount: 1, out int burst);

        Assert.False(snooze);
        Assert.Equal(0, burst);
    }

    [Fact]
    public void 让路时长足够覆盖一次语音输入会话()
    {
        Assert.True(GuardSnoozePolicy.SnoozeDuration >= TimeSpan.FromSeconds(5));
    }
}
