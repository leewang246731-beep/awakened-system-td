using Xunit;
using EraCore.Legacy;
using EraCore.Tests.Checkpoint;
using EraCore.World;
using EraCore.Tests.World;

namespace EraCore.Tests.Legacy;

// [C-20][F-056][F-061] RelicMapper 产品化（T-074）：新大陆确定性/让位/资源
// 避开 + F.7 回放阶梯无丢失计数律 + 核心区完好率带。参数=spike3 §6 推荐
// 值（relic_mapping.json 装载，产品化不再硬编码）。
// [Collection(CheckpointHeavy)]：每用例多次 96×96 大陆生成，属重载邻居
// （单邻居负载设计，见 CheckpointHeavyCollection 注释）。
[Collection(CheckpointHeavyCollection.Name)]
public class RelicMapperTests
{
    private static RelicMappingParams Parameters() => LegacyFixture.Mapping();

    // ---- new continent ---------------------------------------------- legal

    [Fact] // legal — 同 (seed, era) 两次生成新大陆地图哈希全等（确定性）
    public void continent_generation_is_deterministic_per_seed_and_era()
    {
        NewContinent first = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        NewContinent second = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(999));
        Assert.Equal(first.MapHash, second.MapHash); // 入口 rng 种子无关：派生 seed 唯一权威
        Assert.Equal(first.ResourceNodes, second.ResourceNodes, CellCoordEqualityComparer.Instance);
        Assert.Equal(first.ThreatNodes, second.ThreatNodes, CellCoordEqualityComparer.Instance);
        Assert.Equal(first.YieldedCellCount, second.YieldedCellCount);
    }

    [Fact] // legal — 不同时代派生不同大陆（E2≠E3）
    public void continent_differs_across_target_eras()
    {
        NewContinent e2 = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        NewContinent e3 = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 3, new LcgRng(1));
        Assert.NotEqual(e2.MapHash, e3.MapHash);
    }

    [Fact] // legal — 让位半径内 Water/Rock 全部转 Plain（spike3 推荐参数 1）
    public void terrain_yield_flattens_hard_blockers_inside_yield_radius()
    {
        NewContinent continent = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        int radius = Parameters().TerrainYieldRadiusCells;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var cell = new Contracts.Foundation.CellCoord(continent.Anchor.X + dx, continent.Anchor.Y + dy);
                Assert.True(continent.World.IsPassable(cell),
                    $"({dx},{dy}) 内仍存在硬阻挡——让位半径 {radius} 失效");
            }
        }
    }

    [Fact] // legal — 资源点避开让位区（推荐参数 2）+ 数量恰等
    public void resource_nodes_avoid_the_yield_zone_and_match_count()
    {
        RelicMappingParams parameters = Parameters();
        NewContinent continent = NewContinentFactory.Create(parameters, LegacyFixture.Seed, 2, new LcgRng(1));
        Assert.Equal(parameters.ResourceNodeCount, continent.ResourceNodes.Count);
        foreach (Contracts.Foundation.CellCoord node in continent.ResourceNodes)
        {
            Assert.True(
                CoreZoneGeometry.Chebyshev(node, continent.Anchor) > parameters.TerrainYieldRadiusCells,
                $"资源点 ({node.X},{node.Y}) 落在让位区内");
            Assert.True(continent.World.IsOccupied(node)); // 占用层注册=回放冲突源
        }
    }

    [Fact] // legal — threat 让位：威胁点不进核心保留区 + 保留面积 ≤15% 上限
    public void threat_nodes_yield_outside_reserved_core_within_cap()
    {
        RelicMappingParams parameters = Parameters();
        NewContinent continent = NewContinentFactory.Create(parameters, LegacyFixture.Seed, 2, new LcgRng(1));
        Assert.Equal(parameters.ThreatNodeCount, continent.ThreatNodes.Count);
        var reserve = new HashSet<Contracts.Foundation.CellCoord>(continent.ReserveCells);
        foreach (Contracts.Foundation.CellCoord node in continent.ThreatNodes)
        {
            Assert.DoesNotContain(node, reserve); // 让位：核心区不刷威胁
        }

        double fraction = reserve.Count / (double)(parameters.MapSizeCells * parameters.MapSizeCells);
        Assert.True(fraction <= parameters.ReserveAreaCapFraction + 1e-9,
            $"保留面积占比 {fraction:F3} 超上限 {parameters.ReserveAreaCapFraction}");
    }

    // ---- replay ladder ---------------------------------------------- legal

    [Fact] // legal — 无丢失计数律：回放+降级+封顶=总数，两清单严格分割
    public void replay_partition_is_lossless()
    {
        RelicMapperScenario scenario = RunScenario(citySeed: 11, buildings: 60, radius: 16);
        Assert.True(scenario.Mapping.IsNoLoss);
        Assert.Equal(scenario.Mapping.Total,
            scenario.Mapping.IntactCount + scenario.Mapping.RuinCount + scenario.Mapping.CapSkipCount);
        Assert.Equal(scenario.Mapping.Total,
            scenario.Mapping.RelicRegistry.Count + scenario.Mapping.AbstractList.Count);
    }

    [Fact] // legal — spike3 参数下核心区完好率 ≥95% 带（spike3 实测 98.9%）
    public void core_zone_intact_rate_meets_spike3_band()
    {
        RelicMapperScenario scenario = RunScenario(citySeed: 23, buildings: 90, radius: 16);
        Assert.True(scenario.Mapping.CoreZoneTotal >= 24, "核心区样本量不足（场景构造问题）");
        Assert.True(scenario.Mapping.CoreZoneIntactRate >= 0.95,
            $"核心区完好率 {scenario.Mapping.CoreZoneIntactRate:F3} 低于 95% 带（spike3 推荐参数实测 98.9%）");
    }

    [Fact] // legal — 立碑遗迹注册占用：新世界不可再在其上建（单格 ≤1 + 占用层）
    public void standing_relics_register_world_occupancy()
    {
        RelicMapperScenario scenario = RunScenario(citySeed: 31, buildings: 40, radius: 16);
        Assert.True(scenario.Mapping.IntactCount > 0);
        foreach (Contracts.Foundation.CellCoord cell in scenario.Mapping.RelicCells)
        {
            Assert.True(scenario.Continent.World.IsOccupied(cell));
        }

        Assert.Equal(scenario.Mapping.IntactCount, scenario.Mapping.RelicCells.Distinct().Count());
    }

    [Fact] // legal — 同快照+同大陆两次回放结局哈希全等（确定性）
    public void replay_outcomes_are_deterministic()
    {
        RelicMapperScenario first = RunScenario(citySeed: 41, buildings: 70, radius: 16);
        RelicMapperScenario second = RunScenario(citySeed: 41, buildings: 70, radius: 16);
        Assert.Equal(first.Mapping.ComputeOutcomeHash(), second.Mapping.ComputeOutcomeHash());
        Assert.Equal(first.Mapping.RelicRegistry, second.Mapping.RelicRegistry);
        Assert.Equal(first.Mapping.AbstractList, second.Mapping.AbstractList);
    }

    [Fact] // legal — 同格争位：价值序保留 T4 原位，T2 让位移位档（单格 ≤1）
    public void contended_cell_keeps_the_more_valuable_relic_home()
    {
        var anchor = LegacyFixture.Anchor;
        var facts = new List<RelicCityBuildingFact>
        {
            new(200_001, "era1:tower-wood-arrow", 2, 0, anchor),
            new(200_002, "era1:tower-xbow-bastion", 4, 90, anchor), // 同格更高价值
        };
        var mapping = new RelicMapper(Parameters());
        var continent = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        var snapshot = new RelicCitySnapshot(anchor,
            CoreZoneGeometry.ComputeCoreZone(new[] { anchor }, anchor, 10, 2),
            facts.Select(f => new RelicBuildingSnapshot(f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(anchor), f.RelYTo(anchor))).ToList());
        RelicMappingResult result = mapping.Map(continent, snapshot);
        Assert.Equal(2, result.Total);
        Assert.True(result.IsNoLoss);
        RelicRelayOutcome home = result.Outcomes.Single(o => o.Kind == RelayOutcomeKind.IntactHome);
        RelicRelayOutcome other = result.Outcomes.Single(o => o.Kind != RelayOutcomeKind.IntactHome);
        Assert.Equal(200_002, home.BuildingId); // T4 价值序占原位
        Assert.Equal(anchor, home.Cell); // 原位=锚点
        Assert.NotEqual(anchor, other.Cell); // 另一座必须离开该格（单格 ≤1）
        Assert.All(result.RelicCells, cell => Assert.Equal(1, result.RelicCells.Count(c => c == cell)));
    }

    [Fact] // legal — 冲突降级执法：高水耗地形+窄让位半径下，冲突建筑降级为废墟条目不静默丢弃
    public void hostile_terrain_downgrades_conflicts_to_abstract_list()
    {
        // 对抗参数表（测试构造）：整图水域+让位半径 0——任何格位都冲突
        var hostile = Parameters() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 0.9,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        };
        var continent = NewContinentFactory.Create(hostile, LegacyFixture.Seed, 2, new LcgRng(1));
        var anchor = continent.Anchor;
        // 锚点 Cheb≤3 强制水域（覆盖移位档半径 2 的全部环格）：home+移位全域冲突
        for (int dy = -3; dy <= 3; dy++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                continent.World.SetTerrain(new Contracts.Foundation.CellCoord(anchor.X + dx, anchor.Y + dy), TerrainKind.Water);
            }
        }

        var facts = new List<RelicCityBuildingFact> { new(300_001, "era1:tower-wood-arrow", 1, 0, anchor) };
        var snapshot = new RelicCitySnapshot(anchor,
            CoreZoneGeometry.ComputeCoreZone(new[] { anchor }, anchor, hostile.CoreRadiusCells, hostile.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(anchor), f.RelYTo(anchor))).ToList());
        RelicMappingResult result = new RelicMapper(hostile).Map(continent, snapshot);
        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.IntactCount);
        Assert.Equal(1, result.RuinCount); // 全域水域：移位档救不了 → 废墟
        Assert.StartsWith("ruin|id=300001|", result.AbstractList[0]);
        Assert.Contains("cause=terrain", result.AbstractList[0]); // 降级条目带因
        Assert.True(result.IsNoLoss); // 降级产物进抽象清单=无丢失
    }

    // ---- replay ladder --------------------------------------------- illegal

    [Fact] // illegal — 违反无丢失不变式抛异常（阶梯守卫）
    public void mapper_rejects_null_inputs()
    {
        var mapping = new RelicMapper(Parameters());
        Assert.Throws<ArgumentNullException>(() => mapping.Map(null!, new RelicCitySnapshot(
            LegacyFixture.Anchor, Array.Empty<Contracts.Foundation.CellCoord>(), Array.Empty<RelicBuildingSnapshot>())));
        var continent = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        Assert.Throws<ArgumentNullException>(() => mapping.Map(continent, null!));
    }

    // ---- boundary ------------------------------------------------------

    [Fact] // boundary — 空城回放=空结果且无丢失
    public void empty_city_maps_to_empty_result()
    {
        var mapping = new RelicMapper(Parameters());
        var continent = NewContinentFactory.Create(Parameters(), LegacyFixture.Seed, 2, new LcgRng(1));
        var empty = new RelicCitySnapshot(LegacyFixture.Anchor,
            Array.Empty<Contracts.Foundation.CellCoord>(), Array.Empty<RelicBuildingSnapshot>());
        RelicMappingResult result = mapping.Map(continent, empty);
        Assert.Equal(0, result.Total);
        Assert.True(result.IsNoLoss);
        Assert.Equal(1.0, result.CoreZoneIntactRate, 9); // 空分母口径=1
    }

    private sealed record RelicMapperScenario(NewContinent Continent, RelicMappingResult Mapping);

    private static RelicMapperScenario RunScenario(long citySeed, int buildings, int radius)
    {
        RelicMappingParams parameters = Parameters();
        var facts = LegacyFixture.CityFacts(citySeed, buildings, radius, parameters.Orientations);
        var continent = NewContinentFactory.Create(parameters, LegacyFixture.Seed, 2, new LcgRng(citySeed));
        var snapshot = new RelicCitySnapshot(
            LegacyFixture.Anchor,
            CoreZoneGeometry.ComputeCoreZone(
                facts.Select(f => f.Cell).ToList(), LegacyFixture.Anchor,
                parameters.CoreRadiusCells, parameters.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(
                f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(LegacyFixture.Anchor), f.RelYTo(LegacyFixture.Anchor))).ToList());
        return new RelicMapperScenario(continent, new RelicMapper(parameters).Map(continent, snapshot));
    }

    private sealed class CellCoordEqualityComparer : IEqualityComparer<Contracts.Foundation.CellCoord>
    {
        public static readonly CellCoordEqualityComparer Instance = new();

        public bool Equals(Contracts.Foundation.CellCoord? a, Contracts.Foundation.CellCoord? b) =>
            a is not null && b is not null && a.X == b.X && a.Y == b.Y;

        public int GetHashCode(Contracts.Foundation.CellCoord obj) => HashCode.Combine(obj.X, obj.Y);
    }
}
