using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;

namespace EraCore.Supply;

/// <summary>
/// Link guard of one carrier leg (断链语义, T-048, F-046): the slice of the
/// 断供降速 logic layer that owns the no-stall route policy around a reused
/// <see cref="Carrier"/> — terrain changes come in through
/// <see cref="OnTerrainChanged"/>, the affected-only
/// <see cref="PathCache"/> invalidation and the A* re-query stay in the
/// reused Logistics stack, and this guard decides hold-vs-reroute:
/// <list type="bullet">
/// <item>movement phase + fresh path from the current cell ⇒
/// <see cref="Carrier.Reroute"/> onto it (加墙→失效→重算→改道, decided the
/// tick the terrain change is reported, and re-attempted defensively on
/// later ticks while the active walk is blocked);</item>
/// <item>movement phase + no reachable path ⇒ the carrier is HELD — its
/// <see cref="Carrier.Tick"/> is simply not called, so it parks on the
/// spot/last reachable cell (停在原地/最后可达点), its phase timers and
/// cargo frozen, nothing thrown — 效率降 0 但系统不崩;</item>
/// <item>loading at home ⇒ held while any cell of the departure polyline is
/// blocked (a porter does not depart onto a blocked road — the Carrier API
/// has no pre-departure route swap, so the hold is the conservative exact
/// answer; terrain restoration re-opens the exact route it will
/// walk);</item>
/// <item>unloading at the destination ⇒ always allowed to finish the
/// delivery (cargo must not strand mid-handover); the return leg then holds
/// under the movement rule.</item>
/// </list>
/// Recovery: the next <see cref="OnTerrainChanged"/> that re-opens a path
/// re-evaluates — a held carrier resumes ticking and reroutes when a path
/// exists again (恢复路径后效率回升).
/// <para>
/// The guard also tracks the walked polyline (observing
/// <see cref="Carrier.CurrentCell"/> around every tick it performs — the
/// same cells the Carrier internally promotes to its canonical route) so
/// <see cref="BreakCell"/> can report the wall cell on the shortest supply
/// path (最短路径上的断点格) for the bottleneck suggestion, and so the
/// loading-phase hold checks the real departure route. Deterministic:
/// every decision is a pure function of live carrier state + grid + cache
/// contents, visited in fixed order; no RNG, no wall clock (CS-1.4) — same
/// seed + tick sequence ⇒ equal <see cref="ComputeStateHash"/>.
/// </para>
/// </summary>
public sealed class SupplyLinkGuard
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly Carrier _carrier;
    private readonly PathCache _cache;
    private readonly IFlowGrid _grid;
    private readonly List<CellCoord> _departureRoute; // home→destination polyline the carrier next walks
    private readonly List<CellCoord> _tripWalked = new(); // cells observed on the current/last outbound
    private List<CellCoord> _activePath; // current-cell→phase-target cells (movement phases)

    /// <summary>
    /// Guards <paramref name="carrier"/>'s home→destination leg. The
    /// <paramref name="departureRoute"/> must be the route the carrier was
    /// constructed with (its first walk) — its cells form the supply path
    /// <see cref="BreakCell"/> reports from.
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// route has fewer than two cells, or its endpoints do not match the
    /// carrier's home/destination.
    /// </exception>
    public SupplyLinkGuard(Carrier carrier, PathCache cache, IFlowGrid grid, IReadOnlyList<CellCoord> departureRoute)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(departureRoute);
        if (departureRoute.Count < 2)
        {
            throw new ArgumentException("A departure route needs at least two cells.", nameof(departureRoute));
        }

        if (!departureRoute[0].Equals(carrier.Home) || !departureRoute[^1].Equals(carrier.Destination))
        {
            throw new ArgumentException(
                $"Departure route endpoints ({departureRoute[0].X},{departureRoute[0].Y})→({departureRoute[^1].X},{departureRoute[^1].Y}) "
                + $"must match the carrier leg ({carrier.Home.X},{carrier.Home.Y})→({carrier.Destination.X},{carrier.Destination.Y}).",
                nameof(departureRoute));
        }

        _carrier = carrier;
        _cache = cache;
        _grid = grid;
        _departureRoute = new List<CellCoord>(departureRoute);
        _activePath = new List<CellCoord>(_departureRoute);
        _tripWalked.Add(carrier.Home);
    }

    /// <summary>Number of ticks the carrier spent HELD (parked) so far.</summary>
    public int TicksHeld { get; private set; }

    /// <summary>
    /// The wall cell on the shortest supply path (最短路径上的断点格):
    /// the first impassable cell along the CURRENT effective outbound path —
    /// the walk actually in progress (walked prefix + active remaining,
    /// detours included) while outbound, otherwise the departure polyline
    /// (the route the next outbound walks). Null while that whole path is
    /// passable — a live detour is a working link, not a break.
    /// </summary>
    public CellCoord? BreakCell
    {
        get
        {
            IEnumerable<CellCoord> path = _carrier.Phase == CarrierPhase.Outbound
                ? _tripWalked.Concat(_activePath)
                : _departureRoute;
            foreach (CellCoord cell in path)
            {
                if (!_grid.Passable(cell))
                {
                    return cell;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Whether the carrier may tick right now: movement legs need a fully
    /// walkable active path (after a reroute attempt), loading needs a
    /// passable departure route, unloading always completes, and the cell
    /// the carrier stands on must be passable. Live state, no caching.
    /// </summary>
    public bool IsLinked =>
        _grid.Passable(_carrier.CurrentCell)
        && _carrier.Phase switch
        {
            CarrierPhase.Loading => BreakCell is null,
            CarrierPhase.Unloading => true,
            CarrierPhase.Outbound => ActivePathWalkable() || TryReroute(),
            CarrierPhase.Inbound => ActivePathWalkable() || TryReroute(),
            _ => true,
        };

    /// <summary>
    /// Terrain-change hook: invalidates exactly the affected cached routes
    /// (T-047 discipline, delegated to the cache) and — when the carrier is
    /// mid-walk — immediately re-queries and reroutes onto the fresh path,
    /// or leaves it held when none exists. Call once per terrain edit batch,
    /// after the grid mutation.
    /// </summary>
    /// <exception cref="ArgumentNullException">changedCells is null.</exception>
    public void OnTerrainChanged(IEnumerable<CellCoord> changedCells)
    {
        ArgumentNullException.ThrowIfNull(changedCells);
        _cache.Invalidate(changedCells);
        if (_carrier.Phase is CarrierPhase.Outbound or CarrierPhase.Inbound)
        {
            TryReroute();
        }
    }

    /// <summary>
    /// Advances exactly one simulation tick for the guarded carrier — or
    /// holds it (停在原地) when its leg is blocked. Holding means the
    /// carrier's own <see cref="Carrier.Tick"/> is not called: phase
    /// timers, cargo and position freeze; <see cref="TicksHeld"/> grows.
    /// </summary>
    public void Tick()
    {
        if (!IsLinked)
        {
            TicksHeld++;
            return;
        }

        CarrierPhase phaseBefore = _carrier.Phase;
        _carrier.Tick();
        Observe(phaseBefore);
    }

    /// <summary>
    /// FNV-1a hash over the guard's own state (held counter + departure
    /// polyline + walked trip + active path, cell by cell). Compose with
    /// the carrier/converter hashes in replay determinism checks; same
    /// seed + tick sequence ⇒ equal hashes.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)TicksHeld);
        hash = MixList(hash, _departureRoute);
        hash = MixList(hash, _tripWalked);
        hash = MixList(hash, _activePath);
        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }

        static ulong MixList(ulong hash, List<CellCoord> cells)
        {
            hash = Mix(hash, (ulong)cells.Count);
            foreach (CellCoord cell in cells)
            {
                hash = Mix(hash, (ulong)cell.X);
                hash = Mix(hash, (ulong)cell.Y);
            }

            return hash;
        }
    }

    private bool ActivePathWalkable()
    {
        foreach (CellCoord cell in _activePath)
        {
            if (!_grid.Passable(cell))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Re-queries the phase target from the carrier's current cell and
    /// reroutes when a fresh path exists. Returns whether the carrier is
    /// walkable afterwards (rerouted, or the path was fine all along).
    /// </summary>
    private bool TryReroute()
    {
        if (ActivePathWalkable())
        {
            return true;
        }

        CellCoord target = _carrier.Phase == CarrierPhase.Outbound ? _carrier.Destination : _carrier.Home;
        PathQueryResult fresh = _cache.GetPath(_carrier.CurrentCell, target);
        if (fresh.Status != PathQueryStatus.Found)
        {
            return false; // no reachable path — held (停在最后可达点)
        }

        _carrier.Reroute(fresh.Cells);
        _activePath = new List<CellCoord>(fresh.Cells);
        return true;
    }

    /// <summary>
    /// Bookkeeping after a performed carrier tick: appends newly reached
    /// cells to the trip polyline, aligns the active path with the
    /// carrier's cell, and maintains the polylines across phase
    /// transitions (departure completion, home return, new departure).
    /// </summary>
    private void Observe(CarrierPhase phaseBefore)
    {
        CarrierPhase phase = _carrier.Phase;
        if (phase == CarrierPhase.Outbound)
        {
            if (!_carrier.CurrentCell.Equals(_tripWalked[^1]))
            {
                _tripWalked.Add(_carrier.CurrentCell);
            }

            AlignActivePath();
            return;
        }

        if (phase == CarrierPhase.Unloading)
        {
            if (phaseBefore == CarrierPhase.Outbound)
            {
                // Outbound leg finished: the walked polyline (reroutes
                // included — exactly the cells the Carrier internally
                // promotes to its canonical route) becomes the departure
                // route; the return walk is its reverse.
                if (!_carrier.Destination.Equals(_tripWalked[^1]))
                {
                    _tripWalked.Add(_carrier.Destination);
                }

                _departureRoute.Clear();
                _departureRoute.AddRange(_tripWalked);
                _activePath = new List<CellCoord>(_tripWalked);
                _activePath.Reverse();
            }

            return;
        }

        if (phase == CarrierPhase.Inbound)
        {
            AlignActivePath();
            return;
        }

        // Loading: back at home (or still there) — the next outbound walks
        // the departure route from scratch.
        _activePath = new List<CellCoord>(_departureRoute);
        _tripWalked.Clear();
        _tripWalked.Add(_carrier.Home);
    }

    /// <summary>
    /// Drops consumed heads from the active path until its head matches the
    /// carrier's current cell (at porter speed at most one cell boundary
    /// per tick; the loop keeps reroute transitions honest).
    /// </summary>
    private void AlignActivePath()
    {
        while (_activePath.Count > 1 && !_activePath[0].Equals(_carrier.CurrentCell))
        {
            _activePath.RemoveAt(0);
        }
    }
}
