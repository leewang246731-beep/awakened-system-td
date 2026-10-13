using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>
/// (start, goal) → route store in front of <see cref="AStarPathfinder"/>
/// (C-07 precursor, T-047, F-040: 路径缓存). Only successful routes are
/// cached: a <see cref="PathQueryStatus.PathNotFound"/> answer depends on the
/// whole map (removing a blocker anywhere can open it), so caching negatives
/// could not be invalidated precisely — they re-query instead.
/// <para>
/// Affected-only invalidation (缓存仅受影响失效): every stored route indexes
/// each of its cells in an inverted cell → routes map (路径格子索引 /
/// region→路径集合倒排). <see cref="Invalidate(IEnumerable{CellCoord})"/> drops
/// exactly the routes passing through a changed cell or region and touches
/// nothing else — full clears are forbidden by the T-047 acceptance bar. The
/// caller owns change detection: after terrain edits (e.g.
/// <see cref="World.GridWorld.SetTerrain"/>) it passes the changed cells in;
/// the cache never rescans the grid on its own.
/// </para>
/// <para>
/// Counters <see cref="Hits"/>/<see cref="Misses"/> plus the pathfinder's
/// <see cref="AStarPathfinder.SearchCount"/> form the hit-does-not-recompute
/// probe the logic tests assert against. No clock, no RNG (CS-1.4).
/// </para>
/// </summary>
public sealed class PathCache
{
    private readonly AStarPathfinder _pathfinder;
    private readonly Dictionary<(CellCoord Start, CellCoord Goal), PathQueryResult> _entries = new();
    private readonly Dictionary<CellCoord, HashSet<(CellCoord Start, CellCoord Goal)>> _routesThroughCell = new();

    /// <summary>
    /// Wraps one pathfinder (and through it one grid) for the cache's
    /// lifetime.
    /// </summary>
    /// <exception cref="ArgumentNullException">pathfinder is null.</exception>
    public PathCache(AStarPathfinder pathfinder)
    {
        ArgumentNullException.ThrowIfNull(pathfinder);
        _pathfinder = pathfinder;
    }

    /// <summary>Number of routes currently stored.</summary>
    public int Count => _entries.Count;

    /// <summary>Cumulative cache hits (served without a search).</summary>
    public long Hits { get; private set; }

    /// <summary>Cumulative cache misses (a search ran).</summary>
    public long Misses { get; private set; }

    /// <summary>
    /// Route for (start, goal): served from the store on a hit, computed by
    /// the pathfinder and stored on a miss (found routes only). Invalid
    /// endpoints surface the pathfinder's argument exceptions; unreachable
    /// endpoints return a PathNotFound result and are never stored.
    /// </summary>
    /// <exception cref="ArgumentNullException">start or goal is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">start or goal outside the grid.</exception>
    public PathQueryResult GetPath(CellCoord start, CellCoord goal)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(goal);

        var key = (start, goal);
        if (_entries.TryGetValue(key, out PathQueryResult? cached))
        {
            Hits++;
            return cached;
        }

        Misses++;
        PathQueryResult result = _pathfinder.FindPath(start, goal);
        if (result.Status == PathQueryStatus.Found)
        {
            _entries.Add(key, result);
            foreach (CellCoord cell in result.Cells)
            {
                if (!_routesThroughCell.TryGetValue(cell, out HashSet<(CellCoord Start, CellCoord Goal)>? routes))
                {
                    routes = new HashSet<(CellCoord Start, CellCoord Goal)>();
                    _routesThroughCell.Add(cell, routes);
                }

                routes.Add(key);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether (start, goal) is currently stored — observability without
    /// touching the hit/miss counters.
    /// </summary>
    /// <exception cref="ArgumentNullException">start or goal is null.</exception>
    public bool TryGetCachedPath(CellCoord start, CellCoord goal, out PathQueryResult? result)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(goal);
        return _entries.TryGetValue((start, goal), out result);
    }

    /// <summary>
    /// Single-cell region convenience overload of
    /// <see cref="Invalidate(IEnumerable{CellCoord})"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">changedCell is null.</exception>
    public int Invalidate(CellCoord changedCell)
    {
        ArgumentNullException.ThrowIfNull(changedCell);
        return InvalidateCore(new[] { changedCell });
    }

    /// <summary>
    /// Affected-only invalidation for a changed region: removes exactly the
    /// stored routes that pass through any changed cell (deduplicated), keeps
    /// every other entry, and returns how many routes were dropped. A region
    /// touching no cached route is a no-op returning 0 — never a full clear.
    /// </summary>
    /// <exception cref="ArgumentNullException">changedRegion or one of its cells is null.</exception>
    public int Invalidate(IEnumerable<CellCoord> changedRegion)
    {
        ArgumentNullException.ThrowIfNull(changedRegion);
        return InvalidateCore(changedRegion);
    }

    private int InvalidateCore(IEnumerable<CellCoord> changedRegion)
    {
        var affected = new HashSet<(CellCoord Start, CellCoord Goal)>();
        foreach (CellCoord cell in changedRegion)
        {
            ArgumentNullException.ThrowIfNull(cell);
            if (_routesThroughCell.TryGetValue(cell, out HashSet<(CellCoord Start, CellCoord Goal)>? routes))
            {
                affected.UnionWith(routes);
            }
        }

        foreach ((CellCoord Start, CellCoord Goal) key in affected)
        {
            RemoveEntry(key);
        }

        return affected.Count;
    }

    private void RemoveEntry((CellCoord Start, CellCoord Goal) key)
    {
        if (!_entries.Remove(key, out PathQueryResult? removed))
        {
            return;
        }

        foreach (CellCoord cell in removed.Cells)
        {
            if (_routesThroughCell.TryGetValue(cell, out HashSet<(CellCoord Start, CellCoord Goal)>? routes))
            {
                routes.Remove(key);
                if (routes.Count == 0)
                {
                    _routesThroughCell.Remove(cell);
                }
            }
        }
    }
}
