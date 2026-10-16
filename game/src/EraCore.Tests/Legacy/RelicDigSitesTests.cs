using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.World;
using EraCore.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-057][F-168] 考古挖掘位生成（T-125 遗迹映射正式版）：F.7 冲突降级
// 产物的地图实体化——降级废墟按 home 格锚定为 dig-site（占用注册 700_000 段，
// 与遗迹 800_000/资源节点 900_000 段不相交），纪念馆修复位=挖掘位格序列
// （废墟原位修复）。测试形态按 CS-3.1 三行制（legal/boundary/illegal）。
// [Collection(CheckpointHeavy)]：多用例多次 96×96 大陆生成（RelicMapperTests
// 同款重载邻居）。
[Collection(CheckpointHeavyCollection.Name)]
public class RelicDigSitesTests
{
    private static RelicMappingParams Parameters() => Mapping();

    private sealed record Scenario(
        NewContinent Continent, RelicMappingResult Mapping, RelicDigSitePlan Plan);

    private static Scenario RunScenario(
        long citySeed, int buildings, int radius, RelicMappingParams? parameters = null)
    {
        RelicMappingParams table = parameters ?? Parameters();
        var facts = CityFacts(citySeed, buildings, radius, table.Orientations);
        var continent = NewContinentFactory.Create(table, Seed, 2, new LcgRng(citySeed));
        var snapshot = new RelicCitySnapshot(
            Anchor,
            CoreZoneGeometry.ComputeCoreZone(
                facts.Select(f => f.Cell).ToList(), Anchor,
                table.CoreRadiusCells, table.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(
                f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(Anchor), f.RelYTo(Anchor))).ToList());
        var mapping = new RelicMapper(table).Map(continent, snapshot);
        return new Scenario(continent, mapping, RelicDigSites.Build(continent, mapping));
    }

    // ---- dig-site 生成 ------------------------------------------------- legal

    [Fact] // legal — 废墟分割律：锚定+未锚定=降级数；锚定位=回放 home 格且格子必被占用
    public void dig_sites_partition_every_ruin_and_register_occupancy()
    {
        Scenario scenario = RunScenario(citySeed: 51, buildings: 90, radius: 16);
        RelicMappingResult mapping = scenario.Mapping;
        RelicDigSitePlan plan = scenario.Plan;

        Assert.Equal(mapping.RuinCount, plan.TotalRuins);
        Assert.Equal(plan.Sites.Count + plan.UnanchoredRuinCount, mapping.RuinCount);
        if (plan.Sites.Count > 0)
        {
            var homeByBuilding = mapping.Outcomes
                .Where(o => o.Kind == RelayOutcomeKind.RuinDowngraded)
                .ToDictionary(o => o.BuildingId, o => o.HomeCell!);
            foreach (RelicDigSite site in plan.Sites)
            {
                Assert.Equal(homeByBuilding[site.BuildingId], site.Cell); // 锚定=home 格
                // 格子必被占用：注册行=挖掘位自身，未注册行=资源节点（冲突源持有）
                Assert.True(scenario.Continent.World.IsOccupied(site.Cell));
            }

            // 挖掘位 id 段=700_000+ 与遗迹 800_000+/资源节点 900_000+ 互异：同 id
            // 重注册必拒（GridWorld 双注册执法——找一格空闲格注入，真因=已注册）
            CellCoord free = FindFreeCell(scenario.Continent);
            Assert.Throws<InvalidOperationException>(() => scenario.Continent.World.RegisterOccupancy(
                new Contracts.Foundation.EntityId(RelicDigSites.DigSiteEntityBase + 1), new[] { free }));
        }
    }

    private static CellCoord FindFreeCell(NewContinent continent)
    {
        for (int y = 0; y < continent.World.Height; y++)
        {
            for (int x = 0; x < continent.World.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (TerrainInfo.IsPassable(continent.World.TerrainAt(cell))
                    && !continent.World.IsOccupied(cell))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("96×96 大陆无空闲可通行格（场景构造问题）");
    }

    [Fact] // legal — 纪念馆修复位=挖掘位格序列（废墟原位修复，注册序镜像 MemorialAuraBoard）
    public void memorial_repair_slots_mirror_site_cells_in_value_order()
    {
        Scenario scenario = RunScenario(citySeed: 61, buildings: 120, radius: 16);
        Assert.Equal(
            scenario.Plan.Sites.Select(site => site.Cell).ToArray(),
            scenario.Plan.MemorialRepairSlots.ToArray());
        if (scenario.Plan.Sites.Count >= 2)
        {
            // 价值序残留：sites 继承回放的 tier 降序（首个不低于末个）
            Assert.True(scenario.Plan.Sites[0].Tier >= scenario.Plan.Sites[^1].Tier);
        }
    }

    [Fact] // legal — 确定性：同 (seed, continent) 两条通道产物逐字段相等
    public void dig_site_plan_is_deterministic()
    {
        Scenario first = RunScenario(citySeed: 71, buildings: 90, radius: 16);
        Scenario second = RunScenario(citySeed: 71, buildings: 90, radius: 16);
        Assert.Equal(first.Plan.Sites, second.Plan.Sites);
        Assert.Equal(first.Plan.UnanchoredRuinCount, second.Plan.UnanchoredRuinCount);
        Assert.Equal(first.Mapping.ComputeOutcomeHash(), second.Mapping.ComputeOutcomeHash());
    }

    [Fact] // legal — 对抗场景：全域水域让位关——降级废墟锚定在水域 home 格（考古沉城语义，
    // 占用注册不受地形通行性约束=GridWorld 占用层独立契约）；固定行布局在移位
    // 半径之外（让位半径 0 只留锚点一格平原，移位档不可达）
    public void drowned_ruins_anchor_on_water_home_cells()
    {
        var hostile = Parameters() with
        {
            ForestRatio = 0.0, RockRatio = 0.0, WaterRatio = 1.0,
            TerrainYieldRadiusCells = 0, ResourceNodeCount = 0, ThreatNodeCount = 0,
        };
        var facts = HostileCityFacts(6, hostile.Orientations);
        var continent = NewContinentFactory.Create(hostile, Seed, 2, new LcgRng(81));
        var anchor = continent.Anchor;
        Assert.Equal(TerrainKind.Plain, continent.World.TerrainAt(anchor)); // 让位半径 0=锚点单格平原
        Assert.Equal(TerrainKind.Water, continent.World.TerrainAt(facts[0].Cell)); // 城址全域水域

        var snapshot = new RelicCitySnapshot(anchor,
            CoreZoneGeometry.ComputeCoreZone(facts.Select(f => f.Cell).ToList(), anchor,
                hostile.CoreRadiusCells, hostile.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(
                f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(anchor), f.RelYTo(anchor))).ToList());
        RelicMappingResult mapping = new RelicMapper(hostile).Map(continent, snapshot);
        RelicDigSitePlan plan = RelicDigSites.Build(continent, mapping);

        Assert.Equal(6, mapping.RuinCount); // 全域水域+移位不可达锚点 → 全降级
        Assert.Equal(6, plan.TotalRuins);
        Assert.All(plan.Sites, site =>
        {
            Assert.Equal("terrain", site.Cause);
            Assert.True(site.OccupancyRegistered); // 水域无既有占用 → 注册成功
            Assert.Equal(TerrainKind.Water, continent.World.TerrainAt(site.Cell));
            Assert.True(continent.World.IsOccupied(site.Cell));
        });
    }

    // ---- boundary ----------------------------------------------------------

    [Fact] // boundary — 零降级（锚点单建筑，让位半径内必可通行）：空挖掘位计划
    public void intact_city_yields_an_empty_dig_site_plan()
    {
        RelicMappingParams table = Parameters();
        var facts = new List<RelicCityBuildingFact>
        {
            new(400_001, "era1:tower-wood-arrow", 2, 0, Anchor), // home=锚点（让位后平原）
        };
        var continent = NewContinentFactory.Create(table, Seed, 2, new LcgRng(1));
        var snapshot = new RelicCitySnapshot(Anchor,
            CoreZoneGeometry.ComputeCoreZone(new[] { Anchor }, Anchor,
                table.CoreRadiusCells, table.BuildMarginCells),
            facts.Select(f => new RelicBuildingSnapshot(
                f.BuildingId, f.TypeId, f.Tier, f.Orientation,
                f.RelXTo(Anchor), f.RelYTo(Anchor))).ToList());
        RelicMappingResult mapping = new RelicMapper(table).Map(continent, snapshot);

        Assert.Equal(1, mapping.IntactCount); // 让位半径 10=锚点必回放原位
        Assert.Equal(0, mapping.RuinCount);
        RelicDigSitePlan plan = RelicDigSites.Build(continent, mapping);
        Assert.Empty(plan.Sites);
        Assert.Empty(plan.MemorialRepairSlots);
        Assert.Equal(0, plan.TotalRuins);
        Assert.Equal(0, plan.UnanchoredRuinCount);
    }

    // ---- illegal -----------------------------------------------------------

    [Fact] // illegal — 空入参拒绝
    public void build_rejects_null_inputs()
    {
        Scenario scenario = RunScenario(citySeed: 101, buildings: 30, radius: 12);
        Assert.Throws<ArgumentNullException>(() => RelicDigSites.Build(null!, scenario.Mapping));
        Assert.Throws<ArgumentNullException>(() => RelicDigSites.Build(scenario.Continent, null!));
    }
}
