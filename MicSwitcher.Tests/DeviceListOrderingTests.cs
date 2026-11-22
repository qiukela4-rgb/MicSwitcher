using MicSwitcher.Core.Audio;
using MicSwitcher.Core.Config;
using Xunit;

namespace MicSwitcher.Tests;

/// <summary>
/// 设备列表的构建规则：优先级排序 → 排除过滤 → 别名替换。
/// 托盘菜单、自动切换、设置窗口都吃这一份结果，乱了顺序用户会直接看出来。
/// </summary>
public class DeviceListOrderingTests
{
    private static AudioDeviceInfo D(string id, string? name = null) =>
        new(id, name ?? $"设备{id}", AudioFlow.Capture, false, false, 0f, DeviceState.Active);

    private static string[] Ids(IReadOnlyList<AudioDeviceInfo> list) => list.Select(d => d.Id).ToArray();

    [Fact]
    public void 没有设备时返回空列表()
    {
        var result = DeviceListOrdering.Build(Array.Empty<AudioDeviceInfo>(), new AppConfig(), true);

        Assert.Empty(result);
    }

    [Fact]
    public void 无优先级时保持系统枚举顺序()
    {
        var devices = new[] { D("A"), D("B"), D("C") };

        var result = DeviceListOrdering.Build(devices, new AppConfig(), true);

        Assert.Equal(new[] { "A", "B", "C" }, Ids(result));
    }

    [Fact]
    public void 按配置优先级重排()
    {
        var config = new AppConfig { DevicePriority = new List<string> { "C", "A" } };
        var devices = new[] { D("A"), D("B"), D("C") };

        var result = DeviceListOrdering.Build(devices, config, true);

        Assert.Equal(new[] { "C", "A", "B" }, Ids(result));
    }

    [Fact]
    public void 排除的设备默认被过滤()
    {
        var config = new AppConfig { ExcludedDevices = new List<string> { "B" } };
        var devices = new[] { D("A"), D("B"), D("C") };

        var result = DeviceListOrdering.Build(devices, config, includeExcluded: false);

        Assert.Equal(new[] { "A", "C" }, Ids(result));
    }

    [Fact]
    public void 设置窗口需要看到被排除的设备()
    {
        // includeExcluded=true 时保留，否则用户在设置里没法把它取消排除
        var config = new AppConfig { ExcludedDevices = new List<string> { "B" } };
        var devices = new[] { D("A"), D("B") };

        var result = DeviceListOrdering.Build(devices, config, includeExcluded: true);

        Assert.Equal(new[] { "A", "B" }, Ids(result));
    }

    [Fact]
    public void 别名替换显示名但不改设备id()
    {
        var config = new AppConfig { DeviceAliases = { ["A"] = "桌面麦" } };
        var devices = new[] { D("A", "原厂长名字") };

        var result = DeviceListOrdering.Build(devices, config, true);

        Assert.Equal("桌面麦", result[0].FriendlyName);
        Assert.Equal("A", result[0].Id);
    }

    [Fact]
    public void 三层规则叠加时顺序是排序在前过滤在中改名在后()
    {
        var config = new AppConfig
        {
            DevicePriority = new List<string> { "C", "B", "A" },
            ExcludedDevices = new List<string> { "B" },
            DeviceAliases = { ["A"] = "桌面麦", ["C"] = "耳机麦" },
        };
        var devices = new[] { D("A"), D("B"), D("C") };

        var result = DeviceListOrdering.Build(devices, config, includeExcluded: false);

        Assert.Equal(new[] { "C", "A" }, Ids(result));          // 排序后去掉 B
        Assert.Equal(new[] { "耳机麦", "桌面麦" }, result.Select(d => d.FriendlyName).ToArray());
    }

    [Fact]
    public void 优先级里残留已拔掉的设备被忽略()
    {
        var config = new AppConfig { DevicePriority = new List<string> { "已拔掉的", "B" } };
        var devices = new[] { D("A"), D("B") };

        var result = DeviceListOrdering.Build(devices, config, true);

        Assert.Equal(new[] { "B", "A" }, Ids(result));
    }

    [Fact]
    public void 排除列表里写错id不会误伤其他设备()
    {
        var config = new AppConfig { ExcludedDevices = new List<string> { "不存在的" } };
        var devices = new[] { D("A"), D("B") };

        var result = DeviceListOrdering.Build(devices, config, includeExcluded: false);

        Assert.Equal(new[] { "A", "B" }, Ids(result));
    }

    [Fact]
    public void 不修改传入的设备对象()
    {
        // record 的 with 会生成新实例，原名应保持不变
        var original = D("A", "原厂长名字");
        var config = new AppConfig { DeviceAliases = { ["A"] = "桌面麦" } };

        var result = DeviceListOrdering.Build(new[] { original }, config, true);

        Assert.Equal("原厂长名字", original.FriendlyName);
        Assert.Equal("桌面麦", result[0].FriendlyName);
    }
}
