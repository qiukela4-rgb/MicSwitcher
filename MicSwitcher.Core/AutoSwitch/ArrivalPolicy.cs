namespace MicSwitcher.Core.AutoSwitch;

/// <summary>一轮比对的结论：本轮新到的设备 + 下一轮的基线。</summary>
public readonly record struct ArrivalResult(
    IReadOnlyList<string> Arrived,
    IReadOnlySet<string> NextBaseline);

/// <summary>
/// 「哪些设备是本轮新接入的」判定规则（纯函数）。
///
/// 抽出来的原因：这段逻辑决定自动切换会不会误触发。
/// 最危险的是枚举瞬时返回空——若把空当成「设备全拔了」清空基线，
/// 下一轮所有设备都会被当成新插入，连着触发好几次切换。
/// </summary>
public static class ArrivalPolicy
{
    /// <summary>
    /// 用上一轮基线比对本轮设备。
    /// </summary>
    /// <param name="baseline">上一轮见过的设备 id。</param>
    /// <param name="current">本轮枚举到的设备 id（保持枚举顺序）。</param>
    public static ArrivalResult Diff(IReadOnlySet<string> baseline, IReadOnlyList<string> current)
    {
        // 枚举为空：多半是音频服务抖动、驱动初始化中，或整机暂时没有录音设备。
        // 此时保留原基线，不产生新到事件——否则下一轮恢复时会把所有设备当成刚插上。
        if (current.Count == 0)
            return new ArrivalResult(Array.Empty<string>(), baseline);

        var arrived = new List<string>();
        foreach (var id in current)
        {
            if (!baseline.Contains(id)) arrived.Add(id);
        }

        // 基线只保留本轮仍存在的设备：拔掉后再插回来应当算「新接入」
        return new ArrivalResult(arrived, current.ToHashSet());
    }
}
