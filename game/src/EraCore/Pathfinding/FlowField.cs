using System.Diagnostics;
using EraCore.Contracts.Foundation;

namespace EraCore.Pathfinding;

/// <summary>Result mode of a flow field (F-043/F-044, design v3.2 §6).</summary>
public enum FlowFieldMode
{
    /// <summary>Still building (snapshot or frontier not settled); gradient APIs refuse service.</summary>
    NotReady = 0,

    /// <summary>Every walkable cell reaches the target on walkable terrain alone; pure walk field.</summary>
    Normal = 1,

    /// <summary>
    /// At least one walkable cell cannot reach the target without tearing, so the
    /// field was rebuilt over the tear graph (enemy-building cells traversable at
    /// terrain cost + HP × blood multiplier). The gradient then routes through the
    /// lowest-total-cost tearable wall chain — the 拆墙路径 (F-044).
    /// </summary>
    WallTear = 2,
}

/// <summary>
/// Single-target flow field built by reverse Dijkstra flooding from the player
/// base (C-11, F-043/F-044; design v3.2 §6 "流场寻路（8ms/帧、分桶 ≤6、建筑
/// 血量×5 计代价）；全封死→拆墙模式"). This is the real implementation required
/// by RL2: a genuine Dijkstra frontier expansion over an <see cref="IFlowGrid"/>
/// snapshot, not a cached path or a mocked gradient.
/// <para>
/// Cost model — the edge weight of stepping INTO a cell n is
/// segment length × (terrain <see cref="IFlowGrid.Cost"/> + <see
/// cref="IFlowGrid.BloodCost"/> × blood multiplier); segment lengths use the
/// integer metric straight = 10, diagonal = 14. Non-traversable cells are
/// infinite cost. Diagonal moves additionally require BOTH orthogonally
/// adjacent corner cells to be traversable (对角卡角检测), both during flooding
/// and in <see cref="NextStep"/>.
/// </para>
/// <para>
/// Wall-tear mode — after the walk flood settles, any walkable cell left
/// unreached means the target is unreachable without tearing; the field then
/// refloods with tearable cells (blocking buildings carrying blood cost)
/// traversable and reports <see cref="FlowFieldMode.WallTear"/>. Cells still
/// unreached afterwards are sealed by untearable terrain.
/// </para>
/// <para>
/// Time slicing — construction snapshots the grid (row-sliced), then the
/// frontier is advanced in slices. Slice boundaries never influence
/// results: settled distances are a pure function of the input, so a field
/// built in many small frames is bit-identical to a one-shot build and
/// <see cref="Hash"/> proves it. Two advance entries exist:
/// <see cref="Advance(TimeSpan)"/> (wall-clock budget, Stopwatch) and
/// <see cref="AdvanceDeterministic(int)"/> (cell-quota budget, T-067 DEBT
/// 清账). ONLY the deterministic entry may drive simulation ticks: a
/// wall-clock slice consumes a load-dependent amount of work per tick, so
/// the tick on which a rebuild completes (and with it the seed-simulation
/// hash) diverges under CPU contention (T-086 finding — two hash
/// divergences in OffSystemRegressionTests under load). The quota entry
/// spends exactly N charged work units per call — one per snapshot-scanned
/// cell and one per settled (popped) frontier cell — so completion ticks
/// and hashes are load-independent by construction.
/// </para>
/// </summary>
public sealed class FlowField
{
    /// <summary>Design v3.2 §6: enemy-building HP counts ×5 toward cell cost.</summary>
    public const int DefaultBloodCostMultiplier = 5;

    /// <summary>Lower clamp for terrain cost (zero-cost edges would break gradient descent).</summary>
    public const int MinimumCellCost = 1;

    /// <summary>Sentinel distance for cells that cannot reach the target in the current mode.</summary>
    public const long Unreached = long.MaxValue;

    private const int StraightWeight = 10;
    private const int DiagonalWeight = 14;
    private const int BudgetCheckPopBatch = 32;
    private const int BudgetCheckRowBatch = 8;

    private const int PhaseSnapshot = 0;
    private const int PhaseFloodWalk = 1;
    private const int PhaseFloodTear = 2;
    private const int PhaseReady = 3;

    // Fixed 8-neighbour scan order — deterministic tie-breaking everywhere.
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
    private readonly CellCoord _target;
    private readonly int _bloodMultiplier;

    private readonly long[] _cellCost;
    private readonly bool[] _walkable;
    private readonly bool[] _tearable;
    private readonly long[] _dist;
    private readonly bool[] _settled;
    private readonly int[] _heapPos;
    private int[] _heapSlots = new int[256];
    private int _heapCount;
    private int _phase;
    private int _scanRow;
    private int _scanCol;
    private FlowFieldMode _mode;
    private long _hash;
    private bool _hashComputed;
    private long _processedCells;

    private FlowField(IFlowGrid grid, int width, int height, CellCoord target, int bloodMultiplier)
    {
        _grid = grid;
        _width = width;
        _height = height;
        _target = target;
        _bloodMultiplier = bloodMultiplier;
        int cells = width * height;
        _cellCost = new long[cells];
        _walkable = new bool[cells];
        _tearable = new bool[cells];
        _dist = new long[cells];
        _settled = new bool[cells];
        _heapPos = new int[cells];
        _mode = FlowFieldMode.NotReady;
    }

    /// <summary>Target cell the field flows toward (the player base).</summary>
    public CellCoord Target => _target;

    /// <summary>Grid width in cells.</summary>
    public int Width => _width;

    /// <summary>Grid height in cells.</summary>
    public int Height => _height;

    /// <summary>Whether every build phase has settled and the gradient can be served.</summary>
    public bool IsReady => _phase == PhaseReady;

    /// <summary>Build mode; <see cref="FlowFieldMode.NotReady"/> until <see cref="IsReady"/>.</summary>
    public FlowFieldMode Mode => _mode;

    /// <summary>
    /// Cumulative charged work units consumed so far (T-067 DEBT 清账):
    /// one per snapshot-scanned cell, one per settled (popped) frontier
    /// cell. Observation-only telemetry — never influences results; used by
    /// the manager to share one tick's cell quota across pending rebuilds
    /// and by tests/perf records to reason about throughput. Uncharged
    /// bounded bookkeeping (frontier-settle scans, flood resets) is
    /// documented on <see cref="AdvanceDeterministic"/>.
    /// </summary>
    public long ProcessedCells => _processedCells;

    /// <summary>
    /// Starts a buildable field: grid snapshot and frontier expansion happen
    /// lazily inside <see cref="Advance"/>, so construction itself is cheap and
    /// sliceable.
    /// </summary>
    /// <exception cref="ArgumentNullException">grid or target is null.</exception>
    /// <exception cref="ArgumentException">grid dimensions are non-positive or too large.</exception>
    /// <exception cref="ArgumentOutOfRangeException">target outside the grid, or negative multiplier.</exception>
    public static FlowField StartBuild(
        IFlowGrid grid,
        CellCoord target,
        int bloodCostMultiplier = DefaultBloodCostMultiplier)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(target);
        if (bloodCostMultiplier < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bloodCostMultiplier), bloodCostMultiplier, "Blood cost multiplier must not be negative.");
        }

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

        Guard.EnsureInRange(target.X, 0, width - 1, "target.X");
        Guard.EnsureInRange(target.Y, 0, height - 1, "target.Y");
        return new FlowField(grid, width, height, target, bloodCostMultiplier);
    }

    /// <summary>
    /// One-shot build: advances with generous one-second slices until settled
    /// (slice boundaries do not affect results, so this equals any sliced build).
    /// </summary>
    public static FlowField BuildComplete(
        IFlowGrid grid,
        CellCoord target,
        int bloodCostMultiplier = DefaultBloodCostMultiplier)
    {
        var field = StartBuild(grid, target, bloodCostMultiplier);
        while (!field.Advance(TimeSpan.FromSeconds(1)))
        {
        }

        return field;
    }

    /// <summary>
    /// Advances snapshot scanning and frontier settling within the wall-clock
    /// <paramref name="budget"/>. Returns true when the field is complete; an
    /// incomplete field keeps its previous (possibly empty) state and the caller
    /// keeps serving any older field meanwhile.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">budget is negative.</exception>
    public bool Advance(TimeSpan budget)
    {
        if (budget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "Budget must not be negative.");
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        while (_phase != PhaseReady && Stopwatch.GetTimestamp() < deadline)
        {
            if (_phase == PhaseSnapshot)
            {
                RunSnapshot(deadline);
            }
            else if (_phase == PhaseFloodWalk)
            {
                RunFlood(deadline, tearGraph: false);
            }
            else
            {
                RunFlood(deadline, tearGraph: true);
            }
        }

        return _phase == PhaseReady;
    }

    /// <summary>
    /// Advances snapshot scanning and frontier settling by exactly
    /// <paramref name="cellBudget"/> charged work units at most (T-067 DEBT
    /// 清账: deterministic per-tick budget). Returns true when the field is
    /// complete; an incomplete field keeps its previous (possibly empty)
    /// state and the caller keeps serving any older field meanwhile.
    /// <para>
    /// Work accounting — one charged unit per snapshot-scanned cell
    /// (per-cell with mid-row resume, so any positive quota makes progress
    /// on any grid width) and one per settled (popped) frontier cell. The
    /// total charged work of a build is a pure function of the grid
    /// snapshot (Dijkstra pop order is deterministic), so with a fixed
    /// per-tick quota the tick on which the field completes is identical
    /// regardless of wall-clock load — the load-independence property the
    /// wall-clock <see cref="Advance(TimeSpan)"/> entry cannot offer (CPU
    /// contention shrinks its per-tick work, shifting completion and
    /// diverging seed-simulation hashes; T-086 finding).
    /// </para>
    /// <para>
    /// Uncharged bounded bookkeeping: when a flood phase settles (heap
    /// empty) the unreached-walkable scan, flood reset and mode/phase
    /// transitions of that settle run within the same call without spending
    /// quota. Each is O(cells) and happens at most once per phase settle —
    /// a fixed, slice-independent count per build — so per-tick worst case
    /// is quota + O(cells) and determinism is unaffected.
    /// </para>
    /// </summary>
    /// <param name="cellBudget">Charged work units this call may spend; zero is the legal no-progress boundary.</param>
    /// <exception cref="ArgumentOutOfRangeException">cellBudget is negative.</exception>
    public bool AdvanceDeterministic(int cellBudget)
    {
        if (cellBudget < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellBudget), cellBudget, "Cell budget must not be negative; zero performs no work this call.");
        }

        int remaining = cellBudget;
        while (_phase != PhaseReady && remaining > 0)
        {
            if (_phase == PhaseSnapshot)
            {
                RunSnapshotQuota(ref remaining);
            }
            else if (_phase == PhaseFloodWalk)
            {
                RunFloodQuota(ref remaining, tearGraph: false);
            }
            else
            {
                RunFloodQuota(ref remaining, tearGraph: true);
            }
        }

        return _phase == PhaseReady;
    }

    /// <summary>
    /// Gradient read: the 8-neighbour cell minimising entry cost plus integration
    /// distance, honouring the diagonal corner rule (对角卡角检测). Returns the
    /// cell itself when it is the target, unreachable, or has no traversable
    /// neighbour on a cheaper route.
    /// </summary>
    /// <exception cref="InvalidOperationException">field is not ready.</exception>
    /// <exception cref="ArgumentNullException">cell is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">cell outside the grid.</exception>
    public CellCoord NextStep(CellCoord cell)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(cell);
        int x = cell.X;
        int y = cell.Y;
        if (x < 0 || x >= _width || y < 0 || y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, "Cell is outside the grid.");
        }

        if (x == _target.X && y == _target.Y)
        {
            return cell;
        }

        int idx = y * _width + x;
        if (_dist[idx] == Unreached)
        {
            return cell;
        }

        bool[] graph = _mode == FlowFieldMode.WallTear ? _tearable : _walkable;
        CellCoord best = cell;
        long bestScore = long.MaxValue;
        foreach (var (dx, dy, weight, diagonal) in NeighbourOffsets)
        {
            int nx = x + dx;
            int ny = y + dy;
            if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
            {
                continue;
            }

            int n = ny * _width + nx;
            if (_dist[n] == Unreached || !graph[n])
            {
                continue;
            }

            if (diagonal && (!graph[y * _width + nx] || !graph[ny * _width + x]))
            {
                continue;
            }

            long score = _dist[n] + (long)weight * _cellCost[n];
            if (score < bestScore)
            {
                bestScore = score;
                best = new CellCoord(nx, ny);
            }
        }

        return best;
    }

    /// <summary>
    /// Integrated path cost from <paramref name="cell"/> to the target
    /// (sum of entry costs along the cheapest route); <see cref="Unreached"/>
    /// when the cell cannot reach the target in the current mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">field is not ready.</exception>
    /// <exception cref="ArgumentNullException">cell is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">cell outside the grid.</exception>
    public long Distance(CellCoord cell)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(cell);
        int x = cell.X;
        int y = cell.Y;
        if (x < 0 || x >= _width || y < 0 || y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, "Cell is outside the grid.");
        }

        return _dist[y * _width + x];
    }

    /// <summary>Whether the cell reaches the target in the current mode.</summary>
    /// <exception cref="InvalidOperationException">field is not ready.</exception>
    /// <exception cref="ArgumentNullException">cell is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">cell outside the grid.</exception>
    public bool IsReachable(CellCoord cell) => Distance(cell) != Unreached;

    /// <summary>
    /// Whether the cell blocks walking but is tearable — i.e. a building the
    /// wall-tear route must destroy to pass (F-044).
    /// </summary>
    /// <exception cref="InvalidOperationException">field is not ready.</exception>
    /// <exception cref="ArgumentNullException">cell is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">cell outside the grid.</exception>
    public bool IsTearCell(CellCoord cell)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(cell);
        int idx = CellIndex(cell);
        return _tearable[idx] && !_walkable[idx];
    }

    /// <summary>
    /// Gradient-descent path from <paramref name="from"/> to the target by
    /// replaying <see cref="NextStep"/>. In <see cref="FlowFieldMode.WallTear"/>
    /// this is the 拆墙路径: the lowest-total-cost tearable chain. Distance
    /// strictly decreases along every step, so the walk cannot cycle.
    /// </summary>
    /// <exception cref="InvalidOperationException">field is not ready, or the walk exceeds the step bound.</exception>
    /// <exception cref="ArgumentNullException">from is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">from outside the grid.</exception>
    public IReadOnlyList<CellCoord> BuildPath(CellCoord from)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(from);
        if (from.X < 0 || from.X >= _width || from.Y < 0 || from.Y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, "Cell is outside the grid.");
        }

        var path = new List<CellCoord> { from };
        if (_dist[CellIndex(from)] == Unreached || (from.X == _target.X && from.Y == _target.Y))
        {
            return path;
        }

        var current = from;
        int stepBudget = _width * _height + 2;
        while (current.X != _target.X || current.Y != _target.Y)
        {
            CellCoord next = NextStep(current);
            if (next.X == current.X && next.Y == current.Y)
            {
                // Unreachable pocket inside a tear field: stop at the frontier.
                return path;
            }

            path.Add(next);
            current = next;
            if (--stepBudget == 0)
            {
                throw new InvalidOperationException("Path descent exceeded the cell count; gradient is inconsistent.");
            }
        }

        return path;
    }

    /// <summary>
    /// Deterministic content hash over mode and all integration distances.
    /// Equal inputs give equal hashes regardless of how the build was sliced
    /// in time (Stopwatch never enters the result).
    /// </summary>
    /// <exception cref="InvalidOperationException">field is not ready.</exception>
    public long Hash
    {
        get
        {
            EnsureReady();
            if (!_hashComputed)
            {
                unchecked
                {
                    const ulong prime = 1099511628211UL;
                    ulong h = 14695981039346656037UL;
                    h = (h ^ (ulong)_width) * prime;
                    h = (h ^ (ulong)_height) * prime;
                    h = (h ^ (ulong)(int)_mode) * prime;
                    foreach (long d in _dist)
                    {
                        ulong v = d == Unreached ? ulong.MaxValue : (ulong)d;
                        h = (h ^ v) * prime;
                        h = (h ^ (v >> 32)) * prime;
                    }

                    _hash = (long)h;
                }

                _hashComputed = true;
            }

            return _hash;
        }
    }

    private void EnsureReady()
    {
        if (_phase != PhaseReady)
        {
            throw new InvalidOperationException("Flow field gradient is not built yet; advance the build first.");
        }
    }

    private int CellIndex(CellCoord cell) => cell.Y * _width + cell.X;

    private void RunSnapshot(long deadline)
    {
        while (_scanRow < _height)
        {
            if ((_scanRow & (BudgetCheckRowBatch - 1)) == 0 && Stopwatch.GetTimestamp() >= deadline)
            {
                return;
            }

            ScanRow();
        }

        CompleteSnapshot();
    }

    /// <summary>
    /// Quota-driven snapshot: scans CELL BY CELL (mid-row resume via
    /// <see cref="_scanCol"/>) while quota lasts — per-cell charging keeps
    /// the quota an exact ceiling for every grid width (no whole-row
    /// granularity, so a quota below the grid width still makes progress).
    /// </summary>
    private void RunSnapshotQuota(ref int remaining)
    {
        while (_scanRow < _height && remaining > 0)
        {
            int rowBase = _scanRow * _width;
            while (_scanCol < _width && remaining > 0)
            {
                ScanCell(rowBase + _scanCol, _scanCol, _scanRow);
                _scanCol++;
                remaining--;
            }

            if (_scanCol >= _width)
            {
                _scanCol = 0;
                _scanRow++;
            }
        }

        if (_scanRow >= _height)
        {
            CompleteSnapshot();
        }
    }

    /// <summary>Scans the row at <see cref="_scanRow"/> into the snapshot arrays.</summary>
    private void ScanRow()
    {
        int rowBase = _scanRow * _width;
        for (int x = 0; x < _width; x++)
        {
            ScanCell(rowBase + x, x, _scanRow);
        }

        _scanRow++;
    }

    /// <summary>Reads one cell of the grid into the snapshot arrays and charges one work unit.</summary>
    private void ScanCell(int index, int x, int y)
    {
        var cell = new CellCoord(x, y);
        long cost = _grid.Cost(cell);
        long blood = _grid.BloodCost(cell);
        bool passable = _grid.Passable(cell);
        if (cost < MinimumCellCost)
        {
            cost = MinimumCellCost;
        }

        if (blood < 0)
        {
            blood = 0;
        }

        _cellCost[index] = cost + blood * _bloodMultiplier;
        _walkable[index] = passable;
        _tearable[index] = passable || blood > 0;
        _processedCells++;
    }

    private void CompleteSnapshot()
    {
        ResetFlood();
        _phase = PhaseFloodWalk;
    }

    private void ResetFlood()
    {
        Array.Fill(_dist, Unreached);
        Array.Fill(_settled, false);
        Array.Fill(_heapPos, -1);
        _heapCount = 0;
        int targetIdx = _target.Y * _width + _target.X;
        _dist[targetIdx] = 0;
        HeapPush(targetIdx);
    }

    private void RunFlood(long deadline, bool tearGraph)
    {
        bool[] graph = tearGraph ? _tearable : _walkable;
        int batch = 0;
        while (_heapCount > 0)
        {
            if (batch == 0 && Stopwatch.GetTimestamp() >= deadline)
            {
                return;
            }

            batch = batch + 1 == BudgetCheckPopBatch ? 0 : batch + 1;

            PopAndSettle(graph);
        }

        SettleFloodPhase(tearGraph);
    }

    /// <summary>
    /// Quota-driven flood: settles at most <paramref name="remaining"/>
    /// frontier cells. When the quota runs out with frontier work left the
    /// phase settle is deferred to a later call; when the heap empties the
    /// (uncharged, bounded) settle bookkeeping runs within this call.
    /// </summary>
    private void RunFloodQuota(ref int remaining, bool tearGraph)
    {
        bool[] graph = tearGraph ? _tearable : _walkable;
        while (_heapCount > 0 && remaining > 0)
        {
            remaining--;
            PopAndSettle(graph);
        }

        if (_heapCount > 0)
        {
            return;
        }

        SettleFloodPhase(tearGraph);
    }

    private void PopAndSettle(bool[] graph)
    {
        int u = HeapPop();
        _settled[u] = true;
        _processedCells++;
        Relax(u, graph);
    }

    private void SettleFloodPhase(bool tearGraph)
    {
        if (tearGraph)
        {
            _phase = PhaseReady;
            return;
        }

        if (HasUnreachedWalkableCell())
        {
            // 全封死检测: unreachable without tearing — reflood over the tear graph.
            ResetFlood();
            _mode = FlowFieldMode.WallTear;
            _phase = PhaseFloodTear;
        }
        else
        {
            _mode = FlowFieldMode.Normal;
            _phase = PhaseReady;
        }
    }

    private bool HasUnreachedWalkableCell()
    {
        for (int i = 0; i < _walkable.Length; i++)
        {
            if (_walkable[i] && _dist[i] == Unreached)
            {
                return true;
            }
        }

        return false;
    }

    private void Relax(int u, bool[] graph)
    {
        int ux = u % _width;
        int uy = u / _width;
        long fromDist = _dist[u];
        long cellCostU = _cellCost[u];
        foreach (var (dx, dy, weight, diagonal) in NeighbourOffsets)
        {
            int nx = ux + dx;
            int ny = uy + dy;
            if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
            {
                continue;
            }

            int n = ny * _width + nx;
            if (_settled[n] || !graph[n])
            {
                continue;
            }

            if (diagonal && (!graph[uy * _width + nx] || !graph[ny * _width + ux]))
            {
                continue;
            }

            long candidate = fromDist + (long)weight * cellCostU;
            if (candidate < _dist[n])
            {
                _dist[n] = candidate;
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
    /// Indexed binary min-heap over cell indices keyed by <see cref="_dist"/>
    /// (ties broken by cell index — deterministic). Decrease-key keeps each
    /// cell in the heap at most once, so the frontier never carries stale
    /// entries and pops are exactly one per settled cell.
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

    private bool HeapLess(int a, int b) =>
        _dist[a] != _dist[b] ? _dist[a] < _dist[b] : a < b;
}
