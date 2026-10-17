using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-056][F-057][F-008][F-012] E4→E5 跃迁回归（T-134 E5 未来章
// 域——final_levels[4]『引用先行』清账）：真内容行（era_transition
// final_levels["4"]=campaign:level-e4-final + target_era=5 排期三行 +
// era5/era.json 传承策略）驱动真事务，30 对 (遗迹城 seed, 世界 seed) 矩阵复用
// T-074/T-117/T-125 参数化口径——逐对断言 30% 课税守恒、F.7 映射无丢失、新
// 大陆确定性、资源节点数=声明值、E5 排期=e5-01..03（T-134 冻结侧）、仓库
// 4+1=5；正式版面延续：降级废墟 dig-site 实体生成与 RelicMarkerBoard 在 30
// 对上全量一致；G9 指标延续（聚合核心区完好率 ≥95% 带）。
public class E4ToE5TransitionRegressionTests : SqliteSavePortTestBase
{
    private const int SeedPairs = 30;

    private static readonly int[] MatrixCitySeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261500 + i).ToArray();

    private static readonly long[] MatrixWorldSeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261600L + i).ToArray();

    private static ResourceKey Res(string id) => new(id);

    /// <summary>
    /// An E4-era campaign pool: the era4 resources（oil 本代新矿 + coal/iron
    /// 传承 + electronics/concrete 本代制品）——E5 有用途={rare-earth, oil,
    /// iron, nano, fusion-fuel}（era5/era.json），故 electronics/concrete/coal
    /// 走 3:1 折算：期望基金=⌊90/3⌋+⌊135/3⌋+⌊270/3⌋=30+45+90=165（手算冻结
    /// 锚点——coal 的 E5 弹链本代化=无用途旧资源口径）。
    /// </summary>
    private static ResourceStock E4Pool()
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 4);
        _ = pool.Deposit(Res("era4:res-oil"), 1200);
        _ = pool.Deposit(Res("era3:res-coal"), 900);
        _ = pool.Deposit(Res("era2:res-iron"), 600);
        _ = pool.Deposit(Res("era4:res-electronics"), 300);
        _ = pool.Deposit(Res("era4:res-concrete"), 450);
        return pool;
    }

    private static EraTransitionResult RunE4ToE5(
        long worldSeed, int citySeed, ResourceStock pool, LegacyLedger ledger, CountingSavePort port)
    {
        var tech = Tree(4, Transition().FinalLevels[4]);
        var city = CityFacts(citySeed, 30, 9, Mapping().Orientations);
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era5"), "era.json")));
        var request = new EraTransitionRequest(
            worldSeed,
            FromEraIndex: 4,
            pool,
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(worldSeed + 4),
            port,
            EmptyMeta(worldSeed, 4),
            Mapping(),
            Transition(),
            Wonders());
        return new EraTransitionService().Execute(request);
    }

    private static LegacyLedger LedgerWithInternet()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 4).Id); // 互联网=E4→E5 硬前置
        return ledger;
    }

    // ---- legal：30 对 seed 矩阵（T-074/T-117/T-125 参数化复用） ------------

    [Fact] // [F-003][F-056][F-057][F-008][F-012] legal — 矩阵全绿：守恒+无丢失+确定性+挖掘位+标记+G9 带宽+排期/仓库
    public void E4_to_E5_transaction_matrix_over_thirty_seed_pairs()
    {
        var counting = new CountingSavePort(CreatePort());
        var seenMapHashes = new HashSet<long>();
        int coreTotal = 0, coreIntact = 0, digSiteTotal = 0, markerTotal = 0;

        for (int i = 0; i < SeedPairs; i++)
        {
            long worldSeed = MatrixWorldSeeds[i];
            var ledger = LedgerWithInternet();
            EraTransitionResult result = RunE4ToE5(worldSeed, MatrixCitySeeds[i], E4Pool(), ledger, counting);

            // 跃迁形态：4→5、仓库 4+1=5（v3.2 §5 跃迁自动 +1=E5 仓库等级 5）、
            // campaign 域恰一次写
            Assert.Equal(4, result.FromEraIndex);
            Assert.Equal(5, result.ToEraIndex);
            Assert.Equal(5, result.NewPool.WarehouseLevel);

            // 30% 课税守恒（逐行）+带走=⌊0.3×存量⌋（真表 carried_share=0.3）
            Assert.All(result.Levy, line =>
            {
                Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9);
                Assert.Equal(Math.Floor(line.OldAmount * 0.3), (double)line.Carried, 9);
            });
            Assert.Equal(165, result.FundGained); // 手算冻结：coal 270+electronics 90+concrete 135 走 3:1（E5 无用途行）

            // F.7 映射无丢失：四处置分割全体、双台账分账、单格 ≤1
            Assert.Equal(30, result.Mapping.Total);
            Assert.True(result.Mapping.IsNoLoss, $"seed pair #{i}: no-loss violated");

            // E4→E5 排期=e5-01..03（target_era=5 行，逐值对齐 era5/levels.json
            // T-134 冻结排期侧——final_levels[4]『引用先行』状态清账的排期面）
            Assert.Equal(
                new[] { "campaign:level-e5-01", "campaign:level-e5-02", "campaign:level-e5-03" },
                result.Schedule.Select(slot => slot.LevelId).ToArray());

            // D-35（G4）：跨代大陆资源节点=计数旋钮——era5 传承行（oil/iron ×0.5）
            // 不是节点数旋钮，30 对 seed 恒=声明值
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
            IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(
                result.Continent, result.Mapping, result.DigSites);
            Assert.Equal(
                result.Mapping.IntactCount + result.DigSites.Sites.Count * 2
                    + RelicMarkerBoard.ReserveOutline(result.Continent).Count,
                markers.Count);
            markerTotal += markers.Count;

            // G9 指标延续：核心区计数累加（聚合断言在矩阵收口）
            coreTotal += result.Mapping.CoreZoneTotal;
            coreIntact += result.Mapping.CoreZoneIntact;

            // 确定性：同 (worldSeed, citySeed) 重放——地图/映射/挖掘位全等
            var ledger2 = LedgerWithInternet();
            EraTransitionResult replay = RunE4ToE5(worldSeed, MatrixCitySeeds[i], E4Pool(), ledger2, counting);
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
        double aggregateCoreIntact = coreIntact / (double)coreTotal;
        Assert.True(coreTotal >= SeedPairs * 10, $"核心区样本量不足（{coreTotal}）");
        Assert.True(aggregateCoreIntact >= 0.95,
            $"E4→E5 聚合核心区完好率 {aggregateCoreIntact:F3} 低于 95% 带（spike3 推荐参数实测 98.9%）");
    }

    // ---- 硬前置面：final_levels[4]+互联网奇观（清账的 Precheck 侧） ----------

    [Fact] // [F-003] illegal — 缺终局关或缺奇观均硬拒（引用先行清账的负向对照）
    public void Missing_final_level_or_wonder_refuses_the_jump_before_any_mutation()
    {
        // 占领面用 Precheck 谓词口径（RegisterCapture=循环占领入库写侧）：
        // ①终局关 campaign:level-e4-final 未占——即使奇观已建也硬拒且点名 id；
        // ②互联网奇观未建——即使终局关已占也硬拒且点名奇观行（附录 F.3 清单=
        // 跃迁硬条件的两侧各自独立咬合）。
        var counting = new CountingSavePort(CreatePort());

        var techNoFinal = Tree(4, "campaign:level-e4-04"); // 只占非终局关
        EraTransitionRejectedException finalEx = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(new EraTransitionRequest(
                MatrixWorldSeeds[0], 4, E4Pool(), techNoFinal, LedgerWithInternet(),
                CityFacts(11, 20, 9, Mapping().Orientations), Anchor,
                CrossEraResourcePolicy.Parse(
                    File.ReadAllText(Path.Combine(EraDir("era5"), "era.json"))).NewEraResources,
                new SimClock(), new LcgRng(MatrixWorldSeeds[0] + 4), counting,
                EmptyMeta(MatrixWorldSeeds[0], 4), Mapping(), Transition(), Wonders())));
        Assert.Contains("campaign:level-e4-final", finalEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, counting.CampaignWrites); // 拒绝=零写入（拒绝先于一切变更）

        var techWithFinal = Tree(4, Transition().FinalLevels[4]);
        var ledgerNoWonder = new LegacyLedger(Archaeology(), Shop()); // 互联网未建
        EraTransitionRejectedException wonderEx = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(new EraTransitionRequest(
                MatrixWorldSeeds[0], 4, E4Pool(), techWithFinal, ledgerNoWonder,
                CityFacts(11, 20, 9, Mapping().Orientations), Anchor,
                CrossEraResourcePolicy.Parse(
                    File.ReadAllText(Path.Combine(EraDir("era5"), "era.json"))).NewEraResources,
                new SimClock(), new LcgRng(MatrixWorldSeeds[0] + 4), counting,
                EmptyMeta(MatrixWorldSeeds[0], 4), Mapping(), Transition(), Wonders())));
        Assert.Contains("campaign:wonder-internet", wonderEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, counting.CampaignWrites);
    }

    // ---- 对抗大陆 E2E（正式版面链路在 E4→E5 路径上钉死） --------------------

    [Fact] // [F-057][F-168] legal — 全域水域对抗表（RelicMapperTests.hostile 同款口径）
    // 经真 E4→E5 事务：降级废墟全量进挖掘位（占用注册在册）、抽象清单同步继承
    //（双台账口径）、修复位与标记板在事务结果上直接可产。
    public void Hostile_continent_routes_ruins_into_dig_sites_through_the_real_E4_to_E5_transaction()
    {
        var counting = new CountingSavePort(CreatePort());
        var ledger = LedgerWithInternet();
        var tech = Tree(4, Transition().FinalLevels[4]);
        var city = HostileCityFacts(12, Mapping().Orientations);
        var hostile = Mapping() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 1.0,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        };
        var request = new EraTransitionRequest(
            MatrixWorldSeeds[0],
            FromEraIndex: 4,
            E4Pool(),
            tech,
            ledger,
            city,
            Anchor,
            CrossEraResourcePolicy.Parse(
                File.ReadAllText(Path.Combine(EraDir("era5"), "era.json"))).NewEraResources,
            new SimClock(),
            new LcgRng(MatrixWorldSeeds[0] + 4),
            counting,
            EmptyMeta(MatrixWorldSeeds[0], 4),
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

    // ---- 战役壳领养（T-134 清账：跃迁事务 NewPool 与宿主面的合流点） --------

    [Fact] // [F-005][F-003] legal — CampaignState.AdvanceEra(result)：E4→E5 事务结果进 E1~E5 宿主面
    public void CampaignState_adopts_the_E4_to_E5_transition_result()
    {
        // REV-20261017-v10-fullclear §八3 DEBT 收口演示：v1.0 链形态=『E2~E4
        // 战役池实际由跃迁事务 NewPool 携带、壳只认 E1』——清账后壳领养事务
        // 结果（AdvanceEra(result.ToEraIndex, result.NewPool)）：时代 4→5、
        // 池=事务 NewPool（仓库 5）、带走的 30% 存量在壳池内逐资源可读。
        var campaign = new EraCore.CampaignLoop.CampaignState(
            MatrixWorldSeeds[1], new LcgRng(MatrixWorldSeeds[1] + 9), 9, 9);
        for (int era = 2; era <= 4; era++)
        {
            campaign.AdvanceEra(era, new ResourceStock(new SimClock(), warehouseLevel: era)); // E1→E4 步行
        }

        Assert.Equal(4, campaign.EraIndex);
        ResourceStock oldPool = E4Pool();
        var ledger = LedgerWithInternet();
        EraTransitionResult result = RunE4ToE5(
            MatrixWorldSeeds[1], MatrixCitySeeds[1], oldPool, ledger, new CountingSavePort(CreatePort()));

        campaign.AdvanceEra(result); // ← 合流点：壳领养事务结果
        Assert.Equal(5, campaign.EraIndex);
        Assert.Equal(5, campaign.Pool.WarehouseLevel);
        Assert.Same(result.NewPool, campaign.Pool);

        // 带走侧在壳池内逐资源可读（oil/iron=E5 有用途行；coal/electronics/
        // concrete 全额折基金不入池）
        Assert.Equal(Math.Floor(1200 * 0.3), campaign.Pool.AmountOf(Res("era4:res-oil")), 9);
        Assert.Equal(Math.Floor(600 * 0.3), campaign.Pool.AmountOf(Res("era2:res-iron")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era3:res-coal")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era4:res-electronics")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era4:res-concrete")), 9);

        // 领养后存档：SavePoolTo 写当前时代 5（不再硬写 1）
        SqliteSavePort port = CreatePort();
        campaign.SavePoolTo(port);
        Assert.Equal(5, port.ReadCampaign(SaveVersion.Current).EraIndex);
    }

    // ---- G4 裁定口径钉（07 决议 D-35，era5 行） ------------------------------

    [Fact] // [F-008][F-056] legal（D-35/G4）— 节点无类型钉：era5 传承行存在
    //（oil/iron ×0.5）且不进大陆节点层；30 对 seed 节点计数恒=声明值。
    public void Era5_legacy_refresh_stays_out_of_the_continent_node_layer()
    {
        RelicMappingParams mapping = Mapping();
        for (int i = 0; i < SeedPairs; i++)
        {
            NewContinent continent = NewContinentFactory.Create(
                mapping, MatrixWorldSeeds[i], targetEraIndex: 5, new LcgRng(MatrixWorldSeeds[i] + 1));
            Assert.Equal(mapping.ResourceNodeCount, continent.ResourceNodes.Count);
            Assert.All(continent.ResourceNodes, cell => Assert.True(continent.World.Contains(cell)));
        }

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era5"), "era.json")));
        Assert.Equal(2, policy.LegacyRefresh.Count); // 石油+铁=紧邻前代 E4 二资源刷新
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(0.5, row.AbundanceMultiplier, 9));
    }
}
