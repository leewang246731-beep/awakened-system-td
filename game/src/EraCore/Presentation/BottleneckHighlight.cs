using EraCore.Contracts.Foundation;
using EraCore.Supply;

namespace EraCore.Presentation;

/// <summary>
/// One active bottleneck highlight view (T-093, F-120 三层信息③ 物流断流
/// 可视化 = T-063 断点 UI 高亮验收点): the red-ring cell (瓶颈位), the
/// greyed supply line (断流线 source→tower) and the causal hint line
/// (因果提示文案). Pure projection of one
/// <see cref="SupplyBottleneckEvent"/> — painting lives in the Godot
/// layer.
/// </summary>
/// <param name="TowerId">The starved tower's id.</param>
/// <param name="TowerCell">The starved tower's cell.</param>
/// <param name="RingCell">Red-ring anchor: the suggested break cell / relay, else the tower itself.</param>
/// <param name="LineFromCell">Grey line source (relay / fallback supplier cell).</param>
/// <param name="LineToCell">Grey line target (the tower).</param>
/// <param name="ReasonLine">因果提示 one-liner (效率/需求/实到 + 建议看哪里).</param>
/// <param name="ExpiresAtTick">Tick this highlight decays at (TTL).</param>
public sealed record BottleneckHighlightView(
    EntityId TowerId,
    CellCoord TowerCell,
    CellCoord RingCell,
    CellCoord LineFromCell,
    CellCoord LineToCell,
    string ReasonLine,
    long ExpiresAtTick);

/// <summary>
/// Active-highlight registry over
/// <see cref="BottleneckDetector"/> events (T-093, §10.8): one event per
/// bottleneck EPISODE registers a highlight; the view persists for a TTL
/// then decays (the detector emits no recovery event — the next episode
/// re-arms at its own emission). The grey line's source resolves from the
/// event's suggestion (relay position) or the caller's fallback supplier
/// (e.g. the warehouse cell). Pure logic, no Godot; deterministic
/// ordering by registration sequence.
/// </summary>
public sealed class BottleneckHighlightBoard
{
    /// <summary>
    /// Default highlight TTL in 30 Hz ticks: 600 t = 20 s — longer than a
    /// porter round-trip window, shorter than one wave (presentation
    /// constant; the episode latch already dedupes re-arms).
    /// </summary>
    public const long DefaultTtlTicks = 600;

    private readonly List<BottleneckHighlightView> _active = new();
    private readonly long _ttlTicks;

    /// <summary>
    /// Creates the board with a TTL. Pass a custom TTL only in tests —
    /// production uses <see cref="DefaultTtlTicks"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">ttlTicks &lt; 1.</exception>
    public BottleneckHighlightBoard(long ttlTicks = DefaultTtlTicks)
    {
        if (ttlTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ttlTicks), ttlTicks, "must be >= 1.");
        }

        _ttlTicks = ttlTicks;
    }

    /// <summary>Armed TTL (smoke probe).</summary>
    public long TtlTicks => _ttlTicks;

    /// <summary>Currently active highlights in registration order (read-only copy).</summary>
    public IReadOnlyList<BottleneckHighlightView> Active => _active.ToArray();

    /// <summary>
    /// Registers one event: replaces any still-active highlight of the
    /// same tower (one ring per tower), resolves the red-ring cell and the
    /// grey line, and composes the causal hint line. Returns the stored
    /// view.
    /// </summary>
    /// <param name="event">The detector event (F-046 payload).</param>
    /// <param name="fallbackSupplierCell">Grey-line source when the event carries no relay suggestion (e.g. the warehouse cell).</param>
    /// <exception cref="ArgumentNullException">event is null.</exception>
    public BottleneckHighlightView On(SupplyBottleneckEvent @event, CellCoord fallbackSupplierCell)
    {
        ArgumentNullException.ThrowIfNull(@event);

        CellCoord ring = @event.SuggestedCell ?? @event.TowerPosition;
        CellCoord lineFrom = @event.SuggestedRelayId is not null && @event.SuggestedCell is { } relayCell
            ? relayCell
            : fallbackSupplierCell;
        string where = @event.SuggestionKind switch
        {
            SupplyBottleneckSuggestionKind.RouteBreakCell => "断点格",
            SupplyBottleneckSuggestionKind.LowestStockRelay => "最低库存中转",
            _ => "塔位",
        };
        string reason =
            $"塔效率 {(@event.Efficiency * 100d):0}%（需求 {@event.DemandShotsPerSecond:0.#} 发/s，实到 " +
            $"{@event.DeliveryShotsPerSecond:0.#} 发/s）——检查供给线：红圈={where}，灰线=断流线路";

        var view = new BottleneckHighlightView(
            @event.TowerId, @event.TowerPosition, ring, lineFrom, @event.TowerPosition,
            reason, @event.Tick + _ttlTicks);
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].TowerId.Equals(@event.TowerId))
            {
                _active.RemoveAt(i); // 同塔新事件覆盖旧圈（episode 语义）
            }
        }

        _active.Add(view);
        return view;
    }

    /// <summary>
    /// Advances to <paramref name="tick"/>: prunes expired highlights.
    /// Call once per rendered tick (idempotent within a tick).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">tick is negative.</exception>
    public void Tick(long tick)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "tick must be >= 0.");
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (tick >= _active[i].ExpiresAtTick)
            {
                _active.RemoveAt(i);
            }
        }
    }

    /// <summary>True when the given tower has an active highlight right now.</summary>
    public bool IsHighlighted(EntityId towerId) =>
        _active.Any(view => view.TowerId.Equals(towerId));
}
