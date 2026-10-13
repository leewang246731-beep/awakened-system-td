using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-044] buildings-into-flow-field end to end (C-05×C-09×C-11, T-038):
// a real GridWorld + BuildingRegistry + FlowField chain — no test double in
// the seam (RL2). Thick walls (HP 500) with an open detour keep the field in
// Normal mode and route around; a sealed thin wall (HP 2) flips the field
// into WallTear and cuts straight through at the ×5 HP price
// (v3.2 §6「建筑血量×5 计代价」).
public class BuildingFlowFieldIntegrationTests
{
    private const int Width = 11;
    private const int Height = 5;
    private const int WallX = 5;

    private static (GridWorld World, BuildingRegistry Registry) Composed()
    {
        // All-plain seeded world: ratios 0, deterministic LcgRng — the map is
        // fully controlled by the buildings we place.
        var world = new GridWorld(Width, Height, new EraCore.Tests.World.LcgRng(20261014), 0, 0, 0);
        var registry = new BuildingRegistry(world);
        registry.AttachToWorld(); // T-038 DEBT wiring: world blood prices follow the table
        return (world, registry);
    }

    /// <summary>Wall spec from a registry-style JSON document (RL7: HP comes from data).</summary>
    private static TowerSpec Wall(int hp) => CombatContent.Load($$"""
        {"items":[{"id":"test:wall-hp{{hp}}","class":"support","tier":1,"max_tier":1,
          "cost_total":10,"dps":0.0,"damage_per_shot":0,"reload_ticks":null,
          "range_cells":0.0,"hp":{{hp}},"ammo_item":null,"ammo_per_shot":0}]}
        """)[0];

    /// <summary>Places a wall column at x=<see cref="WallX"/>, y in [0, count).</summary>
    private static void PlaceWallColumn(BuildingRegistry registry, int hp, int count)
    {
        for (int y = 0; y < count; y++)
        {
            registry.Place(new EntityId(100 + y), Wall(hp), new CellCoord(WallX, y));
        }
    }

    private static IReadOnlyList<CellCoord> WallCells(int count) =>
        Enumerable.Range(0, count).Select(y => new CellCoord(WallX, y)).ToList();

    // ------------------------------------------------------------ case 1: thick wall → detour (Normal mode)

    [Fact] // [F-044] HP 500 wall column with a one-cell gap: field stays Normal and detours
    public void Thick_wall_with_open_gap_is_detoured_around_in_normal_mode()
    {
        var (world, registry) = Composed();
        PlaceWallColumn(registry, hp: 500, count: Height - 1); // y 0..3 walled, gap at y=4
        var walls = WallCells(Height - 1);

        // The DEBT wiring shows up at the bare world level too.
        Assert.Equal(500, world.BloodCost(new CellCoord(WallX, 0)));
        Assert.Equal(0, world.BloodCost(new CellCoord(WallX, Height - 1))); // the gap cell

        var start = new CellCoord(0, 2);
        var target = new CellCoord(Width - 1, 2);
        var field = FlowField.BuildComplete(registry, target);

        Assert.True(field.IsReady);
        Assert.Equal(FlowFieldMode.Normal, field.Mode); // detour open: no cell sealed off
        Assert.True(field.IsReachable(start));

        var path = field.BuildPath(start);
        Assert.Equal(target, path[^1]);
        Assert.Contains(new CellCoord(WallX, Height - 1), path); // the only crossing is the gap
        Assert.All(path, c => Assert.False(walls.Contains(c), // 拆墙未触发：绕行不碰墙
            $"detour stepped on wall cell ({c.X},{c.Y})"));
    }

    // ------------------------------------------------------------ case 2: sealed thin wall → tear-through (WallTear mode)

    [Fact] // [F-044] HP 2 wall column sealing the map: WallTear mode cuts through at ×5 price
    public void Sealed_thin_wall_is_torn_through_at_exact_times_five_price()
    {
        var (_, registry) = Composed();
        PlaceWallColumn(registry, hp: 2, count: Height); // full seal: y 0..4

        var start = new CellCoord(0, 2);
        var target = new CellCoord(Width - 1, 2);
        var field = FlowField.BuildComplete(registry, target);

        Assert.True(field.IsReady);
        Assert.Equal(FlowFieldMode.WallTear, field.Mode); // 全封死→拆墙模式 (v3.2 §6)

        // Integrated cost, straight row y=2: entering 4 plain cells (cost 1)
        // + 1 wall cell (terrain 1 + HP 2 × blood multiplier 5 = 11) + 5 plain
        // cells = 20 cell-cost units × straight weight 10 = 200.
        Assert.Equal(200L, field.Distance(start));

        // The gradient walks the straight tear line and crosses exactly one wall cell.
        var path = field.BuildPath(start);
        var expected = Enumerable.Range(0, Width).Select(x => new CellCoord(x, 2)).ToList();
        Assert.Equal(expected, path);
        var crossed = path.Where(c => c.X == WallX).ToList();
        Assert.Single(crossed);
        Assert.True(field.IsTearCell(crossed[0]), "the crossed wall cell must be tearable");
        Assert.Equal(2, registry.BloodCost(crossed[0]));
    }

    [Fact] // [F-044] same seal with HP 500: the price scales ×5 per HP — 25100 vs 200
    public void Sealed_thick_wall_prices_twenty_five_times_higher()
    {
        var (_, registry) = Composed();
        PlaceWallColumn(registry, hp: 500, count: Height);

        var start = new CellCoord(0, 2);
        var target = new CellCoord(Width - 1, 2);
        var field = FlowField.BuildComplete(registry, target);

        Assert.Equal(FlowFieldMode.WallTear, field.Mode); // sealed: still tears, but…
        // …the straight line now books terrain 1 + 500×5 = 2501 for the wall
        // cell: (4×1 + 2501 + 5×1) × 10 = 25100 — 厚墙拆不起 (the ×5 law).
        Assert.Equal(25100L, field.Distance(start));
    }

    // ------------------------------------------------------------ teardown round trip

    [Fact] // [F-044] tearing a wall down reopens the corridor: remove → rebuild → Normal
    public void Removing_the_wall_restores_normal_reachability()
    {
        var (world, registry) = Composed();
        PlaceWallColumn(registry, hp: 2, count: Height);

        var start = new CellCoord(0, 2);
        var target = new CellCoord(Width - 1, 2);
        var torn = FlowField.BuildComplete(registry, target);
        Assert.Equal(FlowFieldMode.WallTear, torn.Mode);

        // The tear happens: the wall entity on the crossing cell is removed
        // (registry.Remove releases both the HP entry and the grid occupancy).
        var crossing = new CellCoord(WallX, 2);
        Assert.True(registry.TryGetAt(crossing, out var wall));
        Assert.True(registry.Remove(wall!.Id));
        Assert.False(world.IsOccupied(crossing));

        var healed = FlowField.BuildComplete(registry, target);
        Assert.Equal(FlowFieldMode.Normal, healed.Mode);
        Assert.True(healed.IsReachable(start));
        // Gap of one cell: the crossing is now free terrain (cost 1), so the
        // integrated price drops to (4×1 + 1 + 5×1) × 10 = 100.
        Assert.Equal(100L, healed.Distance(start));
    }
}
