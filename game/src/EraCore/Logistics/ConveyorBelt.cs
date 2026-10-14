using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>
/// Continuous belt transport of ladder tier 4 传送带 (C-07 全量, T-064,
/// F-041/F-042; v3.2 §5 "格到格自动流 15 件/s"). The belt is a fixed chain of
/// cells; every <see cref="TicksPerCell"/> ticks the whole chain advances one
/// cell: the head pulls up to <see cref="TransportTier.CapacityItems"/> from
/// the supplier, every intermediate slot shifts toward the tail, and the tail
/// hands its items to the receiver — but ONLY when the receiver accepts the
/// full tail load (待卸 discipline: a refused or partial accept stalls the
/// chain at the tail with the remainder, backpressure included, until the
/// receiver takes it; cargo never leaves except on accept).
/// <para>
/// Thin-version scope (任务卡 "物品实体化薄版可先计数流"): items are integer
/// per-slot counts, not world entities — the flow accounting (fill level,
/// throughput, backpressure) is complete and deterministic; per-item entity
/// rendering is presentation-layer content.
/// </para>
/// <para>
/// Rate math — surface speed 16 px/tick = half a cell per tick ⇒
/// <see cref="TicksPerCell"/> = 2 ticks per cell with 1 item per cell ⇒
/// 0.5 items/tick = exactly 15 件/s steady state, independent of belt length
/// (length only sets the fill latency = cells × <see cref="TicksPerCell"/>).
/// Determinism (CS-1.4): integer slot arithmetic, no clock, no RNG; one
/// <see cref="Tick"/> = one 1/30 s step.
/// </para>
/// </summary>
public sealed class ConveyorBelt
{
    /// <summary>
    /// Belt advance period: one cell per 2 ticks = 16 px/tick surface speed
    /// (<see cref="TransportTiers.Belt"/> speed ÷ cell size 32).
    /// </summary>
    public const int TicksPerCell = 2;

    private readonly ResourceKey _resource;
    private readonly ICargoSupplier _source;
    private readonly ICargoReceiver _destination;
    private readonly TransportTier _tier;
    private readonly CellCoord[] _cells;
    private readonly int[] _slots;

    private int _advancePhase;
    private long _deliveredItems;
    private long _ticksAlive;
    private long _stalledTicks;

    /// <summary>
    /// Creates a belt over ordered, 8-neighbour-adjacent cells pulling
    /// <paramref name="resource"/> from <paramref name="source"/> at the head
    /// and handing it to <paramref name="destination"/> at the tail.
    /// <paramref name="tier"/> defaults to <see cref="TransportTiers.Belt"/>
    /// and must be a <see cref="TransportForm.BeltFlow"/> tier.
    /// </summary>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// fewer than two cells, a non-adjacent step, or a non-belt tier.
    /// </exception>
    public ConveyorBelt(
        IReadOnlyList<CellCoord> cells,
        ResourceKey resource,
        ICargoSupplier source,
        ICargoReceiver destination,
        TransportTier? tier = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        tier ??= TransportTiers.Belt;
        ArgumentNullException.ThrowIfNull(tier);
        if (cells.Count < 2)
        {
            throw new ArgumentException("A conveyor belt needs at least two cells.", nameof(cells));
        }

        ValidateAdjacency(cells);
        if (tier.Form != TransportForm.BeltFlow)
        {
            throw new ArgumentException($"Tier '{tier.Id}' is not a belt tier.", nameof(tier));
        }

        _cells = cells.ToArray();
        _resource = resource;
        _source = source;
        _destination = destination;
        _tier = tier;
        _slots = new int[_cells.Length];
    }

    /// <summary>The belt cells, head (source end) → tail (destination end).</summary>
    public IReadOnlyList<CellCoord> Cells => _cells;

    /// <summary>The belt tier (form <see cref="TransportForm.BeltFlow"/>).</summary>
    public TransportTier Tier => _tier;

    /// <summary>Items cumulatively accepted by the destination.</summary>
    public long DeliveredItems => _deliveredItems;

    /// <summary>Ticks lived so far (one <see cref="Tick"/> = +1).</summary>
    public long TicksAlive => _ticksAlive;

    /// <summary>
    /// Advance ticks spent stalled at a refusing tail (backpressure
    /// observability: every refused/partial accept adds one).
    /// </summary>
    public long StalledTicks => _stalledTicks;

    /// <summary>Items currently on the belt (sum over all slots).</summary>
    public int ItemsOnBelt
    {
        get
        {
            int sum = 0;
            foreach (int slot in _slots)
            {
                sum += slot;
            }

            return sum;
        }
    }

    /// <summary>Snapshot of the per-slot item counts, head → tail.</summary>
    public IReadOnlyList<int> SlotSnapshot => (int[])_slots.Clone();

    /// <summary>
    /// Formula throughput of this belt in steady state: items per second =
    /// items per cell ÷ ticks per cell × 30 — 15 件/s for the standard tier,
    /// independent of length (the 160 px reference-route acceptance compares
    /// this against a measured window minus the fill latency).
    /// </summary>
    public double ThroughputItemsPerSecond =>
        _tier.CapacityItems * (double)Carrier.TicksPerSecond / TicksPerCell;

    /// <summary>
    /// Advances exactly one simulation tick. Only every
    /// <see cref="TicksPerCell"/>-th tick does the chain try to move: accept
    /// the tail load, shift every slot one cell toward the tail and refill
    /// the head from the supplier. A refused or partial accept keeps the
    /// remainder at the tail and shifts nothing (stall, counted in
    /// <see cref="StalledTicks"/>); a starved head simply leaves the head
    /// slot empty (no stall — the belt runs on).
    /// </summary>
    public void Tick()
    {
        _ticksAlive++;
        _advancePhase++;
        if (_advancePhase < TicksPerCell)
        {
            return;
        }

        _advancePhase = 0;

        int tailLoad = _slots[^1];
        if (tailLoad > 0)
        {
            int accepted = Math.Min(_destination.TryAccept(_resource, tailLoad), tailLoad);
            _deliveredItems += accepted;
            if (accepted < tailLoad)
            {
                // Refused or partial: the remainder keeps its place at the
                // tail and the chain holds — backpressure, cargo only ever
                // leaves on accept (待卸 discipline).
                _slots[^1] = tailLoad - accepted;
                _stalledTicks++;
                return;
            }
        }

        // Chain advance: slot i takes the load of slot i-1, the head pulls
        // fresh supply (empty when the source is starved — no stall).
        for (int i = _slots.Length - 1; i > 0; i--)
        {
            _slots[i] = _slots[i - 1];
        }

        _slots[0] = Math.Min(_source.TryTake(_resource, _tier.CapacityItems), _tier.CapacityItems);
    }

    private static void ValidateAdjacency(IReadOnlyList<CellCoord> cells)
    {
        for (int i = 1; i < cells.Count; i++)
        {
            int dx = Math.Abs(cells[i].X - cells[i - 1].X);
            int dy = Math.Abs(cells[i].Y - cells[i - 1].Y);
            if (dx > 1 || dy > 1 || (dx == 0 && dy == 0))
            {
                throw new ArgumentException(
                    $"Belt step {i - 1}→{i} ({cells[i - 1].X},{cells[i - 1].Y})→({cells[i].X},{cells[i].Y}) is not an 8-neighbour step.",
                    nameof(cells));
            }
        }
    }
}
