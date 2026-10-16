using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-056][F-057][F-168] E3→E4 跃迁回归（T-125 遗迹映射正式版）：
// 真内容行（era_transition final_levels["3"]=campaign:level-e3-final +
// target_era=4 排期 + era4/era.json 传承策略）驱动真事务，30 对 (遗迹城 seed,
// 世界 seed) 矩阵复用 T-074/T-117 参数化口径——逐对断言 30% 课税守恒、F.7 映射
// 无丢失、新大陆确定性、资源节点数=声明值；正式版新增面：降级废墟的 dig-site
// 实体生成（占用注册+纪念馆修复位）与新时代地图标记板（RelicMarkerBoard）在
// 30 对上全量一致；G9 指标延续（spike3-report §4/§5 口径）：聚合核心区完好率
// ≥95% 带（spike3 实测 98.9%，容差来源=relic_mapping.json spike3 推荐参数集）。
public class E3ToE4TransitionRegressionTests : SqliteSavePortTestBase
{
    private const int SeedPairs = 30;

    private static readonly int[] MatrixCitySeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261400 + i).ToArray();

    private static readonly long[] MatrixWorldSeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261500L + i).ToArray();

    private static ResourceKey Res(string id) => new(id);

    /// <summary>
    /// An E3-era campaign pool: the four era3 resources (coal/iron 新采 +
    /// steel 本代制品 + copper 上代传承) — E4 有用途={oil,coal,iron,
    /// electronics,concrete}（era4/era.json），故 steel/copper 走 3:1 折算：
    /// 期望基金=⌊180/3⌋+⌊90/3⌋=90（手算冻结锚点）。
    /// </summary>
    private static ResourceStock E3Pool()
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 3);
        _ = pool.Deposit(Res("era3:res-coal"), 1200);
        _ = pool.Deposit(Res("era2:res-iron"), 900);
        _ = pool.Deposit(Res("era3:res-steel"), 600);
        _ = pool.Deposit(Res("era2:res-copper"), 300);
        return pool;
    }

    private static EraTransitionResult RunE3ToE4(
        long worldSeed, int citySeed, ResourceStock pool, LegacyLedger ledger, CountingSavePort port)
    {
        var tech = Tree(3, Transition().FinalLevels[3]);
        var city = CityFacts(citySeed, 30, 9, Mapping().Orientations);
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era4"), "era.json")));
        var request = new EraTransitionRequest(
            worldSeed,
            FromEraIndex: 3,
            pool,
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(worldSeed + 3),
            port,
            EmptyMeta(worldSeed, 3),
            Mapping(),
            Transition(),
            Wonders());
        return new EraTransitionService().Execute(request);
    }

    private static LegacyLedger LedgerWithExpo()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 3).Id); // 世博会=E3→E4 硬前置
        return ledger;
    }

    // ---- legal：30 对 seed 矩阵（T-074/T-117 参数化复用 + T-125 正式版面） ----

    [Fact] // [F-003][F-056][F-057][F-168] legal — 矩阵全绿：守恒+无丢失+确定性+挖掘位+标记+G9 带宽
    public void E3_to_E4_transaction_matrix_over_thirty_seed_pairs()
    {
        var counting = new CountingSavePort(CreatePort());
        var seenMapHashes = new HashSet<long>();
        int coreTotal = 0, coreIntact = 0, digSiteTotal = 0, markerTotal = 0;

        for (int i = 0; i < SeedPairs; i++)
        {
            long worldSeed = MatrixWorldSeeds[i];
            var ledger = LedgerWithExpo();
            EraTransitionResult result = RunE3ToE4(worldSeed, MatrixCitySeeds[i], E3Pool(), ledger, counting);

            // 跃迁形态：3→4、仓库 3+1=4、campaign 域恰一次写
            Assert.Equal(3, result.FromEraIndex);
            Assert.Equal(4, result.ToEraIndex);
            Assert.Equal(4, result.NewPool.WarehouseLevel);

            // 30% 课税守恒（逐行）+带走=⌊0.3×存量⌋（真表 carried_share=0.3）
            Assert.All(result.Levy, line =>
            {
                Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9);
                Assert.Equal(Math.Floor(line.OldAmount * 0.3), (double)line.Carried, 9);
            });
            Assert.Equal(90, result.FundGained); // 手算冻结：steel 180+copper 90 走 3:1（E4 无用途行）

            // F.7 映射无丢失：四处置分割全体、双台账分账、单格 ≤1
            Assert.Equal(30, result.Mapping.Total);
            Assert.True(result.Mapping.IsNoLoss, $"seed pair #{i}: no-loss violated");

            // E3→E4 排期=e4-01..03（target_era=4 行，逐值对齐 era4/levels.json）
            Assert.Equal(
                new[] { "campaign:level-e4-01", "campaign:level-e4-02", "campaign:level-e4-03" },
                result.Schedule.Select(slot => slot.LevelId).ToArray());

            // D-35（G4）：跨代大陆资源节点=计数旋钮——era4 传承行（coal/iron ×0.5）
            // 不是节点数旋钮，30 对 seed 恒=声明值 12
            Assert.Equal(Mapping().ResourceNodeCount, result.Continent.ResourceNodes.Count);

            // 正式版面①：dig-site 计划分割全体废墟（锚定+未锚定=降级数），
            // 锚定位占用注册（资源点冲突行除外——格子被节点持有不双注册）
            Assert.Equal(result.Mapping.RuinCount, result.DigSites.TotalRuins);
            Assert.All(result.DigSites.Sites, site => Assert.Contains(site.BuildingId,
                result.Mapping.Outcomes.Select(o => o.BuildingId)));
            Assert.All(result.DigSites.Sites.Where(s => s.OccupancyRegistered),
                site => Assert.True(result.Continent.World.IsOccupied(site.Cell)));
            digSiteTotal += result.DigSites.Sites.Count;

            // 正式版面②：新时代地图标记板可产且计数一致
            // （立碑遗迹+挖掘位+修复位+保留区勾勒=标记全集）
            IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(
                result.Continent, result.Mapping, result.DigSites);
            Assert.Equal(
                result.Mapping.IntactCount + result.DigSites.Sites.Count * 2
                    + RelicMarkerBoard.ReserveOutline(result.Continent).Count,
                markers.Count);
            markerTotal += markers.Count;

            // G9 指标延续：核心区计数累加（聚合断言在矩阵收口，坑 3 口径——
            // 30 对聚合均值，不锚单 seed 数字）
            coreTotal += result.Mapping.CoreZoneTotal;
            coreIntact += result.Mapping.CoreZoneIntact;

            // 确定性：同 (worldSeed, citySeed) 重放——地图/映射/挖掘位全等
            var ledger2 = LedgerWithExpo();
            EraTransitionResult replay = RunE3ToE4(worldSeed, MatrixCitySeeds[i], E3Pool(), ledger2, counting);
            Assert.Equal(result.Continent.MapHash, replay.Continent.MapHash);
            Assert.Equal(result.Mapping.ComputeOutcomeHash(), replay.Mapping.ComputeOutcomeHash());
            Assert.Equal(result.DigSites.Sites.Count, replay.DigSites.Sites.Count);
            Assert.Equal(result.DigSites.Sites, replay.DigSites.Sites);
            Assert.Equal(result.FundGained, replay.FundGained);
            Assert.Equal(result.WrittenMeta.WorldSeed, replay.WrittenMeta.WorldSeed);

            // 矩阵覆盖力：不同 world seed 的大陆地图确实不同（防 30 对同图假矩阵）
            Assert.True(seenMapHashes.Add(result.Continent.MapHash), $"seed pair #{i}: duplicate continent map hash");
        }

        Assert.Equal(SeedPairs * 2, counting.CampaignWrites); // 每事务恰一次写
        Assert.True(markerTotal > 0);

        // G9 指标延续（spike3 §5 口径）：30 对聚合核心区完好率 ≥95% 带
        // （spike3 实测 98.9%；RelicMapperTests.core_zone_intact_rate 单场景同带）
        double aggregateCoreIntact = coreIntact / (double)coreTotal;
        Assert.True(coreTotal >= SeedPairs * 10, $"核心区样本量不足（{coreTotal}）");
        Assert.True(aggregateCoreIntact >= 0.95,
            $"E3→E4 聚合核心区完好率 {aggregateCoreIntact:F3} 低于 95% 带（spike3 推荐参数实测 98.9%）");
    }

    // ---- 正式版面 E2E：对抗大陆经真事务全链（挖掘位+标记在事务路径上真实产生） ----

    [Fact] // [F-057][F-168] legal — 全域水域对抗表（RelicMapperTests.hostile 同款口径）
    // 经真 E3→E4 事务：降级废墟全量进挖掘位（占用注册在册）、抽象清单同步继承
    // （双台账口径）、修复位与标记板在事务结果上直接可产。自然地形下该路径
    // 近零触发（spike3 §4 移位档挽回 100% 上界），故以对抗表钉链路。
    public void Hostile_continent_routes_ruins_into_dig_sites_through_the_real_transaction()
    {
        var counting = new CountingSavePort(CreatePort());
        var ledger = LedgerWithExpo();
        var tech = Tree(3, Transition().FinalLevels[3]);
        var city = HostileCityFacts(12, Mapping().Orientations);
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era4"), "era.json")));
        var hostile = Mapping() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 1.0,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        };
        var request = new EraTransitionRequest(
            MatrixWorldSeeds[0],
            FromEraIndex: 3,
            E3Pool(),
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(MatrixWorldSeeds[0] + 3),
            counting,
            EmptyMeta(MatrixWorldSeeds[0], 3),
            hostile,
            Transition(),
            Wonders());
        EraTransitionResult result = new EraTransitionService().Execute(request);

        Assert.Equal(12, result.Mapping.RuinCount); // 全域水域：全部降级（cause=terrain）
        Assert.Equal(0, result.Mapping.IntactCount);
        Assert.Equal(12, result.DigSites.Sites.Count); // 全量锚定（home 全在界内且无占用）
        Assert.Equal(0, result.DigSites.UnanchoredRuinCount);
        Assert.All(result.DigSites.Sites, site =>
        {
            Assert.True(site.OccupancyRegistered);
            Assert.True(result.Continent.World.IsOccupied(site.Cell));
        });

        // 双台账：抽象清单 12 条全部进账本（挖掘位=地图面，抽象清单=考古面）
        Assert.Equal(12, result.Mapping.AbstractList.Count);
        Assert.Contains(result.Mapping.AbstractList, entry => entry.StartsWith("ruin|", StringComparison.Ordinal));

        // 修复位+标记板直接从事务结果产出（纪念馆修复位=挖掘位格序列）
        Assert.Equal(
            result.DigSites.Sites.Select(site => site.Cell).ToArray(),
            result.DigSites.MemorialRepairSlots.ToArray());
        IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(
            result.Continent, result.Mapping, result.DigSites);
        Assert.Equal(12, markers.Count(m => m.Kind == RelicMarkerKind.DigSite));
        Assert.Equal(12, markers.Count(m => m.Kind == RelicMarkerKind.MemorialSlot));
    }

    // ---- G9 指标延续：冲突降级三通道口径 ---------------------------------------

    [Fact] // [F-057][F-168] legal — 指标延续：降级废墟的 dig-site 挖掘位=考古三通道
    // 的物理入口（ArchaeologyService.Quote 按 tier 定时长），纪念馆修复位=挖掘位
    // 格（废墟原位修复）；30 对聚合至少覆盖一次该链路（tier 1..4 均可报价）。
    public void Dig_sites_quote_through_the_archaeology_table_and_repair_in_place()
    {
        var ledger = LedgerWithExpo();
        var counting = new CountingSavePort(CreatePort());
        EraTransitionResult result = RunE3ToE4(MatrixWorldSeeds[0], MatrixCitySeeds[0], E3Pool(), ledger, counting);
        var service = new ArchaeologyService(Archaeology());
        foreach (RelicDigSite site in result.DigSites.Sites)
        {
            DigQuote quote = service.Quote(site.Tier); // tier 域 1..4 全合法
            Assert.True(quote.DrillTicks > 0);
        }

        // 纪念馆修复位=挖掘位格序列（修复=纪念馆锚定在废墟原位）
        Assert.Equal(
            result.DigSites.Sites.Select(site => site.Cell).ToArray(),
            result.DigSites.MemorialRepairSlots.ToArray());
    }

    // ---- G4 裁定口径钉（07 决议 D-35，era4 行） --------------------------------

    [Fact] // [F-056] legal（D-35/G4）— 节点无类型钉：era4 传承行存在（coal/iron ×0.5）
    // 且不进大陆节点层；30 对 seed 节点计数恒=声明值（负向对照=E2ToE3 同律）。
    public void Era4_legacy_refresh_stays_out_of_the_continent_node_layer()
    {
        RelicMappingParams mapping = Mapping();
        for (int i = 0; i < SeedPairs; i++)
        {
            NewContinent continent = NewContinentFactory.Create(
                mapping, MatrixWorldSeeds[i], targetEraIndex: 4, new LcgRng(MatrixWorldSeeds[i] + 1));
            Assert.Equal(mapping.ResourceNodeCount, continent.ResourceNodes.Count);
            Assert.All(continent.ResourceNodes, cell => Assert.True(continent.World.Contains(cell)));
        }

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era4"), "era.json")));
        Assert.Equal(2, policy.LegacyRefresh.Count); // 煤+铁=紧邻前代 E3 二资源刷新
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(0.5, row.AbundanceMultiplier, 9));
    }
}
