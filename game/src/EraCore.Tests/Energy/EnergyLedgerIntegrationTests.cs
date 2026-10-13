using EraCore.Contracts.Foundation;
using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// EnergyLedger integration layer (T-033): a 30-tick driven sequence with
// mid-sequence registrations, asserted against a hand-computed efficiency
// curve (§5.3 min-chain consumption of the satisfaction value), plus the
// determinism contract: same seed + same tick sequence ⇒ equal state hashes.
// [C-08][F-009]
public class EnergyLedgerIntegrationTests
{
    private const long SeedA = 20261013;
    private const long SeedB = 20261014;

    private static readonly ResourceKey Food = new("era1:res-food");
    private static readonly ResourceKey Wood = new("era1:res-wood");

    /// <summary>
    /// Hypothetical consumer (假想消费者): a plain record sampling the ledger
    /// each tick — the system under test is the ledger, this holder only
    /// carries the node id the way a real facility (C-06/T-034) would.
    /// </summary>
    private sealed record FacilityProbe(EnergyNodeId Id, ResourceKey? Supplies)
    {
        public double SampleEfficiency(EnergyLedger ledger) => ledger.SatisfactionOf(Id);
    }

    /// <summary>Drive result: the ledger plus the granary policy it was built with.</summary>
    private sealed record DriveResult(EnergyLedger Ledger, GranaryOverflowEvent Policy);

    /// <summary>
    /// Drives 30 settles (ticks 0..29) with the scripted mid-sequence
    /// changes: a BeastPen joins after tick 9, a second consumer after
    /// tick 19. Returns the ledger (state: after tick 29).
    /// </summary>
    private static DriveResult Drive30Ticks(long seed, List<double>? curve = null, FacilityProbe? probe = null)
    {
        var clock = new SteppedClock(startTick: -1);
        var policy = new GranaryOverflowEvent(new SeededRng(seed), periodTicks: 10);
        var ledger = new EnergyLedger(clock, policy);
        ledger.AttachGranary(Food, capacity: 100);
        ledger.SetGranaryStock(120);

        var woodcutter = new FacilityProbe(new EnergyNodeId(1), Wood); // C1: demand 30
        ledger.RegisterProducer(new EnergyNodeId(10), EnergyNodeKind.LaborCamp); // 10/s
        ledger.RegisterConsumer(woodcutter.Id, 30.0, supplies: Wood);
        ledger.RegisterConsumer(new EnergyNodeId(2), 0.0); // C0: idle consumer, demand 0

        for (long tick = 0; tick < 30; tick++)
        {
            if (tick == 10)
            {
                ledger.RegisterProducer(new EnergyNodeId(11), EnergyNodeKind.BeastPen); // +40/s
            }

            if (tick == 20)
            {
                ledger.RegisterConsumer(new EnergyNodeId(3), 45.0, supplies: Food); // C2
            }

            if (tick == 15)
            {
                ledger.SetGranaryStock(130); // economy layer replenishes mid-sequence
            }

            clock.Advance();
            ledger.Settle();
            curve?.Add(probe?.SampleEfficiency(ledger) ?? ledger.SatisfactionOf(woodcutter.Id));
        }

        return new DriveResult(ledger, policy);
    }

    [Fact] // legal — 30-tick efficiency curve matches the hand-computed sequence [F-009]
    public void Thirty_tick_efficiency_curve_matches_hand_computation()
    {
        var curve = new List<double>();
        DriveResult result = Drive30Ticks(SeedA, curve);
        EnergyLedger ledger = result.Ledger;

        // Hand computation (rates in energy points/s, settle per tick):
        //   ticks 0..9  : 10 vs 30 ⇒ 1/3   (shortage — proportional slowdown)
        //   ticks 10..19: 50 vs 30 ⇒ 1     (BeastPen joined, clamped)
        //   ticks 20..29: 50 vs 75 ⇒ 2/3   (second consumer joined)
        double[] expected = Enumerable.Concat(
            Enumerable.Repeat(1.0 / 3.0, 10),
            Enumerable.Concat(Enumerable.Repeat(1.0, 10), Enumerable.Repeat(2.0 / 3.0, 10))).ToArray();

        Assert.Equal(30, curve.Count);
        for (int i = 0; i < 30; i++)
        {
            // tolerance 1e-9: expected values are exact IEEE ratios of the anchor integers
            Assert.Equal(expected[i], curve[i], 9);
        }

        // The idle consumer reads 1 all the way (需求≤0 恒 1, v3.2 §5 L75).
        Assert.Equal(1.0, ledger.SatisfactionOf(new EnergyNodeId(2)));
        // The second consumer shares the SAME efficiency in phase 3 (同比降速).
        Assert.Equal(2.0 / 3.0, ledger.SatisfactionOf(new EnergyNodeId(3)), 9);

        // Energy accounting over the whole window:
        //   delivered = (10 + 30 + 50)/30 s = 3 points per phase ⇒ 30 total… exactly:
        //   phase1 min(10,30)/30×10t = 10/3; phase2 30/30×10t = 10; phase3 50/30×10t = 50/3
        Assert.Equal(10.0 / 3.0 + 10.0 + 50.0 / 3.0, ledger.CumulativeDelivered, 9);
        //   surplus only in phase 2: (50−30)/30×10t = 20/3
        Assert.Equal(20.0 / 3.0, ledger.CumulativeSurplus, 9);
        Assert.Equal(30, ledger.SettledTicks);

        // Granary woven through the same window: boundaries 0/10/20 all fired,
        // the mid-sequence replenishment (tick 15) kept the stock above the
        // spoilage floor, and the efficiency curve was unaffected by spoilage.
        Assert.Equal(3, result.Policy.TriggerCount);
        Assert.True(ledger.GranaryStock >= 100);
    }

    [Fact] // legal — §5.3 seam: facility efficiency = min(supply rates) [F-009], energy is one dimension
    public void Efficiency_chain_takes_min_over_supply_dimensions()
    {
        // A facility fed by energy plus an (out-of-ledger) ammo dimension at 0.9:
        // its efficiency is the min of the two, per v3.2 §5 L75.
        const double ammoSupplyRate = 0.9;
        var probe = new FacilityProbe(new EnergyNodeId(1), Wood);
        var curve = new List<double>();
        Drive30Ticks(SeedA, curve, probe);

        double[] energyExpected =
        {
            // phase 1: min(1/3, 0.9) = 1/3 (energy is the bottleneck)
            1.0 / 3.0,
            // phase 2: min(1, 0.9) = 0.9 (ammo becomes the bottleneck)
            0.9,
            // phase 3: min(2/3, 0.9) = 2/3 (energy bottlenecks again)
            2.0 / 3.0,
        };

        Assert.Equal(energyExpected[0], Math.Min(curve[5], ammoSupplyRate), 9);
        Assert.Equal(energyExpected[1], Math.Min(curve[15], ammoSupplyRate), 9);
        Assert.Equal(energyExpected[2], Math.Min(curve[25], ammoSupplyRate), 9);

        // The probe itself reports the raw energy dimension for the chain.
        Assert.Equal(1.0 / 3.0, curve[5], 9);
        Assert.Equal(1.0, curve[15], 9);
        Assert.Equal(2.0 / 3.0, curve[25], 9);
    }

    [Fact] // legal — same seed + same tick sequence ⇒ equal hashes at every checkpoint [F-154]
    public void Same_seed_and_tick_sequence_yield_equal_state_hashes()
    {
        EnergyLedger a = Drive30Ticks(SeedA).Ledger;
        EnergyLedger b = Drive30Ticks(SeedA).Ledger;

        // End state after the full scripted window.
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Satisfaction, b.Satisfaction, 9);
        Assert.Equal(a.CumulativeDelivered, b.CumulativeDelivered, 9);
        Assert.Equal(a.CumulativeGranaryLoss, b.CumulativeGranaryLoss, 9);
        Assert.Equal(a.GranaryStock, b.GranaryStock, 9);
        Assert.NotNull(a.LastGranaryLoss);
        Assert.Equal(a.LastGranaryLoss, b.LastGranaryLoss);
    }

    [Fact] // legal — a different seed diverges the granary losses and the state hash [F-154]
    public void Different_seed_diverges_state_hash()
    {
        EnergyLedger a = Drive30Ticks(SeedA).Ledger;
        EnergyLedger b = Drive30Ticks(SeedB).Ledger;

        // The energy math is seed-independent; only the seeded spoilage differs.
        Assert.Equal(a.Satisfaction, b.Satisfaction, 9);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.NotEqual(a.CumulativeGranaryLoss, b.CumulativeGranaryLoss);
    }

    [Fact] // legal — checkpoint replay: identical prefix drives stay hash-equal mid-sequence [F-154]
    public void Prefix_drives_remain_hash_equal_at_checkpoints()
    {
        long[] checkpoints = { 10, 20, 29 };

        foreach (long checkpoint in checkpoints)
        {
            long hashA = DrivePrefix(checkpoint, SeedA);
            long hashB = DrivePrefix(checkpoint, SeedA);
            Assert.Equal(hashA, hashB); // same seed + same tick prefix ⇒ same hash
        }

        // And the hash actually advances between checkpoints (state is live).
        Assert.NotEqual(DrivePrefix(10, SeedA), DrivePrefix(20, SeedA));
    }

    /// <summary>Drives only the first <paramref name="ticks"/> settles of the scripted sequence.</summary>
    private static long DrivePrefix(long ticks, long seed)
    {
        var clock = new SteppedClock(startTick: -1);
        var ledger = new EnergyLedger(clock, new GranaryOverflowEvent(new SeededRng(seed), periodTicks: 10));
        ledger.AttachGranary(Food, capacity: 100);
        ledger.SetGranaryStock(120);
        ledger.RegisterProducer(new EnergyNodeId(10), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(1), 30.0, supplies: Wood);

        for (long tick = 0; tick < ticks; tick++)
        {
            if (tick == 10)
            {
                ledger.RegisterProducer(new EnergyNodeId(11), EnergyNodeKind.BeastPen);
            }

            if (tick == 15)
            {
                ledger.SetGranaryStock(130);
            }

            clock.Advance();
            ledger.Settle();
        }

        return ledger.ComputeStateHash();
    }
}
