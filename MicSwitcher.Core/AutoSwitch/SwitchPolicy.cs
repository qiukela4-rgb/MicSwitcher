namespace MicSwitcher.Core.AutoSwitch;

/// <summary>参与判定的一台设备的窗口平均电平。</summary>
public readonly record struct DeviceLevel(string Id, float Average);

/// <summary>判定结果的动作。</summary>
public enum SwitchAction
{
    /// <summary>不切换。</summary>
    None,

    /// <summary>执行切换，目标见 <see cref="LevelDecision.BestId"/>。</summary>
    Switch,
}

/// <summary>判定原因。既用于日志排查，也让测试能断言"为什么不切"。</summary>
public enum DecisionReason
{
    /// <summary>没有可用设备。</summary>
    NoDevices,

    /// <summary>没有任何设备达到可听条件，视为没人说话。</summary>
    Silent,

    /// <summary>最响的就是当前默认设备，无需切换。</summary>
    SameAsCurrent,

    /// <summary>出现了新的候选，开始防抖计数。</summary>
    NewCandidate,

    /// <summary>候选仍在防抖计数中，尚未达标。</summary>
    DebounceWaiting,

    /// <summary>防抖已达标，执行切换。</summary>
    DebouncePassed,
}

/// <summary>
/// 一次判定的结果，同时携带防抖状态的下一个值。
/// 纯数据，调用方负责把 <see cref="PendingId"/>/<see cref="PendingCount"/> 写回。
/// </summary>
/// <param name="BestId">本轮最响的设备（即使判定不切换也给出，便于日志说明"为什么没切"）。</param>
public readonly record struct LevelDecision(
    SwitchAction Action,
    DecisionReason Reason,
    string? BestId,
    string? PendingId,
    int PendingCount);

/// <summary>
/// 自动切换的判定规则（纯函数，无 COM、无计时器、无状态）。
///
/// 抽出来的原因：这段规则连续出过两次线上问题（虚拟声卡电平恒 0、默认设备失效后死锁），
/// 但它原先埋在 <see cref="LevelMonitor"/> 的采样回调里，必须先枚举真实音频设备才能验证，
/// 导致改动只能靠真机试。抽成纯函数后，判定表可以在任意环境（含 CI）逐条断言。
/// </summary>
public static class SwitchPolicy
{
    /// <summary>相对判据的最小绝对偏移，避免当前电平为 0 时被极小噪声触发。</summary>
    private const float RelativeFloor = 0.0005f;

    /// <summary>相对判据的倍数：最响的要比当前明显更响才值得切。</summary>
    private const float RelativeRatio = 1.5f;

    /// <summary>
    /// 判定本轮是否该切换麦克风。
    /// </summary>
    /// <param name="levels">按设备顺序排列的窗口平均电平（顺序决定平局时谁优先，靠前者胜）。</param>
    /// <param name="currentWinnerId">当前由本程序认定的赢家设备；未知时为 null。</param>
    /// <param name="pendingId">上一轮的防抖目标。</param>
    /// <param name="pendingCount">上一轮已累计的达标次数。</param>
    /// <param name="silenceThreshold">绝对静音阈值，窗口平均低于它视为没人说话。</param>
    /// <param name="debounceSamples">需要连续达标多少次才真正切换。</param>
    public static LevelDecision Evaluate(
        IReadOnlyList<DeviceLevel> levels,
        string? currentWinnerId,
        string? pendingId,
        int pendingCount,
        float silenceThreshold,
        int debounceSamples)
    {
        if (levels.Count == 0)
            return new LevelDecision(SwitchAction.None, DecisionReason.NoDevices, null, pendingId, pendingCount);

        // 找窗口平均最高的设备。用严格大于，平局时保留靠前的那个，
        // 与「按优先级排好序的输入」配合，保证结果确定。
        var best = levels[0];
        for (int i = 1; i < levels.Count; i++)
        {
            if (levels[i].Average > best.Average) best = levels[i];
        }

        float currentAvg = 0f;
        if (currentWinnerId is not null)
        {
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i].Id != currentWinnerId) continue;
                currentAvg = levels[i].Average;
                break;
            }
        }

        bool bestAudible = best.Average >= silenceThreshold;

        // 相对判据：虚拟声卡电平常比真实麦小，只看绝对值会漏掉它。
        // 只要最响的明显比当前响（≥1.5 倍且超过阈值一半），也算"这边有人在说话"。
        bool clearlyLouder = best.Average >= silenceThreshold * 0.5f
                             && best.Average >= currentAvg * RelativeRatio + RelativeFloor;

        if (!bestAudible && !clearlyLouder)
            return new LevelDecision(SwitchAction.None, DecisionReason.Silent, best.Id, null, 0);

        if (best.Id == currentWinnerId)
            return new LevelDecision(SwitchAction.None, DecisionReason.SameAsCurrent, best.Id, null, 0);

        // 换了候选就重新计数，避免两个设备交替微弱领先时被累加通过防抖
        if (best.Id != pendingId)
            return new LevelDecision(SwitchAction.None, DecisionReason.NewCandidate, best.Id, best.Id, 1);

        int next = pendingCount + 1;
        if (next >= debounceSamples)
            return new LevelDecision(SwitchAction.Switch, DecisionReason.DebouncePassed, best.Id, null, 0);

        return new LevelDecision(SwitchAction.None, DecisionReason.DebounceWaiting, best.Id, best.Id, next);
    }
}
