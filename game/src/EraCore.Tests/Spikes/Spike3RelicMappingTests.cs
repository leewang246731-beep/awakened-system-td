using System.Globalization;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Tests.World;
using EraCore.World;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Spikes;

/// <summary>
/// T-058 spike③ (F-057/F-168): 遗迹映射原型验证——E1 遗迹城跃迁快照 → E2 新大陆
/// 锚点回放 → 冲突降级（废墟物品）→ 密度封顶（单格 ≤1）。三组验收：
/// <list type="bullet">
/// <item><b>往返无丢失</b>：每建筑 either 回放（原位/移位）/降级/封顶跳过，三种
/// 处置计数和=建筑总数，且双台账（遗迹注册表+抽象清单）恰好分割全体——
/// 快照字节经 <see cref="ILevelLayoutSnapshot"/> 形态（含 CampaignMetaDto JSON
/// 双域存档链路）往返后无一件静默丢失；</item>
/// <item><b>冲突率量化</b>：30 对 seed × 建筑密度 {60,90,120} × 地形粗糙度
/// {0.5,1.0,1.5}×基准比例 = 270 次跃迁的完好率/移位率/降级率矩阵+线性拟合；</item>
/// <item><b>A5 核心区口径</b>：核心区（切比雪夫半径 10 ∩ 已建区外扩 2，显式快照
/// 持久化）内 vs 区外建筑完好率对比，与新大陆"地形让位"约束开/关的对照。</item>
/// </list>
/// 全部确定性（seed 全钉死，同 seed 必复现，CS-1.4 经 IRng 端口注入）。
/// </summary>
// [F-057] [F-168] spike③ 遗迹映射原型（T-058；C-20 前体）
public sealed class Spike3RelicMappingTests
{
    private const long DemoE1Seed = 20261013;

    private const long DemoE2Seed = 20261014;

    private const int DemoBuildingCount = 90;

    private const int SeedPairCount = 30;

    private static readonly int[] MatrixDensities = { 60, 90, 120 };

    private static readonly double[] MatrixRoughness = { 0.5, 1.0, 1.5 };

    private readonly ITestOutputHelper _output;

    public Spike3RelicMappingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------ 往返无丢失 demo

    [Fact] // [F-057] [F-168] 跃迁快照→两级存档字节→锚点回放→降级→封顶：三处置计数和=总数
    public void Roundtrip_no_loss_demo()
    {
        var scenario = new Spike3RelicMappingScenario(
            DemoE1Seed, DemoE2Seed, DemoBuildingCount, roughnessMultiplier: 1.0, terrainYielding: true);

        // ---- E1 遗迹城真实在场：占用注册数=建筑数 --------------------------
        Assert.Equal(DemoBuildingCount, scenario.E1BuiltCells.Count);
        foreach (CellCoord cell in scenario.E1BuiltCells)
        {
            Assert.True(scenario.E1World.IsOccupied(cell), $"E1 building cell ({cell.X},{cell.Y}) should be occupied.");
        }

        // ---- A5 核心区判定语义：半径 10 ∩ 已建区外扩 2 ---------------------
        var zone = new HashSet<CellCoord>(scenario.Snapshot.CoreZoneCells);
        Assert.NotEmpty(zone);
        var built = new HashSet<CellCoord>(scenario.E1BuiltCells);
        foreach (CellCoord cell in zone)
        {
            Assert.True(
                Spike3RelicMappingScenario.Chebyshev(cell, scenario.E1Anchor) <= Spike3RelicMappingScenario.CoreRadius,
                $"Zone cell ({cell.X},{cell.Y}) outside Chebyshev radius 10.");
            Assert.Contains(built, b => Spike3RelicMappingScenario.Chebyshev(cell, b) <= Spike3RelicMappingScenario.BuildMargin);
        }

        var parsedSnapshot = Spike3SnapshotCodec.Decode(scenario.SnapshotPayload);
        foreach (Spike3BuildingSnapshot building in parsedSnapshot.Buildings)
        {
            var absolute = new CellCoord(scenario.E1Anchor.X + building.RelX, scenario.E1Anchor.Y + building.RelY);
            if (Spike3RelicMappingScenario.Chebyshev(absolute, scenario.E1Anchor) <= Spike3RelicMappingScenario.CoreRadius)
            {
                Assert.True(zone.Contains(absolute), $"Building {building.BuildingId} inside radius 10 must be core-zone.");
            }
        }

        // ---- 两级存档字节往返：payload→parse→逐字段一致 --------------------
        Assert.True(
            parsedSnapshot.ValueEquals(scenario.Snapshot),
            "Decoded snapshot must equal the encoded snapshot field by field.");
        Assert.True(parsedSnapshot.ValueEquals(Spike3SnapshotCodec.Decode(Spike3SnapshotCodec.Encode(parsedSnapshot))), "Re-encode/decode must be a fixed point.");

        // ---- K-7 链路：ILevelLayoutSnapshot → CampaignMetaDto → JSON → 回读 --
        var layoutDto = new LevelLayoutSnapshotDto("level-e1-relic-city", scenario.SnapshotPayload);
        var meta = new CampaignMetaDto(
            WorldSeed: DemoE1Seed,
            EraIndex: 1,
            ResourcePool: new Dictionary<string, long> { ["iron"] = 320 },
            UnlockedTechs: new List<string> { "tech:e1-sentry" },
            RelicRegistry: new List<string>(),
            LevelLayoutSnapshots: new List<LevelLayoutSnapshotDto> { layoutDto });
        string json = CampaignSaveJsonContext.SerializeCampaign(meta);
        CampaignMetaDto metaBack = CampaignSaveJsonContext.DeserializeCampaign(json);
        Assert.Equal("level-e1-relic-city", ((ICampaignMeta)metaBack).LevelLayoutSnapshots[0].LevelId);
        Assert.True(
            metaBack.LevelLayoutSnapshots[0].LayoutPayload.AsSpan().SequenceEqual(scenario.SnapshotPayload),
            "Snapshot payload bytes must survive the campaign JSON roundtrip unchanged.");

        // ---- 映射回放（原位 shift=0 与移位 shift=2 两档） --------------------
        var replay = scenario.ReplayFromPayload(shiftRadius: 0);
        var replayShifted = scenario.ReplayFromPayload(shiftRadius: 2);

        // 往返无丢失：三种处置（回放=原位+移位/降级/封顶跳过）计数和=总数
        Assert.True(replay.IsNoLoss, $"shift0 no-loss violated: {Describe(replay)}");
        Assert.True(replayShifted.IsNoLoss, $"shift2 no-loss violated: {Describe(replayShifted)}");
        Assert.Equal(DemoBuildingCount, replay.Total);
        Assert.Equal(
            DemoBuildingCount,
            replay.IntactHomeCount + replay.IntactShiftedCount + replay.RuinCount + replay.CapSkipCount);
        Assert.Equal(
            DemoBuildingCount,
            replayShifted.IntactHomeCount + replayShifted.IntactShiftedCount + replayShifted.RuinCount + replayShifted.CapSkipCount);

        // 双台账恰好分割全体：遗迹注册表=回放数，抽象清单=降级+封顶数，无静默丢弃
        Assert.Equal(replay.IntactCount, replay.RelicRegistry.Count);
        Assert.Equal(replay.RuinCount + replay.CapSkipCount, replay.AbstractList.Count);
        Assert.All(replay.AbstractList, entry => Assert.True(
            entry.StartsWith("ruin|", StringComparison.Ordinal) ^ entry.StartsWith("abstract|", StringComparison.Ordinal),
            "Every abstract-list entry must be exactly one of the ruin/density-cap forms."));
        Assert.All(replay.AbstractList, entry => Assert.Contains("tier=T", entry)); // "某处曾有一座 T3 弩塔"式条目

        // 三种处置都被真实行使（固定 seed，确定性）：有完好、有降级
        Assert.True(replay.IntactHomeCount > 0, "Demo must replay at least one building at its home cell.");
        Assert.True(replay.RuinCount > 0, "Demo must degrade at least one building to a ruin item (terrain conflict).");
        Assert.Contains(replay.Outcomes, o => o.Cause == "terrain");
        // 移位阶（F.7 冲突降级第一档）单调挽回：shift2 完好数 ≥ shift0
        Assert.True(replayShifted.IntactCount >= replay.IntactCount, "Shift rung must never reduce intact count.");

        // 密度封顶 单格≤1：互异格子数=回放数
        Assert.Equal(replayShifted.IntactCount, replayShifted.RelicCells.Count);
        Assert.Equal(replayShifted.IntactCount, replayShifted.RelicCells.Distinct().Count());

        // 确定性：同 payload 两次回放，处置序列逐一相同（须在任何世界注册写入之前）
        var replayAgain = scenario.ReplayFromPayload(shiftRadius: 2);
        Assert.True(replayShifted.Outcomes.SequenceEqual(replayAgain.Outcomes), "Replay must be deterministic for the same payload and world.");

        // GridWorld 自身重叠执法作 单格≤1 的外部交叉验证：全部注册成功
        int registered = 0;
        foreach (CellCoord cell in replayShifted.RelicCells)
        {
            scenario.E2World.RegisterOccupancy(new EntityId(700_000 + registered), new[] { cell });
            registered++;
        }

        Assert.Equal(replayShifted.IntactCount, registered);

        // A5 核心区实际效果（单次 demo 数字；批统计见 Core_zone_retention 测试）
        _output.WriteLine($"payload-bytes={scenario.SnapshotPayload.Length} zone-cells={zone.Count} e2-yielded-cells={scenario.YieldedCellCount} resource-nodes={scenario.ResourceNodes.Count}");
        _output.WriteLine($"shift0: {Describe(replay)}");
        _output.WriteLine($"shift0 causes: {string.Join(", ", replay.Outcomes.Where(o => o.Kind == Spike3RelayOutcomeKind.RuinDowngraded).GroupBy(o => o.Cause).Select(g => $"{g.Key}={g.Count()}"))}");
        _output.WriteLine($"shift2: {Describe(replayShifted)}");
        _output.WriteLine($"core-zone: intact {replay.CoreZoneIntact}/{replay.CoreZoneTotal} ({replay.CoreZoneIntactRate:P1}); outside: {replay.OutsideIntact}/{replay.OutsideTotal} ({replay.OutsideIntactRate:P1})");
        Assert.True(replay.CoreZoneIntactRate >= 0.90, "Yielded core zone must keep ≥90% buildings intact in the demo leg.");
    }

    // ------------------------------------------------------------ 冲突率矩阵（30 对 seed）

    [Fact] // [F-057] [F-168] 30 seed 对 × 密度{60,90,120} × 粗糙度{0.5,1.0,1.5}：冲突率矩阵+拟合
    public void Conflict_rate_matrix_over_30_seed_pairs()
    {
        // cell[(densityIndex, roughnessIndex)] 聚合 shift0/shift2 两种回放的处置计数
        var intact0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var shifted0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var ruin0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var cap0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var causeTerrain0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var causeResource0 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var intact2 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var shifted2 = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var totals = new long[MatrixDensities.Length, MatrixRoughness.Length];
        var blockerRates = new List<double>();

        for (int pair = 0; pair < SeedPairCount; pair++)
        {
            long e1Seed = 3_000_000 + 7_919L * pair;
            long e2Seed = 7_000_000 + 104_729L * pair + 13;
            for (int d = 0; d < MatrixDensities.Length; d++)
            {
                for (int r = 0; r < MatrixRoughness.Length; r++)
                {
                    var scenario = new Spike3RelicMappingScenario(
                        e1Seed, e2Seed, MatrixDensities[d], MatrixRoughness[r], terrainYielding: true);
                    Spike3RelicMappingResult run0 = scenario.ReplayFromPayload(shiftRadius: 0);
                    Spike3RelicMappingResult run2 = scenario.ReplayFromPayload(shiftRadius: 2);

                    // 每次跃迁都满足往返无丢失（矩阵的前提不变式）
                    Assert.True(run0.IsNoLoss, $"no-loss violated at pair={pair} density={MatrixDensities[d]} rough={MatrixRoughness[r]} shift0: {Describe(run0)}");
                    Assert.True(run2.IsNoLoss, $"no-loss violated at pair={pair} density={MatrixDensities[d]} rough={MatrixRoughness[r]} shift2: {Describe(run2)}");
                    Assert.True(run2.IntactCount >= run0.IntactCount, $"shift rung reduced intact count at pair={pair} d={MatrixDensities[d]} r={MatrixRoughness[r]}");

                    totals[d, r] += run0.Total;
                    intact0[d, r] += run0.IntactHomeCount;
                    shifted0[d, r] += run0.IntactShiftedCount; // shift=0 时恒 0（纯平移单射）
                    ruin0[d, r] += run0.RuinCount;
                    cap0[d, r] += run0.CapSkipCount;
                    intact2[d, r] += run2.IntactHomeCount + run2.IntactShiftedCount;
                    shifted2[d, r] += run2.IntactShiftedCount;
                    causeTerrain0[d, r] += run0.Outcomes.Count(o => o.Kind == Spike3RelayOutcomeKind.RuinDowngraded && o.Cause == "terrain");
                    causeResource0[d, r] += run0.Outcomes.Count(o => o.Kind == Spike3RelayOutcomeKind.RuinDowngraded && o.Cause == "resource");
                    blockerRates.Add(scenario.E2FootprintBlockerRate);
                }
            }
        }

        // ---- 矩阵输出（完好率=回放/总数；冲突率=降级+封顶/总数） -------------
        _output.WriteLine($"matrix: {SeedPairCount} seed pairs x {MatrixDensities.Length} densities x {MatrixRoughness.Length} roughness levels = {SeedPairCount * MatrixDensities.Length * MatrixRoughness.Length} era jumps");
        _output.WriteLine("density | rough | total  | intact% (shift0) | intact% (shift2) | shifted% (shift2) | ruin% | cap% | terrain | resource");
        var levelMeans = new double[MatrixRoughness.Length];
        var levelTotals = new long[MatrixRoughness.Length];
        var levelConflicts = new long[MatrixRoughness.Length];
        for (int d = 0; d < MatrixDensities.Length; d++)
        {
            for (int r = 0; r < MatrixRoughness.Length; r++)
            {
                long total = totals[d, r];
                double intactRate0 = (double)intact0[d, r] / total;
                double intactRate2 = (double)intact2[d, r] / total;
                double shiftedRate2 = (double)shifted2[d, r] / total;
                double ruinRate = (double)ruin0[d, r] / total;
                double capRate = (double)cap0[d, r] / total;
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{MatrixDensities[d],7} | {MatrixRoughness[r],5:F1} | {total,6} | {intactRate0,16:P1} | {intactRate2,16:P1} | {shiftedRate2,17:P1} | {ruinRate,5:P1} | {capRate,4:P1} | {causeTerrain0[d, r],7} | {causeResource0[d, r],8}"));
                levelTotals[r] += total;
                levelConflicts[r] += ruin0[d, r] + cap0[d, r];
            }
        }

        for (int r = 0; r < MatrixRoughness.Length; r++)
        {
            levelMeans[r] = (double)levelConflicts[r] / levelTotals[r];
        }

        double meanBlocker = blockerRates.Average();
        _output.WriteLine($"mean E2 footprint blocker rate (post-yield, Chebyshev<=16): {meanBlocker:F4}");

        // ---- 拟合：冲突率 = f(水+岩比例)，x=(0.08+0.10)*粗糙度 ---------------
        double[] xs = MatrixRoughness.Select(m => (GridWorld.DefaultWaterRatio + GridWorld.DefaultRockRatio) * m).ToArray();
        (double slope, double intercept, double r2) = LeastSquares(xs, levelMeans);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"fit: conflictRate = {slope:F3} * (water+rock) + {intercept:F3}   (R^2 = {r2:F3})"));

        // 带内断言（验收带，非实测值誊录）：斜率≈区外建筑占比（核心区让位后被清零）
        Assert.InRange(slope, 0.40, 0.85);
        Assert.InRange(intercept, 0.0, 0.05); // 截距≈资源点占用冲突的固定成本
        Assert.True(r2 >= 0.95, $"Linear fit R^2={r2:F3} below 0.95.");

        // 单调性：粗糙度升 → 冲突率升
        Assert.True(levelMeans[2] > levelMeans[1] && levelMeans[1] > levelMeans[0],
            $"Conflict rate must rise with roughness: {string.Join(", ", levelMeans.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))}");

        // 移位阶（shift=2）相对原位回放（shift=0）的挽回效果：冲突率显著下降
        long totalAll = 0, intact0All = 0, intact2All = 0;
        for (int d = 0; d < MatrixDensities.Length; d++)
        {
            for (int r = 0; r < MatrixRoughness.Length; r++)
            {
                totalAll += totals[d, r];
                intact0All += intact0[d, r] + shifted0[d, r];
                intact2All += intact2[d, r];
            }
        }

        long conflict0All = totalAll - intact0All;
        long conflict2All = totalAll - intact2All;
        double recover = 1.0 - (double)conflict2All / conflict0All;
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"shift rung recovery: conflict {conflict0All}/{totalAll} ({(double)conflict0All / totalAll:P1}) -> {conflict2All}/{totalAll} ({(double)conflict2All / totalAll:P1}), recovered {recover:P1}"));
        Assert.True(recover >= 0.80, $"Shift rung (radius 2) must recover >=80% of home-cell conflicts, got {recover:P1}.");
        Assert.True(conflict0All > 0, "Matrix must observe a nonzero baseline conflict mass.");
    }

    // ------------------------------------------------------------ A5 核心区口径验证

    [Fact] // [F-168] [C-20] 核心区（半径10∩外扩2）内 vs 区外完好率；地形让位开/关对照
    public void Core_zone_retention_comparison()
    {
        // 让位 ON：30 对 seed，密度 90，粗糙度 1.0
        long coreTotalOn = 0, coreIntactOn = 0, outTotalOn = 0, outIntactOn = 0;
        long coreTotalOff = 0, coreIntactOff = 0, outTotalOff = 0, outIntactOff = 0;
        for (int pair = 0; pair < SeedPairCount; pair++)
        {
            long e1Seed = 5_000_000 + 7_919L * pair;
            long e2Seed = 9_000_000 + 104_729L * pair + 7;
            var on = new Spike3RelicMappingScenario(e1Seed, e2Seed, 90, 1.0, terrainYielding: true);
            var off = new Spike3RelicMappingScenario(e1Seed, e2Seed, 90, 1.0, terrainYielding: false);
            Spike3RelicMappingResult runOn = on.ReplayFromPayload(shiftRadius: 0);
            Spike3RelicMappingResult runOff = off.ReplayFromPayload(shiftRadius: 0);
            Assert.True(runOn.IsNoLoss && runOff.IsNoLoss, "Every A5 comparison leg must itself be no-loss.");

            coreTotalOn += runOn.CoreZoneTotal;
            coreIntactOn += runOn.CoreZoneIntact;
            outTotalOn += runOn.OutsideTotal;
            outIntactOn += runOn.OutsideIntact;
            coreTotalOff += runOff.CoreZoneTotal;
            coreIntactOff += runOff.CoreZoneIntact;
            outTotalOff += runOff.OutsideTotal;
            outIntactOff += runOff.OutsideIntact;
        }

        double coreOn = (double)coreIntactOn / coreTotalOn;
        double outsideOn = (double)outIntactOn / outTotalOn;
        double coreOff = (double)coreIntactOff / coreTotalOff;
        double outsideOff = (double)outIntactOff / outTotalOff;
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"yield ON : core {coreIntactOn}/{coreTotalOn} ({coreOn:P1}) vs outside {outIntactOn}/{outTotalOn} ({outsideOn:P1}), gap {coreOn - outsideOn:P1}"));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"yield OFF: core {coreIntactOff}/{coreTotalOff} ({coreOff:P1}) vs outside {outIntactOff}/{outTotalOff} ({outsideOff:P1}), gap {coreOff - outsideOff:P1}"));

        // 主城核心区保留效果（验收带）：让位开 ⇒ 核心区完好率≥95%，领先区外≥10 个百分点
        Assert.True(coreOn >= 0.95, $"Yielded core intact rate {coreOn:P1} below 95%.");
        Assert.True(coreOn - outsideOn >= 0.10, $"Core-vs-outside gap {coreOn - outsideOn:P1} below 10 points.");

        // 对照组：让位关 ⇒ 核心区不再特殊（差距坍缩），且显著低于让位开
        Assert.True(Math.Abs(coreOff - outsideOff) <= 0.08, $"Without yielding the core/outside gap must collapse, got {coreOff - outsideOff:P1}.");
        Assert.True(coreOn - coreOff >= 0.10, $"Terrain yielding must lift core intact rate by >=10 points, got {coreOn - coreOff:P1}.");

        // 样本非空：区内外都有足量建筑
        Assert.True(coreTotalOn >= 500 && outTotalOn >= 500, "Both core and outside samples must be populated.");
    }

    // ------------------------------------------------------------ 分支微测试

    [Fact] // [F-057] 密度封顶 单格≤1：移位竞争下高价值保留、低价值转抽象清单
    public void Density_cap_single_cell_enforcement()
    {
        // 全水微地图：仅 (48,50) 一格可建（home(48,48) 的 2 环内）
        var world = new GridWorld(96, 96, new LcgRng(1), forestRatio: 0, rockRatio: 0, waterRatio: 1.0);
        world.SetTerrain(new CellCoord(48, 50), TerrainKind.Plain);
        var snapshot = new Spike3RelicCitySnapshot(
            Anchor: new CellCoord(0, 0),
            CoreZoneCells: new[] { new CellCoord(0, 0) },
            Buildings: new Spike3BuildingSnapshot[]
            {
                new(7, "e1:fire-oil-tower", Tier: 4, Orientation: 0, RelX: 0, RelY: 0),
                new(3, "e1:xbow-bastion", Tier: 3, Orientation: 90, RelX: 0, RelY: 0),
                new(5, "e1:arrow-tower", Tier: 2, Orientation: 180, RelX: 0, RelY: 0),
            });
        byte[] payload = Spike3SnapshotCodec.Encode(snapshot);

        var result = new Spike3RelicReplayEngine(shiftRadius: 2).Replay(world, new CellCoord(48, 48), Spike3SnapshotCodec.Decode(payload));

        Assert.True(result.IsNoLoss, Describe(result));
        // 价值排序（T4→T3→T2）：T4 抢到唯一可建格（移位 2 环），T3/T2 被单格≤1 封顶拒绝
        Assert.Equal(Spike3RelayOutcomeKind.IntactShifted, result.Outcomes[0].Kind);
        Assert.Equal(7, result.Outcomes[0].BuildingId);
        Assert.Equal(2, result.Outcomes[0].ShiftDistance);
        Assert.Equal(new CellCoord(48, 50), result.Outcomes[0].Cell);
        Assert.Equal(Spike3RelayOutcomeKind.CapSkipped, result.Outcomes[1].Kind);
        Assert.Equal(Spike3RelayOutcomeKind.CapSkipped, result.Outcomes[2].Kind);
        Assert.Equal(2, result.CapSkipCount);
        Assert.Single(result.RelicCells);
        Assert.All(result.AbstractList, e => Assert.StartsWith("abstract|", e));
        Assert.All(result.AbstractList, e => Assert.Contains("reason=density-cap", e));

        // 外部交叉验证：GridWorld 自身重叠执法放行恰 1 次注册
        world.RegisterOccupancy(new EntityId(700_001), new[] { new CellCoord(48, 50) });
        Assert.Throws<InvalidOperationException>(() =>
            world.RegisterOccupancy(new EntityId(700_002), new[] { new CellCoord(48, 50) }));
    }

    [Fact] // [F-057] 出界映射=降级（cause=oob），不抛异常不静默丢弃
    public void Out_of_bounds_home_degrades_to_ruin()
    {
        var world = new GridWorld(96, 96, new LcgRng(5));
        var snapshot = new Spike3RelicCitySnapshot(
            Anchor: new CellCoord(0, 0),
            CoreZoneCells: new[] { new CellCoord(0, 0) },
            Buildings: new[] { new Spike3BuildingSnapshot(11, "e1:watchtower", 1, 90, RelX: -5, RelY: -5) });

        var result = new Spike3RelicReplayEngine(shiftRadius: 2).Replay(world, new CellCoord(0, 0), snapshot);

        Assert.True(result.IsNoLoss, Describe(result));
        Spike3RelayOutcome outcome = Assert.Single(result.Outcomes);
        Assert.Equal(Spike3RelayOutcomeKind.RuinDowngraded, outcome.Kind);
        Assert.Equal("oob", outcome.Cause);
        Assert.Null(outcome.Cell);
        Assert.Contains("cause=oob", Assert.Single(result.AbstractList));
    }

    // ------------------------------------------------------------ 编解码三行（CS-3.1）

    [Fact] // [F-057] 快照 payload 编解码：legal / boundary / illegal
    public void Snapshot_codec_legal_boundary_illegal()
    {
        // legal —— 最小合法快照逐字段往返
        var legal = new Spike3RelicCitySnapshot(
            Anchor: new CellCoord(10, 20),
            CoreZoneCells: new[] { new CellCoord(11, 21), new CellCoord(12, 22) },
            Buildings: new[]
            {
                new Spike3BuildingSnapshot(1, "e1:watchtower", 1, 0, -10, -10),
                new Spike3BuildingSnapshot(2, "e1:fire-oil-tower", 4, 270, 10, 10),
            });
        byte[] payload = Spike3SnapshotCodec.Encode(legal);
        Assert.True(Spike3SnapshotCodec.Decode(payload).ValueEquals(legal), "Legal snapshot must roundtrip field-wise.");

        // boundary —— 空建筑空核心区（遗迹城尚未建成即跃迁）
        var empty = new Spike3RelicCitySnapshot(new CellCoord(5, 5), Array.Empty<CellCoord>(), Array.Empty<Spike3BuildingSnapshot>());
        byte[] emptyPayload = Spike3SnapshotCodec.Encode(empty);
        Assert.True(Spike3SnapshotCodec.Decode(emptyPayload).ValueEquals(empty), "Empty snapshot must roundtrip.");
        var emptyResult = new Spike3RelicReplayEngine().Replay(new GridWorld(32, 32, new LcgRng(9)), new CellCoord(16, 16), Spike3SnapshotCodec.Decode(emptyPayload));
        Assert.True(emptyResult.IsNoLoss && emptyResult.Total == 0 && emptyResult.AbstractList.Count == 0);

        // boundary —— 相对坐标取满 ±511 仍可往返
        var farCorner = new Spike3RelicCitySnapshot(
            new CellCoord(0, 0),
            new[] { new CellCoord(0, 0) },
            new[] { new Spike3BuildingSnapshot(9, "e1:watchtower", 1, 180, 511, -511) });
        Assert.True(Spike3SnapshotCodec.Decode(Spike3SnapshotCodec.Encode(farCorner)).ValueEquals(farCorner));

        // illegal —— 坏魔数（StoredVersion=0）
        byte[] garbage = { 0xDE, 0xAD, 0xBE, 0x01, 0x00, 0x00, 0x00, 0x00 };
        SaveCorruptException badMagic = Assert.Throws<SaveCorruptException>(() => Spike3SnapshotCodec.Decode(garbage));
        Assert.Equal(0, badMagic.StoredVersion);

        // illegal —— 合法 payload 截断
        byte[] truncated = payload[..^4];
        Assert.Throws<SaveCorruptException>(() => Spike3SnapshotCodec.Decode(truncated));

        // illegal —— 版本号不识别（StoredVersion=回读到的版本字节）
        byte[] wrongVersion = payload.ToArray();
        wrongVersion[3] = 0x7F;
        SaveCorruptException badVersion = Assert.Throws<SaveCorruptException>(() => Spike3SnapshotCodec.Decode(wrongVersion));
        Assert.Equal(0x7F, badVersion.StoredVersion);

        // illegal —— 编码侧拒绝非法快照字段
        Assert.Throws<ArgumentException>(() => Spike3SnapshotCodec.Encode(new Spike3RelicCitySnapshot(
            new CellCoord(0, 0), new[] { new CellCoord(0, 0) },
            new[] { new Spike3BuildingSnapshot(1, "e1:watchtower", 5, 0, 0, 0) }))); // tier>4
        Assert.Throws<ArgumentException>(() => Spike3SnapshotCodec.Encode(new Spike3RelicCitySnapshot(
            new CellCoord(0, 0), new[] { new CellCoord(0, 0) },
            new[] { new Spike3BuildingSnapshot(1, "e1:watchtower", 1, 45, 0, 0) }))); // 非 90° 倍数
        Assert.Throws<ArgumentException>(() => Spike3SnapshotCodec.Encode(new Spike3RelicCitySnapshot(
            new CellCoord(0, 0), new[] { new CellCoord(0, 0) },
            new[]
            {
                new Spike3BuildingSnapshot(1, "e1:watchtower", 1, 0, 0, 0),
                new Spike3BuildingSnapshot(1, "e1:arrow-tower", 2, 0, 1, 0),
            }))); // 重复 id
        Assert.Throws<ArgumentException>(() => Spike3SnapshotCodec.Encode(new Spike3RelicCitySnapshot(
            new CellCoord(0, 0), new[] { new CellCoord(-3, 0) },
            Array.Empty<Spike3BuildingSnapshot>()))); // 负坐标格子

        // illegal —— 回放引擎移位半径越界
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spike3RelicReplayEngine(shiftRadius: 5));
    }

    // ------------------------------------------------------------ helpers

    private static string Describe(Spike3RelicMappingResult result) =>
        $"total={result.Total} intactHome={result.IntactHomeCount} intactShifted={result.IntactShiftedCount} "
        + $"ruin={result.RuinCount} capSkip={result.CapSkipCount} registry={result.RelicRegistry.Count} abstractList={result.AbstractList.Count}";

    /// <summary>最小二乘线性拟合 y = slope*x + intercept（含 R²，T-058 报告拟合口径）。</summary>
    private static (double Slope, double Intercept, double R2) LeastSquares(double[] xs, double[] ys)
    {
        double mx = xs.Average();
        double my = ys.Average();
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
            sxy += (xs[i] - mx) * (ys[i] - my);
        }

        double slope = sxy / sxx;
        double intercept = my - slope * mx;
        double r2 = sxx == 0 || syy == 0 ? 0 : (sxy * sxy) / (sxx * syy);
        return (slope, intercept, r2);
    }
}
