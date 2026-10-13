using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-070] C-03 世界视图适配器：Simulation 公共视图 → IActionWorldView 投影正确性
public class SimulationActionViewTests
{
    [Fact]
    public void View_projects_tick_wave_and_resources_from_the_simulation()
    {
        // legal：t=0 时点与公共视图一致（库存=初始授予 100 木）
        var (simulation, view) = RegistryFixture.Create();
        Assert.Equal(simulation.CurrentTick, view.Tick);
        Assert.Equal(simulation.CurrentWave, view.CurrentWave);
        ResourceStockLine wood = Assert.Single(view.Resources, r => r.Resource.Value == "era1:res-wood");
        Assert.Equal(simulation.Stock.AmountOf(new ResourceKey("era1:res-wood")), wood.Amount);
    }

    [Fact]
    public void Cost_currency_and_tower_prices_come_from_the_level_catalog()
    {
        // legal：计价币种与塔价来自内容表（towers.json cost_total，非硬编码）
        var (_, view) = RegistryFixture.Create();
        Assert.Equal(SimE1Fixture.Wood, view.CostCurrency);
        Assert.True(view.TryGetTowerCost("era1:tower-wood-arrow", out double cost));
        Assert.Equal(SimEra1Content.WoodArrowTower().CostTotal, cost);

        // illegal：目录外塔 id 不给价
        Assert.False(view.TryGetTowerCost("era1:tower-missing", out _));
    }

    [Fact]
    public void Buildability_uses_the_sim_rejection_vocabulary()
    {
        // legal：仓库格=占用、界外=越界、空地=可建；岩石格=地形不可建
        var (simulation, view) = RegistryFixture.Create();

        Assert.True(view.IsInsideGrid(SimE1Fixture.WarehouseCell));
        Assert.False(view.IsBuildable(SimE1Fixture.WarehouseCell, out string? occupied));
        Assert.Equal("cell-occupied", occupied);

        Assert.False(view.IsInsideGrid(new CellCoord(-1, 0)));
        Assert.False(view.IsBuildable(new CellCoord(-1, 0), out string? outside));
        Assert.Equal("cell-out-of-bounds", outside);

        Assert.True(view.IsBuildable(new CellCoord(12, 8), out string? free)
            || TryFindBuildableCell(simulation, view, out free), $"应有可建格（{free}）");

        // 岩石格：按地形拒绝（seed 固定下 5% 岩石率必有岩格）
        CellCoord rock = FindTerrain(simulation, TerrainKind.Rock);
        Assert.False(view.IsBuildable(rock, out string? terrain));
        Assert.Equal("cell-not-buildable", terrain);
    }

    [Fact]
    public void Enemy_presence_counts_only_living_units()
    {
        // legal：首波入场后在场敌数=存活单位数（与 Simulation.Units 一致）
        var (simulation, view) = RegistryFixture.Create(stepToTick: 3_300);
        int alive = simulation.Units.Count(u => u.Alive);
        Assert.True(alive > 0, "首波应已入场");
        Assert.Equal(alive, view.EnemiesPresent);
        Assert.Equal(simulation.Units.Where(u => u.Alive).Sum(u => u.Hp), view.EnemyHpPresent);
    }

    [Fact]
    public void Adapter_rejects_null_constructor_arguments()
    {
        // illegal：null 仿真/配置立即拒绝
        var (simulation, _) = RegistryFixture.Create();
        var config = SimE1Fixture.Config();
        Assert.Throws<ArgumentNullException>(() => new SimulationActionView(null!, config));
        Assert.Throws<ArgumentNullException>(() => new SimulationActionView(simulation, null!));
    }

    private static bool TryFindBuildableCell(Simulation simulation, SimulationActionView view, out string? note)
    {
        for (int y = 0; y < simulation.World.Height; y++)
        {
            for (int x = 0; x < simulation.World.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (view.IsBuildable(cell, out _))
                {
                    note = null;
                    return true;
                }
            }
        }

        note = "none-buildable";
        return false;
    }

    private static CellCoord FindTerrain(Simulation simulation, TerrainKind kind)
    {
        for (int y = 0; y < simulation.World.Height; y++)
        {
            for (int x = 0; x < simulation.World.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (simulation.World.TerrainAt(cell) == kind)
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException($"未找到 {kind} 地形格（seed 固定应存在）");
    }
}
