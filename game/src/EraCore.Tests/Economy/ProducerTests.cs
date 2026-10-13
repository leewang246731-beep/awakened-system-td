using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-019][F-021] Producer unit layer (T-034): harvest rate table
// (T1 0.5 件/s/坐席, seats/tier scaling, energy satisfaction halves the
// rate) and 满仓反压 (output full → efficiency 0, buffer and fractional
// progress preserved — 降速不停摆). Tick counts in the tables land the
// fractional accumulator at x.5 of an item, away from the whole-item
// boundary, so FP accumulation noise (~1e-15 per 1/60 summand) cannot
// flip a count (tolerance rationale: accumulator is a plain running sum).
// [C-06][F-019][F-021]
public class ProducerTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly ResourceKey Stone = new("era1:res-stone");

    /// <summary>Producer on its own ledger + clock; drive(n) settles n ticks end-to-end.</summary>
    private sealed record Harness(Producer Producer, EnergyLedger Ledger, SteppedClock Clock)
    {
        public static Harness Make(int seats = 1, int tier = 1, bool starved = false)
        {
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10/s
            if (starved)
            {
                // total demand 1 (producer) + 19 = 20 vs 10/s output ⇒ satisfaction 0.5
                ledger.RegisterConsumer(new EnergyNodeId(9), 19.0);
            }

            var producer = new Producer(new EntityId(1), Wood, seats, tier, ledger, new EnergyNodeId(2));
            return new Harness(producer, ledger, clock);
        }

        public void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Clock.Advance();
                Ledger.Settle();
                Producer.Tick();
            }
        }
    }

    // ---- 公式组 1/6：采集速率表 -----------------------------------------

    [Theory] // legal — T1 0.5 件/s/坐席 at 30 Hz: ticks land accumulator at x.5 ⇒ floor counts
    [InlineData(30, 0)] // 0.5 件
    [InlineData(90, 1)] // 1.5 件
    [InlineData(150, 2)] // 2.5 件
    [InlineData(210, 3)] // 3.5 件
    public void Tier1_harvest_rate_is_half_item_per_second_per_seat(int ticks, int expectedItems)
    {
        var h = Harness.Make();

        h.Drive(ticks);

        Assert.Equal(expectedItems, h.Producer.OutputBuffer);
        Assert.Equal(expectedItems, h.Producer.CumulativeHarvested);
    }

    [Theory] // legal — seats scale the rate (2 坐席 = 1.0 件/s, 3 坐席 = 1.5 件/s)
    [InlineData(2, 75, 2)] // accumulator 2.5
    [InlineData(2, 105, 3)] // accumulator 3.5
    [InlineData(3, 90, 4)] // accumulator 4.5
    public void Seats_scale_the_harvest_rate(int seats, int ticks, int expectedItems)
    {
        var h = Harness.Make(seats: seats);

        h.Drive(ticks);

        Assert.Equal(expectedItems, h.Producer.OutputBuffer);
    }

    [Theory] // legal — tier ladder ×1.8 output at fixed 1 seat; tiers 2/3 [NEW-T-034]
    [InlineData(1, 90, 1)] // 0.5 件/s × 3 s = 1.5 ⇒ 1
    [InlineData(2, 50, 1)] // 0.9 件/s × 5/3 s = 1.5 ⇒ 1
    [InlineData(3, 100, 5)] // 1.62 件/s × 10/3 s = 5.4 ⇒ 5
    public void Tier_ladder_multiplies_the_harvest_rate(int tier, int ticks, int expectedItems)
    {
        var h = Harness.Make(tier: tier);

        h.Drive(ticks);

        Assert.Equal(expectedItems, h.Producer.OutputBuffer);
    }

    [Theory] // legal — energy satisfaction scales the rate (同比降速不停摆)
    [InlineData(1.0, 210, 3)] // full power: 3.5 ⇒ 3
    [InlineData(0.5, 210, 1)] // half power: 1.75 ⇒ 1 — strictly fewer, not stalled
    [InlineData(0.5, 450, 3)] // half power, longer window: 3.75 ⇒ 3
    [InlineData(0.0, 600, 0)] // starved pool: nothing harvested
    public void Energy_satisfaction_scales_the_harvest_rate(double satisfaction, int ticks, int expectedItems)
    {
        var h = Harness.Make(starved: satisfaction < 1.0);
        if (satisfaction == 0.0)
        {
            h.Ledger.Unregister(new EnergyNodeId(1)); // drop the labor camp ⇒ output 0 ⇒ sat 0
        }

        h.Drive(1);
        Assert.Equal(satisfaction, h.Ledger.Satisfaction, 9); // 前置：账本确实处于该 satisfaction
        Assert.Equal(satisfaction, h.Producer.Efficiency, 9); // §5.3 能源维度直通

        h.Drive(ticks - 1);

        Assert.Equal(expectedItems, h.Producer.CumulativeHarvested);
    }

    // ---- 满仓反压 ---------------------------------------------------------

    [Fact] // legal — a full yard pile pauses production without destroying anything
    public void Full_output_buffer_pauses_harvest_and_preserves_progress()
    {
        var h = Harness.Make();
        h.Drive(630); // accumulator ~10.5 ⇒ 10 items = buffer cap, residue ~0.5

        Assert.Equal(10, h.Producer.OutputBuffer);
        Assert.True(h.Producer.IsBackpressured);
        Assert.Equal(0.0, h.Producer.Efficiency); // 停产：效率 0（§5.3 降速不停摆语义）
        double frozenProgress = h.Producer.ProgressItems;
        Assert.InRange(frozenProgress, 0.0, 1.0); // 残余进度不足一件

        h.Drive(300); // 300 more ticks while full — nothing moves

        Assert.Equal(10, h.Producer.OutputBuffer);
        Assert.Equal(10, h.Producer.CumulativeHarvested);
        Assert.Equal(frozenProgress, h.Producer.ProgressItems); // bit-for-bit preserved
    }

    [Fact] // legal — draining the pile resumes production where it stopped
    public void Draining_the_buffer_resumes_harvest()
    {
        var h = Harness.Make();
        h.Drive(630);
        int taken = h.Producer.TryTake(Wood, 4);

        Assert.Equal(4, taken);
        Assert.False(h.Producer.IsBackpressured);
        Assert.NotEqual(0.0, h.Producer.Efficiency);

        h.Drive(120); // residue ~0.5 + 2.0 = 2.5 ⇒ +2 items

        Assert.Equal(8, h.Producer.OutputBuffer);
        Assert.Equal(12, h.Producer.CumulativeHarvested);
        // 守恒：累计产出 = 已取走 + 缓冲存量（残余 <1 件不计）
        Assert.Equal(h.Producer.CumulativeHarvested, (long)(taken + h.Producer.OutputBuffer));
    }

    // ---- 供给口（ICargoSupplier seam） ------------------------------------

    [Fact] // legal — TryTake hands over only this producer's resource
    public void TryTake_only_hands_over_the_produced_resource()
    {
        var h = Harness.Make();
        h.Drive(90); // 1 item

        Assert.Equal(0, h.Producer.TryTake(Stone, 5)); // wrong resource ⇒ nothing given
        Assert.Equal(1, h.Producer.OutputBuffer); // buffer untouched by the refusal

        Assert.Equal(1, h.Producer.TryTake(Wood, 5));
        Assert.Equal(0, h.Producer.OutputBuffer);
        Assert.Equal(0, h.Producer.TryTake(Wood, 5)); // empty ⇒ 0, porter waits
    }

    [Fact] // illegal — null resource / negative max rejected
    public void TryTake_rejects_bad_arguments()
    {
        var h = Harness.Make();

        Assert.Throws<ArgumentNullException>(() => h.Producer.TryTake(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Producer.TryTake(Wood, -1));
    }

    // ---- 构造与注册 -------------------------------------------------------

    [Fact] // legal — the energy demand of the anchor harvester is the tier-1 law value
    public void Constructor_registers_the_law_energy_demand()
    {
        var h = Harness.Make(seats: 2);

        Assert.Equal(2.0, h.Producer.EnergyCostPerSecond, 9); // 1.0/坐席 ×2 坐席（[NEW-T-034] 基准）
        Assert.Equal(2.0, h.Ledger.TotalDemandPerSecond, 9); // 账本侧只挂本生产者（harness 未加额外消费者）
    }

    [Theory] // illegal — bad shape rejected (seats/tier bands, null wiring)
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_bad_seats(int seats)
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Producer(new EntityId(1), Wood, seats, 1, ledger, new EnergyNodeId(2)));
    }

    [Theory] // illegal — tier outside [1,3]
    [InlineData(0)]
    [InlineData(4)]
    public void Constructor_rejects_bad_tier(int tier)
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Producer(new EntityId(1), Wood, 1, tier, ledger, new EnergyNodeId(2)));
    }

    [Fact] // illegal — null arguments rejected
    public void Constructor_rejects_null_arguments()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentNullException>(
            () => new Producer(null!, Wood, 1, 1, ledger, new EnergyNodeId(2)));
        Assert.Throws<ArgumentNullException>(
            () => new Producer(new EntityId(1), null!, 1, 1, ledger, new EnergyNodeId(2)));
        Assert.Throws<ArgumentNullException>(
            () => new Producer(new EntityId(1), Wood, 1, 1, null!, new EnergyNodeId(2)));
        Assert.Throws<ArgumentNullException>(
            () => new Producer(new EntityId(1), Wood, 1, 1, ledger, null!));
    }

    [Fact] // illegal — a duplicate energy node id surfaces the ledger's one-id-one-node rule
    public void Constructor_passes_through_duplicate_node_rejection()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        var node = new EnergyNodeId(5);
        _ = new Producer(new EntityId(1), Wood, 1, 1, ledger, node);

        Assert.Throws<InvalidOperationException>(
            () => new Producer(new EntityId(2), Stone, 1, 1, ledger, node));
    }
}
