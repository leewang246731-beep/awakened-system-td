using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Units;

namespace EraCore.Scrolls;

/// <summary>One living enemy as the scroll layer sees it (positions in cells, C-05 coordinates).</summary>
/// <param name="Id">Enemy entity id.</param>
/// <param name="ZoneId">Zone containing the enemy's cell; null when it stands in no named zone.</param>
/// <param name="Hp">Current hit points.</param>
/// <param name="MaxHp">Maximum hit points.</param>
/// <param name="CellX">Cell column (floor(px / 32)).</param>
/// <param name="CellY">Cell row.</param>
public sealed record ScrollEnemyFact(long Id, string? ZoneId, int Hp, int MaxHp, int CellX, int CellY);

/// <summary>One placed tower as the scroll layer sees it.</summary>
/// <param name="Id">Tower building entity id.</param>
/// <param name="ContentId">Tower content id (e.g. era2:tower-arrow).</param>
/// <param name="TowerClass">Six-class taxonomy (SP-1a).</param>
/// <param name="ZoneId">Zone containing the tower's cell; null when in no named zone.</param>
/// <param name="Hp">Current hit points.</param>
/// <param name="MaxHp">Maximum hit points.</param>
/// <param name="CellX">Cell column.</param>
/// <param name="CellY">Cell row.</param>
public sealed record ScrollTowerFact(
    long Id,
    string ContentId,
    TowerClass TowerClass,
    string? ZoneId,
    int Hp,
    int MaxHp,
    int CellX,
    int CellY);

/// <summary>One named zone (F-117 partition layer) the scroll layer may condition on.</summary>
/// <param name="ZoneId">Language-neutral zone id.</param>
/// <param name="Cells">Zone cells (labels, may overlap other zones).</param>
public sealed record ScrollZoneFact(string ZoneId, IReadOnlyList<CellCoord> Cells);

/// <summary>
/// Read-only world projection the scroll runtime evaluates conditions
/// against (C-18 port, T-071). The scroll layer never touches simulation
/// internals — same seam discipline as the C-03 IActionWorldView; reverse
/// mutations still enter the simulation through the host (verb dispatches
/// go through IActionExecutor, heals through <see cref="IScrollEffectSink"/>).
/// </summary>
public interface IScrollWorldView
{
    /// <summary>Current simulation tick (30 Hz domain; -1 before the first step).</summary>
    long Tick { get; }

    /// <summary>Wave number of the installed spawn plan; 0 before wave 1.</summary>
    int CurrentWave { get; }

    /// <summary>Living enemies right now, simulation list order (deterministic).</summary>
    IReadOnlyList<ScrollEnemyFact> Enemies { get; }

    /// <summary>Placed towers right now, placement order (deterministic).</summary>
    IReadOnlyList<ScrollTowerFact> Towers { get; }

    /// <summary>Named zones, sorted by zone id (deterministic iteration).</summary>
    IReadOnlyList<ScrollZoneFact> Zones { get; }

    /// <summary>Amount of one resource currently stored in the shared era pool.</summary>
    double ResourceAmount(ResourceKey resource);
}

/// <summary>
/// Mutation sink for instant scroll effects (C-18 port): heals land through
/// the host's lawful lane (a SimCommand in the C-13 wiring), never by
/// touching entity internals from the scroll layer. Timed modifiers do NOT
/// pass through here — the combat/logistics layers read
/// <see cref="ScrollEngine.ActiveEffects"/> directly.
/// </summary>
public interface IScrollEffectSink
{
    /// <summary>Heals one tower by <paramref name="amount"/> hit points (SC-004 断后).</summary>
    void HealTower(long towerId, int amount);
}

/// <summary>
/// <see cref="IScrollWorldView"/> projection over the live C-12
/// <see cref="Simulation"/> (T-071): a pure composition of public sim
/// surfaces — Units, Towers, Buildings (tower HP join), World zones, Stock.
/// Zone ids are injected because <see cref="GridWorld"/> answers membership
/// queries by id but does not enumerate its zones; the host passes the ids
/// it defined. Nothing is mutated and no subsystem internals are touched.
/// </summary>
public sealed class SimulationScrollWorldView : IScrollWorldView
{
    private readonly Simulation _simulation;
    private readonly IReadOnlyList<string> _zoneIds;

    /// <param name="simulation">The live simulation to project.</param>
    /// <param name="zoneIds">Zone ids the level defined (zone membership is computed per cell).</param>
    public SimulationScrollWorldView(Simulation simulation, IReadOnlyList<string>? zoneIds = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _simulation = simulation;
        _zoneIds = (zoneIds ?? Array.Empty<string>()).OrderBy(static z => z, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public long Tick => _simulation.CurrentTick;

    /// <inheritdoc />
    public int CurrentWave => _simulation.CurrentWave;

    /// <inheritdoc />
    public IReadOnlyList<ScrollEnemyFact> Enemies
    {
        get
        {
            var facts = new List<ScrollEnemyFact>();
            foreach (Unit unit in _simulation.Units)
            {
                if (!unit.Alive)
                {
                    continue;
                }

                int cellX = (int)Math.Floor(unit.X / Simulation.CellSizePx);
                int cellY = (int)Math.Floor(unit.Y / Simulation.CellSizePx);
                facts.Add(new ScrollEnemyFact(
                    unit.Id.Value, ZoneOfCell(cellX, cellY), unit.Hp, unit.MaxHp, cellX, cellY));
            }

            return facts;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScrollTowerFact> Towers
    {
        get
        {
            var facts = new List<ScrollTowerFact>();
            foreach (SimTower tower in _simulation.Towers)
            {
                if (!_simulation.TryGetBuilding(tower.Id, out SimBuilding building))
                {
                    continue;
                }

                int maxHp = tower.Spec.Hp;
                facts.Add(new ScrollTowerFact(
                    tower.Id.Value,
                    tower.Spec.Id,
                    tower.Spec.Class,
                    ZoneOfCell(tower.Cell.X, tower.Cell.Y),
                    building.Hp,
                    maxHp,
                    tower.Cell.X,
                    tower.Cell.Y));
            }

            return facts;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScrollZoneFact> Zones
    {
        get
        {
            var zones = new List<ScrollZoneFact>(_zoneIds.Count);
            foreach (string zoneId in _zoneIds)
            {
                if (_simulation.World.TryGetZoneCells(zoneId, out IReadOnlyList<CellCoord> cells))
                {
                    zones.Add(new ScrollZoneFact(zoneId, cells));
                }
            }

            return zones;
        }
    }

    /// <inheritdoc />
    public double ResourceAmount(ResourceKey resource) => _simulation.Stock.AmountOf(resource);

    private string? ZoneOfCell(int x, int y)
    {
        // First zone in id order wins an overlap (labels may overlap — GridWorld contract).
        foreach (string zoneId in _zoneIds)
        {
            if (_simulation.World.TryGetZoneCells(zoneId, out IReadOnlyList<CellCoord> cells))
            {
                foreach (CellCoord cell in cells)
                {
                    if (cell.X == x && cell.Y == y)
                    {
                        return zoneId;
                    }
                }
            }
        }

        return null;
    }
}
