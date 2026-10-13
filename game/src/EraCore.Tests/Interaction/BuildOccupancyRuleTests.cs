using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Units;
using EraCore.Tests.Logistics;
using Xunit;

namespace EraCore.Tests.Interaction;

// [C-23][F-036] T-056 DEBT spike2-pit#6 纯规则面：占格建筑放置 × 单位站位
// 互斥/推移（spike2-report §5 坑 6 清账）。三场景 = 放置格站敌→拒绝、民夫
// 正在该格装卸→拒绝、民夫在途→不拒绝（推移归执行段）。拒绝码词表与
// SimulationActionView（预览）/Simulation（SimCommand 再校验）共享。
public class BuildOccupancyRuleTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");

    /// <summary>Unit placed at a cell centre (spawn convention: centre px, WaveSpawner semantics).</summary>
    private static Unit UnitAt(long id, int cellX, int cellY)
    {
        float centre = Simulation.CellSizePx / 2f;
        return new Unit(
            new EntityId(id),
            "era1:enemy-beastling",
            (cellX * Simulation.CellSizePx) + centre,
            (cellY * Simulation.CellSizePx) + centre,
            maxHp: 100,
            armor: 0,
            speedPxPerTick: 1.0f);
    }

    /// <summary>Carrier built on a two-cell route (home, destination) — real Carrier, test cargo ports.</summary>
    private static Carrier CarrierOn((int X, int Y) home, (int X, int Y) destination) => new(
        new EntityId(9_000),
        Wood,
        new StockSupplier(initialStock: 10),
        new GateReceiver(initialCapacity: 10),
        new[] { new CellCoord(home.X, home.Y), new CellCoord(destination.X, destination.Y) });

    [Fact] // legal：三场景之外的一切站位——空单位表/邻格敌/死敌/在途民夫——全部放行（在途民夫=推移，非拒绝）
    public void Standing_units_outside_the_three_reject_branches_never_block() // [C-23]
    {
        var cell = new CellCoord(5, 5);
        var units = new List<Unit> { UnitAt(1, 5, 6), UnitAt(2, 4, 5) }; // 邻格敌：不挡本格
        var dead = UnitAt(3, 5, 5); // 本格但已阵亡
        Assert.True(dead.ApplyDamage(999, tick: 0) >= 0, "ApplyDamage settled");
        Assert.False(dead.Alive, "预置：该敌已死亡");
        units.Add(dead);

        var movingCarrier = CarrierOn((5, 7), (6, 7));
        Advance(movingCarrier, Carrier.LoadTicks + 1); // 补给充足 → Outbound（在途）
        Assert.Equal(CarrierPhase.Outbound, movingCarrier.Phase);

        Assert.False(BuildOccupancyRule.EnemyStandingOn(cell, units));
        Assert.False(BuildOccupancyRule.CarrierServicingOn(cell, new[] { movingCarrier }));
        Assert.Null(BuildOccupancyRule.UnitLayerRejection(cell, units, new[] { movingCarrier }));
    }

    [Fact] // illegal：场景一——放置格上站着活敌 → 拒绝码 cell-enemy-occupied
    public void Living_enemy_inside_the_cell_rejects_with_the_shared_code() // [C-23]
    {
        var cell = new CellCoord(5, 5);
        var units = new List<Unit> { UnitAt(1, 5, 5), UnitAt(2, 6, 6) };

        Assert.True(BuildOccupancyRule.EnemyStandingOn(cell, units));
        Assert.Equal(
            BuildOccupancyRule.EnemyStandingRejection,
            BuildOccupancyRule.UnitLayerRejection(cell, units, Array.Empty<Carrier>()));
        Assert.Equal("cell-enemy-occupied", BuildOccupancyRule.EnemyStandingRejection);
    }

    [Fact] // illegal：场景二——民夫正在该格装卸（Loading 于 home / Unloading 于 destination）→ cell-carrier-servicing
    public void Servicing_porter_on_the_cell_rejects_both_window_kinds() // [C-23]
    {
        var home = new CellCoord(5, 5);
        var destination = new CellCoord(5, 6);

        Carrier loading = CarrierOn((5, 5), (5, 6));
        Assert.Equal(CarrierPhase.Loading, loading.Phase); // 出生即 Loading 于 home
        Assert.Equal(
            BuildOccupancyRule.CarrierServicingRejection,
            BuildOccupancyRule.UnitLayerRejection(home, Array.Empty<Unit>(), new[] { loading }));

        Carrier unloading = CarrierOn((5, 5), (5, 6));
        Advance(unloading, Carrier.LoadTicks + 20); // 装载 20t + 16t 走完 32px 段（+4t 余量）→ Unloading 窗口内
        Assert.Equal(CarrierPhase.Unloading, unloading.Phase);
        Assert.Equal(
            BuildOccupancyRule.CarrierServicingRejection,
            BuildOccupancyRule.UnitLayerRejection(destination, Array.Empty<Unit>(), new[] { unloading }));

        // 卸载中的民夫不挡 home 格（服务点≠home），装载中的不挡 destination 格
        Assert.Null(BuildOccupancyRule.UnitLayerRejection(destination, Array.Empty<Unit>(), new[] { loading }));
        Assert.Null(BuildOccupancyRule.UnitLayerRejection(home, Array.Empty<Unit>(), new[] { unloading }));
    }

    [Theory] // boundary：像素-格子换算边界——格左界恰好、右界内 0.001px、越 1px 进邻格
    [InlineData(0.0f, 0)]     // 格左界 px 恰好落第 0 格
    [InlineData(31.999f, 0)]  // 第 0 格右极限内 0.001px
    [InlineData(32.0f, 1)]    // 越 1px → 第 1 格
    [InlineData(63.999f, 1)]  // 第 1 格右极限内
    public void Pixel_to_cell_edges_settle_on_the_correct_cell(float offsetPx, int expectedCell) // [C-23]
    {
        var unit = new Unit(
            new EntityId(1), "era1:enemy-beastling",
            offsetPx, 9 * Simulation.CellSizePx + 1f, 100, 0, 1.0f);
        var units = new List<Unit> { unit };

        Assert.Equal(expectedCell, (int)Math.Floor(unit.X / Simulation.CellSizePx));
        Assert.True(
            BuildOccupancyRule.EnemyStandingOn(new CellCoord(expectedCell, 9), units),
            $"({offsetPx}px, y=9) 的敌必须命中格 {expectedCell}");
        Assert.False(
            BuildOccupancyRule.EnemyStandingOn(new CellCoord(expectedCell + 1, 9), units),
            "右邻格必不命中");
    }

    [Fact] // illegal：越权防御——null 入参按契约抛 ArgumentNullException（不是静默放行）
    public void Null_arguments_throw_instead_of_answering() // [C-23]
    {
        Assert.Throws<ArgumentNullException>(
            () => BuildOccupancyRule.EnemyStandingOn(null!, Array.Empty<Unit>()));
        Assert.Throws<ArgumentNullException>(
            () => BuildOccupancyRule.CarrierServicingOn(null!, Array.Empty<Carrier>()));
    }

    private static void Advance(Carrier carrier, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            carrier.Tick();
        }
    }
}
