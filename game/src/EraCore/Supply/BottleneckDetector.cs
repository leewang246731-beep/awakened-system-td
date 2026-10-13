using EraCore.Contracts.Foundation;

namespace EraCore.Supply;

/// <summary>
/// Kind of the bottleneck suggestion attached to a
/// <see cref="SupplyBottleneckEvent"/> (F-046 断点可观测: where to look).
/// </summary>
public enum SupplyBottleneckSuggestionKind
{
    /// <summary>No locating input available (no break cell reported, no relay registered).</summary>
    None = 0,

    /// <summary>The wall cell on the tower's shortest supply path (最短路径上的断点格).</summary>
    RouteBreakCell = 1,

    /// <summary>The registered relay station with the lowest stock (最低库存中转站).</summary>
    LowestStockRelay = 2,
}

/// <summary>
/// Structured bottleneck event (F-046 物流断流可视化 — 逻辑层事件, T-048):
/// emitted by <see cref="BottleneckDetector"/> after a tower's efficiency
/// stayed under the threshold for the configured consecutive ticks. Pure
/// simulation-side record — no UI shape (the §10.8 highlight and the "叮"
/// relay consume this through the bus/callback seam later, K-8 carries it
/// as JSON). Implements <see cref="IGameEvent"/> so the future event bus
/// can carry it without re-shaping; <c>(tick, seq)</c> ordering is assigned
/// by the detector in raise order (CS-1.5 synchronous discipline).
/// </summary>
/// <param name="Tick">Simulation tick of the emission (30 Hz domain).</param>
/// <param name="Seq">Per-detector emission counter, strictly increasing from 1.</param>
/// <param name="TowerId">Stable id of the starved tower.</param>
/// <param name="TowerPosition">Grid cell of the tower (UI 高亮 anchor).</param>
/// <param name="Efficiency">Efficiency the curve read at emission (&lt; threshold).</param>
/// <param name="Threshold">Armed threshold in [0,1].</param>
/// <param name="DemandShotsPerSecond">Registered demand rate 发/s.</param>
/// <param name="DeliveryShotsPerSecond">Window-measured delivery rate 发/s.</param>
/// <param name="SuggestionKind">Which suggestion flavor the payload carries.</param>
/// <param name="SuggestedCell">
/// Break cell (RouteBreakCell) or relay position (LowestStockRelay); null for None.
/// </param>
/// <param name="SuggestedRelayId">Relay id when the suggestion is a relay; else null.</param>
/// <param name="SuggestedRelayStock">Relay stock in items when the suggestion is a relay; else null.</param>
public sealed record SupplyBottleneckEvent(
    long Tick,
    long Seq,
    EntityId TowerId,
    CellCoord TowerPosition,
    double Efficiency,
    double Threshold,
    double DemandShotsPerSecond,
    double DeliveryShotsPerSecond,
    SupplyBottleneckSuggestionKind SuggestionKind,
    CellCoord? SuggestedCell,
    EntityId? SuggestedRelayId,
    long? SuggestedRelayStock) : IGameEvent;

/// <summary>
/// Bottleneck detector (断点事件, T-048, F-046): watches the
/// <see cref="TowerSupplyMonitor"/> efficiency curve of every registered
/// tower and raises one structured <see cref="SupplyBottleneckEvent"/> per
/// bottleneck EPISODE — an episode opens when the efficiency stayed below
/// the threshold for <see cref="DefaultConsecutiveTicks"/> consecutive
/// ticks and closes only when the curve recovers to ≥ threshold (one event
/// per episode, no per-tick spam). The payload carries the observability
/// contract of the task card: tower id, position, current efficiency, and
/// a suggestion — the route break cell when the link guard reported one
/// (最短路径上的断点格), otherwise the lowest-stock registered relay
/// (最低库存中转站).
/// <para>
/// Observability seam (CS-1.5, 不搞 UI): a dedicated synchronous callback
/// <see cref="BottleneckDetected"/> invoked inline inside the detector's
/// tick, plus the <see cref="Events"/> log — the concrete IEventBus twin
/// lands with C-12/K-8 and replays this exact record shape. Deterministic:
/// towers are visited in registration order, thresholds are pure
/// comparisons, no RNG, time only via the injected <see cref="IClock"/>
/// (CS-1.4) — same seed + tick sequence ⇒ equal
/// <see cref="ComputeStateHash"/>.
/// </para>
/// </summary>
public sealed class BottleneckDetector
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Default efficiency threshold: an efficiency below 0.7 for
    /// <see cref="DefaultConsecutiveTicks"/> ticks is a bottleneck.
    /// [NEW-T-048] task-card example value (如 0.7); v3.2 pins no number.
    /// </summary>
    public const double DefaultThreshold = 0.7;

    /// <summary>
    /// Default consecutive-tick streak that arms the event: 60 ticks = 2 s
    /// at 30 Hz. [NEW-T-048] long enough to ride out single burst gaps in
    /// the delivery window, short enough to alarm well inside one wave.
    /// </summary>
    public const int DefaultConsecutiveTicks = 60; // ci-allow: R7 T-048 [NEW-T-048] 瓶颈判定连击窗 60t=2s（任务卡示例口径，v3.2 未钉数值）

    private readonly IClock _clock;
    private readonly TowerSupplyMonitor _monitor;
    private readonly double _threshold;
    private readonly int _consecutiveTicks;
    private readonly Dictionary<EntityId, Streak> _streaks = new();
    private readonly List<SupplyBottleneckEvent> _events = new();
    private long _lastSeq;

    /// <summary>
    /// Creates the detector over one monitor. Events are timestamped from
    /// <paramref name="clock"/> (K-1, 30 Hz domain).
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// threshold outside (0, 1] or consecutiveTicks &lt; 1.
    /// </exception>
    public BottleneckDetector(IClock clock, TowerSupplyMonitor monitor, double threshold = DefaultThreshold,
        int consecutiveTicks = DefaultConsecutiveTicks)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monitor);
        if (threshold <= 0 || threshold > 1 || !double.IsFinite(threshold))
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold), threshold, "Threshold must be finite and inside (0, 1].");
        }

        if (consecutiveTicks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveTicks), consecutiveTicks, "Consecutive ticks must be at least 1.");
        }

        _clock = clock;
        _monitor = monitor;
        _threshold = threshold;
        _consecutiveTicks = consecutiveTicks;
    }

    /// <summary>Armed efficiency threshold in (0, 1].</summary>
    public double Threshold => _threshold;

    /// <summary>Consecutive-tick streak that arms an event, ≥ 1.</summary>
    public int ConsecutiveTicks => _consecutiveTicks;

    /// <summary>
    /// Synchronous inline callback on every emitted event (dedicated
    /// observability seam — the bus-facing twin lands with C-12/K-8 and
    /// will relay this exact record; UI adapters subscribe there, never
    /// here). Invoked inside <see cref="Tick"/>, in emission order.
    /// </summary>
    public event Action<SupplyBottleneckEvent>? BottleneckDetected;

    /// <summary>Emitted events in (tick, seq) raise order.</summary>
    public IReadOnlyList<SupplyBottleneckEvent> Events => _events;

    /// <summary>
    /// Advances exactly one simulation tick: reads every registered tower's
    /// efficiency (registration order), grows or resets its under-threshold
    /// streak, and emits one event at the moment the streak reaches
    /// <see cref="ConsecutiveTicks"/>. The episode latch keeps the detector
    /// quiet until the efficiency recovers to ≥ threshold.
    /// </summary>
    public void Tick()
    {
        foreach (EntityId towerId in _monitor.Towers)
        {
            double efficiency = _monitor.EfficiencyOf(towerId);
            Streak streak = GetStreak(towerId);
            if (efficiency < _threshold)
            {
                streak.UnderTicks++;
                if (!streak.EpisodeOpen && streak.UnderTicks >= _consecutiveTicks)
                {
                    streak.EpisodeOpen = true;
                    Emit(towerId, efficiency);
                }
            }
            else
            {
                streak.UnderTicks = 0;
                streak.EpisodeOpen = false;
            }
        }
    }

    /// <summary>
    /// FNV-1a hash over the observable state (per-tower streak/latch in
    /// registration order + event log tail). Same seed + tick sequence ⇒
    /// equal hashes.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        foreach (EntityId towerId in _monitor.Towers)
        {
            Streak streak = GetStreak(towerId);
            hash = Mix(hash, (ulong)streak.UnderTicks);
            hash = Mix(hash, streak.EpisodeOpen ? 1UL : 0UL);
            hash = Mix(hash, (ulong)towerId.Value);
        }

        hash = Mix(hash, (ulong)_events.Count);
        hash = Mix(hash, (ulong)_lastSeq);
        if (_events.Count > 0)
        {
            SupplyBottleneckEvent last = _events[^1];
            hash = Mix(hash, (ulong)last.Tick);
            hash = Mix(hash, (ulong)last.Seq);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(last.Efficiency));
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    private void Emit(EntityId towerId, double efficiency)
    {
        _lastSeq++;
        CellCoord? breakCell = _monitor.BreakCellOf(towerId);
        SupplyBottleneckSuggestionKind kind;
        CellCoord? cell;
        EntityId? relayId;
        long? relayStock;
        if (breakCell is not null)
        {
            kind = SupplyBottleneckSuggestionKind.RouteBreakCell;
            cell = breakCell;
            relayId = null;
            relayStock = null;
        }
        else if (_monitor.LowestStockRelay() is { } relay)
        {
            kind = SupplyBottleneckSuggestionKind.LowestStockRelay;
            cell = relay.Position;
            relayId = relay.Id;
            relayStock = relay.StockItems;
        }
        else
        {
            kind = SupplyBottleneckSuggestionKind.None;
            cell = null;
            relayId = null;
            relayStock = null;
        }

        var evt = new SupplyBottleneckEvent(
            _clock.Tick,
            _lastSeq,
            towerId,
            _monitor.PositionOf(towerId),
            efficiency,
            _threshold,
            _monitor.DemandShotsPerSecondOf(towerId),
            _monitor.DeliveryShotsPerSecondOf(towerId),
            kind,
            cell,
            relayId,
            relayStock);
        _events.Add(evt);
        BottleneckDetected?.Invoke(evt);
    }

    private Streak GetStreak(EntityId towerId)
    {
        if (!_streaks.TryGetValue(towerId, out Streak? streak))
        {
            streak = new Streak();
            _streaks.Add(towerId, streak);
        }

        return streak;
    }

    private sealed class Streak
    {
        public int UnderTicks { get; set; }

        public bool EpisodeOpen { get; set; }
    }
}
