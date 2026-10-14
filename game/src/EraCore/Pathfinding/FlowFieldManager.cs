using System.Diagnostics;
using EraCore;
using EraCore.Contracts.Foundation;

namespace EraCore.Pathfinding;

/// <summary>
/// Bucket key of a flow field: attack lane × cost profile (design v3.2 §6
/// "分桶 ≤6"). Lanes come from level data; cost types separate e.g. ground
/// vs. future profiles without multiplying active fields.
/// </summary>
/// <param name="Lane">Language-independent lane id (K-4 SetRally domain).</param>
/// <param name="CostType">Cost profile discriminator (0 = standard).</param>
public sealed record FlowFieldKey(int Lane, int CostType);

/// <summary>
/// Axis-aligned cell region used for dirty marking. Cells covered are
/// [X, X+Width) × [Y, Y+Height); a zero-sized region covers nothing.
/// </summary>
/// <param name="X">Leftmost column.</param>
/// <param name="Y">Topmost row.</param>
/// <param name="Width">Extent in columns (cells X .. X+Width-1).</param>
/// <param name="Height">Extent in rows.</param>
public sealed record CellRegion(int X, int Y, int Width, int Height)
{
    /// <summary>Whether the region covers no cell at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whole-grid region for the given grid.</summary>
    public static CellRegion EntireGrid(IFlowGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return new CellRegion(0, 0, grid.Width, grid.Height);
    }

    /// <summary>Whether the two regions share at least one cell (touching edges alone do not count).</summary>
    public bool Intersects(CellRegion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsEmpty || other.IsEmpty)
        {
            return false;
        }

        return X < other.X + other.Width && other.X < X + Width
            && Y < other.Y + other.Height && other.Y < Y + Height;
    }
}

/// <summary>
/// Owns the active flow fields (C-11, F-043; design v3.2 §6 "流场寻路
/// （8ms/帧、分桶 ≤6、建筑血量×5 计代价）"). One bucket per
/// <see cref="FlowFieldKey"/> (lane × cost type); at most
/// <see cref="DefaultMaxActiveFields"/> stay resident with LRU eviction.
/// Rebuilds are driven by dirty regions — <see cref="MarkDirty"/> only flags
/// buckets whose coverage intersects the changed region, and the next
/// <see cref="Update"/> recomputes exactly those buckets, batched. While a
/// rebuild is in flight the previous field keeps serving gradient reads
/// (results swap in only when complete). Each <see cref="Update"/> call is one
/// simulation frame's slice: frontier advancement runs under a wall-clock
/// budget measured with <see cref="Stopwatch"/> (8 ms/frame by default,
/// parameterizable), so a large flood simply continues on later frames.
/// <para>
/// Per-bucket grid views (T-064, spike2-report §5 pit#3): a bucket key may
/// carry its OWN <see cref="IFlowGrid"/> view via <see cref="RegisterGridView"/>
/// — the dual-cost seam of the corridor traffic rule (one repriced enemy
/// view per lane×costType bucket) — while view-less keys flood the manager
/// default grid. Registrations are capped at the bucket cap (≤6), so cost
/// profiles multiply buckets, never raw view objects.
/// </para>
/// </summary>
public sealed class FlowFieldManager
{
    /// <summary>Design v3.2 §6: at most 6 active flow fields (分桶 ≤6).</summary>
    public const int DefaultMaxActiveFields = 6;

    /// <summary>Design v3.2 §6 / F-043: 8 ms per frame of flow-field budget.</summary>
    public const double DefaultFrameBudgetMs = 8.0;

    private sealed class Bucket(FlowFieldKey key)
    {
        public FlowFieldKey Key { get; } = key;

        public CellCoord Target = new(0, 0);

        // T-064 pit#3 (spike2-report §5 坑3): per-bucket grid view — the
        // bucket's build ground. Assigned by Acquire on bucket creation
        // (registered view of this key, else the manager default grid), so
        // the null-forgiving default never survives to a build.
        public IFlowGrid Grid = null!;

        // Always assigned by Acquire on bucket creation; empty default keeps
        // MarkDirty inert if that ever changed.
        public CellRegion Coverage = new(0, 0, 0, 0);

        public FlowField? Live;

        public FlowField? Pending;

        public bool Dirty;

        public int RebuildCount;

        public long LastUsed;

        public long BuildSeq;
    }

    private readonly IFlowGrid _grid;
    private readonly int _maxActiveFields;
    private readonly double _defaultBudgetMs;
    private readonly int _bloodMultiplier;
    private readonly Dictionary<FlowFieldKey, Bucket> _buckets = new();
    private readonly List<Bucket> _pending = new();

    // T-064 pit#3 (spike2-report §5 坑3): registered per-key grid views
    // (lane × costType → one IFlowGrid each). Views outlive bucket eviction
    // on purpose — an evicted lane rebuilt later must flood the SAME view.
    // The count is capped at the bucket cap, so views can never multiply
    // past the active-field budget (防视图数膨胀).
    private readonly Dictionary<FlowFieldKey, IFlowGrid> _gridViews = new();
    private long _lruSeq;
    private long _buildSeq;

    /// <summary>
    /// Creates a manager over one grid. <paramref name="maxActiveFields"/> is
    /// the bucket cap (≤6 by design), <paramref name="defaultFrameBudgetMs"/>
    /// the default per-<see cref="Update"/> wall-clock budget.
    /// </summary>
    /// <exception cref="ArgumentNullException">grid is null.</exception>
    /// <exception cref="ArgumentException">grid dimensions are non-positive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">cap &lt; 1, budget ≤ 0, or negative multiplier.</exception>
    public FlowFieldManager(
        IFlowGrid grid,
        int maxActiveFields = DefaultMaxActiveFields,
        double defaultFrameBudgetMs = DefaultFrameBudgetMs,
        int bloodCostMultiplier = FlowField.DefaultBloodCostMultiplier)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Width <= 0 || grid.Height <= 0)
        {
            throw new ArgumentException("Grid dimensions must be positive.", nameof(grid));
        }

        if (maxActiveFields < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActiveFields), maxActiveFields, "At least one active field must be allowed.");
        }

        if (defaultFrameBudgetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultFrameBudgetMs), defaultFrameBudgetMs,
                "Default frame budget must be positive; per-call Update budgets of zero are allowed.");
        }

        if (bloodCostMultiplier < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bloodCostMultiplier), bloodCostMultiplier, "Blood cost multiplier must not be negative.");
        }

        _grid = grid;
        _maxActiveFields = maxActiveFields;
        _defaultBudgetMs = defaultFrameBudgetMs;
        _bloodMultiplier = bloodCostMultiplier;
    }

    /// <summary>Number of resident buckets (never above the configured cap).</summary>
    public int ActiveFieldCount => _buckets.Count;

    /// <summary>Number of rebuilds currently in flight.</summary>
    public int PendingFieldCount => _pending.Count;

    /// <summary>
    /// T-064 pit#3 (spike2-report §5 坑3): number of registered per-key grid
    /// views — never above the bucket cap (see <see cref="RegisterGridView"/>).
    /// </summary>
    public int RegisteredViewCount => _gridViews.Count;

    /// <summary>
    /// T-064 pit#3 (spike2-report §5 坑3 清偿: 双代价视图走 lane×costType
    /// 分桶): registers ONE grid view for one bucket key. A bucket with a
    /// registered view floods THAT view (e.g. the enemy corridor repricing
    /// of <c>EraCore.Logistics.CorridorRule</c>) while every other bucket
    /// keeps flooding the manager's default grid — the formal product
    /// interface replacing the spike's hand-built second manager instance.
    /// Re-registering an existing key replaces its view in place; the
    /// registration survives bucket eviction (a rebuilt lane re-attaches to
    /// its view) and a view change flags the bucket dirty on the next
    /// <see cref="Acquire"/> so the field converges onto the new view.
    /// <para>
    /// Anti-inflation guard (防视图数膨胀): distinct registrations are capped
    /// at the bucket cap (<see cref="DefaultMaxActiveFields"/> = 6) — one
    /// view per bucket, never one per query; exceeding the cap throws.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">key or view is null.</exception>
    /// <exception cref="ArgumentException">view dimensions differ from the manager grid.</exception>
    /// <exception cref="InvalidOperationException">
    /// a NEW registration would exceed the bucket cap.
    /// </exception>
    public void RegisterGridView(FlowFieldKey key, IFlowGrid view)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(view);
        if (view.Width != _grid.Width || view.Height != _grid.Height)
        {
            throw new ArgumentException(
                $"Grid view dimensions ({view.Width}×{view.Height}) must match the manager grid ({_grid.Width}×{_grid.Height}).",
                nameof(view));
        }

        if (!_gridViews.ContainsKey(key) && _gridViews.Count >= _maxActiveFields)
        {
            throw new InvalidOperationException(
                $"At most {_maxActiveFields} grid views may be registered (one per active bucket, lane×costType keyed); key {key} would exceed the cap.");
        }

        bool changed = !_gridViews.TryGetValue(key, out IFlowGrid? existing) || existing != view;
        _gridViews[key] = view;
        if (changed && _buckets.TryGetValue(key, out Bucket? bucket))
        {
            // T-064 pit#3: live field was built on another view — flag and
            // restart exactly like MarkDirty so the bucket converges onto
            // the new view.
            bucket.Grid = view;
            bucket.Dirty = true;
            if (bucket.Pending is not null)
            {
                DropPending(bucket);
                StartPending(bucket);
            }
        }
    }

    /// <summary>
    /// T-064 pit#3: looks up the registered view of a key (null when the
    /// bucket floods the manager default grid).
    /// </summary>
    /// <exception cref="ArgumentNullException">key is null.</exception>
    public IFlowGrid? GetGridView(FlowFieldKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _gridViews.TryGetValue(key, out IFlowGrid? view) ? view : null;
    }

    /// <summary>
    /// Returns the field for <paramref name="key"/>, touching its LRU stamp.
    /// Creates the bucket on first use (evicting the least recently used one
    /// when the cap is exceeded) and queues a build when no live field exists
    /// yet or the target moved. While a rebuild is pending the previous live
    /// field keeps being returned.
    /// </summary>
    /// <param name="coverage">
    /// Cells this lane's field is responsible for; dirty regions outside the
    /// coverage never trigger a rebuild of this bucket. Defaults to the whole
    /// grid.
    /// </param>
    /// <exception cref="ArgumentNullException">key or target is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">target outside the grid.</exception>
    public FlowField Acquire(FlowFieldKey key, CellCoord target, CellRegion? coverage = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(target);
        Guard.EnsureInRange(target.X, 0, _grid.Width - 1, "target.X");
        Guard.EnsureInRange(target.Y, 0, _grid.Height - 1, "target.Y");

        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(key)
            {
                Target = target,
                // T-064 pit#3: attach the registered view of this key at
                // creation; unregistered keys flood the manager default grid.
                Grid = _gridViews.TryGetValue(key, out IFlowGrid? view) ? view : _grid,
                Coverage = coverage ?? CellRegion.EntireGrid(_grid),
                LastUsed = ++_lruSeq,
            };
            _buckets.Add(key, bucket);
            EvictOverCapacity(bucket);
        }
        else
        {
            bucket.LastUsed = ++_lruSeq;
            bucket.Target = target;
            if (coverage is not null)
            {
                bucket.Coverage = coverage;
            }
        }

        if (bucket.Live is not null && bucket.Live.Target == target && bucket.Pending is null)
        {
            return bucket.Live;
        }

        if (bucket.Pending is not null && (bucket.Pending.Target.X != target.X || bucket.Pending.Target.Y != target.Y))
        {
            DropPending(bucket);
        }

        if (bucket.Pending is null && (bucket.Live is null || bucket.Live.Target != target))
        {
            StartPending(bucket);
        }

        return bucket.Live ?? bucket.Pending!;
    }

    /// <summary>
    /// Flags buckets whose coverage intersects <paramref name="region"/> for
    /// rebuild on the next <see cref="Update"/> — the 脏区合批 increment: only
    /// affected buckets recompute, untouched buckets keep serving their field.
    /// A rebuild already in flight is restarted so its grid snapshot re-reads
    /// the mutated cells.
    /// </summary>
    /// <exception cref="ArgumentNullException">region is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">region extent is negative.</exception>
    public void MarkDirty(CellRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Width < 0 || region.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), region, "Region extent must not be negative.");
        }

        if (region.IsEmpty)
        {
            return;
        }

        foreach (Bucket bucket in _buckets.Values)
        {
            if (!bucket.Coverage.Intersects(region))
            {
                continue;
            }

            bucket.Dirty = true;
            if (bucket.Pending is not null)
            {
                DropPending(bucket);
                StartPending(bucket);
            }
        }
    }

    /// <summary>
    /// One frame's slice: starts rebuilds flagged dirty, then advances all
    /// pending frontiers in deterministic build order while the shared
    /// wall-clock budget lasts. Returns true when nothing is pending.
    /// A zero budget performs no flood work but still reports pending state.
    /// </summary>
    /// <param name="frameBudgetMs">
    /// Budget for this call; null uses the manager default (8 ms). Zero is the
    /// legal no-progress boundary.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">budget is negative.</exception>
    public bool Update(double? frameBudgetMs = null)
    {
        double budgetMs = frameBudgetMs ?? _defaultBudgetMs;
        if (budgetMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameBudgetMs), "Frame budget must not be negative; zero performs no flood work this call.");
        }

        foreach (Bucket bucket in _buckets.Values)
        {
            if (!bucket.Dirty)
            {
                continue;
            }

            bucket.Dirty = false;
            if (bucket.Pending is null)
            {
                StartPending(bucket);
            }
        }

        if (_pending.Count == 0)
        {
            return true;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs / 1000.0 * Stopwatch.Frequency);
        foreach (Bucket bucket in _pending.ToArray())
        {
            if (bucket.Pending is null)
            {
                continue;
            }

            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            var slice = remainingTicks > 0
                ? TimeSpan.FromTicks((long)(remainingTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency))
                : TimeSpan.Zero;
            if (bucket.Pending.Advance(slice))
            {
                bucket.Live = bucket.Pending;
                bucket.Pending = null;
                bucket.RebuildCount++;
            }
        }

        _pending.RemoveAll(b => b.Pending is null);
        return _pending.Count == 0;
    }

    /// <summary>The live (served) field of a bucket, or null when absent or never completed.</summary>
    public FlowField? GetField(FlowFieldKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _buckets.TryGetValue(key, out var bucket) ? bucket.Live : null;
    }

    /// <summary>Completed rebuild count of a bucket (0 when unknown).</summary>
    public int GetRebuildCount(FlowFieldKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _buckets.TryGetValue(key, out var bucket) ? bucket.RebuildCount : 0;
    }

    private void StartPending(Bucket bucket)
    {
        // T-064 pit#3: build on the bucket's own grid view (registered per
        // lane×costType), not the manager default.
        bucket.Pending = FlowField.StartBuild(bucket.Grid, bucket.Target, _bloodMultiplier);
        bucket.BuildSeq = ++_buildSeq;
        _pending.Add(bucket);
    }

    private void DropPending(Bucket bucket)
    {
        _pending.Remove(bucket);
        bucket.Pending = null;
    }

    private void EvictOverCapacity(Bucket keep)
    {
        while (_buckets.Count > _maxActiveFields)
        {
            Bucket? victim = null;
            foreach (Bucket bucket in _buckets.Values)
            {
                if (bucket == keep || (victim is not null && bucket.LastUsed >= victim.LastUsed))
                {
                    continue;
                }

                victim = bucket;
            }

            if (victim is null)
            {
                break;
            }

            DropPending(victim);
            _buckets.Remove(victim.Key);
        }
    }
}
