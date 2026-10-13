using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08][F-009] GranaryOverflowEvent (T-033, 粮仓满溢): deterministic
// periodic spoilage — IClock tick period + IRng seed decide trigger ticks
// and loss amounts; all loss numbers are [NEW-T-033] bands asserted below.
public class GranaryOverflowEventTests
{
    private const long SeedA = 20261013;
    private const long SeedB = 20261014;

    // ---- periodic triggering -------------------------------------------------

    [Fact] // legal — with period 5 the check fires exactly on ticks 0/5/10 over 15 ticks
    public void Overflow_fires_only_on_period_boundaries()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 5);
        int fired = 0;

        for (long tick = 0; tick < 15; tick++)
        {
            GranaryOverflowEvent.Loss? loss = policy.Evaluate(tick, stock: 120, capacity: 100);
            if (tick % 5 == 0)
            {
                Assert.NotNull(loss);
                Assert.Equal(tick, loss!.Tick);
                fired++;
            }
            else
            {
                Assert.Null(loss);
            }
        }

        Assert.Equal(3, fired);
        Assert.Equal(3, policy.TriggerCount);
    }

    [Fact] // boundary — stock exactly at capacity never spoils (strict overflow)
    public void Stock_at_capacity_does_not_trigger()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 1);

        for (long tick = 0; tick < 10; tick++)
        {
            Assert.Null(policy.Evaluate(tick, stock: 100, capacity: 100));
        }

        Assert.Equal(0, policy.TriggerCount);
    }

    [Fact] // boundary — under-capacity stock never spoils
    public void Stock_below_capacity_does_not_trigger()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 1);

        Assert.Null(policy.Evaluate(tick: 0, stock: 99.5, capacity: 100));
        Assert.Null(policy.Evaluate(tick: 1, stock: 0, capacity: 100));
        Assert.Equal(0, policy.TriggerCount);
    }

    [Fact] // legal — the default period is the documented 10 s cadence at 30 Hz
    public void Default_period_is_ten_seconds_of_simulation_time()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA));

        Assert.Equal(300, GranaryOverflowEvent.DefaultPeriodTicks);
        Assert.Equal(300, policy.PeriodTicks);
    }

    [Fact] // legal — overflowing stock can recover below capacity between checks
    public void Overflow_stops_after_stock_drains_below_capacity()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 2);
        GranaryOverflowEvent.Loss? first = policy.Evaluate(0, stock: 150, capacity: 100);
        Assert.NotNull(first);

        // Economy drains the granary; quiet from then on.
        for (long tick = 2; tick < 12; tick++)
        {
            Assert.Null(policy.Evaluate(tick, stock: 90, capacity: 100));
        }

        Assert.Equal(1, policy.TriggerCount);
    }

    // ---- loss amounts ([NEW-T-033] bands) ------------------------------------

    [Theory] // legal — both loss kinds appear across seeds and respect their bands
    [InlineData(SeedA)]
    [InlineData(SeedB)]
    [InlineData(31)]
    [InlineData(4096)]
    public void Loss_amounts_stay_inside_their_kind_bands(long seed)
    {
        var policy = new GranaryOverflowEvent(new SeededRng(seed), periodTicks: 1);
        var kinds = new HashSet<GranaryOverflowEvent.Kind>();
        const double stock = 200, capacity = 100; // overflow = 100

        for (long tick = 0; tick < 64; tick++)
        {
            GranaryOverflowEvent.Loss? loss = policy.Evaluate(tick, stock, capacity);
            Assert.NotNull(loss);
            double overflow = loss!.StockBefore - capacity;
            Assert.Equal(100.0, overflow, 9);
            kinds.Add(loss.LossKind);

            // [NEW-T-033] band per kind, loss targets only the overflow …
            double amount = loss.Amount;
            if (loss.LossKind == GranaryOverflowEvent.Kind.RatPlague)
            {
                Assert.InRange(
                    amount / overflow,
                    GranaryOverflowEvent.RatLossFractionMin,
                    GranaryOverflowEvent.RatLossFractionMax);
            }
            else
            {
                Assert.InRange(
                    amount / overflow,
                    GranaryOverflowEvent.MoldLossFractionMin,
                    GranaryOverflowEvent.MoldLossFractionMax);
            }

            // … so the surviving stock never drops below capacity.
            Assert.Equal(loss.StockBefore - amount, loss.StockAfter, 9);
            Assert.True(loss.StockAfter >= capacity);
        }

        Assert.Equal(2, kinds.Count); // both flavors occur under this LCG
    }

    [Fact] // legal — without replenishment the overflow itself shrinks strictly each period
    public void Unfed_overflow_shrinks_strictly_each_period()
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 1);
        double stock = 500;
        const double capacity = 100;
        double previousOverflow = stock - capacity;

        for (long tick = 0; tick < 8; tick++)
        {
            GranaryOverflowEvent.Loss? loss = policy.Evaluate(tick, stock, capacity);
            Assert.NotNull(loss);
            double overflow = loss!.StockAfter - capacity;
            // Every band fraction is strictly positive, so each fired event
            // removes a nonzero part of the remaining overflow.
            Assert.True(overflow < previousOverflow, $"overflow {overflow} should shrink below {previousOverflow}");
            previousOverflow = overflow;
            stock = loss.StockAfter; // no replenishment between checks
        }

        Assert.True(stock >= capacity); // spoilage converges toward capacity, never below
        Assert.Equal(8, policy.TriggerCount);
    }

    // ---- determinism ----------------------------------------------------------

    [Fact] // legal — same seed + same tick sequence ⇒ identical loss records (F-154)
    public void Same_seed_same_ticks_produce_identical_losses()
    {
        var a = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 3);
        var b = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 3);

        for (long tick = 0; tick < 30; tick++)
        {
            GranaryOverflowEvent.Loss? la = a.Evaluate(tick, stock: 150, capacity: 100);
            GranaryOverflowEvent.Loss? lb = b.Evaluate(tick, stock: 150, capacity: 100);
            Assert.Equal(la is null, lb is null);
            if (la is not null && lb is not null)
            {
                Assert.Equal(la, lb); // record equality: tick, kind, amount, stocks
            }
        }

        Assert.Equal(a.TriggerCount, b.TriggerCount);
    }

    [Fact] // legal — a different seed diverges the loss stream
    public void Different_seed_changes_loss_stream()
    {
        var a = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 1);
        var b = new GranaryOverflowEvent(new SeededRng(SeedB), periodTicks: 1);

        bool diverged = false;
        for (long tick = 0; tick < 8; tick++)
        {
            GranaryOverflowEvent.Loss? la = a.Evaluate(tick, stock: 150, capacity: 100);
            GranaryOverflowEvent.Loss? lb = b.Evaluate(tick, stock: 150, capacity: 100);
            diverged |= la!.Amount != lb!.Amount || la.LossKind != lb.LossKind;
        }

        Assert.True(diverged);
    }

    // ---- input guards -----------------------------------------------------------

    [Fact] // illegal — period below one tick rejected
    public void Constructor_rejects_non_positive_period()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 0));
    }

    [Fact] // illegal — null rng rejected (CS-1.4: randomness must come through the port)
    public void Constructor_rejects_null_rng()
    {
        Assert.Throws<ArgumentNullException>(() => new GranaryOverflowEvent(null!));
    }

    [Theory] // illegal — negative stock/capacity rejected
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(-5, -5)]
    public void Evaluate_rejects_negative_stock_or_capacity(double stock, double capacity)
    {
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(0, stock, capacity));
    }

    // ---- ledger integration -------------------------------------------------------

    [Fact] // legal — the ledger applies spoilage at period boundaries during Settle (F-009)
    public void Ledger_applies_granary_losses_at_period_boundaries()
    {
        var clock = new SteppedClock(startTick: -1); // settles land on ticks 0..9
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 5);
        var ledger = new EnergyLedger(clock, policy);
        ledger.AttachGranary(new Contracts.Foundation.ResourceKey("era1:res-food"), capacity: 100);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        ledger.SetGranaryStock(150);

        for (long tick = 0; tick < 10; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        // Boundaries 0 and 5 fired exactly once each; the tracked stock follows
        // the applied losses and never falls below capacity.
        Assert.Equal(2, policy.TriggerCount);
        Assert.NotNull(ledger.LastGranaryLoss);
        Assert.Equal(5, ledger.LastGranaryLoss!.Tick);
        Assert.True(ledger.GranaryStock >= 100);
        Assert.True(ledger.CumulativeGranaryLoss > 0);
        Assert.True(ledger.CumulativeGranaryLoss <= 50); // ≤ band caps on the 50-point overflow
    }

    [Fact] // legal — stock stays put on ticks without an overflow or boundary
    public void Ledger_granary_stock_untouched_on_quiet_ticks()
    {
        var clock = new SteppedClock(startTick: -1); // settles land on ticks 0..5
        var policy = new GranaryOverflowEvent(new SeededRng(SeedA), periodTicks: 5);
        var ledger = new EnergyLedger(clock, policy);
        ledger.AttachGranary(new Contracts.Foundation.ResourceKey("era1:res-food"), capacity: 100);
        ledger.SetGranaryStock(80); // below capacity

        for (long tick = 0; tick < 6; tick++)
        {
            clock.Advance();
            ledger.Settle();
        }

        Assert.Equal(0, policy.TriggerCount);
        Assert.Null(ledger.LastGranaryLoss);
        Assert.Equal(80.0, ledger.GranaryStock, 9);
        Assert.Equal(0.0, ledger.CumulativeGranaryLoss, 9);
    }
}
