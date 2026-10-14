using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Units;

namespace EraCore.Agent.Actions;

/// <summary>One resource line of the situation summary (K-6 read-side view).</summary>
/// <param name="Resource">Resource key (e.g. "era1:res-wood").</param>
/// <param name="Amount">Amount currently stored in the shared era pool.</param>
public sealed record ResourceStockLine(ResourceKey Resource, double Amount);

/// <summary>One tower of the agent-visible roster (T-068 state projection).</summary>
/// <param name="Id">Stable entity id of the placed tower.</param>
/// <param name="ContentId">Content id of the placed row.</param>
/// <param name="Cell">Cell the tower stands on.</param>
/// <param name="Tier">Tier of the placed row.</param>
public sealed record TowerLine(long Id, string ContentId, CellCoord Cell, int Tier);

/// <summary>One living enemy of the agent-visible roster (T-068 state projection).</summary>
/// <param name="Id">Entity id (the FocusFire target key).</param>
/// <param name="ContentId">Content id of the enemy spec.</param>
/// <param name="X">Centre position X in px.</param>
/// <param name="Y">Centre position Y in px.</param>
/// <param name="Hp">Current HP.</param>
/// <param name="IsBoss">Boss flag.</param>
public sealed record EnemyLine(long Id, string ContentId, float X, float Y, int Hp, bool IsBoss);

/// <summary>One placed building of the agent-visible roster (T-068 state projection).</summary>
/// <param name="Id">Entity id (the RerouteLogistics endpoint key).</param>
/// <param name="ContentId">Content id of the building.</param>
/// <param name="Cell">Cell the building stands on.</param>
/// <param name="Kind">Building family (tower/labor camp/harvester/converter/warehouse).</param>
public sealed record BuildingLine(long Id, string ContentId, CellCoord Cell, SimBuildingKind Kind);

/// <summary>
/// Read-only world view the action handlers run against (C-03, T-051).
/// The agent layer never touches simulation internals: handlers consume
/// this projection only, and every reverse mutation still enters the
/// simulation through SimCommand (03-decomposition §1.2 snapshot
/// principle). Implemented by <see cref="SimulationActionView"/>; the seam
/// keeps C-03 decoupled from C-12 (lower-coupling option chosen per the
/// T-051 card: port view, not Simulation type).
/// </summary>
public interface IActionWorldView
{
    /// <summary>Current simulation tick (30 Hz domain; -1 before the first step).</summary>
    long Tick { get; }

    /// <summary>Wave number of the installed spawn plan; 0 before wave 1.</summary>
    int CurrentWave { get; }

    /// <summary>Resources currently stored in the shared era pool, registry order.</summary>
    IReadOnlyList<ResourceStockLine> Resources { get; }

    /// <summary>Living enemies present right now.</summary>
    int EnemiesPresent { get; }

    /// <summary>Aggregate current HP of all living enemies (observable pressure figure).</summary>
    int EnemyHpPresent { get; }

    /// <summary>Resource key the build economy prices things in.</summary>
    ResourceKey CostCurrency { get; }

    /// <summary>Amount of one resource currently stored.</summary>
    double ResourceAmount(ResourceKey resource);

    /// <summary>Tower build cost in resource points; false when the id is unknown to the level catalog.</summary>
    bool TryGetTowerCost(string towerId, out double cost);

    /// <summary>True when the cell lies inside the level grid.</summary>
    bool IsInsideGrid(CellCoord cell);

    /// <summary>
    /// Buildability verdict using the same rejection vocabulary as the sim
    /// command lane: null when buildable, otherwise one of
    /// <c>cell-out-of-bounds</c> / <c>cell-not-buildable</c> / <c>cell-occupied</c>.
    /// </summary>
    bool IsBuildable(CellCoord cell, out string? rejection);
}

/// <summary>
/// Extended world-state projection the T-068 handlers read (C-13 full):
/// target rosters (towers/enemies/buildings) and the carrier count on top
/// of the base view — still strictly read-only. Implemented by
/// <see cref="SimulationActionView"/>; a registry built over a bare
/// <see cref="IActionWorldView"/> refuses the verbs that need this surface
/// with an explicit code (never a guess).
/// </summary>
public interface IActionWorldState : IActionWorldView
{
    /// <summary>Placed towers in sim order (id/content/cell/tier).</summary>
    IReadOnlyList<TowerLine> Towers { get; }

    /// <summary>Living enemies in spawn order (FocusFire targets).</summary>
    IReadOnlyList<EnemyLine> LivingEnemies { get; }

    /// <summary>Placed buildings in sim order (RerouteLogistics endpoints).</summary>
    IReadOnlyList<BuildingLine> Buildings { get; }

    /// <summary>Carriers currently walking routes (paths-query figure).</summary>
    int CarrierCount { get; }

    /// <summary>Whether any building occupies the cell (InspectTile stats aspect).</summary>
    bool IsOccupied(CellCoord cell);
}

/// <summary>
/// <see cref="IActionWorldView"/> projection over the live C-12
/// <see cref="Simulation"/> (T-051): a pure composition of public sim
/// surfaces — <see cref="Simulation.CurrentTick"/>,
/// <see cref="Simulation.CurrentWave"/>, <see cref="Simulation.Stock"/>,
/// <see cref="Simulation.Units"/>, <see cref="Simulation.World"/> — plus
/// the level config the caller built the simulation with (tower catalog
/// prices, cost currency). No subsystem internals are touched and nothing
/// is ever mutated: plan execution stays on the SimCommand lane (T-053).
/// T-068 extends the same projection with the
/// <see cref="IActionWorldState"/> rosters (towers/enemies/buildings/
/// carriers) the full 19-verb pipeline reads.
/// </summary>
public sealed class SimulationActionView : IActionWorldState
{
    private readonly Simulation _simulation;
    private readonly SimulationConfig _config;

    /// <param name="simulation">The live simulation to project.</param>
    /// <param name="config">The config that simulation was built with (catalog + prices).</param>
    public SimulationActionView(Simulation simulation, SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(config);
        _simulation = simulation;
        _config = config;
    }

    /// <inheritdoc />
    public long Tick => _simulation.CurrentTick;

    /// <inheritdoc />
    public int CurrentWave => _simulation.CurrentWave;

    /// <inheritdoc />
    public IReadOnlyList<ResourceStockLine> Resources
    {
        get
        {
            var lines = new List<ResourceStockLine>(_simulation.Stock.Resources.Count);
            foreach (ResourceKey resource in _simulation.Stock.Resources)
            {
                lines.Add(new ResourceStockLine(resource, _simulation.Stock.AmountOf(resource)));
            }

            return lines;
        }
    }

    /// <inheritdoc />
    public int EnemiesPresent
    {
        get
        {
            int count = 0;
            foreach (Unit unit in _simulation.Units)
            {
                if (unit.Alive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public int EnemyHpPresent
    {
        get
        {
            int total = 0;
            foreach (Unit unit in _simulation.Units)
            {
                if (unit.Alive)
                {
                    total += unit.Hp;
                }
            }

            return total;
        }
    }

    /// <inheritdoc />
    public ResourceKey CostCurrency =>
        _config.Costs?.Currency ?? new ResourceKey("era1:res-wood");

    /// <inheritdoc />
    public double ResourceAmount(ResourceKey resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _simulation.Stock.AmountOf(resource);
    }

    /// <inheritdoc />
    public bool TryGetTowerCost(string towerId, out double cost)
    {
        if (_config.TowerCatalog is not null)
        {
            foreach (var spec in _config.TowerCatalog)
            {
                if (string.Equals(spec.Id, towerId, StringComparison.Ordinal))
                {
                    cost = spec.CostTotal;
                    return true;
                }
            }
        }

        cost = 0;
        return false;
    }

    /// <inheritdoc />
    public bool IsInsideGrid(CellCoord cell) => _simulation.World.Contains(cell);

    /// <inheritdoc />
    public bool IsBuildable(CellCoord cell, out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (!IsInsideGrid(cell))
        {
            rejection = "cell-out-of-bounds";
            return false;
        }

        if (!_simulation.World.IsPassable(cell))
        {
            rejection = "cell-not-buildable";
            return false;
        }

        if (_simulation.World.IsOccupied(cell))
        {
            rejection = "cell-occupied";
            return false;
        }

        // T-056 (spike2-pit#6): the unit layer shares the sim lane's rule
        // body — a living enemy or a servicing porter on the cell rejects
        // with the same code <see cref="Sim.Simulation"/> re-checks at
        // command-apply time, so a green preview can never turn into a
        // unit-occupancy rejection one tick later (and vice versa).
        rejection = BuildOccupancyRule.UnitLayerRejection(cell, _simulation.Units, _simulation.Carriers);
        return rejection is null;
    }

    /// <inheritdoc cref="IActionWorldState.Towers"/>
    public IReadOnlyList<TowerLine> Towers
    {
        get
        {
            var lines = new List<TowerLine>(_simulation.Towers.Count);
            foreach (SimTower tower in _simulation.Towers)
            {
                lines.Add(new TowerLine(tower.Id.Value, tower.Spec.Id, tower.Cell, tower.Spec.Tier));
            }

            return lines;
        }
    }

    /// <inheritdoc cref="IActionWorldState.LivingEnemies"/>
    public IReadOnlyList<EnemyLine> LivingEnemies
    {
        get
        {
            var lines = new List<EnemyLine>();
            foreach (Unit unit in _simulation.Units)
            {
                if (unit.Alive)
                {
                    lines.Add(new EnemyLine(unit.Id.Value, unit.ContentId, unit.X, unit.Y, unit.Hp, unit.IsBoss));
                }
            }

            return lines;
        }
    }

    /// <inheritdoc cref="IActionWorldState.Buildings"/>
    public IReadOnlyList<BuildingLine> Buildings
    {
        get
        {
            var lines = new List<BuildingLine>(_simulation.Buildings.Count);
            foreach (SimBuilding building in _simulation.Buildings)
            {
                lines.Add(new BuildingLine(building.Id.Value, building.ContentId, building.Cell, building.Kind));
            }

            return lines;
        }
    }

    /// <inheritdoc cref="IActionWorldState.CarrierCount"/>
    public int CarrierCount => _simulation.Carriers.Count;

    /// <inheritdoc cref="IActionWorldState.IsOccupied"/>
    public bool IsOccupied(CellCoord cell) => _simulation.World.IsOccupied(cell);
}
