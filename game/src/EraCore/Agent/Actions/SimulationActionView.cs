using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Units;

namespace EraCore.Agent.Actions;

/// <summary>One resource line of the situation summary (K-6 read-side view).</summary>
/// <param name="Resource">Resource key (e.g. "era1:res-wood").</param>
/// <param name="Amount">Amount currently stored in the shared era pool.</param>
public sealed record ResourceStockLine(ResourceKey Resource, double Amount);

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
/// <see cref="IActionWorldView"/> projection over the live C-12
/// <see cref="Simulation"/> (T-051): a pure composition of public sim
/// surfaces — <see cref="Simulation.CurrentTick"/>,
/// <see cref="Simulation.CurrentWave"/>, <see cref="Simulation.Stock"/>,
/// <see cref="Simulation.Units"/>, <see cref="Simulation.World"/> — plus
/// the level config the caller built the simulation with (tower catalog
/// prices, cost currency). No subsystem internals are touched and nothing
/// is ever mutated: plan execution stays on the SimCommand lane (T-053).
/// </summary>
public sealed class SimulationActionView : IActionWorldView
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
}
