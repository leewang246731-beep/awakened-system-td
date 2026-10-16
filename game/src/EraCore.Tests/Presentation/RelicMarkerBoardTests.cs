using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using EraCore.Presentation;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Legacy;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Presentation;

// [C-22][F-057][F-168] 新时代地图遗迹标记板（T-125 遗迹映射正式版）：
// RelicMarkerBoard 把映射生产输出（大陆+回放结果+挖掘位计划）转成地图 UI
// 标记决策——立碑遗迹/挖掘位/纪念馆修复位/核心保留区勾勒四类，纹理 stem
// 与 textures/relic/ 资产逐一对应（跨层契约，RelicArtMappingTests 实锚）。
// 决策全在纯逻辑层（BeltRenderDecisions/ZoneLabels 先例），headless 可全测。
// [Collection(CheckpointHeavy)]：多用例 96×96 大陆生成（RelicMapperTests 同款）。
[Collection(CheckpointHeavyCollection.Name)]
public class RelicMarkerBoardTests
{
    private sealed record BoardScenario(
        NewContinent Continent, RelicMappingResult Mapping, RelicDigSitePlan DigSites);

    private static BoardScenario RunScenario(long citySeed, int buildings, int radius)
    {
        return RunScenario(citySeed, buildings, radius, Mapping());
    }

    /// <summary>
    /// 对抗参数场景：全域水域+让位关（RelicMapperTests.hostile 同款口径）——
    /// 自然地形下移位档挽回近乎全部原位冲突（spike3 §4：shift2 完好 100%），
    /// 修复位消费断言需要确定性废墟，故走对抗表。
    /// </summary>
    private static BoardScenario RunHostileScenario(long citySeed, int buildings, int radius)
    {
        return RunScenario(citySeed, buildings, radius, Mapping() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 1.0,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        });
    }

    private static BoardScenario RunScenario(
        long citySeed, int buildings, int radius, RelicMappingParams table)
    {
        var facts = CityFacts(citySeed, buildings, radius, table.Orientations);
        return RunScenario(table, facts, new LcgRng(citySeed));
    }

    /// <summary>
    /// 对抗参数场景：全域水域+让位关（RelicMapperTests.hostile 同款口径）+
    /// 固定行布局（HostileCityFacts：锚点切比雪夫距 6=移位半径 2 之外）——
    /// 自然地形下移位档挽回近乎全部原位冲突（spike3 §4：shift2 完好 100%），
    /// 修复位消费断言需要确定性废墟，故走对抗表+钉死布局。
    /// </summary>
    private static BoardScenario RunHostileScenario(int buildings)
    {
        RelicMappingParams table = Mapping() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 1.0,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        };
        return RunScenario(table, HostileCityFacts(buildings, table.Orientations), new LcgRng(1));
    }

    private static BoardScenario RunScenario(
        RelicMappingParams table, List<RelicCityBuildingFact> facts, LcgRng rng)
    {
        var continent = NewContinentFactory.Create(table, Seed, 2, rng);
        var snapshot = new RelicCitySnapshot(
            Anchor,
            CoreZoneGeometry.ComputeCoreZone(
                facts.Select(f => f.Cell).ToList(), Anchor,
                table.CoreRadiusCells, table.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(
                f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(Anchor), f.RelYTo(Anchor))).ToList());
        var mapping = new RelicMapper(table).Map(continent, snapshot);
        return new BoardScenario(continent, mapping, RelicDigSites.Build(continent, mapping));
    }

    // ---- 标记全集 ------------------------------------------------------- legal

    [Fact] // legal — 四类标记分割全集：立碑遗迹数+挖掘位数+修复位数+勾勒格数=标记总数，
    // 纹理 stem 逐类对应，标签携带内容 id 与 tier。对抗参数场景（全域水域：
    // 废墟=挖掘位=修复位三联全量在场；自然地形下该三联近零=spike3 §4 移位收益
    // 上界口径，立碑侧由 standing_relic_markers 镜像测试覆盖）
    public void marker_plan_partitions_all_four_kinds_with_texture_contracts()
    {
        BoardScenario scenario = RunHostileScenario(buildings: 12);
        Assert.Equal(12, scenario.Mapping.RuinCount); // 全域水域：全部降级
        IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(
            scenario.Continent, scenario.Mapping, scenario.DigSites);

        Assert.Equal(scenario.Mapping.IntactCount,
            markers.Count(m => m.Kind == RelicMarkerKind.StandingRelic));
        Assert.Equal(scenario.DigSites.Sites.Count,
            markers.Count(m => m.Kind == RelicMarkerKind.DigSite));
        Assert.Equal(scenario.DigSites.Sites.Count,
            markers.Count(m => m.Kind == RelicMarkerKind.MemorialSlot));
        Assert.Equal(RelicMarkerBoard.ReserveOutline(scenario.Continent).Count,
            markers.Count(m => m.Kind == RelicMarkerKind.CoreReserveOutline));

        Assert.All(markers.Where(m => m.Kind == RelicMarkerKind.StandingRelic), m =>
        {
            Assert.Equal(RelicMarkerBoard.StandingRelicTexture, m.TextureStem);
            Assert.Contains("T", m.Label, StringComparison.Ordinal); // 标签带 tier
            Assert.InRange(m.Tier, 1, 4);
        });
        Assert.All(markers.Where(m => m.Kind == RelicMarkerKind.DigSite), m =>
            Assert.Equal(RelicMarkerBoard.DigSiteTexture, m.TextureStem));
        Assert.All(markers.Where(m => m.Kind == RelicMarkerKind.MemorialSlot), m =>
            Assert.Equal(RelicMarkerBoard.MemorialSlotTexture, m.TextureStem));
        Assert.All(markers.Where(m => m.Kind == RelicMarkerKind.CoreReserveOutline), m =>
        {
            Assert.Equal(RelicMarkerBoard.CoreReserveTexture, m.TextureStem);
            Assert.Equal(0, m.Tier);
        });
    }

    [Fact] // legal — 立碑遗迹标记=回放结局逐条镜像（原位/移位都在册，废墟/封顶不进标记）
    public void standing_relic_markers_mirror_replay_outcomes()
    {
        BoardScenario scenario = RunScenario(citySeed: 121, buildings: 60, radius: 14);
        IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(
            scenario.Continent, scenario.Mapping, scenario.DigSites);
        var relicCells = markers
            .Where(m => m.Kind == RelicMarkerKind.StandingRelic)
            .Select(m => m.Cell)
            .ToHashSet();
        foreach (RelicRelayOutcome outcome in scenario.Mapping.Outcomes)
        {
            if (outcome.Kind is RelayOutcomeKind.IntactHome or RelayOutcomeKind.IntactShifted)
            {
                Assert.Contains(outcome.Cell!, relicCells);
            }
            else
            {
                Assert.Null(outcome.Cell); // 抽象清单处置不带立碑格
            }
        }
    }

    [Fact] // legal — 纪念馆修复位消费：已注册 N 座纪念馆 → 前N 槽位收口（第 N+1 座
    // 挖掘起才标记新修复位；MemorialAuraBoard 注册序镜像）。对抗参数场景=
    // 全域水域（自然地形下移位档近乎全挽回，spike3 §4 移位收益上界口径）。
    public void registered_memorials_consume_repair_slots_in_order()
    {
        BoardScenario scenario = RunHostileScenario(buildings: 12);
        Assert.True(scenario.DigSites.Sites.Count >= 2, "全域水域下废墟不足（场景构造问题）");

        IReadOnlyList<RelicMarker> full = RelicMarkerBoard.Build(
            scenario.Continent, scenario.Mapping, scenario.DigSites, memorialsRegistered: 0);
        IReadOnlyList<RelicMarker> consumed = RelicMarkerBoard.Build(
            scenario.Continent, scenario.Mapping, scenario.DigSites, memorialsRegistered: 1);
        Assert.Equal(
            full.Count(m => m.Kind == RelicMarkerKind.MemorialSlot) - 1,
            consumed.Count(m => m.Kind == RelicMarkerKind.MemorialSlot));
        // 首槽已被第 1 座纪念馆占用：消费后剩余槽从第 2 个挖掘位起
        var remaining = consumed.Where(m => m.Kind == RelicMarkerKind.MemorialSlot).Select(m => m.Cell).ToList();
        Assert.Equal(scenario.DigSites.MemorialRepairSlots.Skip(1).Take(remaining.Count), remaining);
    }

    [Fact] // legal — 核心保留区勾勒=边界格：锚点格（正方形中心）不在勾勒集，
    // 保留区四角在；勾勒格数=空心正方形周长律（4×(2r+1)−4，半径 10 → 80）
    public void reserve_outline_is_the_zone_boundary_not_the_fill()
    {
        RelicMappingParams table = Mapping();
        NewContinent continent = NewContinentFactory.Create(table, Seed, 2, new LcgRng(1));
        IReadOnlyList<RelicMarker> outline = RelicMarkerBoard.ReserveOutline(continent);

        int side = 2 * table.CoreRadiusCells + 1;
        Assert.Equal(4 * side - 4, outline.Count); // 空心正方形周长律（半径 10 → 80 格）
        var cells = outline.Select(m => m.Cell).ToHashSet();
        Assert.DoesNotContain(new CellCoord(continent.Anchor.X, continent.Anchor.Y), cells); // 中心=内部
        Assert.Contains(new CellCoord(
            continent.Anchor.X - table.CoreRadiusCells,
            continent.Anchor.Y - table.CoreRadiusCells), cells); // 左上角=边界
        Assert.Contains(new CellCoord(
            continent.Anchor.X + table.CoreRadiusCells,
            continent.Anchor.Y + table.CoreRadiusCells), cells); // 右下角=边界
    }

    [Fact] // legal — 确定性：同输入两次构建标记序列逐一相等（UI 回放稳定）
    public void marker_plan_is_deterministic()
    {
        BoardScenario first = RunScenario(citySeed: 141, buildings: 60, radius: 14);
        BoardScenario second = RunScenario(citySeed: 141, buildings: 60, radius: 14);
        Assert.Equal(
            RelicMarkerBoard.Build(first.Continent, first.Mapping, first.DigSites),
            RelicMarkerBoard.Build(second.Continent, second.Mapping, second.DigSites));
    }

    // ---- boundary ----------------------------------------------------------

    [Fact] // boundary — 空（E1 无跃迁）输出：无遗迹大陆时不产出标记的调用面=空清单
    public void empty_mapping_yields_only_reserve_outline()
    {
        RelicMappingParams table = Mapping();
        NewContinent continent = NewContinentFactory.Create(table, Seed, 2, new LcgRng(1));
        var empty = new RelicCitySnapshot(Anchor,
            Array.Empty<CellCoord>(), Array.Empty<RelicBuildingSnapshot>());
        RelicMappingResult mapping = new RelicMapper(table).Map(continent, empty);
        RelicDigSitePlan plan = RelicDigSites.Build(continent, mapping);

        IReadOnlyList<RelicMarker> markers = RelicMarkerBoard.Build(continent, mapping, plan);
        Assert.DoesNotContain(markers, m => m.Kind != RelicMarkerKind.CoreReserveOutline);
        Assert.Equal(RelicMarkerBoard.ReserveOutline(continent).Count, markers.Count);
    }

    // ---- illegal -----------------------------------------------------------

    [Fact] // illegal — 空入参拒绝（大陆/回放结果；勾勒路径同律）
    public void build_rejects_null_inputs()
    {
        BoardScenario scenario = RunScenario(citySeed: 151, buildings: 30, radius: 12);
        Assert.Throws<ArgumentNullException>(() =>
            RelicMarkerBoard.Build(null!, scenario.Mapping, scenario.DigSites));
        Assert.Throws<ArgumentNullException>(() =>
            RelicMarkerBoard.Build(scenario.Continent, null!, scenario.DigSites));
        Assert.Throws<ArgumentNullException>(() => RelicMarkerBoard.ReserveOutline(null!));
    }
}
