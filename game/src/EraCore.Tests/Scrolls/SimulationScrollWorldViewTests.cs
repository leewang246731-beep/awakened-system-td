using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Scrolls;
using EraCore.Sim;
using EraCore.Units;
using Xunit;
using EraCore.Tests.Sim;

namespace EraCore.Tests.Scrolls;

// [C-18][F-037] T-071 Sim 视图适配层：SimulationScrollWorldView 只做公共面
// 的纯投影（Units/Towers/Buildings HP join/分区/资源池），零内部触碰、零变异。
public class SimulationScrollWorldViewTests
{
    private static (Simulation Sim, SimulationScrollWorldView View) Build()
    {
        var rng = new SimRng(20261019);
        var config = new SimulationConfig(
            Width: 10,
            Height: 10,
            ForestRatio: 0.0,
            RockRatio: 0.0,
            WaterRatio: 0.0,
            BaseCell: new CellCoord(5, 9),
            EntryCells: new[] { new CellCoord(5, 0) },
            TowerCatalog: SimEra2Content.LoadTowerSpecs(),
            Enemies: SimEra2Content.LoadEnemyCatalog(),
            Waves: new WaveSpawnConfig(
                BaseCount: 4, BeginWave: 99, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era2:enemy-skirmisher" },
                FirstWaveGraceMultiplier: 1.0, ValleySeconds: 15.0, BatchesPerWave: 1,
                TickRateHz: 30, CellSizePx: 32,
                EntryCells: new[] { new CellCoord(5, 0) }),
            InitialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(new ResourceKey("era2:res-timber"), 300.0),
            },
            Costs: new BuildCostPolicy(new ResourceKey("era2:res-timber")));
        var sim = new Simulation(config, rng, seed: 20261019);
        sim.World.DefineZone("north", new[] { new CellCoord(5, 1), new CellCoord(5, 2), new CellCoord(6, 1) });
        _ = sim.Enqueue(new SimCommand(
            5, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-tower\",\"tower\":\"era2:tower-arrow\",\"cell\":{\"x\":6,\"y\":1}}"));
        var view = new SimulationScrollWorldView(sim, new[] { "north", "unused" });
        return (sim, view);
    }

    [Fact]
    public void Empty_sim_projects_empty_facts_and_zones_only_report_defined()
    {
        // legal: 空关卡投影——无敌无塔；Zones 只报已定义分区（注入 id 过滤）。
        var (sim, view) = Build();
        Assert.Equal(0, sim.CurrentTick);
        Assert.Empty(view.Enemies);
        Assert.Empty(view.Towers);
        Assert.Single(view.Zones);
        Assert.Equal("north", view.Zones[0].ZoneId);
        Assert.Equal(300.0, view.ResourceAmount(new ResourceKey("era2:res-timber")), 3);
    }

    [Fact]
    public void Tower_fact_joins_building_hp_with_spec_class_and_zone()
    {
        // legal: 塔事实=SimTower（类别/格位）⋈ SimBuilding（HP）；分区归属按格判定。
        var (sim, view) = Build();
        sim.Run(10); // tower built at tick 5

        ScrollTowerFact tower = Assert.Single(view.Towers);
        Assert.Equal("era2:tower-arrow", tower.ContentId);
        Assert.Equal(TowerClass.Pierce, tower.TowerClass);
        Assert.Equal("north", tower.ZoneId);
        Assert.Equal(6, tower.CellX);
        Assert.Equal(1, tower.CellY);
        Assert.Equal(150, tower.MaxHp); // era2 towers.json hp 锚点
        Assert.Equal(150, tower.Hp);
    }

    [Fact]
    public void Enemy_facts_carry_zone_and_position_after_spawn()
    {
        // legal: 波 1 于 grace=2160×1.0 入场；BaseCount 4→(4+0)÷2×2.0=4 散兵
        // （威胁 1.2 数量乘子手算）同批入场，入场后首 tick y=17.6 → row 0
        // （north 只含 rows 1~2 → zone 为空）。
        var (sim, view) = Build();
        sim.Run(2161);

        Assert.Equal(1, sim.CurrentWave);
        Assert.Equal(4, view.Enemies.Count);
        foreach (ScrollEnemyFact enemy in view.Enemies)
        {
            Assert.Equal(90, enemy.Hp); // ⌈60×1.5⌉（威胁 1.2 HP 乘子手算）
            Assert.Equal(90, enemy.MaxHp);
            Assert.Equal(5, enemy.CellX); // 入口列
            Assert.Equal(0, enemy.CellY);
            Assert.Null(enemy.ZoneId); // row 0 不在 north 分区
        }

        sim.Run(30); // y=17.6+1.6×30=65.6 → row 2 → (5,2) ∈ north
        Assert.Equal("north", view.Enemies[0].ZoneId!);
    }

    [Fact]
    public void View_is_read_only_against_the_simulation()
    {
        // boundary: 反复读取投影不改变仿真状态哈希（纯投影，无副作用）。
        var (sim, view) = Build();
        sim.Run(50);
        long hashBefore = sim.ComputeStateHash();
        _ = view.Enemies;
        _ = view.Towers;
        _ = view.Zones;
        _ = view.ResourceAmount(new ResourceKey("era2:res-timber"));
        Assert.Equal(hashBefore, sim.ComputeStateHash());
    }
}
