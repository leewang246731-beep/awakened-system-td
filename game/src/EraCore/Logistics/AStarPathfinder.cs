using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.Logistics;

/// <summary>Outcome of one transport path query (F-040).</summary>
public enum PathQueryStatus
{
    /// <summary>
    /// A route over passable terrain exists; <see cref="PathQueryResult.Cells"/>
    /// carries it start → goal with both endpoints included.
    /// </summary>
    Found = 0,

    /// <summary>
    /// No route over passable terrain connects the endpoints: the start or
    /// goal cell is impassable, or the passable component does not contain
    /// both (明确 PathNotFound, T-047 task card).
    /// </summary>
    PathNotFound = 1,
}

/// <summary>
/// Result of one transport path query (secondary type of AStarPathfinder.cs):
/// status, the cell route, the integrated integer cost and a deterministic
/// content hash two replays can compare (同 seed 同请求序列 → 路径哈希一致).
/// </summary>
public sealed class PathQueryResult
{
    /// <summary>
    /// Found-only factory for unreachable queries: empty route, cost −1
    /// (cost is undefined for <see cref="PathQueryStatus.PathNotFound"/>).
    /// </summary>
    public static PathQueryResult NotFound(CellCoord start, CellCoord goal) =>
        new(PathQueryStatus.PathNotFound, start, goal, Array.Empty<CellCoord>(), -1);

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Creates a query result and freezes its content hash.</summary>
    public PathQueryResult(
        PathQueryStatus status,
        CellCoord start,
        CellCoord goal,
        IReadOnlyList<CellCoord> cells,
        long cost)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(cells);

        Status = status;
        Start = start;
        Goal = goal;
        Cells = cells.ToArray();
        Cost = cost;
        Hash = ComputeHash();
    }

    /// <summary>Whether <see cref="Cells"/> carries a usable route.</summary>
    public PathQueryStatus Status { get; }

    /// <summary>Query origin (inclusive first cell of the route when found).</summary>
    public CellCoord Start { get; }

    /// <summary>Query target (inclusive last cell of the route when found).</summary>
    public CellCoord Goal { get; }

    /// <summary>
    /// Route start → goal, endpoints included; empty for
    /// <see cref="PathQueryStatus.PathNotFound"/>. Defensive copy — callers
    /// cannot mutate a stored cache entry.
    /// </summary>
    public IReadOnlyList<CellCoord> Cells { get; }

    /// <summary>
    /// Integrated integer cost on the shared 10/14 metric (straight 10,
    /// diagonal 14, × terrain cost of the entered cell); −1 when not found.
    /// </summary>
    public long Cost { get; }

    /// <summary>
    /// FNV-1a 64 over status, endpoints and every route cell: identical
    /// queries over identical grids yield identical hashes, which is what the
    /// T-047 determinism gate compares across replays.
    /// </summary>
    public long Hash { get; }

    private long ComputeHash()
    {
        unchecked
        {
            ulong h = FnvOffset;
            h = Mix(h, (ulong)(int)Status);
            h = Mix(h, (ulong)Start.X);
            h = Mix(h, (ulong)Start.Y);
            h = Mix(h, (ulong)Goal.X);
            h = Mix(h, (ulong)Goal.Y);
            foreach (CellCoord cell in Cells)
            {
                h = Mix(h, (ulong)cell.X);
                h = Mix(h, (ulong)cell.Y);
            }

            return (long)h;
        }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (value >> shift) & 0xFFUL;
            hash *= FnvPrime;
        }

        return hash;
    }
}

/// <summary>
/// Grid A* pathfinder for transporters (C-07 precursor, T-047, F-040: 运输者
/// =网格 A*+路径缓存+待卸队列, design v3.2 §5/G.1 条 6). Searches the shared
/// <see cref="IFlowGrid"/> seam read-only — terrain <see cref="IFlowGrid.Cost"/>
/// and <see cref="IFlowGrid.Passable"/> only.
/// <para>
/// Semantic difference to the enemy flow field (C-11): <see
/// cref="IFlowGrid.BloodCost"/> NEVER participates in transport pathfinding.
/// The enemy flow field charges building HP ×5 and tears through blocking
/// buildings (F-044 拆墙模式); a porter never tears anything — a blocking
/// building is simply impassable, and the porter routes around it or the
/// query answers <see cref="PathQueryStatus.PathNotFound"/>. This class
/// therefore never calls <c>BloodCost</c> at all.
/// </para>
/// <para>
/// Movement metric — identical to <see cref="FlowField"/> so both engines
/// agree on distance: entering a cell costs segment length (straight 10,
/// diagonal 14) × terrain cost clamped up to
/// <see cref="FlowField.MinimumCellCost"/>. 8-neighbour expansion with the
/// same diagonal corner rule (对角卡角检测): a diagonal step additionally
/// requires BOTH orthogonally adjacent cells to be passable.
/// </para>
/// <para>
/// Determinism — neighbour scan order is fixed, and the open heap orders by
/// (f, remaining octile distance h, row-major cell index), so equal-cost
/// route ties always resolve the same way (距离+坐标序平局判定, T-047 task
/// card). No clock, no RNG (CS-1.4); <see cref="SearchCount"/> is the
/// recompute probe cache tests assert against.
/// </para>
/// </summary>
public sealed class AStarPathfinder
{
    private const int StraightWeight = 10;
    private const int DiagonalWeight = 14;

    // Fixed 8-neighbour scan order — same order as FlowField, deterministic
    // tie-breaking everywhere.
    private static readonly (int Dx, int Dy, int Weight, bool Diagonal)[] NeighbourOffsets =
    {
        (-1, -1, DiagonalWeight, true),
        (-1, 0, StraightWeight, false),
        (-1, 1, DiagonalWeight, true),
        (0, -1, StraightWeight, false),
        (0, 1, StraightWeight, false),
        (1, -1, DiagonalWeight, true),
        (1, 0, StraightWeight, false),
        (1, 1, DiagonalWeight, true),
    };

    private readonly IFlowGrid _grid;
    private readonly int _width;
    private readonly int _height;

    private long[] _g = Array.Empty<long>();
    private long[] _f = Array.Empty<long>();
    private long[] _h = Array.Empty<long>();
    private int[] _cameFrom = Array.Empty<int>();
    private bool[] _closed = Array.Empty<bool>();
    private int[] _heapPos = Array.Empty<int>();
    private int[] _heapSlots = new int[256];
    private int _heapCount;

    /// <summary>
    /// Binds the pathfinder to one grid (same grid for its lifetime; a
    /// changed world means a new pathfinder, or explicit invalidation of the
    /// owning <see cref="PathCache"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException">grid is null.</exception>
    /// <exception cref="ArgumentException">grid dimensions are non-positive or too large.</exception>
    public AStarPathfinder(IFlowGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int width = grid.Width;
        int height = grid.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Grid dimensions must be positive.", nameof(grid));
        }

        if ((long)width * height > int.MaxValue)
        {
            throw new ArgumentException("Grid is too large for a flat cell index.", nameof(grid));
        }

        _grid = grid;
        _width = width;
        _height = height;
    }

    /// <summary>Grid width in cells.</summary>
    public int Width => _width;

    /// <summary>Grid height in cells.</summary>
    public int Height => _height;

    /// <summary>
    /// Number of searches executed (recompute probe): one per
    /// <see cref="FindPath"/> call that passed argument validation — including
    /// the trivial impassable-endpoint early-outs. A cache layer compares
    /// Misses against this to prove hits never recompute.
    /// </summary>
    public long SearchCount { get; private set; }

    /// <summary>
    /// Finds the cheapest route start → goal over passable terrain. Both
    /// endpoints inclusive; cost on the shared 10/14 metric; empty result with
    /// <see cref="PathQueryStatus.PathNotFound"/> when either endpoint is
    /// impassable or no passable route connects them.
    /// </summary>
    /// <exception cref="ArgumentNullException">start or goal is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// start or goal outside the grid.
    /// </exception>
    public PathQueryResult FindPath(CellCoord start, CellCoord goal)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(goal);
        Guard.EnsureInRange(start.X, 0, _width - 1, "start.X");
        Guard.EnsureInRange(start.Y, 0, _height - 1, "start.Y");
        Guard.EnsureInRange(goal.X, 0, _width - 1, "goal.X");
        Guard.EnsureInRange(goal.Y, 0, _height - 1, "goal.Y");

        SearchCount++;
        if (!_grid.Passable(start) || !_grid.Passable(goal))
        {
            return PathQueryResult.NotFound(start, goal);
        }

        int startIdx = CellIndex(start);
        int goalIdx = CellIndex(goal);
        if (startIdx == goalIdx)
        {
            return new PathQueryResult(PathQueryStatus.Found, start, goal, new[] { start }, 0);
        }

        ResetSearch();
        _g[startIdx] = 0;
        _h[startIdx] = Octile(startIdx, goalIdx);
        _f[startIdx] = _h[startIdx];
        _cameFrom[startIdx] = startIdx;
        HeapPush(startIdx);

        while (_heapCount > 0)
        {
            int u = HeapPop();
            _closed[u] = true;
            if (u == goalIdx)
            {
                return Reconstruct(start, goal, goalIdx);
            }

            Relax(u, goalIdx);
        }

        return PathQueryResult.NotFound(start, goal);
    }

    private void Relax(int u, int goalIdx)
    {
        int ux = u % _width;
        int uy = u / _width;
        long fromG = _g[u];
        foreach (var (dx, dy, weight, diagonal) in NeighbourOffsets)
        {
            int nx = ux + dx;
            int ny = uy + dy;
            if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
            {
                continue;
            }

            int n = ny * _width + nx;
            if (_closed[n] || !_grid.Passable(new CellCoord(nx, ny)))
            {
                continue;
            }

            if (diagonal && !CornerClear(ux, uy, nx, ny))
            {
                continue;
            }

            long terrain = _grid.Cost(new CellCoord(nx, ny));
            if (terrain < FlowField.MinimumCellCost)
            {
                terrain = FlowField.MinimumCellCost;
            }

            long candidate = fromG + (long)weight * terrain;
            if (candidate < _g[n])
            {
                _g[n] = candidate;
                _cameFrom[n] = u;
                _h[n] = Octile(n, goalIdx);
                _f[n] = candidate + _h[n];
                if (_heapPos[n] < 0)
                {
                    HeapPush(n);
                }
                else
                {
                    HeapBubbleUp(_heapPos[n]);
                }
            }
        }
    }

    /// <summary>
    /// Diagonal corner rule (对角卡角检测, same as FlowField): the diagonal
    /// step (ux,uy) → (nx,ny) requires both orthogonal flanking cells to be
    /// passable, so a porter cannot slip between two touching blockers.
    /// </summary>
    private bool CornerClear(int ux, int uy, int nx, int ny) =>
        _grid.Passable(new CellCoord(nx, uy)) && _grid.Passable(new CellCoord(ux, ny));

    private PathQueryResult Reconstruct(CellCoord start, CellCoord goal, int goalIdx)
    {
        var route = new List<CellCoord>();
        int at = goalIdx;
        while (true)
        {
            route.Add(new CellCoord(at % _width, at / _width));
            if (at == CellIndex(start))
            {
                break;
            }

            at = _cameFrom[at];
        }

        route.Reverse();
        return new PathQueryResult(PathQueryStatus.Found, start, goal, route, _g[goalIdx]);
    }

    /// <summary>
    /// Octile lower bound on the 10/14 metric with unit terrain cost: since
    /// every entered cell costs at least <see cref="FlowField.MinimumCellCost"/>
    /// = 1, this heuristic is admissible and consistent.
    /// </summary>
    private long Octile(int cellIdx, int goalIdx)
    {
        int dx = Math.Abs((cellIdx % _width) - (goalIdx % _width));
        int dy = Math.Abs((cellIdx / _width) - (goalIdx / _width));
        int max = Math.Max(dx, dy);
        int min = Math.Min(dx, dy);
        return (long)StraightWeight * max + (DiagonalWeight - StraightWeight) * min;
    }

    private void ResetSearch()
    {
        int cells = _width * _height;
        if (_g.Length != cells)
        {
            _g = new long[cells];
            _f = new long[cells];
            _h = new long[cells];
            _cameFrom = new int[cells];
            _closed = new bool[cells];
            _heapPos = new int[cells];
        }

        Array.Fill(_g, long.MaxValue);
        Array.Fill(_f, 0L);
        Array.Fill(_h, 0L);
        Array.Fill(_cameFrom, -1);
        Array.Fill(_closed, false);
        Array.Fill(_heapPos, -1);
        _heapCount = 0;
    }

    private int CellIndex(CellCoord cell) => cell.Y * _width + cell.X;

    /// <summary>
    /// Indexed binary min-heap over cell indices keyed by (f, h, cell index)
    /// — 距离+坐标序 deterministic tie-breaking. Decrease-key keeps each cell
    /// in the heap at most once (same shape as the FlowField heap).
    /// </summary>
    private void HeapPush(int cell)
    {
        if (_heapCount == _heapSlots.Length)
        {
            Array.Resize(ref _heapSlots, _heapSlots.Length * 2);
        }

        _heapSlots[_heapCount] = cell;
        _heapPos[cell] = _heapCount;
        _heapCount++;
        HeapBubbleUp(_heapCount - 1);
    }

    private void HeapBubbleUp(int slot)
    {
        int cell = _heapSlots[slot];
        while (slot > 0)
        {
            int parent = (slot - 1) >> 1;
            if (!HeapLess(cell, _heapSlots[parent]))
            {
                break;
            }

            _heapSlots[slot] = _heapSlots[parent];
            _heapPos[_heapSlots[slot]] = slot;
            slot = parent;
        }

        _heapSlots[slot] = cell;
        _heapPos[cell] = slot;
    }

    private int HeapPop()
    {
        int root = _heapSlots[0];
        _heapPos[root] = -1;
        _heapCount--;
        if (_heapCount > 0)
        {
            int last = _heapSlots[_heapCount];
            int slot = 0;
            while (true)
            {
                int left = 2 * slot + 1;
                if (left >= _heapCount)
                {
                    break;
                }

                int min = left;
                int right = left + 1;
                if (right < _heapCount && HeapLess(_heapSlots[right], _heapSlots[left]))
                {
                    min = right;
                }

                if (!HeapLess(_heapSlots[min], last))
                {
                    break;
                }

                _heapSlots[slot] = _heapSlots[min];
                _heapPos[_heapSlots[slot]] = slot;
                slot = min;
            }

            _heapSlots[slot] = last;
            _heapPos[last] = slot;
        }

        return root;
    }

    private bool HeapLess(int a, int b)
    {
        if (_f[a] != _f[b])
        {
            return _f[a] < _f[b];
        }

        if (_h[a] != _h[b])
        {
            return _h[a] < _h[b];
        }

        return a < b;
    }
}
