using MicSwitcher.Core.Audio;

namespace MicSwitcher.Core.Config;

/// <summary>
/// 把「枚举到的设备」整理成界面和托盘要显示的顺序（纯函数）。
///
/// 规则叠了三层：用户自定义优先级排序 → 可选的排除过滤 → 设备别名替换显示名。
/// 三层顺序会影响结果（先排除再改名和反过来一样，但都必须发生在排序之后），
/// 因此单独抽出来锁行为：托盘菜单、自动切换、设置窗口都用这一份结果。
/// </summary>
public static class DeviceListOrdering
{
    /// <summary>
    /// 构建最终展示用的设备列表。
    /// </summary>
    /// <param name="devices">当前枚举到的设备（顺序为系统枚举顺序）。</param>
    /// <param name="config">配置：提供优先级、别名、排除列表。</param>
    /// <param name="includeExcluded">true 时保留被排除的设备（设置窗口要能看到并取消排除）。</param>
    public static IReadOnlyList<AudioDeviceInfo> Build(
        IReadOnlyList<AudioDeviceInfo> devices,
        AppConfig config,
        bool includeExcluded)
    {
        if (devices.Count == 0) return Array.Empty<AudioDeviceInfo>();

        var byId = devices.ToDictionary(d => d.Id);

        var ordered = config.SortByPriority(devices.Select(d => d.Id))
            .Where(byId.ContainsKey)          // 优先级里残留的旧 id 会被过滤掉
            .Select(id => byId[id]);

        if (!includeExcluded)
            ordered = ordered.Where(d => !config.ExcludedDevices.Contains(d.Id));

        return ordered
            .Select(d => d with { FriendlyName = config.DisplayName(d.Id, d.FriendlyName) })
            .ToList();
    }
}
