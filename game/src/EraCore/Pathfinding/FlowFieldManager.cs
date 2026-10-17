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
/// Which budget entry drove the most recent manager advance — the W4 门禁
/// F1 observation bit (REV-20261015-wave4-gate):
/// <see cref="FlowFieldManager.Update(double?)"/> stamps
/// <see cref="WallClock"/>, <see cref="FlowFieldManager.UpdateDeterministic(int?)"/>
/// stamps <see cref="Deterministic"/>. Observation only (never a behavior
/// input): the binding guard SimulationFlowFieldBindingTests asserts
/// Deterministic after Simulation ticks, so re-wiring the simulation slice
/// back to the load-dependent wall-clock entry goes test-red instead of
/// silently diverging same-seed hashes (T-086 finding, T-067 DEBT 清账).
/// </summary>
public enum FlowFieldAdvanceMode
{
    /// <summary>No advance call yet (fresh manager).</summary>
    None = 0,

    /// <summary>Legacy wall-clock entry (tooling and non-simulation callers only).</summary>
    WallClock = 1,

    /// <summary>Deterministic cell-quota entry (THE simulation entry).</summary>
    Deterministic = 2,
}

/// <summary>
/// Owns the active flow fields (C-11, F-043; design v3.2 §6 "流场寻路
/// （8ms/帧、分桶 ≤6、建筑血量×5 计代价）"). One bucket per
/// <see cref="FlowFieldKey"/> (lane × cost type); at most
/// <see cref="DefaultMaxActiveFields"/> stay resident with LRU eviction.
/// Rebuilds are driven by dirty regions — <see cref="MarkDirty"/> only flags
/// buckets whose coverage intersects the changed region, and the next
/// <see cref="UpdateDeterministic"/> recomputes exactly those buckets,
/// batched. While a rebuild is in flight the previous field keeps serving
/// gradient reads (results swap in only when complete). Each update call is
/// one simulation frame's slice, with two budget entries (T-067 DEBT 清账):
/// <list type="bullet">
/// <item><see cref="UpdateDeterministic(int?)"/> — the SIMULATION entry: a
/// cell quota (<see cref="DefaultCellsPerTick"/> = 6144 charged work units
/// per tick, workload- not time-based; T-153 recalibration). A large flood simply continues on
/// later frames, and the tick on which it completes is a pure function of
/// the grid snapshot — load-independent by construction, so same-seed
/// simulations hash identically under any CPU contention (AGENTS.md
/// discipline 9; the wall-clock entry's budget misses shifted rebuild
/// completion and diverged hashes — T-086 finding, OffSystemRegressionTests
/// two divergences under load).</item>
/// <item><see cref="Update(double?)"/> — the legacy wall-clock entry
/// (Stopwatch, 8 ms/frame by default), retained for tooling and
/// already-written callers that never feed simulation ticks.</item>
/// </list>
/// The Stopwatch inside <see cref="UpdateDeterministic"/> is demoted to an
/// observation-only soft guard (warn beyond <see cref="SoftGuardWarnMs"/>,
/// never a behavior change).
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

    /// <summary>
    /// Default deterministic quota of <see cref="UpdateDeterministic(int?)"/>
    /// — charged work units per simulation tick (one per snapshot-scanned
    /// cell or settled frontier cell; see
    /// <see cref="FlowField.AdvanceDeterministic(int)"/>).
    /// <para>
    /// Calibration v1 (T-067, 2026-10-15, A5-class dev box under a parallel
    /// CI lane): ≈0.32 µs per charged unit ⇒ 2048 units ≈ 0.7 ms/tick,
    /// quota-vs-wallclock throughput ratio 0.928 — passed with margin then.
    /// <br/>
    /// Calibration v2 (T-153, 2026-10-13 recalibration, RED-FLAG
    /// 2026-10-17: the 2048 quota rode the throughput gate's line all day —
    /// 0.899 → 0.862 loaded → quiet-window red/green/red). Measured on this
    /// machine (8-core dev box, quiet window, Debug test-host profile,
    /// 300×300 sparse d=0.08 seed=42 WallTear full flood = 255,794 charged
    /// units, wall-clock 8 ms entry processes a median ≈19.2k cells per
    /// 8 ms frame — but see the envelope bound below): per-unit cost
    /// ≈0.30-0.34 µs mean-field and ≈0.60 µs on the heaviest single tick
    /// (quota path worst-phase Debug) ⇒
    /// <list type="bullet">
    /// <item>6144 units ≈ 1.9-3.7 ms/tick Debug worst-phase (8-core dev
    /// box), ≈2.7× margin under the 8 ms design slice (v3.2 §6) and
    /// ≈2.7× under the 10 ms single-slice spike gate (Spike1PerformanceTests
    /// gate ④); ≈4.3× under the 16 ms soft guard (<see cref="SoftGuardWarnMs"/>).</item>
    /// <item>300×300 rebuild completes in 42 ticks (~1.4 s @ 30 Hz; was 125
    /// ticks at 2048); E1 128×128 maps ≈46k units ⇒ ~8 ticks.</item>
    /// <item>Guardrail DeterministicQuotaThroughputMatchesWallclockOnFullFlood
    /// (same-round self-comparison, machine-independent ≥90% ratio): quiet
    /// window three runs 1.000/0.987/1.014 (min-of-3 steady-state sampling,
    /// T-153) — the 2048 era measured 0.814-0.963 per attempt with a
    /// structural per-tick tax (~125 ticks × ~0.06 ms manager bookkeeping
    /// ≈ 8-10% throughput) plus cold-JIT/first-call draws.</item>
    /// </list>
    /// Formula-vs-envelope note (T-153 derivation): "quota = median
    /// 8 ms-frame cells × 0.95" evaluates to ≈18.2k on this machine, but a
    /// single 18k-unit tick measured 11.03 ms Debug on this very box — over
    /// the 10 ms spike-gate line even quiet; the binding constraint is the
    /// single-slice envelope (quota × worst-phase per-unit cost ≤ 10 ms ÷
    /// load factor), giving an upper bound ≈11k at 1.5× load; 6144 sits at
    /// ≈55% of that bound with the guardrail ratio already at parity (~1.0),
    /// so nothing between 6144 and 11k buys gate margin — it only burns
    /// frame-budget margin.
    /// <br/>
    /// This constant enters the deterministic hash chain (Simulation
    /// UpdateDeterministic call site): changing it shifts rebuild-completion
    /// ticks and re-pins the golden files (T-153 re-pinned all seven golden
    /// channels; REV-20261017-T153). Enlarging further is safe for
    /// determinism (workload-based) but re-pins goldens again — do it only
    /// with a fresh calibration record here.
    /// </para>
    /// </summary>
    public const int DefaultCellsPerTick = 6144;

    /// <summary>
    /// Soft-guard line of <see cref="UpdateDeterministic(int?)"/>: a tick
    /// slower than this is OBSERVED (counter + optional sink) but never
    /// changes behavior — the quota stays authoritative. 16 ms ≈ half a
    /// 30 Hz tick (K-1), the point where sustained overshoot deserves
    /// eyes, not corrective action.
    /// </summary>
    public const double SoftGuardWarnMs = 16.0;

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
    /// Charged work units spent by the LAST <see cref="UpdateDeterministic"/>
    /// call across all pending buckets (observation-only telemetry — quota
    /// accounting, never a behavior input). Zero when the call had nothing
    /// pending. Spike/perf records read this for per-frame cell throughput.
    /// </summary>
    public int LastTickCellsProcessed { get; private set; }

    /// <summary>
    /// Budget entry that drove the LAST advance call (W4 门禁 F1 观察位 —
    /// observation only, never a behavior input): the binding guard
    /// SimulationFlowFieldBindingTests reads this after Simulation ticks;
    /// see <see cref="FlowFieldAdvanceMode"/>.
    /// </summary>
    public FlowFieldAdvanceMode LastAdvanceMode { get; private set; }

    /// <summary>
    /// Number of <see cref="UpdateDeterministic"/> ticks whose wall time
    /// exceeded the soft-guard threshold so far (observation only — see
    /// <see cref="SoftGuardWarnMs"/>; never influences slicing).
    /// </summary>
    public long SlowTickCount { get; private set; }

    /// <summary>
    /// Soft-guard line actually in force (defaults to
    /// <see cref="SoftGuardWarnMs"/>; also the deterministic test seam —
    /// tests lower it to force the observation path without real delay).
    /// Observation only.
    /// </summary>
    public double SoftGuardThresholdMs { get; set; } = SoftGuardWarnMs;

    /// <summary>
    /// Optional host telemetry sink invoked once per guarded
    /// (<see cref="SlowTickCount"/>-incrementing) deterministic tick with
    /// the elapsed milliseconds — the "warning log" of the soft guard
    /// (EraCore stays a pure logic library with no logger of its own; the
    /// host decides what a warning means). Observation only.
    /// </summary>
    public Action<double>? SlowTickObserver { get; set; }

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
    /// One frame's slice under the LEGACY wall-clock budget (Stopwatch):
    /// starts rebuilds flagged dirty, then advances all pending frontiers in
    /// deterministic build order while the shared wall-clock budget lasts.
    /// Returns true when nothing is pending. A zero budget performs no flood
    /// work but still reports pending state.
    /// <para>
    /// T-067 DEBT 清账 note: load-dependent work per call makes the rebuild
    /// completion tick (and with it the seed-simulation hash) diverge under
    /// CPU contention — simulation must use
    /// <see cref="UpdateDeterministic(int?)"/>. This entry remains for
    /// tooling and pre-existing callers that never feed simulation ticks.
    /// </para>
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

        // W4 门禁 F1 观察位：墙钟入口盖章（仅观察，见 LastAdvanceMode）。
        LastAdvanceMode = FlowFieldAdvanceMode.WallClock;
        StartDirtyRebuilds();

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

    /// <summary>
    /// One frame's slice under the deterministic cell quota (T-067 DEBT 清账
    /// — THE simulation entry): starts rebuilds flagged dirty, then advances
    /// all pending frontiers in deterministic build order while the SHARED
    /// quota lasts (first bucket in build order eats first, exactly like the
    /// legacy wall-clock entry's shared deadline). Returns true when nothing
    /// is pending. A zero quota performs no flood work but still starts
    /// dirty rebuilds and reports pending state — the legal no-progress
    /// boundary.
    /// <para>
    /// Determinism: work per tick is a function of the quota and the grid
    /// snapshot only — never of wall-clock — so rebuild completion ticks and
    /// served-field swaps are identical under any CPU load (the property the
    /// wall-clock entry lacks; T-086 finding). A Stopwatch wraps the call
    /// purely as the soft guard: beyond <see cref="SoftGuardThresholdMs"/>
    /// the counter/sink fire and NOTHING else changes.
    /// </para>
    /// </summary>
    /// <param name="maxCellsPerTick">
    /// Charged work units for this call; null uses
    /// <see cref="DefaultCellsPerTick"/>. Zero is the legal no-progress
    /// boundary.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">quota is negative.</exception>
    public bool UpdateDeterministic(int? maxCellsPerTick = null)
    {
        int quota = maxCellsPerTick ?? DefaultCellsPerTick;
        if (quota < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCellsPerTick), quota, "Cell quota must not be negative; zero performs no flood work this call.");
        }

        // W4 门禁 F1 观察位：确定性入口盖章（仅观察，见 LastAdvanceMode）。
        LastAdvanceMode = FlowFieldAdvanceMode.Deterministic;
        long guardStart = Stopwatch.GetTimestamp();
        StartDirtyRebuilds();
        LastTickCellsProcessed = 0;
        if (_pending.Count == 0)
        {
            ObserveSoftGuard(guardStart);
            return true;
        }

        int remaining = quota;
        foreach (Bucket bucket in _pending.ToArray())
        {
            if (bucket.Pending is null || remaining <= 0)
            {
                continue;
            }

            FlowField pending = bucket.Pending;
            long before = pending.ProcessedCells;
            if (pending.AdvanceDeterministic(remaining))
            {
                bucket.Live = pending;
                bucket.Pending = null;
                bucket.RebuildCount++;
            }

            int consumed = (int)Math.Min(int.MaxValue, pending.ProcessedCells - before);
            LastTickCellsProcessed += consumed;
            remaining -= consumed;
        }

        _pending.RemoveAll(b => b.Pending is null);
        ObserveSoftGuard(guardStart);
        return _pending.Count == 0;
    }

    /// <summary>
    /// Soft-guard observation of one deterministic tick (T-067 DEBT 清账):
    /// elapsed wall time beyond the threshold bumps the counter and the
    /// optional sink. Observation only — the quota above stays the sole
    /// behavior input.
    /// </summary>
    private void ObserveSoftGuard(long startTimestamp)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs > SoftGuardThresholdMs)
        {
            SlowTickCount++;
            SlowTickObserver?.Invoke(elapsedMs);
        }
    }

    /// <summary>Starts pending rebuilds for buckets flagged dirty (shared by both update entries).</summary>
    private void StartDirtyRebuilds()
    {
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
