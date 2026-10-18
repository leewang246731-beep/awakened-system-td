using EraCore.Procedural;
using Xunit;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Procedural;

// [C-10][C-19][F-007] T-140 战役外程序关入口数据位：campaign/procedural.json
// 注册表（入口行/约束带/档位梯）装载即校验 + 时代域动态枚举（Endless
// AvailableStartEras 同纪律——入口不硬编码时代域，装载集驱动）。
public class ProceduralCampaignEntryTests
{
    [Fact]
    public void Registry_loads_and_validates()
    {
        // legal — 注册表装载：入口行/约束带/三档梯全部过生产记录校验，
        // 缺省档存在于梯内，时长带=40~90 分钟（F-007 验收带数据位）。
        ProceduralCampaignEntry entry = Load();
        Assert.Equal("campaign:procedural-entry", entry.Id);
        Assert.Equal(40, entry.DurationBandMinutesMin);
        Assert.Equal(90, entry.DurationBandMinutesMax);
        Assert.Equal("regular-1", entry.DefaultTierId);
        Assert.Equal(3, entry.Tiers.Count);
        Assert.NotNull(entry.TryGetTier("regular-1"));
        Assert.NotNull(entry.TryGetTier("regular-2"));
        Assert.NotNull(entry.TryGetTier("endgame"));
        Assert.Null(entry.TryGetTier("no-such-tier"));
    }

    [Fact]
    public void Era_domain_is_enumerated_from_the_loaded_packs()
    {
        // legal — 动态枚举纪律：入口时代面=装载内容集驱动（era1 装载 ⇒
        // era1 开放；未装载时代不出现；入口不硬编码时代域）。
        ProceduralCampaignEntry entry = Load();
        Assert.Equal(new[] { "era1" }, entry.OpenEras(new[] { Era1Pack() }));

        var era2Pack = Era1Pack() with { EraId = "era2:era-classical" };
        Assert.Empty(entry.OpenEras(new[] { era2Pack })); // 未装载 era1 ⇒ 入口隐而不坏
        Assert.Equal(new[] { "era1" }, entry.OpenEras(new[] { Era1Pack(), era2Pack }));
    }

    [Fact]
    public void Malformed_entries_are_rejected()
    {
        // illegal — 注册表守卫：空时代列表/缺省档缺席/时长带倒挂/空档梯。
        ProceduralCampaignEntry entry = Load();
        Assert.Throws<ArgumentException>(() => (entry with { Eras = Array.Empty<string>() }).Validate());
        Assert.Throws<ArgumentException>(() => (entry with { DefaultTierId = "missing" }).Validate());
        Assert.Throws<ArgumentException>(
            () => (entry with { DurationBandMinutesMin = 90, DurationBandMinutesMax = 40 }).Validate());
        Assert.Throws<ArgumentException>(() => (entry with { Tiers = Array.Empty<ProceduralTier>() }).Validate());
        Assert.Throws<ArgumentException>(
            () => (entry with { Tiers = new[] { Load().Tiers[0], Load().Tiers[0] with { Slot = 9 } } }).Validate());
    }
}
