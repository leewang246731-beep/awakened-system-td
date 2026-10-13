using EraCore.Contracts.Foundation;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.World;

/// <summary>
/// Deterministic 64-bit LCG test double for <see cref="IRng"/> (K-1:
/// tests inject fixed sequences; CS-3.4 — the port is the whitelisted
/// seam, this is a real seeded implementation, not a bypass).
/// </summary>
// [C-01] K-1 seeded-random port test double (IRng)
internal sealed class LcgRng : IRng
{
    private ulong _state;

    public LcgRng(long seed) => Reseed(seed);

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue));
        }

        long span = maxValue - (long)minValue;
        return minValue + (int)(NextDouble() * span);
    }

    public double NextDouble()
    {
        _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
        return (_state >> 11) * (1.0 / (1UL << 53));
    }

    public void Reseed(long seed) => _state = unchecked((ulong)seed);
}

// [C-05] grid world: procedural generation determinism, bounds, occupancy, zones
// and the Pathfinding.IFlowGrid seam (F-117 zone naming, F-043 cost/passable view).
public class GridWorldTests
{
    private const long SeedA = 20261013;
    private const long SeedB = 20261014;

    private static GridWorld Make(int width = 16, int height = 12, long seed = SeedA) =>
        new(width, height, new LcgRng(seed));

    // ---- construction & determinism --------------------------------------

    [Fact] // legal — same seed ⇒ identical map (F-154 determinism prerequisite)
    public void Same_seed_produces_identical_map_hash()
    {
        var a = Make(seed: SeedA);
        var b = Make(seed: SeedA);
        Assert.Equal(a.ComputeMapHash(), b.ComputeMapHash());
    }

    [Fact] // legal — different seeds redraw the map
    public void Different_seed_changes_map_hash()
    {
        Assert.NotEqual(Make(seed: SeedA).ComputeMapHash(), Make(seed: SeedB).ComputeMapHash());
    }

    [Fact] // legal — the generator honors ratio semantics
    public void Pure_ratios_generate_single_terrain_maps()
    {
        var allWater = new GridWorld(8, 8, new LcgRng(SeedA), forestRatio: 0, rockRatio: 0, waterRatio: 1.0);
        Assert.All(Enumerate(allWater), c => Assert.Equal(TerrainKind.Water, allWater.TerrainAt(c)));

        var allForest = new GridWorld(8, 8, new LcgRng(SeedA), forestRatio: 1.0, rockRatio: 0, waterRatio: 0);
        Assert.All(Enumerate(allForest), c => Assert.Equal(TerrainKind.Forest, allForest.TerrainAt(c)));

        var allPlain = new GridWorld(8, 8, new LcgRng(SeedA), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        Assert.All(Enumerate(allPlain), c => Assert.Equal(TerrainKind.Plain, allPlain.TerrainAt(c)));
    }

    [Fact] // boundary — full ratio budget (sum exactly 1) leaves no plain cells behind
    public void Full_ratio_budget_is_accepted()
    {
        var world = new GridWorld(8, 8, new LcgRng(SeedA), forestRatio: 0.3, rockRatio: 0.3, waterRatio: 0.4);
        Assert.All(
            Enumerate(world),
            c => Assert.NotEqual(TerrainKind.Plain, world.TerrainAt(c)));
    }

    [Fact] // legal — mixed ratios yield a map that actually contains several kinds
    public void Mixed_ratios_generate_varied_terrain()
    {
        var world = Make(width: 64, height: 64);
        var kinds = Enumerate(world).Select(world.TerrainAt).Distinct().ToHashSet();
        Assert.Contains(TerrainKind.Plain, kinds);
        Assert.True(kinds.Count >= 2, $"expected a mixed map, got [{string.Join(",", kinds)}]");
    }

    [Theory] // illegal — non-positive or oversized dimensions rejected
    [InlineData(0, 12)]
    [InlineData(-4, 12)]
    [InlineData(16, 0)]
    [InlineData(16, -1)]
    [InlineData(512, 4)] // T-032 task card: dimensions of 512+ are rejected (512 = MaxSide + 1)
    [InlineData(4, 512)]
    [InlineData(100000, 100000)]
    public void Constructor_rejects_invalid_dimensions(int width, int height)
    {
        // ArgumentOutOfRangeException derives from ArgumentException (task-card wording)
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GridWorld(width, height, new LcgRng(SeedA)));
    }

    [Fact] // boundary — extreme legal dimensions accepted; hash proves determinism + discrimination (review W1R1 #3: NotEqual(0) is near-tautological)
    public void Constructor_accepts_extreme_dimensions()
    {
        var tiny = new GridWorld(1, 1, new LcgRng(SeedA));
        Assert.Equal(1, tiny.Width);
        Assert.Equal(1, tiny.Height);

        var huge = new GridWorld(GridWorld.MaxSide, GridWorld.MaxSide, new LcgRng(SeedA));
        Assert.Equal(GridWorld.MaxSide, huge.Width);
        Assert.Equal(GridWorld.MaxSide, huge.Height);

        // Determinism: same seed at extreme dimensions → identical hash (F-154).
        var hugeAgain = new GridWorld(GridWorld.MaxSide, GridWorld.MaxSide, new LcgRng(SeedA));
        Assert.Equal(huge.ComputeMapHash(), hugeAgain.ComputeMapHash());

        // Seed sensitivity: a different seed redraws the huge map.
        var hugeOtherSeed = new GridWorld(GridWorld.MaxSide, GridWorld.MaxSide, new LcgRng(SeedB));
        Assert.NotEqual(huge.ComputeMapHash(), hugeOtherSeed.ComputeMapHash());

        // Discrimination: the huge map hash differs from a medium map's —
        // dimensions + full terrain both feed the FNV-1a mix.
        var medium = new GridWorld(64, 64, new LcgRng(SeedA));
        Assert.NotEqual(medium.ComputeMapHash(), huge.ComputeMapHash());
    }

    [Theory] // illegal — ratio corruption rejected
    [InlineData(-0.1, 0.1, 0.1)]
    [InlineData(0.1, -0.1, 0.1)]
    [InlineData(0.1, 0.1, -0.1)]
    [InlineData(0.6, 0.6, 0.6)]
    public void Constructor_rejects_invalid_ratios(double forest, double rock, double water)
    {
        Assert.Throws<ArgumentException>(
            () => new GridWorld(8, 8, new LcgRng(SeedA), forest, rock, water));
    }

    [Fact] // illegal — null rng rejected (CS-1.4: randomness only via the port)
    public void Constructor_rejects_null_rng()
    {
        Assert.Throws<ArgumentNullException>(() => new GridWorld(8, 8, rng: null!));
    }

    // ---- bounds & terrain access -----------------------------------------

    [Theory] // boundary — exact grid corners are inside
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(0, 11)]
    [InlineData(15, 11)]
    public void Contains_accepts_exact_boundaries(int x, int y)
    {
        Assert.True(Make().Contains(new CellCoord(x, y)));
    }

    [Theory] // illegal — one step past any edge (incl. negative coordinates) is outside
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(16, 0)]
    [InlineData(0, 12)]
    [InlineData(int.MinValue, int.MinValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Contains_rejects_out_of_grid(int x, int y)
    {
        Assert.False(Make().Contains(new CellCoord(x, y)));
    }

    [Theory] // illegal — reading/writing terrain outside the grid throws
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    [InlineData(16, 4)]
    [InlineData(4, 12)]
    public void Terrain_access_throws_out_of_bounds(int x, int y)
    {
        var world = Make();
        var cell = new CellCoord(x, y);
        Assert.Throws<ArgumentOutOfRangeException>(() => world.TerrainAt(cell));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.SetTerrain(cell, TerrainKind.Wall));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.IsPassable(cell));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.IsOccupied(cell));
    }

    [Fact] // illegal — undefined enum value rejected on write
    public void SetTerrain_rejects_undefined_enum_value()
    {
        var world = Make();
        Assert.Throws<ArgumentException>(
            () => world.SetTerrain(new CellCoord(0, 0), (TerrainKind)99));
    }

    [Fact] // legal — SetTerrain then TerrainAt round-trips
    public void SetTerrain_round_trips()
    {
        var world = Make();
        var cell = new CellCoord(3, 4);
        world.SetTerrain(cell, TerrainKind.Road);
        Assert.Equal(TerrainKind.Road, world.TerrainAt(cell));
    }

    [Fact] // legal — map hash tracks terrain edits
    public void Map_hash_changes_with_terrain_edits()
    {
        var a = Make();
        var b = Make();
        b.SetTerrain(new CellCoord(0, 0), b.TerrainAt(new CellCoord(0, 0)) == TerrainKind.Rock
            ? TerrainKind.Plain
            : TerrainKind.Rock);
        Assert.NotEqual(a.ComputeMapHash(), b.ComputeMapHash());
    }

    // ---- IFlowGrid seam (C-11, F-043) -------------------------------------

    [Fact] // legal — flow grid view mirrors terrain rules
    public void FlowGrid_view_exposes_width_height_cost_passable()
    {
        GridWorld world = Make(4, 3);
        Assert.Equal(4, world.Width);
        Assert.Equal(3, world.Height);

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new CellCoord(x, y);
                var kind = world.TerrainAt(cell);
                Assert.Equal(TerrainInfo.IsPassable(kind), world.Passable(cell));
                Assert.Equal(TerrainInfo.Cost(kind), world.Cost(cell));
                Assert.Equal(0, world.BloodCost(cell)); // terrain layer owns no HP (C-09 lands later)
            }
        }
    }

    [Fact] // boundary — costs never undercut the flow field minimum (FlowField.MinimumCellCost = 1)
    public void FlowGrid_costs_never_undercut_minimum_cell_cost()
    {
        var world = Make(24, 24);
        Assert.All(
            Enumerate(world),
            c => Assert.True(world.Cost(c) >= 1, $"cost under 1 at ({c.X},{c.Y})"));
    }

    [Fact] // illegal — flow grid queries out of bounds throw
    public void FlowGrid_view_rejects_out_of_bounds()
    {
        GridWorld world = Make();
        var cell = new CellCoord(-2, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Cost(cell));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Passable(cell));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.BloodCost(cell));
    }

    // ---- occupancy --------------------------------------------------------

    [Fact] // legal — register, query, clear round-trip
    public void Occupancy_register_query_clear_round_trips()
    {
        var world = Make();
        var id = new EntityId(7);
        var cells = new[] { new CellCoord(1, 1), new CellCoord(2, 1) };

        world.RegisterOccupancy(id, cells);
        Assert.True(world.IsOccupied(cells[0]));
        Assert.True(world.IsOccupied(cells[1]));
        Assert.True(world.TryGetOccupant(cells[0], out EntityId occupant));
        Assert.Equal(id, occupant);

        Assert.True(world.ClearOccupancy(id));
        Assert.False(world.IsOccupied(cells[0]));
        Assert.False(world.IsOccupied(cells[1]));
        Assert.False(world.ClearOccupancy(id)); // already gone: reports false, no throw
    }

    [Fact] // legal — distinct entities coexist on disjoint cells
    public void Occupancy_allows_disjoint_entities()
    {
        var world = Make();
        world.RegisterOccupancy(new EntityId(1), new[] { new CellCoord(0, 0) });
        world.RegisterOccupancy(new EntityId(2), new[] { new CellCoord(5, 5) });
        Assert.True(world.TryGetOccupant(new CellCoord(0, 0), out var first));
        Assert.Equal(new EntityId(1), first);
        Assert.True(world.TryGetOccupant(new CellCoord(5, 5), out var second));
        Assert.Equal(new EntityId(2), second);
    }

    [Fact] // illegal — overlapping occupancy rejected with the conflicting cell named
    public void Occupancy_conflict_throws_with_cell_details()
    {
        var world = Make();
        world.RegisterOccupancy(new EntityId(1), new[] { new CellCoord(2, 2) });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            world.RegisterOccupancy(new EntityId(2), new[] { new CellCoord(3, 3), new CellCoord(2, 2) }));
        Assert.Contains("(2, 2)", ex.Message);
        Assert.False(world.IsOccupied(new CellCoord(3, 3)), "registration must be atomic: no partial state");
    }

    [Fact] // illegal — re-registering an already placed entity rejected
    public void Occupancy_re_register_without_clear_throws()
    {
        var world = Make();
        var id = new EntityId(9);
        world.RegisterOccupancy(id, new[] { new CellCoord(0, 0) });
        Assert.Throws<InvalidOperationException>(() =>
            world.RegisterOccupancy(id, new[] { new CellCoord(1, 1) }));
    }

    [Theory] // illegal — malformed registrations rejected
    [InlineData(0)] // empty list
    [InlineData(-1)] // out of bounds cell
    [InlineData(99)] // out of bounds cell
    public void Occupancy_rejects_malformed_input(int x)
    {
        var world = Make();
        var cells = x == 0
            ? Array.Empty<CellCoord>()
            : new[] { new CellCoord(x, 0) };
        Assert.Throws<ArgumentException>(() => world.RegisterOccupancy(new EntityId(1), cells));
        Assert.Throws<ArgumentNullException>(() => world.RegisterOccupancy(new EntityId(1), null!));
        Assert.Throws<ArgumentNullException>(() => world.RegisterOccupancy(null!, new[] { new CellCoord(0, 0) }));
    }

    [Fact] // illegal — occupant query out of bounds throws (not silently false)
    public void Occupant_query_out_of_bounds_throws()
    {
        var world = Make();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => world.TryGetOccupant(new CellCoord(-1, 0), out _));
    }

    // ---- zones (F-117 partition naming) -----------------------------------

    [Theory] // legal — well-formed language-neutral ids: ^[a-z0-9-]+$ (T-032 task card)
    [InlineData("north-gate")]
    [InlineData("z1")]
    [InlineData("a")]
    [InlineData("1north")] // leading digit is legal in the task-card charset
    [InlineData("-gate")] // hyphen is in the task-card charset
    [InlineData("north-2-gate")]
    [InlineData("mvp")]
    public void Zone_ids_accept_lane_style_ids(string zoneId)
    {
        Assert.True(GridWorld.IsValidZoneId(zoneId));
    }

    [Fact] // boundary — minimal id, and no length cap in ^[a-z0-9-]+$ (only the charset is constrained)
    public void Zone_ids_accept_minimal_and_long_ids()
    {
        Assert.True(GridWorld.IsValidZoneId("a"));
        Assert.True(GridWorld.IsValidZoneId(new string('a', 128) + "-0"));
    }

    [Theory] // illegal — ids violating the language-neutral rule
    [InlineData("")]
    [InlineData("NorthGate")] // uppercase
    [InlineData("mid_lane")] // underscore is outside the task-card charset
    [InlineData("_north")] // underscore
    [InlineData("north gate")] // space
    [InlineData("北门")] // non-ASCII — display names live in content data, not ids
    public void Zone_ids_reject_non_lane_style_ids(string zoneId)
    {
        Assert.False(GridWorld.IsValidZoneId(zoneId));
    }

    [Fact] // illegal — null is not a zone id
    public void Zone_ids_reject_null()
    {
        Assert.False(GridWorld.IsValidZoneId(null));
    }

    [Fact] // legal — define then look up zones; zones may overlap
    public void Zone_define_and_lookup_round_trips()
    {
        var world = Make();
        var cellsA = new[] { new CellCoord(0, 0), new CellCoord(1, 0) };
        var cellsB = new[] { new CellCoord(1, 0), new CellCoord(2, 0) }; // overlaps A

        world.DefineZone("north-gate", cellsA);
        world.DefineZone("east-road", cellsB);

        Assert.True(world.ZoneExists("north-gate"));
        Assert.True(world.TryGetZoneCells("north-gate", out var lookup));
        Assert.Equal(cellsA, lookup);
        Assert.True(world.TryGetZoneCells("east-road", out var lookupB));
        Assert.Equal(cellsB, lookupB);
        Assert.False(world.ZoneExists("south-gate"));
        Assert.False(world.TryGetZoneCells("south-gate", out var none));
        Assert.Empty(none);
    }

    [Fact] // illegal — duplicate zone id rejected
    public void Zone_duplicate_id_throws()
    {
        var world = Make();
        world.DefineZone("north-gate", new[] { new CellCoord(0, 0) });
        Assert.Throws<ArgumentException>(
            () => world.DefineZone("north-gate", new[] { new CellCoord(5, 5) }));
    }

    [Theory] // illegal — malformed zone definitions rejected
    [InlineData("NorthGate", 0, 0)] // bad id casing
    [InlineData("", 0, 0)] // empty id
    [InlineData("ok-id", -1, 0)] // out of bounds cell
    [InlineData("ok-id", 99, 0)] // out of bounds cell
    public void Zone_define_rejects_malformed_input(string zoneId, int x, int y)
    {
        var world = Make();
        Assert.Throws<ArgumentException>(() => world.DefineZone(zoneId, new[] { new CellCoord(x, y) }));
    }

    [Fact] // illegal — empty zone cell list rejected
    public void Zone_define_rejects_empty_cell_list()
    {
        var world = Make();
        Assert.Throws<ArgumentException>(() => world.DefineZone("valid-id", Array.Empty<CellCoord>()));
    }

    private static IEnumerable<CellCoord> Enumerate(GridWorld world)
    {
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                yield return new CellCoord(x, y);
            }
        }
    }
}
