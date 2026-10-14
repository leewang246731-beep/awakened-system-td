using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>
/// Energy price port of the teleport tier (secondary type of
/// TeleporterLink.cs; C-07 T-064, F-042): the formal seam the C-08 energy
/// system plugs into — v3.2 §5 ladder tier 6 is "∞ 件/s" with energy as the
/// ONLY limiter, so the link asks the port how much energy is available and
/// draws exactly what it spends. Implementations must answer deterministically
/// on the tick domain (CS-1.4: no clock, no RNG inside the port).
/// </summary>
public interface ITeleportEnergyPort
{
    /// <summary>Energy units currently drawable (never negative).</summary>
    int Available { get; }

    /// <summary>
    /// Draws exactly <paramref name="units"/> (callers only ever ask for
    /// amounts ≤ <see cref="Available"/>); implementations must not refuse a
    /// legal draw.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">units is negative.</exception>
    void Draw(int units);
}

/// <summary>
/// Instant transport of ladder tier 6 传送阵 (C-07 全量, T-064, F-041/F-042;
/// v3.2 §5 "传送阵 ∞ 耗能"): one <see cref="Transfer"/> call IS the whole
/// trip — items leave the supplier and land at the receiver in the same call,
/// with zero travel time and zero service window, so the throughput is
/// unbounded and solely energy-priced: every delivered item costs
/// <see cref="EnergyCostPerItem"/> units through the <see
/// cref="ITeleportEnergyPort"/> hook (瞬时+能量成本钩子).
/// <para>
/// Transfer discipline — pull, pay, accept, keep the remainder: the link
/// first pulls up to <paramref name="maxItems"/> from the source into its
/// buffer, then hands the buffered stock to the destination; a refusing or
/// partial destination leaves the undelivered remainder buffered (the tier-6
/// form of 待卸队列 — it retries on the next call and is never lost or
/// double-counted), and energy is drawn for exactly the accepted amount.
/// Determinism (CS-1.4): pure integer accounting, no clock, no RNG.
/// </para>
/// </summary>
public sealed class TeleporterLink
{
    /// <summary>
    /// Energy units charged per delivered item ([NEW-任务卡口径] 能量成本
    /// 钩子的单价常量; v3.2 §5 tier 6 "∞ 件/s，耗能").
    /// </summary>
    public const int EnergyCostPerItem = 5;

    private readonly ITeleportEnergyPort _energy;

    private int _bufferedItems;
    private long _deliveredItems;
    private long _energySpent;
    private long _transferCalls;

    /// <summary>Creates a link drawing its per-item price from one energy port.</summary>
    /// <exception cref="ArgumentNullException">energy is null.</exception>
    public TeleporterLink(ITeleportEnergyPort energy)
    {
        ArgumentNullException.ThrowIfNull(energy);
        _energy = energy;
    }

    /// <summary>Items cumulatively accepted by the destination.</summary>
    public long DeliveredItems => _deliveredItems;

    /// <summary>
    /// Items pulled from the source but not yet accepted by the destination
    /// (refused remainders retry on later calls — nothing is ever lost).
    /// </summary>
    public int BufferedItems => _bufferedItems;

    /// <summary>Energy units cumulatively drawn (exactly delivered × price).</summary>
    public long EnergySpent => _energySpent;

    /// <summary>Number of <see cref="Transfer"/> calls served.</summary>
    public long TransferCalls => _transferCalls;

    /// <summary>
    /// One instant transfer cycle: pull up to <paramref name="maxItems"/>
    /// fresh items from <paramref name="source"/> into the buffer, then
    /// deliver as much of the buffer as the destination accepts AND the
    /// energy port affords (min of both), drawing
    /// <see cref="EnergyCostPerItem"/> per delivered item. Returns the number
    /// delivered THIS call; the shortfall stays buffered. With a rich port
    /// and an accepting destination, any amount moves in a single call —
    /// that is the tier's ∞ throughput (∞ 的唯一限流器=能量).
    /// </summary>
    /// <exception cref="ArgumentNullException">source or destination is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxItems is negative.</exception>
    public int Transfer(ICargoSupplier source, ICargoReceiver destination, ResourceKey resource, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(resource);
        Guard.EnsureInRange(maxItems, 0, int.MaxValue, nameof(maxItems));
        _transferCalls++;

        if (maxItems > 0)
        {
            _bufferedItems += source.TryTake(resource, maxItems);
        }

        if (_bufferedItems == 0)
        {
            return 0;
        }

        // Ask the destination only for what the energy port can pay for —
        // an accept call is a commitment on the receiver's books, so an
        // unaffordable amount must never be offered.
        int movable = Math.Min(_bufferedItems, _energy.Available / EnergyCostPerItem);
        if (movable <= 0)
        {
            // Energy-starved: nothing moves this call and the stock stays
            // buffered (retried on the next call).
            return 0;
        }

        int accepted = Math.Min(destination.TryAccept(resource, movable), movable);
        _energy.Draw(accepted * EnergyCostPerItem);
        _energySpent += accepted * (long)EnergyCostPerItem;
        _deliveredItems += accepted;
        _bufferedItems -= accepted;
        return accepted;
    }
}
