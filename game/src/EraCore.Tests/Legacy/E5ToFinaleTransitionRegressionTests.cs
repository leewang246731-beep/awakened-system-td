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

// [C-20][F-003][F-056][F-057][F-008][F-012][F-018] E5→终章跃迁回归（T-135
// 终章内容与机制域——第五跳）：真内容行（era_transition final_levels["5"]=
// campaign:level-e5-final + target_era=6 排期三行 + era6/era.json 传承策略）
// 驱动真事务，30 对 (遗迹城 seed, 世界 seed) 矩阵复用 T-074/T-117/T-125/T-134
// 参数化口径——逐对断言 30% 课税守恒、F.7 映射无丢失、新大陆确定性、资源节点
// 数=声明值、终章排期=e6-01..03（T-135 冻结侧）、仓库 5+1=6（池上限
// 2000×6=12000）；正式版面延续：降级废墟 dig-site 实体生成与 RelicMarkerBoard
// 在 30 对上全量一致；G9 指标延续（聚合核心区完好率 ≥95% 带）。EraTransition
// 服务侧时代域已含 5→6（"Only era jumps 1→2 … 5→6 exist"——T-064~T-134 期
// 间即按六代上限装载，本域零引擎改动）。
public class E5ToFinaleTransitionRegressionTests : SqliteSavePortTestBase
{
    private const int SeedPairs = 30;

    private static readonly int[] MatrixCitySeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261700 + i).ToArray();

    private static readonly long[] MatrixWorldSeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261800L + i).ToArray();

    private static ResourceKey Res(string id) => new(id);

    /// <summary>
    /// An E5-era campaign pool: the era5 resources（rare-earth 本代新矿 +
    /// oil/iron 传承 + nano/fusion-fuel 本代制品）——终章有用途={dark-matter,
    /// antimatter, rare-earth, iron}（era6/era.json），故 nano/fusion-fuel/oil
    /// 走 3:1 折算：期望基金=⌊270/3⌋+⌊180/3⌋+⌊360/3⌋=90+60+120=270（手算冻结
    /// 锚点——E5 制品（纳米/聚变燃料）+不再续刷的石油=无用途旧资源口径）。
    /// </summary>
    private static ResourceStock E5Pool()
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 5);
        _ = pool.Deposit(Res("era5:res-rare-earth"), 800);
        _ = pool.Deposit(Res("era5:res-nano"), 900);
        _ = pool.Deposit(Res("era5:res-fusion-fuel"), 600);
        _ = pool.Deposit(Res("era4:res-oil"), 1200);
        _ = pool.Deposit(Res("era2:res-iron"), 400);
        return pool;
    }

    private static EraTransitionResult RunE5ToFinale(
        long worldSeed, int citySeed, ResourceStock pool, LegacyLedger ledger, CountingSavePort port)
    {
        var tech = Tree(5, Transition().FinalLevels[5]);
        var city = CityFacts(citySeed, 30, 9, Mapping().Orientations);
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era6"), "era.json")));
        var request = new EraTransitionRequest(
            worldSeed,
            FromEraIndex: 5,
            pool,
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(worldSeed + 5),
            port,
            EmptyMeta(worldSeed, 5),
            Mapping(),
            Transition(),
            Wonders());
        return new EraTransitionService().Execute(request);
    }

    private static LegacyLedger LedgerWithDysonCloud()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 5).Id); // 戴森云=E5→终章硬前置
        return ledger;
    }

    // ---- legal：30 对 seed 矩阵（T-074/T-117/T-125/T-134 参数化复用） --------

    [Fact] // [F-003][F-056][F-057][F-008][F-012][F-018] legal — 矩阵全绿：守恒+无丢失+确定性+挖掘位+标记+G9 带宽+排期/仓库
    public void E5_to_finale_transaction_matrix_over_thirty_seed_pairs()
    {
        var counting = new CountingSavePort(CreatePort());
        var seenMapHashes = new HashSet<long>();
        int coreTotal = 0, coreIntact = 0, digSiteTotal = 0, markerTotal = 0;

        for (int i = 0; i < SeedPairs; i++)
        {
            long worldSeed = MatrixWorldSeeds[i];
            var ledger = LedgerWithDysonCloud();
            EraTransitionResult result = RunE5ToFinale(worldSeed, MatrixCitySeeds[i], E5Pool(), ledger, counting);

            // 跃迁形态：5→6、仓库 5+1=6（v3.2 §5 跃迁自动 +1=终章仓库等级 6，
            // 池上限=2000×6×(1+金字塔 0.2)=14400——附录 F.3 公式；NewPool 构造面
            // 的金字塔位=PyramidBonusCarriesOver && OldEraWonderBuilt：戴森云
            //（FromEra=5 奇观行）在账=真实六环链形态（E1 金字塔随遗迹城跨代
            // 保留，终章关卡装载的无金字塔基准 12000 见 CampaignState 宿主面
            // 与 e6.json 手工关面）、campaign 域恰一次写
            Assert.Equal(5, result.FromEraIndex);
            Assert.Equal(6, result.ToEraIndex);
            Assert.Equal(6, result.NewPool.WarehouseLevel);
            Assert.Equal(14400.0, result.NewPool.Capacity, 9); // 2000×6×1.2（金字塔随代保留）

            // 30% 课税守恒（逐行）+带走=⌊0.3×存量⌋（真表 carried_share=0.3）
            Assert.All(result.Levy, line =>
            {
                Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9);
                Assert.Equal(Math.Floor(line.OldAmount * 0.3), (double)line.Carried, 9);
            });
            Assert.Equal(270, result.FundGained); // 手算冻结：nano 90+fusion-fuel 60+oil 120 走 3:1（终章无用途行）

            // F.7 映射无丢失：四处置分割全体、双台账分账、单格 ≤1
            Assert.Equal(30, result.Mapping.Total);
            Assert.True(result.Mapping.IsNoLoss, $"seed pair #{i}: no-loss violated");

            // E5→终章排期=e6-01..03（target_era=6 行，逐值对齐 era6/levels.json
            // T-135 冻结排期侧——第五跳的排期面）
            Assert.Equal(
                new[] { "campaign:level-e6-01", "campaign:level-e6-02", "campaign:level-e6-03" },
                result.Schedule.Select(slot => slot.LevelId).ToArray());

            // D-35（G4）：跨代大陆资源节点=计数旋钮——era6 传承行（rare-earth/
            // iron ×0.5）不是节点数旋钮，30 对 seed 恒=声明值
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
            var ledger2 = LedgerWithDysonCloud();
            EraTransitionResult replay = RunE5ToFinale(worldSeed, MatrixCitySeeds[i], E5Pool(), ledger2, counting);
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
            $"E5→终章聚合核心区完好率 {aggregateCoreIntact:F3} 低于 95% 带（spike3 推荐参数实测 98.9%）");
    }

    // ---- 硬前置面：final_levels[5]+戴森云奇观（第五跳的 Precheck 侧） --------

    [Fact] // [F-003] illegal — 缺终局关或缺奇观均硬拒（零写入负向对照）
    public void Missing_final_level_or_wonder_refuses_the_jump_before_any_mutation()
    {
        // 占领面用 Precheck 谓词口径（RegisterCapture=循环占领入库写侧）：
        // ①终局关 campaign:level-e5-final 未占——即使奇观已建也硬拒且点名 id；
        // ②戴森云奇观未建——即使终局关已占也硬拒且点名奇观行（附录 F.3 清单=
        // 跃迁硬条件的两侧各自独立咬合）。
        var counting = new CountingSavePort(CreatePort());

        var techNoFinal = Tree(5, "campaign:level-e6-04"); // 只占非终局关（终章实关行）
        EraTransitionRejectedException finalEx = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(new EraTransitionRequest(
                MatrixWorldSeeds[0], 5, E5Pool(), techNoFinal, LedgerWithDysonCloud(),
                CityFacts(11, 20, 9, Mapping().Orientations), Anchor,
                CrossEraResourcePolicy.Parse(
                    File.ReadAllText(Path.Combine(EraDir("era6"), "era.json"))).NewEraResources,
                new SimClock(), new LcgRng(MatrixWorldSeeds[0] + 5), counting,
                EmptyMeta(MatrixWorldSeeds[0], 5), Mapping(), Transition(), Wonders())));
        Assert.Contains("campaign:level-e5-final", finalEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, counting.CampaignWrites); // 拒绝=零写入（拒绝先于一切变更）

        var techWithFinal = Tree(5, Transition().FinalLevels[5]);
        var ledgerNoWonder = new LegacyLedger(Archaeology(), Shop()); // 戴森云未建
        EraTransitionRejectedException wonderEx = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(new EraTransitionRequest(
                MatrixWorldSeeds[0], 5, E5Pool(), techWithFinal, ledgerNoWonder,
                CityFacts(11, 20, 9, Mapping().Orientations), Anchor,
                CrossEraResourcePolicy.Parse(
                    File.ReadAllText(Path.Combine(EraDir("era6"), "era.json"))).NewEraResources,
                new SimClock(), new LcgRng(MatrixWorldSeeds[0] + 5), counting,
                EmptyMeta(MatrixWorldSeeds[0], 5), Mapping(), Transition(), Wonders())));
        Assert.Contains("campaign:wonder-dyson-cloud", wonderEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, counting.CampaignWrites);
    }

    // ---- 战役壳领养（T-135 宿主面小扩：E5→终章与终章文档恢复的合流点） ------

    [Fact] // [F-005][F-003] legal — CampaignState.AdvanceEra(result)：E5→终章事务结果进 E1~终章宿主面
    public void CampaignState_adopts_the_E5_to_finale_transition_result()
    {
        // T-135 宿主面小扩（CampaignState.MaxHostedEraIndex 5→6）：E1 步行至
        // E5 后领养第五跳事务结果——时代 5→6、池=事务 NewPool（仓库 6、上限
        // 12000）、带走的 30% 存量在壳池内逐资源可读；领养后存档写时代 6、
        // 时代 6 文档可恢复（终章入面的往返闭环）；时代 7 无跳（六时代终点）。
        var campaign = new EraCore.CampaignLoop.CampaignState(
            MatrixWorldSeeds[1], new LcgRng(MatrixWorldSeeds[1] + 9), 9, 9);
        for (int era = 2; era <= 5; era++)
        {
            campaign.AdvanceEra(era, new ResourceStock(new SimClock(), warehouseLevel: era)); // E1→E5 步行
        }

        Assert.Equal(5, campaign.EraIndex);
        ResourceStock oldPool = E5Pool();
        var ledger = LedgerWithDysonCloud();
        EraTransitionResult result = RunE5ToFinale(
            MatrixWorldSeeds[1], MatrixCitySeeds[1], oldPool, ledger, new CountingSavePort(CreatePort()));

        campaign.AdvanceEra(result); // ← 合流点：壳领养事务结果
        Assert.Equal(6, campaign.EraIndex);
        Assert.Equal(6, campaign.Pool.WarehouseLevel);
        Assert.Equal(14400.0, campaign.Pool.Capacity, 9); // 2000×6×1.2（金字塔随代保留——事务 NewPool 构造面）
        Assert.Same(result.NewPool, campaign.Pool);

        // 带走侧在壳池内逐资源可读（rare-earth/iron=终章有用途行；nano/
        // fusion-fuel/oil 全额折基金不入池）
        Assert.Equal(Math.Floor(800 * 0.3), campaign.Pool.AmountOf(Res("era5:res-rare-earth")), 9);
        Assert.Equal(Math.Floor(400 * 0.3), campaign.Pool.AmountOf(Res("era2:res-iron")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era5:res-nano")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era5:res-fusion-fuel")), 9);
        Assert.Equal(0.0, campaign.Pool.AmountOf(Res("era4:res-oil")), 9);

        // 领养后存档：SavePoolTo 写当前时代 6；时代 6 文档可恢复（终章入面往返）
        SqliteSavePort port = CreatePort();
        campaign.SavePoolTo(port);
        ICampaignMeta written = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(6, written.EraIndex);

        var reloaded = new EraCore.CampaignLoop.CampaignState(
            MatrixWorldSeeds[1], new LcgRng(MatrixWorldSeeds[1] + 9), 9, 9);
        Assert.All(reloaded.LoadPoolFrom(port), deposit => Assert.Equal(0.0, deposit.Refused, 9));
        Assert.Equal(6, reloaded.EraIndex);
        Assert.Equal(6, reloaded.Pool.WarehouseLevel);

        // 六时代终点：时代 7 无跳（AdvanceEra 只走一步且 ≤6——终章封顶）
        Assert.Throws<ArgumentOutOfRangeException>(
            () => campaign.AdvanceEra(7, new ResourceStock(new SimClock(), warehouseLevel: 7)));
    }

    // ---- G4 裁定口径钉（07 决议 D-35，era6 行） ------------------------------

    [Fact] // [F-008][F-056] legal（D-35/G4）— 节点无类型钉：era6 传承行存在
    //（rare-earth/iron ×0.5）且不进大陆节点层；30 对 seed 节点计数恒=声明值。
    public void Era6_legacy_refresh_stays_out_of_the_continent_node_layer()
    {
        RelicMappingParams mapping = Mapping();
        for (int i = 0; i < SeedPairs; i++)
        {
            NewContinent continent = NewContinentFactory.Create(
                mapping, MatrixWorldSeeds[i], targetEraIndex: 6, new LcgRng(MatrixWorldSeeds[i] + 1));
            Assert.Equal(mapping.ResourceNodeCount, continent.ResourceNodes.Count);
            Assert.All(continent.ResourceNodes, cell => Assert.True(continent.World.Contains(cell)));
        }

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era6"), "era.json")));
        Assert.Equal(2, policy.LegacyRefresh.Count); // 稀土+铁=紧邻前代 E5 二资源刷新
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(0.5, row.AbundanceMultiplier, 9));
    }
}
