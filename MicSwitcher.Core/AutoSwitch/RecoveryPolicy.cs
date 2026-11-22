namespace MicSwitcher.Core.AutoSwitch;

/// <summary>参与恢复判定的设备候选。</summary>
/// <param name="Id">设备 id。</param>
/// <param name="Peak">当前峰值，用于同优先级下挑更响的。</param>
/// <param name="IsVirtual">是否为虚拟声卡（由 VirtualDeviceDetector 预先判定）。</param>
public readonly record struct RecoveryCandidate(string Id, float Peak, bool IsVirtual);

/// <summary>
/// 「默认设备失效了该切到谁」的选择规则（纯函数）。
///
/// 抽出来的原因与 <see cref="SwitchPolicy"/> 相同：这段逻辑决定了死锁能否被解开——
/// 默认麦被禁用后，剩下的麦不是默认设备、电平可能一直读 0，如果这里选不出人，
/// 整机就没输入了。它此前埋在 <see cref="LevelMonitor"/> 里，没有测试。
/// </summary>
public static class RecoveryPolicy
{
    /// <summary>
    /// 在真实（非虚拟）设备里挑一个：先按用户优先级，同优先级取电平更高的。
    /// 没有真实设备时返回 null。
    /// </summary>
    public static string? SelectPhysicalBest(
        IReadOnlyList<RecoveryCandidate> candidates,
        IReadOnlyList<string> priority)
    {
        string? best = null;
        int bestRank = int.MaxValue;
        float bestPeak = float.MinValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.IsVirtual) continue;

            int rank = Rank(priority, c.Id);
            // 严格比较：名次相同且电平相同时保留靠前的那个，结果确定
            if (best is null || rank < bestRank || (rank == bestRank && c.Peak > bestPeak))
            {
                best = c.Id;
                bestRank = rank;
                bestPeak = c.Peak;
            }
        }

        return best;
    }

    /// <summary>
    /// 默认设备消失时的恢复目标。
    /// 开了「优先真实设备」就先挑真实设备；挑不到（全是虚拟声卡）则退回按优先级取第一个。
    /// </summary>
    public static string? SelectRecoveryTarget(
        IReadOnlyList<RecoveryCandidate> candidates,
        IReadOnlyList<string> priority,
        bool preferPhysical)
    {
        if (candidates.Count == 0) return null;

        if (preferPhysical)
        {
            var physical = SelectPhysicalBest(candidates, priority);
            if (physical is not null) return physical;
        }

        // 退化路径与原实现一致：只按优先级取第一个，不再比较电平
        string? best = null;
        int bestRank = int.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            int rank = Rank(priority, candidates[i].Id);
            if (best is null || rank < bestRank)
            {
                best = candidates[i].Id;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>优先级名次：越小越优先；不在列表里的排最后。</summary>
    private static int Rank(IReadOnlyList<string> priority, string id)
    {
        for (int i = 0; i < priority.Count; i++)
        {
            if (priority[i] == id) return i;
        }
        return int.MaxValue;
    }
}
