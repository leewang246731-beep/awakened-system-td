using EraCore.Contracts.Foundation;

namespace EraCore.Energy;

/// <summary>
/// Granary overflow spoilage policy (粮仓满溢事件, C-08 T-033, F-009;
/// spec provenance = design v3.2 §4.2 E1 行「粮仓满溢（鼠患/霉变损耗食物）」
/// — the E1 risk-event row of the energy ladder, successor of v2.1 baseline
/// §4.2, full wording restored by v3.2.2 batch2 review F4): while the food
/// stock of a granary exceeds its capacity, a periodic spoilage check fires
/// on a fixed <see cref="IClock"/> tick period and a seeded
/// <see cref="IRng"/> decides the loss kind (rat plague 鼠患 or mold
/// 霉变) and the lost fraction of the overflow. Determinism contract:
/// the <see cref="IRng"/> is drawn exactly twice per fired event (kind,
/// then fraction) and never on quiet ticks, so same seed + same tick
/// sequence ⇒ identical losses (03-decomposition §1.2). All numeric bands
/// are [NEW-T-033]: v3.2 fixes no E1 granary numbers (era1.json
/// energy.risk_events=false refers to the E3+ load-rate ladder risks only
/// — SP-4 L142; the granary overflow is the E1 food-loss event and stays
/// in force, per the restored §4.2 E1 wording).
/// </summary>
public sealed class GranaryOverflowEvent
{
    /// <summary>
    /// Default check period: 300 ticks = 10 s of simulation time at the
    /// 30 Hz tick domain. [NEW-T-033] (v3.2 has no E1 cadence anchor).
    /// </summary>
    public const int DefaultPeriodTicks = 300;

    /// <summary>Minimum rat-plague loss fraction of the overflow. [NEW-T-033]</summary>
    public const double RatLossFractionMin = 0.40;

    /// <summary>Maximum rat-plague loss fraction of the overflow. [NEW-T-033]</summary>
    public const double RatLossFractionMax = 0.80;

    /// <summary>Minimum mold loss fraction of the overflow. [NEW-T-033]</summary>
    public const double MoldLossFractionMin = 0.20;

    /// <summary>Maximum mold loss fraction of the overflow. [NEW-T-033]</summary>
    public const double MoldLossFractionMax = 0.50;

    private readonly IRng _rng;

    /// <summary>
    /// Creates the policy. <paramref name="periodTicks"/> must be ≥ 1
    /// (a check every tick is legal but wasteful); rejected otherwise with
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public GranaryOverflowEvent(IRng rng, int periodTicks = DefaultPeriodTicks)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (periodTicks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodTicks), periodTicks, "Spoilage check period must be at least 1 tick.");
        }

        _rng = rng;
        PeriodTicks = periodTicks;
    }

    /// <summary>Check period in ticks; events can only fire when <c>tick % PeriodTicks == 0</c>.</summary>
    public int PeriodTicks { get; }

    /// <summary>Number of spoilage events fired so far (observability, feeds the state hash).</summary>
    public long TriggerCount { get; private set; }

    /// <summary>
    /// Runs one check. Returns <c>null</c> on quiet ticks (not on a period
    /// boundary, or stock ≤ capacity); on a fired event draws the kind and
    /// fraction from the seeded <see cref="IRng"/> and returns the applied
    /// loss. Both bounds are exclusive-open uniform draws mapped into the
    /// kind's fraction band; the loss always targets only the overflow
    /// (stock − capacity), so the surviving stock stays ≥ capacity.
    /// </summary>
    /// <param name="tick">Current simulation tick (from <see cref="IClock.Tick"/>).</param>
    /// <param name="stock">Current food stock; negative values are rejected.</param>
    /// <param name="capacity">Granary capacity; negative values are rejected.</param>
    public Loss? Evaluate(long tick, double stock, double capacity)
    {
        if (stock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stock), stock, "Food stock must not be negative.");
        }

        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Granary capacity must not be negative.");
        }

        if (tick % PeriodTicks != 0)
        {
            return null;
        }

        double overflow = stock - capacity;
        if (overflow <= 0)
        {
            return null;
        }

        // Deterministic draw order: kind first, fraction second (fixed at
        // exactly two draws per fired event — the determinism contract).
        Kind kind = _rng.NextDouble() < 0.5 ? Kind.RatPlague : Kind.Mold;
        double draw = _rng.NextDouble();
        double amount = (kind == Kind.RatPlague
            ? RatLossFractionMin + draw * (RatLossFractionMax - RatLossFractionMin)
            : MoldLossFractionMin + draw * (MoldLossFractionMax - MoldLossFractionMin))
            * overflow;

        TriggerCount++;
        return new Loss(tick, kind, amount, stock, stock - amount);
    }

    /// <summary>Spoilage kind. [NEW-T-033] flavor split — v3.2 defines neither.</summary>
    public enum Kind
    {
        /// <summary>Rat plague (鼠患) — heavier band, 40%~80% of the overflow.</summary>
        RatPlague = 1,

        /// <summary>Mold (霉变) — lighter band, 20%~50% of the overflow.</summary>
        Mold = 2,
    }

    /// <summary>One applied spoilage event (immutable, journaled by the ledger).</summary>
    /// <param name="Tick">Tick the event fired on.</param>
    /// <param name="LossKind">Rat plague or mold.</param>
    /// <param name="Amount">Food points destroyed (fraction band × overflow).</param>
    /// <param name="StockBefore">Stock before the loss.</param>
    /// <param name="StockAfter">Stock after the loss; always ≥ capacity.</param>
    public sealed record Loss(long Tick, Kind LossKind, double Amount, double StockBefore, double StockAfter);
}
