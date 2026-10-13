using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// EnergyLedger logic layer (T-033): shortage = proportional slowdown, never
// a stall — the 3-producers × 2-consumers matrix over the E1 anchor outputs
// (10/40), asserting every demanding consumer receives the SAME efficiency
// (同比降速) and settlement keeps running (不停摆).
// [C-08][F-009]
public class EnergyLedgerShortageMatrixTests
{
    // Rows: (laborCamps, beastPens, demandA, demandB, expectedSatisfaction)
    // output ∈ {30,60,90,120} / demand ∈ {35,60,120}, clamped to [0,1].
    [Theory]
    // legal — balanced band
    [InlineData(3, 0, 15, 20, 30.0 / 35.0)] // 30 vs 35 ⇒ 6/7 shortfall
    [InlineData(2, 1, 25, 35, 1.0)] // 60 vs 60 ⇒ exact
    [InlineData(1, 2, 40, 80, 0.75)] // 90 vs 120 ⇒ 3/4
    // boundary — clamp edges
    [InlineData(0, 3, 15, 20, 1.0)] // 120 vs 35 ⇒ oversupply clamps at 1
    [InlineData(3, 0, 25, 35, 0.5)] // 30 vs 60 ⇒ half speed
    [InlineData(1, 2, 25, 35, 1.0)] // 90 vs 60 ⇒ clamped
    // boundary — deep shortage keeps a nonzero efficiency (no stall)
    [InlineData(3, 0, 40, 80, 0.25)] // 30 vs 120 ⇒ quarter speed
    [InlineData(2, 1, 40, 80, 0.5)] // 60 vs 120 ⇒ half speed
    [InlineData(0, 3, 40, 80, 1.0)] // 120 vs 120 ⇒ exact at tier cap
    // legal — mixed-tier producers
    [InlineData(2, 1, 15, 20, 1.0)] // 60 vs 35 ⇒ clamped
    [InlineData(1, 2, 15, 20, 1.0)] // 90 vs 35 ⇒ clamped
    [InlineData(0, 3, 25, 35, 1.0)] // 120 vs 60 ⇒ clamped
    public void Shortage_matrix_slows_consumers_proportionally_without_stall(
        int laborCamps,
        int beastPens,
        int demandA,
        int demandB,
        double expected)
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        long nextId = 1;
        for (int i = 0; i < laborCamps; i++)
        {
            ledger.RegisterProducer(new EnergyNodeId(nextId++), EnergyNodeKind.LaborCamp);
        }

        for (int i = 0; i < beastPens; i++)
        {
            ledger.RegisterProducer(new EnergyNodeId(nextId++), EnergyNodeKind.BeastPen);
        }

        var consumerA = new EnergyNodeId(nextId++);
        var consumerB = new EnergyNodeId(nextId);
        ledger.RegisterConsumer(consumerA, demandA);
        ledger.RegisterConsumer(consumerB, demandB);

        clock.Advance();
        ledger.Settle(); // 不停摆: the starved settle must complete, not throw

        // tolerance 1e-9: expected values are exact IEEE ratios of the same integers
        Assert.Equal(expected, ledger.Satisfaction, 9);
        Assert.Equal(expected, ledger.SatisfactionOf(consumerA), 9); // 同比降速
        Assert.Equal(expected, ledger.SatisfactionOf(consumerB), 9); // same factor for both
        Assert.Equal(1, ledger.SettledTicks);

        double output = laborCamps * 10.0 + beastPens * 40.0;
        double demand = demandA + demandB;
        Assert.Equal(Math.Min(output, demand) / EnergyLedger.TicksPerSecond, ledger.CumulativeDelivered, 9);
    }

    [Fact] // legal — 30 consecutive starved settles keep delivering: slowdown, not a shutdown (F-009)
    public void Prolonged_shortage_keeps_settling_and_delivering()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10/s
        ledger.RegisterConsumer(new EnergyNodeId(2), 40.0); // ⇒ 0.25 forever

        for (int i = 0; i < 30; i++)
        {
            clock.Advance();
            ledger.Settle();
            Assert.Equal(0.25, ledger.Satisfaction, 9); // stable, never stalls to an error state
        }

        Assert.Equal(30, ledger.SettledTicks);
        // 10/s × 30 ticks × (1/30 s/tick) = 10 points delivered despite the shortage
        Assert.Equal(10.0, ledger.CumulativeDelivered, 9);
        Assert.Equal(0.0, ledger.CumulativeSurplus, 9);
    }

    [Fact] // legal — the E1 ladder ×4 sits inside the v3.2 band [3,5] as a matrix-level check (T-033 验收)
    public void One_beast_pen_replaces_four_labor_camps_within_ladder_band()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        var tower = new EnergyNodeId(9);
        ledger.RegisterConsumer(tower, 40.0);

        // One camp covers a quarter of the 40/s demand (10 vs 40 ⇒ 0.25).
        var camp = new EnergyNodeId(1);
        ledger.RegisterProducer(camp, EnergyNodeKind.LaborCamp);
        clock.Advance();
        ledger.Settle();
        Assert.Equal(0.25, ledger.SatisfactionOf(tower), 9);

        // Four camps cover it exactly (40 vs 40 ⇒ 1).
        ledger.RegisterProducer(new EnergyNodeId(2), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(3), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(4), EnergyNodeKind.LaborCamp);
        clock.Advance();
        ledger.Settle();
        Assert.Equal(1.0, ledger.SatisfactionOf(tower), 9);

        // Tear the camps down; ONE tier-2 pen replaces all four (40 vs 40 ⇒ 1):
        // the tier jump is exactly ×4 …
        for (long id = 1; id <= 4; id++)
        {
            Assert.True(ledger.Unregister(new EnergyNodeId(id)));
        }

        ledger.RegisterProducer(new EnergyNodeId(5), EnergyNodeKind.BeastPen);
        clock.Advance();
        ledger.Settle();
        Assert.Equal(1.0, ledger.SatisfactionOf(tower), 9);
        Assert.Equal(1, ledger.ProducerCount);

        // … and ×4 lies inside the v3.2 §4 L65 ladder band [3,5].
        Assert.Equal(4.0, E1EnergyAnchors.L1ToL2Multiplier);
        Assert.InRange(
            E1EnergyAnchors.L1ToL2Multiplier,
            E1EnergyAnchors.LadderMultiplierMin,
            E1EnergyAnchors.LadderMultiplierMax);
    }
}
