using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-044] placed-building bookkeeping of the combat composition (C-09,
// T-038): occupancy through the C-05 world, HP on the tear-price lookup,
// composed IFlowGrid view (building cells block but stay tearable), and the
// GridWorld.BloodCostOverride wiring that clears the T-038 DEBT.
public class BuildingRegistryTests
{
    private static GridWorld PlainWorld(int width = 8, int height = 6) =>
        new(width, height, new EraCore.Tests.World.LcgRng(20261014), 0, 0, 0);

    private static TowerSpec Wall(int hp) => CombatContent.Load($$"""
        {"items":[{"id":"test:wall-hp{{hp}}","class":"support","tier":1,"max_tier":1,
          "cost_total":10,"dps":0.0,"damage_per_shot":0,"reload_ticks":null,
          "range_cells":0.0,"hp":{{hp}},"ammo_item":null,"ammo_per_shot":0}]}
        """)[0];

    [Fact] // legal — placing a tower registers occupancy, HP and the K-6 view
    public void Place_registers_cell_occupancy_and_hp()
    {
        // [F-044] GridWorld.Place occupancy API + HP bookkeeping
        var world = PlainWorld();
        var registry = new BuildingRegistry(world);
        var cell = new CellCoord(3, 2);

        var building = registry.Place(new EntityId(11), CombatContent.Tower("era1:tower-wood-arrow"), cell);

        Assert.Equal(new EntityId(11), building.Id);
        Assert.Equal("era1:tower-wood-arrow", building.ContentId);
        Assert.Equal(cell, building.Cell);
        Assert.Equal(120, building.Hp);   // hp from towers.json (RL7)
        Assert.Equal(120, building.MaxHp);
        Assert.Equal(1, building.Tier);
        Assert.Equal(1, registry.Count);
        Assert.True(world.IsOccupied(cell));
        Assert.True(registry.TryGetAt(cell, out var found));
        Assert.Equal(new EntityId(11), found!.Id);
    }

    [Fact] // legal — the composed IFlowGrid view blocks building cells but prices them
    public void Composed_flow_grid_marks_building_cells_blocking_and_tearable()
    {
        // [F-044] blocking-but-tearable: Passable=false + BloodCost=HP on the
        // building cell; plain terrain elsewhere
        var world = PlainWorld();
        var registry = new BuildingRegistry(world);
        var cell = new CellCoord(4, 3);
        registry.Place(new EntityId(12), CombatContent.Tower("era1:tower-rockfall"), cell);

        Assert.False(registry.Passable(cell));
        Assert.Equal(150, registry.BloodCost(cell)); // rockfall hp (towers.json)
        Assert.Equal(world.Cost(cell), registry.Cost(cell)); // terrain cost forwards
        Assert.Equal(world.Width, registry.Width);
        Assert.Equal(world.Height, registry.Height);

        var free = new CellCoord(0, 0);
        Assert.True(registry.Passable(free));
        Assert.Equal(0, registry.BloodCost(free));
    }

    [Fact] // legal — DEBT wiring: a bare world reports HP after AttachToWorld
    public void AttachToWorld_feeds_the_blood_cost_lookup_into_the_world()
    {
        // [F-044] T-038 DEBT: GridWorld.BloodCost consults the building table
        var world = PlainWorld();
        var registry = new BuildingRegistry(world);
        var cell = new CellCoord(2, 4);
        registry.Place(new EntityId(13), CombatContent.Tower("era1:tower-spike-trap"), cell);

        Assert.Equal(0, world.BloodCost(cell)); // before wiring: world knows no buildings
        registry.AttachToWorld();
        Assert.Equal(40, world.BloodCost(cell)); // spike-trap hp from towers.json
        Assert.Equal(0, world.BloodCost(new CellCoord(7, 5))); // no building → 0
    }

    [Fact] // legal — removal releases the cell and the price
    public void Remove_releases_cell_and_drops_the_price()
    {
        // [F-044] teardown after a wall is torn down
        var world = PlainWorld();
        var registry = new BuildingRegistry(world);
        var cell = new CellCoord(1, 1);
        var id = new EntityId(14);
        registry.Place(id, Wall(2), cell);
        registry.AttachToWorld();

        Assert.True(registry.Remove(id));
        Assert.False(world.IsOccupied(cell));
        Assert.Equal(0, world.BloodCost(cell));
        Assert.Equal(0, registry.Count);
        Assert.True(registry.Passable(cell));

        Assert.False(registry.Remove(id)); // second removal: nothing to remove
    }

    [Fact] // illegal — overlapping placements are rejected by the world invariants
    public void Overlapping_placement_is_rejected()
    {
        // [F-044] C-05 owns the overlap invariant; the registry stays untouched
        var world = PlainWorld();
        var registry = new BuildingRegistry(world);
        var cell = new CellCoord(5, 5);
        registry.Place(new EntityId(15), Wall(500), cell);

        Assert.Throws<InvalidOperationException>(
            () => registry.Place(new EntityId(16), Wall(2), cell));
        Assert.Equal(1, registry.Count);
        Assert.Equal(500, registry.BloodCost(cell)); // first placement survived intact
    }

    [Fact] // illegal — out-of-bounds cells are rejected
    public void Out_of_bounds_placement_is_rejected()
    {
        // [F-044] C-05 bounds contract re-thrown
        var registry = new BuildingRegistry(PlainWorld());
        Assert.Throws<ArgumentException>(
            () => registry.Place(new EntityId(17), Wall(2), new CellCoord(99, 99)));
    }

    [Fact] // illegal — tier above the registry ceiling is rejected
    public void Tier_above_max_tier_is_rejected()
    {
        // [F-027] tier ∈ [1, max_tier] (wood arrow max_tier=2)
        var registry = new BuildingRegistry(PlainWorld());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.Place(
                new EntityId(18), CombatContent.Tower("era1:tower-wood-arrow"),
                new CellCoord(0, 0), tier: 3));
    }

    [Fact] // illegal — a null registry target lookup is simply absent, null ids rejected
    public void Null_id_operations_are_rejected()
    {
        // [F-044] guards on entry
        var registry = new BuildingRegistry(PlainWorld());
        Assert.Throws<ArgumentNullException>(() => registry.Place(null!, Wall(2), new CellCoord(0, 0)));
        Assert.Throws<ArgumentNullException>(() => registry.Remove(null!));
        Assert.False(registry.TryGetAt(new CellCoord(0, 0), out _));
    }
}
