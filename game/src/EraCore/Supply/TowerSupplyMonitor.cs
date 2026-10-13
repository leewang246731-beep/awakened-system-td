using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Logistics;

namespace EraCore.Supply;

/// <summary>
/// Demand-side registration of one tower's ammo need (secondary type of
/// TowerSupplyMonitor.cs): the fire-cycle numbers the efficiency curve is
/// measured against — derived from the WeaponFire inputs
/// (<c>reload_ticks</c> + <c>ammo_per_shot</c> ⇒ 发/s; <c>damage_per_shot</c>
/// ⇒ the full-rate DPS the curve scales) — plus the two observability seams:
/// a polled cumulative-delivered-shots counter (弹药计数差分, the magazine
/// side exposes one) and the optional supply route whose blocked cells
/// surface in bottleneck suggestions (最短路径上的断点格).
/// </summary>
/// <param name="TowerId">Stable id of the tower building (K-1).</param>
/// <param name="Position">Grid cell of the tower (bottleneck payload, F-046).</param>
/// <param name="ReloadTicks">Fire-cycle length in 30 Hz ticks, ≥ 1 (WeaponFire reload_ticks).</param>
/// <param name="AmmoPerShot">Shots consumed per firing, ≥ 0 (0 = no ammo need ⇒ 需求≤0 恒 1).</param>
/// <param name="DamagePerShot">Undecayed single-shot damage d, finite ≥ 0 (DPS anchor).</param>
/// <param name="CumulativeDeliveredShots">
/// Monotonic counter of shots delivered into the tower magazine so far —
/// polled once per <see cref="TowerSupplyMonitor.Tick"/> and differenced
/// (轮询弹药计数差分). The tower magazine adapter supplies this.
/// </param>
/// <param name="SupplyRoute">
/// Cells of the tower's shortest supply path (home→tower polyline), when
/// known — used to locate the break cell for suggestions. May be null.
/// </param>
public sealed record TowerSupplyRegistration(
    EntityId TowerId,
    CellCoord Position,
    int ReloadTicks,
    int AmmoPerShot,
    double DamagePerShot,
    Func<long> CumulativeDeliveredShots,
    IReadOnlyList<CellCoord>? SupplyRoute = null);

/// <summary>
/// Per-tower ammo supply monitor (断供降速逻辑层, C-06×C-07×C-09 seam, T-048,
/// F-046): registers each tower's demand rate from its WeaponFire numbers
/// (发/s = <c>ammo_per_shot × 30 / reload_ticks</c>; DPS anchor
/// <c>damage_per_shot × 发/s</c>), polls the magazine delivery counter once
/// per tick (弹药计数差分), and derives the §5.3 efficiency curve
/// <c>efficiency = clamp(交付率 / 需求率, 0, 1)</c> as a sliding-window mean:
/// both rates are measured over the last <see cref="DefaultWindowTicks"/>
/// ticks (ratio of window sums — the sliding mean of the delivery/demand
/// ratio), composed through <see cref="EfficiencyLaw.Min"/> so the [0,1]
/// clamp and non-finite guards are the SAME law the converter chain uses.
/// A tower with <c>ammo_per_shot ≤ 0</c> has no demand and reads 1
/// (需求≤0 恒 1, v3.2 §5 L75). A freshly registered tower reads 1 for
/// <see cref="DefaultWarmupTicks"/> ticks (新建链路宽限: a porter round trip
/// must fit inside the window before the curve can judge the chain —
/// otherwise every new tower would read as a bottleneck before its first
/// delivery could possibly land).
/// <para>
/// The monitor also carries the F-046 observability inputs the
/// <see cref="BottleneckDetector"/> turns into events: per-tower route
/// break cells (断链格, reported by the link guard when a wall cuts the
/// path) and relay stations (中转站) with stock levels for the
/// lowest-stock suggestion. Deterministic: registration order drives every
/// iteration, pure per-tick arithmetic, no RNG, no wall clock (CS-1.4) —
/// same seed + tick sequence ⇒ equal <see cref="ComputeStateHash"/>.
/// </para>
/// </summary>
public sealed class TowerSupplyMonitor
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Fixed simulation tick rate (K-1: 30 Hz domain) — re-exported from the
    /// Logistics anchor so the demand derivation stays unit-aligned.
    /// </summary>
    public const int TicksPerSecond = Carrier.TicksPerSecond;

    /// <summary>
    /// Default efficiency smoothing window in ticks (最近 N tick 滑动均值):
    /// 90 ticks = 3 s of simulation. [NEW-T-048] v3.2 pins no window; 3 s
    /// covers the stagger of a small porter pool (arrivals every ~2 s) so a
    /// healthy chain reads a steady 1.0 while a cut drains the window within
    /// one window length.
    /// </summary>
    public const int DefaultWindowTicks = 90;

    /// <summary>
    /// Default fresh-chain warm-up in ticks before the efficiency curve
    /// binds: 600 ticks = 20 s = three reference porter round trips (200
    /// ticks each). [NEW-T-048] prevents a just-registered tower from
    /// reading 0 (and firing a bottleneck event) before its first delivery
    /// could physically arrive.
    /// </summary>
    public const int DefaultWarmupTicks = 600;

    private readonly int _windowTicks;
    private readonly int _warmupTicks;
    private readonly List<Tower> _towers = new();
    private readonly Dictionary<EntityId, Tower> _byId = new();
    private readonly List<Relay> _relays = new();

    /// <summary>
    /// Creates an empty monitor.
    /// </summary>
    /// <param name="windowTicks">Sliding-window length in ticks, [1, 10000].</param>
    /// <param name="warmupTicks">Fresh-chain grace in ticks, ≥ 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// windowTicks outside [1, 10000] or warmupTicks negative.
    /// </exception>
    public TowerSupplyMonitor(int windowTicks = DefaultWindowTicks, int warmupTicks = DefaultWarmupTicks)
    {
        if (windowTicks < 1 || windowTicks > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowTicks), windowTicks, "Efficiency window must be inside [1, 10000] ticks.");
        }

        if (warmupTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupTicks), warmupTicks, "Warm-up must be non-negative.");
        }

        _windowTicks = windowTicks;
        _warmupTicks = warmupTicks;
    }

    /// <summary>Sliding-window length in ticks (最近 N tick).</summary>
    public int WindowTicks => _windowTicks;

    /// <summary>Fresh-chain grace length in ticks.</summary>
    public int WarmupTicks => _warmupTicks;

    private sealed class Tower
    {
        public required TowerSupplyRegistration Registration { get; init; }

        // Demand per tick in shots = ammo_per_shot / reload_ticks (0 ⇒ no ammo need ⇒ efficiency 1).
        public double DemandShotsPerTick { get; init; }

        public long LastDeliveredShots { get; set; }

        public long[] WindowShots { get; set; } = Array.Empty<long>();

        public int WindowCount { get; set; }

        public int WindowHead { get; set; }

        public long WindowSum { get; set; }

        public long TicksObserved { get; set; }

        public double Efficiency { get; set; } = 1.0;

        public CellCoord? BreakCell { get; set; }
    }

    private sealed record Relay(EntityId Id, CellCoord Position, Func<long> StockItems);

    /// <summary>
    /// Registers one tower's demand and delivery seams. Duplicate tower ids
    /// are rejected — one monitor tracks one fire loop per tower.
    /// </summary>
    /// <exception cref="ArgumentNullException">registration or a member is null.</exception>
    /// <exception cref="ArgumentException">
    /// reload_ticks &lt; 1, ammo_per_shot &lt; 0, damage_per_shot non-finite or
    /// negative, a non-empty supply route with fewer than 2 cells, or a
    /// duplicate tower id.
    /// </exception>
    public void RegisterTower(TowerSupplyRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.TowerId);
        ArgumentNullException.ThrowIfNull(registration.Position);
        ArgumentNullException.ThrowIfNull(registration.CumulativeDeliveredShots);
        if (registration.ReloadTicks < 1)
        {
            throw new ArgumentException(
                $"reload_ticks must be at least 1 (got {registration.ReloadTicks}).", nameof(registration));
        }

        if (registration.AmmoPerShot < 0)
        {
            throw new ArgumentException(
                $"ammo_per_shot must be non-negative (got {registration.AmmoPerShot}).", nameof(registration));
        }

        if (!double.IsFinite(registration.DamagePerShot) || registration.DamagePerShot < 0)
        {
            throw new ArgumentException(
                $"damage_per_shot must be finite and non-negative (got {registration.DamagePerShot}).",
                nameof(registration));
        }

        if (registration.SupplyRoute is { Count: < 2 })
        {
            throw new ArgumentException("A supply route needs at least two cells.", nameof(registration));
        }

        if (_byId.ContainsKey(registration.TowerId))
        {
            throw new ArgumentException(
                $"Tower {registration.TowerId.Value} is already registered — one fire loop per monitor.",
                nameof(registration));
        }

        var tower = new Tower
        {
            Registration = registration,
            DemandShotsPerTick = registration.AmmoPerShot / (double)registration.ReloadTicks,
            WindowShots = new long[_windowTicks],
            LastDeliveredShots = registration.CumulativeDeliveredShots(),
        };
        _towers.Add(tower);
        _byId.Add(registration.TowerId, tower);
    }

    /// <summary>
    /// Registers a relay station (中转站) for the lowest-stock bottleneck
    /// suggestion: <paramref name="stockItems"/> is polled only when a
    /// suggestion is built, in registration order (first minimal wins —
    /// deterministic tie-break).
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentException">duplicate relay id.</exception>
    public void RegisterRelay(EntityId relayId, CellCoord position, Func<long> stockItems)
    {
        ArgumentNullException.ThrowIfNull(relayId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(stockItems);
        if (_relays.Any(r => r.Id.Equals(relayId)))
        {
            throw new ArgumentException($"Relay {relayId.Value} is already registered.", nameof(relayId));
        }

        _relays.Add(new Relay(relayId, position, stockItems));
    }

    /// <summary>
    /// Marks the tower's supply route as broken at <paramref name="breakCell"/>
    /// (the wall cell on the path — reported by the link guard). Feeds the
    /// bottleneck suggestion (最短路径上的断点格).
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public void NotifyRouteBlocked(EntityId towerId, CellCoord breakCell)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        ArgumentNullException.ThrowIfNull(breakCell);
        Tower tower = RequireTower(towerId);
        tower.BreakCell = breakCell;
    }

    /// <summary>Clears the tower's route-break mark (path restored).</summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public void NotifyRouteRestored(EntityId towerId)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        Tower tower = RequireTower(towerId);
        tower.BreakCell = null;
    }

    /// <summary>Registered tower ids in registration order.</summary>
    public IReadOnlyList<EntityId> Towers => _towers.Select(static t => t.Registration.TowerId).ToArray();

    /// <summary>
    /// Last computed efficiency of one tower in [0,1] (the sliding-window
    /// curve; 1 during warm-up or when the tower has no ammo demand).
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public double EfficiencyOf(EntityId towerId) => RequireTower(towerId).Efficiency;

    /// <summary>
    /// Tower position (bottleneck payload anchor) — the registered cell.
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public CellCoord PositionOf(EntityId towerId) => RequireTower(towerId).Registration.Position;

    /// <summary>
    /// Demand rate in shots/s (发/s) derived at registration:
    /// <c>ammo_per_shot × 30 / reload_ticks</c>; 0 for no-ammo towers.
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public double DemandShotsPerSecondOf(EntityId towerId) =>
        RequireTower(towerId).DemandShotsPerTick * TicksPerSecond;

    /// <summary>
    /// Full-rate DPS anchor: <c>damage_per_shot × 发/s</c> — the value the
    /// effective DPS scales by efficiency (塔射速乘 efficiency).
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public double FullDpsOf(EntityId towerId)
    {
        Tower tower = RequireTower(towerId);
        return tower.Registration.DamagePerShot * tower.DemandShotsPerTick * TicksPerSecond;
    }

    /// <summary>
    /// Window-measured delivery rate in shots/s (交付率 — the numerator of
    /// the curve; window mean over the observed ticks, capped at the window
    /// length).
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public double DeliveryShotsPerSecondOf(EntityId towerId)
    {
        Tower tower = RequireTower(towerId);
        long ticks = Math.Min(tower.TicksObserved, _windowTicks);
        return ticks == 0 ? 0.0 : tower.WindowSum * (double)TicksPerSecond / ticks;
    }

    /// <summary>
    /// Current route-break cell of the tower, when the link guard reported
    /// one and no restore has followed (null = none).
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId is null.</exception>
    /// <exception cref="ArgumentException">unknown tower id.</exception>
    public CellCoord? BreakCellOf(EntityId towerId) => RequireTower(towerId).BreakCell;

    /// <summary>
    /// Advances exactly one simulation tick: polls every registered tower's
    /// delivery counter (must be monotonic — a regressing counter is a
    /// wiring bug), differences it into that tower's sliding window and
    /// recomputes the efficiency curve. During warm-up the curve holds 1.
    /// Call once per 30 Hz tick, in the same phase of the orchestrator loop
    /// (after the logistics tick, before the weapon tick).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// a delivery counter regressed between polls.
    /// </exception>
    public void Tick()
    {
        foreach (Tower tower in _towers)
        {
            tower.TicksObserved++;
            long delivered = tower.Registration.CumulativeDeliveredShots();
            if (delivered < tower.LastDeliveredShots)
            {
                throw new InvalidOperationException(
                    $"Delivery counter of tower {tower.Registration.TowerId.Value} regressed "
                    + $"({tower.LastDeliveredShots} → {delivered}); magazine counters are monotonic.");
            }

            long delta = delivered - tower.LastDeliveredShots;
            tower.LastDeliveredShots = delivered;

            // Sliding window of per-tick delivered shots (ring buffer).
            if (tower.WindowCount == _windowTicks)
            {
                tower.WindowSum -= tower.WindowShots[tower.WindowHead];
            }
            else
            {
                tower.WindowCount++;
            }

            tower.WindowShots[tower.WindowHead] = delta;
            tower.WindowSum += delta;
            tower.WindowHead = (tower.WindowHead + 1) % _windowTicks;

            tower.Efficiency = ComputeEfficiency(tower);
        }
    }

    /// <summary>
    /// FNV-1a hash over the full observable state (per tower, registration
    /// order: delivered counter, window contents, efficiency bits, break
    /// cell, relay stocks are NOT included — they are polled lazily for
    /// suggestions only). Same seed + tick sequence ⇒ equal hashes.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        foreach (Tower tower in _towers)
        {
            hash = Mix(hash, (ulong)tower.LastDeliveredShots);
            hash = Mix(hash, (ulong)tower.WindowCount);
            hash = Mix(hash, (ulong)tower.WindowSum);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(tower.Efficiency));
            hash = Mix(hash, tower.BreakCell is null ? 0UL : 1UL);
            if (tower.BreakCell is not null)
            {
                hash = Mix(hash, (ulong)tower.BreakCell.X);
                hash = Mix(hash, (ulong)tower.BreakCell.Y);
            }

            for (int i = 0; i < tower.WindowCount; i++)
            {
                hash = Mix(hash, (ulong)tower.WindowShots[i]);
            }
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    /// <summary>
    /// Picks the bottleneck suggestion relay: the registered relay with the
    /// lowest stock (ties → registration order). Null when no relay is
    /// registered.
    /// </summary>
    public (EntityId Id, CellCoord Position, long StockItems)? LowestStockRelay()
    {
        Relay? best = null;
        long bestStock = long.MaxValue;
        foreach (Relay relay in _relays)
        {
            long stock = relay.StockItems();
            if (stock < bestStock)
            {
                best = relay;
                bestStock = stock;
            }
        }

        return best is null ? null : (best.Id, best.Position, bestStock);
    }

    private double ComputeEfficiency(Tower tower)
    {
        if (tower.DemandShotsPerTick <= 0)
        {
            return 1.0; // 需求≤0 恒 1 (v3.2 §5 L75)
        }

        if (tower.TicksObserved <= _warmupTicks)
        {
            return 1.0; // 新建链路宽限 — curve binds after warm-up
        }

        long ticks = Math.Min(tower.TicksObserved, _windowTicks);
        double demandShots = tower.DemandShotsPerTick * ticks;
        if (demandShots <= 0)
        {
            return 1.0;
        }

        // 效率曲线: clamp(交付率/需求率, 0, 1) over the sliding window —
        // composed through the SAME §5.3 law the converter chain uses.
        return EfficiencyLaw.Min(tower.WindowSum / demandShots, 1.0);
    }

    private Tower RequireTower(EntityId towerId)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        return _byId.TryGetValue(towerId, out Tower? tower)
            ? tower
            : throw new ArgumentException($"Tower {towerId.Value} is not registered.", nameof(towerId));
    }
}
