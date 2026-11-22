using System.Text.Json;
using MicSwitcher.Core.Config;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 配置里的排序与显示名规则。
/// 它们决定托盘菜单顺序、自动切换的优先级判定、以及界面上显示什么名字，
/// 但此前完全没有测试。
/// </summary>
public class AppConfigTests
{
    // ---------- 优先级排序 ----------

    [Fact]
    public void 没有配置优先级时保持原顺序()
    {
        var config = new AppConfig();

        var sorted = config.SortByPriority(new[] { "A", "B", "C" });

        Assert.Equal(new[] { "A", "B", "C" }, sorted);
    }

    [Fact]
    public void 按配置的优先级排列()
    {
        var config = new AppConfig { DevicePriority = new List<string> { "C", "A", "B" } };

        var sorted = config.SortByPriority(new[] { "A", "B", "C" });

        Assert.Equal(new[] { "C", "A", "B" }, sorted);
    }

    [Fact]
    public void 不在优先级列表里的设备排在最后且保持原顺序()
    {
        var config = new AppConfig { DevicePriority = new List<string> { "B" } };

        var sorted = config.SortByPriority(new[] { "X", "A", "B", "Y" });

        Assert.Equal(new[] { "B", "X", "A", "Y" }, sorted);
    }

    [Fact]
    public void 优先级里残留已拔掉的设备不影响排序()
    {
        // 设备拔掉后配置里仍留着它的 id，不能让它把别的设备挤开
        var config = new AppConfig { DevicePriority = new List<string> { "已拔掉的", "B" } };

        var sorted = config.SortByPriority(new[] { "A", "B" });

        Assert.Equal(new[] { "B", "A" }, sorted);
    }

    [Fact]
    public void 设备数量与优先级不一致时不丢设备()
    {
        var config = new AppConfig { DevicePriority = new List<string> { "A" } };

        var sorted = config.SortByPriority(new[] { "A", "B", "C", "D" });

        Assert.Equal(4, sorted.Count);
        Assert.Equal("A", sorted[0]);
        Assert.Equal(new[] { "A", "B", "C", "D" }, sorted);
    }

    // ---------- 显示名 ----------

    [Fact]
    public void 有别名时用别名()
    {
        var config = new AppConfig { DeviceAliases = { ["id-1"] = "桌面麦" } };

        Assert.Equal("桌面麦", config.DisplayName("id-1", "原厂长名字"));
    }

    [Fact]
    public void 没有别名时用原名()
    {
        var config = new AppConfig();

        Assert.Equal("原厂长名字", config.DisplayName("id-1", "原厂长名字"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 别名是空或空白时回退原名(string alias)
    {
        // 用户把别名清空后，不该显示成一片空白
        var config = new AppConfig { DeviceAliases = { ["id-1"] = alias } };

        Assert.Equal("原厂长名字", config.DisplayName("id-1", "原厂长名字"));
    }

    // ---------- 序列化往返 ----------

    [Fact]
    public void 配置序列化后能原样读回()
    {
        var config = new AppConfig
        {
            StartMinimized = true,
            AutoStrategy = AutoStrategy.ByNewDevice,
            DevicePriority = new List<string> { "B", "A" },
            ExcludedDevices = new List<string> { "V" },
            DeviceAliases = { ["id-1"] = "桌面麦" },
            SilenceThreshold = 0.004f,
            DebounceSamples = 5,
        };

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.Equal(config.StartMinimized, back.StartMinimized);
        Assert.Equal(AutoStrategy.ByNewDevice, back.AutoStrategy);
        Assert.Equal(config.DevicePriority, back.DevicePriority);
        Assert.Equal(config.ExcludedDevices, back.ExcludedDevices);
        Assert.Equal("桌面麦", back.DeviceAliases["id-1"]);
        Assert.Equal(0.004f, back.SilenceThreshold);
        Assert.Equal(5, back.DebounceSamples);
    }

    [Fact]
    public void 策略以字符串形式存储便于手改()
    {
        // settings.json 是给人看的，AutoStrategy 存 "ByLevel" 而不是 0
        var json = JsonSerializer.Serialize(new AppConfig { AutoStrategy = AutoStrategy.ByLevel });

        Assert.Contains("\"ByLevel\"", json);
    }

    [Fact]
    public void 配置缺字段时用默认值兜底()
    {
        // 旧版本的 settings.json 不含新加的字段，反序列化不能抛异常
        var back = JsonSerializer.Deserialize<AppConfig>("{\"StartMinimized\":true}")!;

        Assert.True(back.StartMinimized);
        Assert.Equal(AutoStrategy.ByLevel, back.AutoStrategy);
        Assert.Equal(0.004f, back.SilenceThreshold);
        Assert.NotNull(back.DevicePriority);
        Assert.NotNull(back.DeviceAliases);
    }
}
