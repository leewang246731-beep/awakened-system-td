using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Pathfinding;
using EraCore.World;

namespace EraCore.Combat;

/// <summary>
/// One placed building entry (K-6 <see cref="IBuilding"/> view): content id,
/// cell, hit points, tier. Immutable in the E1 thin version — combat-side HP
/// mutation lands with the unit damage system (C-10/C-12).
/// </summary>
public sealed class PlacedBuilding : IBuilding
{
    internal PlacedBuilding(EntityId id, string contentId, CellCoord cell, int hp, int tier)
    {
        Id = id;
        ContentId = contentId;
        Cell = cell;
        Hp = hp;
        MaxHp = hp;
        Tier = tier;
    }

    /// <inheritdoc />
    public EntityId Id { get; }

    /// <inheritdoc />
    public string ContentId { get; }

    /// <inheritdoc />
    public CellCoord Cell { get; }

    /// <inheritdoc />
    public int Hp { get; }

    /// <inheritdoc />
    public int MaxHp { get; }

    /// <inheritdoc />
    public int Tier { get; }
}

/// <summary>
/// Placed-building bookkeeping of the combat composition (C-09 E1, T-038;
/// F-044): every placed tower/wall occupies one grid cell (occupancy goes
/// through <see cref="GridWorld.RegisterOccupancy"/> — C-05 owns the grid)
/// and carries its registry hit points.
/// <para>
/// Flow-field seam — this class IS the composed C-05×C-09 grid the pathfinder
/// consumes (<see cref="IFlowGrid"/>): terrain costs forward to the world,
/// and a cell with a building on it is blocking but tearable —
/// <see cref="Passable"/> false with a positive <see cref="BloodCost"/> equal
/// to building HP (F-044 拆墙 semantics; the ×5 multiplier stays a
/// <see cref="FlowField"/> constant, design v3.2 §6「建筑血量×5 计代价」).
/// </para>
/// <para>
/// World wiring (T-038 DEBT) — <see cref="AttachToWorld"/> feeds
/// <see cref="GridWorld.BloodCostOverride"/> so a bare world reports the same
/// HP prices. The dependency direction is Combat → World only: the world
/// owns a delegate hook, never a Combat reference.
/// </para>
/// </summary>
public sealed class BuildingRegistry : IFlowGrid
{
    private readonly GridWorld _world;
    private readonly Dictionary<CellCoord, PlacedBuilding> _byCell = new();
    private readonly Dictionary<EntityId, PlacedBuilding> _byId = new();

    /// <summary>Creates the registry over one grid world.</summary>
    /// <exception cref="ArgumentNullException">world is null.</exception>
    public BuildingRegistry(GridWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    /// <summary>The world this registry composes with.</summary>
    public GridWorld World => _world;

    /// <summary>Number of buildings currently placed.</summary>
    public int Count => _byId.Count;

    /// <inheritdoc />
    public int Width => _world.Width;

    /// <inheritdoc />
    public int Height => _world.Height;

    /// <inheritdoc />
    public int Cost(CellCoord cell) => _world.Cost(cell);

    /// <summary>
    /// F-044 composed passability: terrain-passable AND free of a building.
    /// A building cell reports false while carrying a positive
    /// <see cref="BloodCost"/> — blocking but tearable.
    /// </summary>
    public bool Passable(CellCoord cell) => _world.Passable(cell) && !_byCell.ContainsKey(cell);

    /// <summary>
    /// F-044 tear price through the composed view: building HP on the cell,
    /// 0 without one. The ×5 multiplier is applied by <see cref="FlowField"/>.
    /// </summary>
    public int BloodCost(CellCoord cell) => BloodCostAt(cell);

    /// <summary>
    /// Places one building: registers grid occupancy (C-05 rejects overlaps,
    /// out-of-bounds cells and duplicate ids) and records the HP entry.
    /// Returns the placed <see cref="IBuilding"/> view.
    /// </summary>
    /// <exception cref="ArgumentNullException">id or spec is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">tier outside [1, max_tier].</exception>
    /// <exception cref="ArgumentException">
    /// Re-thrown from the world for out-of-bounds cells.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Re-thrown from the world for occupied cells or a duplicate entity id.
    /// </exception>
    public IBuilding Place(EntityId id, TowerSpec spec, CellCoord cell, int tier = 1)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(spec);
        Guard.EnsureInRange(tier, 1, Math.Max(1, spec.MaxTier), nameof(tier));

        // World first: it owns the occupancy invariants. A rejection leaves
        // this registry untouched.
        _world.RegisterOccupancy(id, new[] { cell });
        var building = new PlacedBuilding(id, spec.Id, cell, spec.Hp, tier);
        _byCell.Add(cell, building);
        _byId.Add(id, building);
        return building;
    }

    /// <summary>Looks up the building standing on a cell.</summary>
    public bool TryGetAt(CellCoord cell, out IBuilding building)
    {
        if (_byCell.TryGetValue(cell, out var placed))
        {
            building = placed;
            return true;
        }

        building = null!;
        return false;
    }

    /// <summary>Looks up a building by entity id.</summary>
    public bool TryGetById(EntityId id, out IBuilding building)
    {
        if (_byId.TryGetValue(id, out var placed))
        {
            building = placed;
            return true;
        }

        building = null!;
        return false;
    }

    /// <summary>
    /// Tear price lookup: the HP of the building on the cell, 0 without one.
    /// This is the delegate wired into <see cref="GridWorld.BloodCostOverride"/>
    /// by <see cref="AttachToWorld"/>.
    /// </summary>
    public int BloodCostAt(CellCoord cell) =>
        _byCell.TryGetValue(cell, out var placed) ? placed.Hp : 0;

    /// <summary>
    /// Removes a building: releases its cell and drops the HP entry. Returns
    /// false (touching nothing) for unknown ids.
    /// </summary>
    public bool Remove(EntityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_byId.Remove(id, out var placed))
        {
            return false;
        }

        _byCell.Remove(placed.Cell);
        _world.ClearOccupancy(id);
        return true;
    }

    /// <summary>
    /// Wires the HP lookup into <see cref="GridWorld.BloodCostOverride"/> —
    /// the T-038 DEBT connection: a bare world (or a manager bound directly
    /// to it) then reports building HP as its tear price too. Pure
    /// composition, no Combat knowledge leaks into World.
    /// </summary>
    public void AttachToWorld() => _world.BloodCostOverride = BloodCostAt;
}
