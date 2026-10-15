using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-056][F-060] E2→E3 跃迁回归（T-117）：真内容行（era_transition
// final_levels["2"]+target_era=3 排期）驱动真事务，30 对 (遗迹城 seed, 世界 seed)
// 矩阵复用 T-074 参数化口径——逐对断言 30% 课税守恒、F.7 映射无丢失、新大陆
// 确定性、资源节点数=声明值（D-35 G4：节点无类型、计数不是丰度旋钮）；端到端
// 世博会链：跃迁成功后建成博览会 → 全局物流乘数=1.25（吞吐对比前恰 1.25×）；
// G5 口径钉：E2 石料刷新语料物化 = E1 教学关基准 × 声明乘数（0×0.5=0）。
public class E2ToE3TransitionRegressionTests : SqliteSavePortTestBase
{
    private const int SeedPairs = 30;

    private static readonly int[] MatrixCitySeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261200 + i).ToArray();

    private static readonly long[] MatrixWorldSeeds =
        Enumerable.Range(0, SeedPairs).Select(i => 20261300L + i).ToArray();

    private static ResourceKey Res(string id) => new(id);

    /// <summary>An E2-era campaign pool mirroring the T-115 audit matrix rows (Σ折算=243 的冻结口径).</summary>
    private static ResourceStock E2Pool()
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 2);
        _ = pool.Deposit(Res("era1:res-wood"), 600);
        _ = pool.Deposit(Res("era1:res-stone"), 300);
        _ = pool.Deposit(Res("era2:res-grain"), 900);
        _ = pool.Deposit(Res("era2:res-horse"), 150);
        _ = pool.Deposit(Res("era2:res-copper"), 500);
        _ = pool.Deposit(Res("era2:res-iron"), 800);
        _ = pool.Deposit(Res("era2:res-copper-ingot"), 210);
        _ = pool.Deposit(Res("era2:res-iron-ingot"), 90);
        _ = pool.Deposit(Res("era2:ammo-arrow"), 120);
        _ = pool.Deposit(Res("era2:ammo-iron-bolt"), 60);
        return pool;
    }

    private static EraTransitionResult RunE2ToE3(
        long worldSeed, int citySeed, ResourceStock pool, LegacyLedger ledger, CountingSavePort port)
    {
        var tech = Tree(2, Transition().FinalLevels[2]);
        var city = CityFacts(citySeed, 30, 9, Mapping().Orientations);
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era3"), "era.json")));
        var request = new EraTransitionRequest(
            worldSeed,
            FromEraIndex: 2,
            pool,
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(worldSeed + 3),
            port,
            EmptyMeta(worldSeed, 2),
            Mapping(),
            Transition(),
            Wonders());
        return new EraTransitionService().Execute(request);
    }

    private static LegacyLedger LedgerWithGreatLibrary()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 2).Id); // 大图书馆=E2→E3 硬前置
        return ledger;
    }

    // ---- legal：30 对 seed 矩阵（T-074 参数化复用） --------------------------

    [Fact] // [F-003][F-056] legal — 矩阵全绿：守恒+无丢失+确定性+节点数+排期，30 对逐对断言
    public void E2_to_E3_transaction_matrix_over_thirty_seed_pairs()
    {
        var counting = new CountingSavePort(CreatePort());
        var seenMapHashes = new HashSet<long>();

        for (int i = 0; i < SeedPairs; i++)
        {
            long worldSeed = MatrixWorldSeeds[i];
            var ledger = LedgerWithGreatLibrary();
            EraTransitionResult result = RunE2ToE3(worldSeed, MatrixCitySeeds[i], E2Pool(), ledger, counting);

            // 跃迁形态：2→3、仓库 2+1=3、campaign 域恰一次写
            Assert.Equal(2, result.FromEraIndex);
            Assert.Equal(3, result.ToEraIndex);
            Assert.Equal(3, result.NewPool.WarehouseLevel);

            // 30% 课税守恒（逐行）+带走=⌊0.3×存量⌋（真表 carried_share=0.3）
            Assert.All(result.Levy, line =>
            {
                Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9);
                Assert.Equal(Math.Floor(line.OldAmount * 0.3), (double)line.Carried, 9);
            });
            Assert.Equal(243, result.FundGained); // T-115 冻结：八无用途行 Σ折算=243

            // F.7 映射无丢失：四处置分割全体、双台账分账、单格 ≤1
            Assert.Equal(30, result.Mapping.Total);
            Assert.True(result.Mapping.IsNoLoss, $"seed pair #{i}: no-loss violated");

            // E2→E3 排期=e3-01..03（target_era=3 行）
            Assert.Equal(
                new[] { "campaign:level-e3-01", "campaign:level-e3-02", "campaign:level-e3-03" },
                result.Schedule.Select(slot => slot.LevelId).ToArray());

            // D-35（G4）：跨代大陆资源节点=无类型计数旋钮——era3 传承丰度 ×0.5
            // 不是节点数旋钮，30 对 seed 恒=声明值 12（丰度物化在关卡 map 层）
            Assert.Equal(Mapping().ResourceNodeCount, result.Continent.ResourceNodes.Count);

            // 确定性：同 (worldSeed, citySeed) 重放——地图/映射/基金全等
            var ledger2 = LedgerWithGreatLibrary();
            EraTransitionResult replay = RunE2ToE3(worldSeed, MatrixCitySeeds[i], E2Pool(), ledger2, counting);
            Assert.Equal(result.Continent.MapHash, replay.Continent.MapHash);
            Assert.Equal(result.Mapping.ComputeOutcomeHash(), replay.Mapping.ComputeOutcomeHash());
            Assert.Equal(result.FundGained, replay.FundGained);
            Assert.Equal(result.WrittenMeta.WorldSeed, replay.WrittenMeta.WorldSeed);

            // 矩阵覆盖力：不同 world seed 的大陆地图确实不同（防 30 对同图假矩阵）
            Assert.True(seenMapHashes.Add(result.Continent.MapHash), $"seed pair #{i}: duplicate continent map hash");
        }

        Assert.Equal(SeedPairs * 2, counting.CampaignWrites); // 每事务恰一次写
    }

    [Fact] // [F-060] legal — 端到端：E2→E3 跃迁成功后建成世博会 → 全局吞吐恰 +25%
    public void Expo_built_after_the_E2_to_E3_transition_boosts_throughput_by_exactly_25_percent()
    {
        var ledger = LedgerWithGreatLibrary();
        var counting = new CountingSavePort(CreatePort());
        EraTransitionResult result = RunE2ToE3(MatrixWorldSeeds[0], MatrixCitySeeds[0], E2Pool(), ledger, counting);
        Assert.Equal(3, result.ToEraIndex); // 跃迁先成功（真 final_levels["2"] 行放行）

        // 跃迁前（E2 期）无博览会：乘数=1
        var beforeBoard = new WonderThroughputBoard(Wonders(), ledger);
        Assert.Equal(1.0, beforeBoard.Multiplier, 12);
        double porterBefore = TransportTiers.Porter.ThroughputItemsPerSecondAt(
            TransportTiers.ReferenceOneWayPx, beforeBoard.Multiplier);

        // E3 期建成世界博览会（附录 F.3：物流吞吐 +25%）——同一账本，跨跃迁连续
        WonderSpec expo = Wonders().Single(w => w.BonusKind == WonderThroughputBoard.ThroughputBonusKind);
        ledger.MarkWonderBuilt(expo.Id);
        var afterBoard = new WonderThroughputBoard(Wonders(), ledger);
        Assert.Equal(1.25, afterBoard.Multiplier, 12);

        // 吞吐对比前=1.25×（手算对照：民夫档 0.3→0.375 件/s @160px）
        double porterAfter = TransportTiers.Porter.ThroughputItemsPerSecondAt(
            TransportTiers.ReferenceOneWayPx, afterBoard.Multiplier);
        Assert.Equal(porterBefore * 1.25, porterAfter, 12);
        Assert.Equal(0.3 * 1.25, porterAfter, 9); // 手算锚点：0.375 件/s
        Assert.True(ledger.IsWonderBuilt(expo.Id)); // 账本侧建成状态持久（跨存档续读）
    }

    // ---- G4/G5 裁定口径钉（07 决议 D-35） -------------------------------------

    [Fact] // [F-011] legal（D-35/G5）— 石料刷新物化律：E2 语料 rock 恰 = E1 教学关基准
    // rock × era2 声明乘数（0×0.5=0）——与 wood 行同律（forest 0.05=0.10×0.5，
    // T-115 审计表 3 同式）；声明与语料在该律下一致，era2.json 数值维持不改。
    public void E2_stone_refresh_materializes_the_teaching_baseline_times_the_declared_multiplier()
    {
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era2"), "era.json")));
        double stoneMult = policy.AbundanceOf(Res("era1:res-stone"));
        double woodMult = policy.AbundanceOf(Res("era1:res-wood"));
        Assert.Equal(0.5, stoneMult, 9); // 声明侧：石料刷新 ×0.5（era2.json 冻结行）
        Assert.Equal(0.5, woodMult, 9);

        JsonElement e1Teaching = LevelRows("levels", "e1.json")[0]; // e1-01=教学关基准（审计表 3 口径）
        double baseRock = e1Teaching.GetProperty("map").GetProperty("rock_ratio").GetDouble();
        double baseForest = e1Teaching.GetProperty("map").GetProperty("forest_ratio").GetDouble();

        foreach (JsonElement level in LevelRows("levels", "e2.json"))
        {
            string id = level.GetProperty("id").GetString()!;
            double rock = level.GetProperty("map").GetProperty("rock_ratio").GetDouble();
            double forest = level.GetProperty("map").GetProperty("forest_ratio").GetDouble();
            Assert.Equal(baseRock * stoneMult, rock, 9); // 0×0.5=0：石料行一致（非虚设——通道级生效）
            Assert.Equal(baseForest * woodMult, forest, 9); // 0.10×0.5=0.05：木材行同律对照
            Assert.Equal(0.0, rock, 9); // 现值显式钉零：e1 语料无岩基准 → 物化为零
        }
    }

    [Fact] // [F-056] legal（D-35/G4）— 节点无类型钉：大陆资源节点=纯格子列表（无资源 id），
    // 计数恒=声明值且与 era3 传承乘数无关——丰度走 CrossEraResourcePolicy.AbundanceOf
    // 全局通道（关卡 map.ratio 层物化），本层不引入类型 schema。
    public void Continent_resource_nodes_stay_untyped_and_count_pinned_across_seeds()
    {
        RelicMappingParams mapping = Mapping();
        for (int i = 0; i < SeedPairs; i++)
        {
            NewContinent continent = NewContinentFactory.Create(
                mapping, MatrixWorldSeeds[i], targetEraIndex: 3, new LcgRng(MatrixWorldSeeds[i] + 1));
            Assert.Equal(mapping.ResourceNodeCount, continent.ResourceNodes.Count);
            Assert.All(continent.ResourceNodes, cell => Assert.True(continent.World.Contains(cell)));
        }

        // era3 传承行存在且 ×0.5——但节点计数不消费它（G4 裁定的负向对照）
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era3"), "era.json")));
        Assert.Equal(2, policy.LegacyRefresh.Count);
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(0.5, row.AbundanceMultiplier, 9));
    }

    private static JsonElement[] LevelRows(string relativeUnderContent, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", relativeUnderContent);
            if (Directory.Exists(probe))
            {
                using JsonDocument doc = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(probe, fileName)));
                return doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(row => row.Clone()) // 脱离文档生命周期（Dispose 后可读）
                    .ToArray();
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{relativeUnderContent}");
    }
}
