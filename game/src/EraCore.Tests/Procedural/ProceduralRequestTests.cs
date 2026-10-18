using EraCore.Procedural;
using Xunit;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Procedural;

// [C-19][F-007] T-140 请求面守卫：难度档/内容包/约束集/整请求的
// legal/boundary/illegal 三行覆盖（装载即校验——内容缺陷在生成前红）。
public class ProceduralRequestTests
{
    private static ProceduralTier Tier(int camps = 0) => Load().Tiers[0] with { AdjacentEnemyCamps = camps };

    private static ProceduralConstraints Constraints() => Load().Constraints;

    // ---- 档位威胁标定（§7 公式对齐） ---------------------------------------

    [Theory]
    [InlineData(0, 0.3)]
    [InlineData(1, 0.6)]
    [InlineData(2, 0.9)]
    public void Tier_threat_follows_the_frozen_section7_camp_formula(int camps, double expected)
    {
        // legal — 威胁度标定：档位威胁=clamp(0.3+0.3×营数)（§7 与手工关同公式，
        // 0.9=终局带对齐 e1-final）。
        ProceduralTier tier = Tier(camps);
        Assert.Equal(expected, tier.Threat, 5);
        Assert.Equal(EraCore.Balance.Threat.ClampThreat(camps), tier.Threat, 5);
        Assert.Equal(EraCore.Balance.Campaign.WinWaves(tier.Threat), tier.WinWaves);
    }

    [Fact]
    public void Registry_tiers_cover_regular_and_endgame_bands()
    {
        // legal — 注册表档位带：三档威胁 0.3/0.6/0.9（常规带+终局带），
        // 胜波 13/16/19=§7 公式双面，终局档带 Boss 行。
        ProceduralCampaignEntry entry = Load();
        double[] threats = entry.Tiers.Select(t => t.Threat).ToArray();
        Assert.Equal(3, threats.Length);
        Assert.Equal(0.3, threats[0], 5);
        Assert.Equal(0.6, threats[1], 5);
        Assert.Equal(0.9, threats[2], 5);
        Assert.Equal(new[] { 13, 16, 19 }, entry.Tiers.Select(t => t.WinWaves));
        Assert.True(entry.Tiers[2].BossLineEnabled);
        Assert.Equal("era1:enemy-boss", entry.Tiers[2].BossEnemyId);
        Assert.All(entry.Tiers.Take(2), t => Assert.False(t.BossLineEnabled));
    }

    [Fact]
    public void Tier_rejects_camp_counts_past_the_endgame_band()
    {
        // illegal — 营数=3（威胁 1.2）离开 0.3~0.9 标定带（时长带结构性失守）
        // ——拒绝生成（无尽域不是程序关卡域）。
        Assert.Throws<ArgumentException>(() => Tier(3).Validate());
        Assert.Throws<ArgumentException>(() => Tier(-1).Validate());
    }

    [Fact]
    public void Tier_rejects_malformed_wave_and_plan_rows()
    {
        // illegal — 波表/建造计划负值行（装载即红，不静默降级）。
        Assert.Throws<ArgumentException>(() => (Tier() with { WaveBaseCount = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { WaveSpacing = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { WaveScaling = 0.05 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { ValleySeconds = 999.0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { ArrowTowerCount = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { WaveOneTowerCount = 99 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tier() with { StockMultiplier = 0.0 }).Validate());
    }

    [Fact]
    public void Tier_rejects_bossless_or_double_boss_rows()
    {
        // illegal — Boss 行成对性（enabled 与 id 必须同在/同缺）。
        Assert.Throws<ArgumentException>(
            () => (Tier(2) with { BossLineEnabled = true, BossEnemyId = null }).Validate());
        Assert.Throws<ArgumentException>(
            () => (Tier() with { BossLineEnabled = false, BossEnemyId = "era1:enemy-boss" }).Validate());
    }

    [Fact]
    public void Tier_rejects_empty_or_duplicate_objective_pools()
    {
        // illegal — 三重目标池空行/重复行（装载面 objectives ≥1 且去重）。
        Assert.Throws<ArgumentException>(
            () => (Tier() with { StrategyObjectives = Array.Empty<string>() }).Validate());
        Assert.Throws<ArgumentException>(
            () => (Tier() with { ChallengeObjectives = new[] { "campaign:obj-c8", "campaign:obj-c8" } }).Validate());
    }

    // ---- 约束集 ------------------------------------------------------------

    [Fact]
    public void Registry_constraints_are_the_loader_compatible_band()
    {
        // legal — 注册表约束带=装载兼容口径（water/rock=0——手工装载面
        // E1 行硬性要求，T-140 生成产物走既有装载管线不变）+森林比带。
        ProceduralConstraints c = Constraints();
        Assert.Equal(0.0, c.RockRatioMin);
        Assert.Equal(0.0, c.RockRatioMax);
        Assert.Equal(0.0, c.WaterRatioMin);
        Assert.Equal(0.0, c.WaterRatioMax);
        Assert.InRange(c.ForestRatioMin, 0.0, 1.0);
        Assert.True(c.ForestRatioMin < c.ForestRatioMax);
        Assert.InRange(c.GraceMin, 1.5, 2.0);
        Assert.InRange(c.GraceMax, 1.5, 2.0);
    }

    [Fact]
    public void Constraints_reject_degenerate_bands()
    {
        // illegal — 退化带（min>max/越 [0,1]/图面 <3×3/入口 <1/宽限越 §6 带）。
        Assert.Throws<ArgumentException>(() => (Constraints() with { WidthMin = 40, WidthMax = 16 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { HeightMin = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { ForestRatioMin = -0.1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { WaterRatioMax = 1.1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { EntryCountMin = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { GraceMin = 1.2 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { GraceMax = 2.5 }).Validate());
        Assert.Throws<ArgumentException>(() => (Constraints() with { MaxRerolls = -1 }).Validate());
    }

    // ---- 内容包与整请求 -----------------------------------------------------

    [Fact]
    public void Era_pack_resolves_tower_costs_from_the_content_tables()
    {
        // legal — 内容包：塔价查表（RL7——初始资源 sizing 出自 towers.json
        // cost_total，产品码零硬编码）。
        ProceduralEraContent pack = Era1Pack();
        Assert.Equal(30.0, pack.TowerCost("era1:tower-wood-arrow"));
        Assert.Equal(60.0, pack.TowerCost("era1:tower-rockfall"));
        Assert.Null(pack.TowerCost("era1:tower-no-such-row"));
        Assert.Equal(1, pack.WarehouseLevel); // era.json 仓库等级（宿主接线面）
        Assert.Equal("porter", pack.CarrierTier);
        Assert.All(pack.RegularRoster, id => Assert.Equal(id, Era1Enemies().Get(id).Id));
    }

    [Fact]
    public void Request_rejects_unresolved_towers_and_boss_roster_collisions()
    {
        // illegal — 塔线缺席内容表 / Boss 撞常规名册（一线一种）。
        ProceduralTier tier = Tier();
        Assert.Throws<ArgumentException>(() =>
            new ProceduralRequest(
                1, tier with { ArrowTowerId = "era1:tower-missing" }, Era1Pack(), Constraints()).Validate());
        Assert.Throws<ArgumentException>(() =>
            new ProceduralRequest(
                1, tier with { BossLineEnabled = true, BossEnemyId = "era1:enemy-beastling" },
                Era1Pack(), Constraints()).Validate());
    }
}
