using EraCore.Contracts.Foundation;
using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08][F-009] EnergyLedger unit layer (T-033): satisfaction boundary
// semantics (v3.2 §5 L75 — clamp(产出/需求,0,1)，需求≤0 恒 1), registration
// contracts, settle monotonicity, granary attach/stock guards.
public class EnergyLedgerTests
{
    private static readonly ResourceKey Food = new("era1:res-food");

    /// <summary>Ledger plus its clock, with a one-tick drive (advance clock, then settle).</summary>
    private sealed record Harness(EnergyLedger Ledger, SteppedClock Clock)
    {
        public void SettleOne()
        {
            Clock.Advance();
            Ledger.Settle();
        }
    }

    private static Harness Make(GranaryOverflowEvent? granary = null)
    {
        var clock = new SteppedClock();
        return new Harness(new EnergyLedger(clock, granary), clock);
    }

    // ---- satisfaction boundaries (v3.2 §5 L75) ----------------------------

    [Fact] // boundary — demand > 0 with zero output ⇒ satisfaction 0 (starved pool)
    public void Satisfaction_is_zero_when_output_is_zero()
    {
        var ledger = Make().Ledger;
        ledger.RegisterConsumer(new EnergyNodeId(1), 25.0);

        ledger.Settle();

        Assert.Equal(0.0, ledger.Satisfaction);
    }

    [Fact] // boundary — exact balance 30/30 ⇒ exactly 1
    public void Satisfaction_is_one_on_exact_balance()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(2), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(3), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(4), 30.0);

        ledger.Settle();

        Assert.Equal(1.0, ledger.Satisfaction);
    }

    [Fact] // legal — oversupply 40/30 clamps at 1, never above
    public void Satisfaction_clamps_to_one_when_oversupplied()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.BeastPen);
        ledger.RegisterConsumer(new EnergyNodeId(2), 30.0);

        ledger.Settle();

        Assert.Equal(1.0, ledger.Satisfaction);
    }

    [Fact] // boundary — total demand 0 (producers only) ⇒ 恒 1
    public void Satisfaction_is_one_when_total_demand_is_zero()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        ledger.Settle();

        Assert.Equal(1.0, ledger.Satisfaction);
    }

    [Fact] // boundary — completely empty ledger ⇒ demand ≤ 0 ⇒ 1
    public void Empty_ledger_is_fully_satisfied()
    {
        var ledger = Make().Ledger;

        ledger.Settle();

        Assert.Equal(1.0, ledger.Satisfaction);
    }

    [Fact] // boundary — a zero-demand consumer reads 1 inside a starved pool (需求≤0 恒 1, per-dimension rule)
    public void Zero_demand_consumer_reads_one_inside_starved_pool()
    {
        var ledger = Make().Ledger;
        var starved = new EnergyNodeId(1);
        var idle = new EnergyNodeId(2);
        ledger.RegisterConsumer(starved, 40.0); // against 1× LaborCamp = 10/s ⇒ pool 0.25
        ledger.RegisterConsumer(idle, 0.0);
        ledger.RegisterProducer(new EnergyNodeId(3), EnergyNodeKind.LaborCamp);

        ledger.Settle();

        // tolerance 1e-9: exact-ratio IEEE division, guard against accumulation noise only
        Assert.Equal(0.25, ledger.Satisfaction, 9);
        Assert.Equal(1.0, ledger.SatisfactionOf(idle));
        Assert.Equal(0.25, ledger.SatisfactionOf(starved), 9);
    }

    // ---- delivered / surplus accounting ------------------------------------

    [Fact] // legal — one settle of 30/s into 75/s demand delivers 1 energy point (min × 1 tick / 30 Hz)
    public void Delivered_accounts_min_of_output_and_demand_per_tick()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(2), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(3), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(4), 75.0);

        ledger.Settle();

        // min(30,75) = 30/s × (1/30) s = 1 point; surplus = max(0,30−75) = 0
        Assert.Equal(1.0, ledger.CumulativeDelivered, 9);
        Assert.Equal(0.0, ledger.CumulativeSurplus, 9);
    }

    [Fact] // legal — oversupply waste is tracked separately from delivery
    public void Surplus_accounts_output_above_demand()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.BeastPen); // 40/s
        ledger.RegisterConsumer(new EnergyNodeId(2), 30.0);

        ledger.Settle();

        // delivered = 30/s × 1/30 = 1; surplus = 10/s × 1/30 = 1/3
        Assert.Equal(1.0, ledger.CumulativeDelivered, 9);
        Assert.Equal(1.0 / 3.0, ledger.CumulativeSurplus, 9);
    }

    // ---- registration contracts ---------------------------------------------

    [Fact] // illegal — null id rejected
    public void RegisterProducer_rejects_null_id()
    {
        var ledger = Make().Ledger;
        Assert.Throws<ArgumentNullException>(() => ledger.RegisterProducer(null!, EnergyNodeKind.LaborCamp));
    }

    [Theory] // illegal — undefined enum values rejected
    [InlineData((EnergyNodeKind)0)]
    [InlineData((EnergyNodeKind)99)]
    public void RegisterProducer_rejects_undefined_kinds(EnergyNodeKind kind)
    {
        var ledger = Make().Ledger;
        Assert.Throws<ArgumentException>(() => ledger.RegisterProducer(new EnergyNodeId(1), kind));
    }

    [Fact] // illegal — consumer kind carries no anchor output
    public void RegisterProducer_rejects_consumer_kind()
    {
        var ledger = Make().Ledger;
        Assert.Throws<ArgumentException>(
            () => ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.Facility));
    }

    [Fact] // illegal — duplicate id across producers and consumers rejected (one id, one node)
    public void Register_rejects_duplicate_ids()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        Assert.Throws<InvalidOperationException>(
            () => ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.BeastPen));
        Assert.Throws<InvalidOperationException>(
            () => ledger.RegisterConsumer(new EnergyNodeId(1), 5.0));
    }

    [Theory] // illegal — non-finite or negative demand rejected
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RegisterConsumer_rejects_bad_demand(double demand)
    {
        var ledger = Make().Ledger;
        Assert.Throws<ArgumentException>(() => ledger.RegisterConsumer(new EnergyNodeId(1), demand));
    }

    [Fact] // boundary — zero demand is a legal registration (idle consumer)
    public void RegisterConsumer_accepts_zero_demand()
    {
        var ledger = Make().Ledger;
        ledger.RegisterConsumer(new EnergyNodeId(1), 0.0);

        Assert.Equal(1, ledger.ConsumerCount);
        Assert.Equal(0.0, ledger.TotalDemandPerSecond);
    }

    [Fact] // legal — unregister takes effect on the next settle
    public void Unregister_recomputes_satisfaction_next_settle()
    {
        var h = Make();
        var camp = new EnergyNodeId(1);
        h.Ledger.RegisterProducer(camp, EnergyNodeKind.LaborCamp); // 10/s
        h.Ledger.RegisterConsumer(new EnergyNodeId(2), 40.0); // ⇒ 0.25

        h.SettleOne();
        Assert.Equal(0.25, h.Ledger.Satisfaction, 9);

        Assert.True(h.Ledger.Unregister(camp));
        h.SettleOne();

        Assert.Equal(0.0, h.Ledger.Satisfaction); // no producers left ⇒ starved
        Assert.Equal(0, h.Ledger.ProducerCount);
    }

    [Fact] // legal — unregister of an unknown id is a no-op returning false
    public void Unregister_unknown_id_returns_false()
    {
        var ledger = Make().Ledger;

        Assert.False(ledger.Unregister(new EnergyNodeId(123)));
    }

    // ---- settle monotonicity ------------------------------------------------

    [Fact] // illegal — settling the same tick twice rejected (one settle per tick)
    public void Settle_rejects_double_settle_of_same_tick()
    {
        var ledger = Make().Ledger;
        ledger.Settle();

        Assert.Throws<InvalidOperationException>(ledger.Settle);
    }

    [Fact] // illegal — a backwards clock is rejected
    public void Settle_rejects_non_monotonic_clock()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.Settle();

        clock.Rewind(1);
        Assert.Throws<InvalidOperationException>(ledger.Settle);
    }

    // ---- SatisfactionOf contracts -------------------------------------------

    [Fact] // illegal — producer ids and unknown ids carry no efficiency
    public void SatisfactionOf_rejects_producer_and_unknown_ids()
    {
        var ledger = Make().Ledger;
        var producer = new EnergyNodeId(1);
        ledger.RegisterProducer(producer, EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentException>(() => ledger.SatisfactionOf(producer));
        Assert.Throws<ArgumentException>(() => ledger.SatisfactionOf(new EnergyNodeId(404)));
        Assert.Throws<ArgumentNullException>(() => ledger.SatisfactionOf(null!));
    }

    // ---- granary attach/stock guards ----------------------------------------

    [Fact] // illegal — attaching a granary without a policy in the ctor rejected
    public void AttachGranary_rejects_ledger_without_policy()
    {
        var ledger = Make(granary: null).Ledger;

        Assert.Throws<InvalidOperationException>(() => ledger.AttachGranary(Food, 100.0));
    }

    [Fact] // illegal — second attach rejected (one granary per E1 ledger)
    public void AttachGranary_rejects_second_attach()
    {
        var ledger = Make(new GranaryOverflowEvent(new SeededRng(1))).Ledger;
        ledger.AttachGranary(Food, 100.0);

        Assert.Throws<InvalidOperationException>(() => ledger.AttachGranary(Food, 200.0));
    }

    [Theory] // illegal — non-finite or negative capacity rejected
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AttachGranary_rejects_bad_capacity(double capacity)
    {
        var ledger = Make(new GranaryOverflowEvent(new SeededRng(1))).Ledger;

        Assert.Throws<ArgumentException>(() => ledger.AttachGranary(Food, capacity));
    }

    [Fact] // illegal — stock sync before attach rejected
    public void SetGranaryStock_rejects_missing_granary()
    {
        var ledger = Make(new GranaryOverflowEvent(new SeededRng(1))).Ledger;

        Assert.Throws<InvalidOperationException>(() => ledger.SetGranaryStock(50.0));
    }

    [Fact] // illegal — negative stock rejected (resource pools never go below zero)
    public void SetGranaryStock_rejects_negative_stock()
    {
        var ledger = Make(new GranaryOverflowEvent(new SeededRng(1))).Ledger;
        ledger.AttachGranary(Food, 100.0);

        Assert.Throws<ArgumentException>(() => ledger.SetGranaryStock(-1.0));
    }

    // ---- state hash ----------------------------------------------------------

    [Fact] // legal — the hash reacts to settle progress (F-154 prerequisite)
    public void State_hash_changes_after_settle()
    {
        var ledger = Make().Ledger;
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(2), 20.0);
        long before = ledger.ComputeStateHash();

        ledger.Settle();

        Assert.NotEqual(before, ledger.ComputeStateHash());
    }

    // ---- ctor ----------------------------------------------------------------

    [Fact] // illegal — null clock rejected
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() => new EnergyLedger(null!));
    }
}
