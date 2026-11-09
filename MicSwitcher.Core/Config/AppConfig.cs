using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicSwitcher.Core.Config;

/// <summary>自动模式的切换策略。</summary>
public enum AutoStrategy
{
    /// <summary>按音量电平：谁在说话就切到谁。</summary>
    ByLevel = 0,

    /// <summary>按新设备：有新麦克风接入就切过去。</summary>
    ByNewDevice = 1,
}

/// <summary>
/// 应用配置：存 exe 旁边的 settings.json。
/// 只存与系统无关的本地偏好；开机自启走注册表（见 Startup.AutoStart），不在这里存。
/// </summary>
public sealed class AppConfig
{
    /// <summary>启动时直接最小化到托盘（不弹设置窗口）。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>是否在系统托盘显示图标。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>关闭设置窗口时是缩到托盘（true）还是直接退出（false）。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>自动切换麦克风时是否弹气泡提示。</summary>
    public bool NotifyOnAutoSwitch { get; set; } = true;

    /// <summary>自动模式的策略（按声音 / 按新设备）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AutoStrategy AutoStrategy { get; set; } = AutoStrategy.ByLevel;

    /// <summary>切默认麦时是否同时设置「通信设备」角色（通话软件才会跟着变）。</summary>
    public bool SyncCommunicationRole { get; set; } = true;

    /// <summary>是否启用全局热键（Ctrl+Alt+M 静音，Ctrl+Alt+N / P 切麦）。</summary>
    public bool EnableHotkeys { get; set; } = true;

    /// <summary>静音切换热键，文本格式如 "Ctrl+Alt+M"。</summary>
    public string HotkeyMute { get; set; } = "Ctrl+Alt+M";

    /// <summary>下一个麦克风热键。</summary>
    public string HotkeyNext { get; set; } = "Ctrl+Alt+N";

    /// <summary>上一个麦克风热键。</summary>
    public string HotkeyPrev { get; set; } = "Ctrl+Alt+P";

    /// <summary>设备优先级顺序（设备 ID 列表，越靠前越优先）。</summary>
    public List<string> DevicePriority { get; set; } = new();

    /// <summary>按优先级给设备排序：优先列表里有的排前面，其余保持原顺序。</summary>
    public List<string> SortByPriority(IEnumerable<string> deviceIds)
    {
        var ids = deviceIds.ToList();
        return ids
            .OrderBy(id =>
            {
                int idx = DevicePriority.IndexOf(id);
                return idx < 0 ? int.MaxValue : idx;
            })
            .ToList();
    }

    // ---------- 热键：每个动作单独启用 ----------

    public bool HotkeyMuteEnabled { get; set; } = true;
    public bool HotkeyNextEnabled { get; set; } = true;
    public bool HotkeyPrevEnabled { get; set; } = true;

    // ---------- 启动 ----------

    /// <summary>开机自启时延迟启动的秒数（等音频服务就绪）。</summary>
    public bool DelayedAutostart { get; set; }

    /// <summary>延迟多少秒，默认 8 秒。</summary>
    public int DelayedAutostartSeconds { get; set; } = 8;

    // ---------- 托盘 ----------

    /// <summary>托盘图标是否反映当前静音状态（静音时换成灰色图标）。</summary>
    public bool TrayIconShowsMute { get; set; } = true;

    // ---------- 自动模式灵敏度 ----------

    /// <summary>采样间隔（毫秒）。</summary>
    public int SampleIntervalMs { get; set; } = 300;

    /// <summary>静音阈值：窗口平均电平低于它就算没人说话（0.001 ~ 0.2）。</summary>
    public float SilenceThreshold { get; set; } = 0.004f;

    /// <summary>防抖次数：连续多少个采样周期达标才切换。</summary>
    public int DebounceSamples { get; set; } = 3;

    /// <summary>都没声音时优先使用真实设备（不被虚拟声卡占着）。</summary>
    public bool PreferPhysicalDevices { get; set; } = true;

    // ---------- 设备 ----------

    /// <summary>设备别名：设备 Id → 显示名。</summary>
    public Dictionary<string, string> DeviceAliases { get; set; } = new();

    /// <summary>被排除的设备：不参与自动切换，也不出现在托盘菜单里。</summary>
    public List<string> ExcludedDevices { get; set; } = new();

    /// <summary>取设备的显示名（有别名用别名）。</summary>
    public string DisplayName(string deviceId, string fallback)
        => DeviceAliases.TryGetValue(deviceId, out var alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias
            : fallback;

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>从磁盘加载配置；文件不存在或损坏时返回默认值。</summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>保存配置；失败静默忽略（不抛异常，避免 UI 闪退）。</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 配置写盘失败不影响程序运行，忽略
        }
    }
}