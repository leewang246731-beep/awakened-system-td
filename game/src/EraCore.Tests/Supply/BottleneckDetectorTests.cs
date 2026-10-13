using EraCore.Contracts.Foundation;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Supply;

/// <summary>
/// Unit tests of the bottleneck detector (T-048, F-046): the
/// consecutive-under-threshold streak arming, one-event-per-episode latch,
/// the structured event payload (塔 id/位置/当前效率/建议), the suggestion
/// fallbacks (最短路径上的断点格 → 最低库存中转站 → None), the synchronous
/// callback seam and the replay hash. Efficiencies come from a REAL
/// <see cref="TowerSupplyMonitor"/> over a scripted delivery stream — the
/// detector is never fed canned numbers (CS-3.4).
/// </summary>
// [C-06][F-046] 瓶颈事件：阈值连击/单事件每回合/载荷/建议/回调
public class BottleneckDetectorTests
{
    private static readonly EntityId Tower = new(1);

    private static readonly CellCoord Position = new(4, 2);

    /// <summary>Scenario bundle: monitor (reload 3 ⇒ demand 10 发/s) + detector + clock.</summary>
    private sealed class Harness
    {
        public readonly SteppedClock Clock = new();
        public readonly DeliveryCounter Counter = new();
        public readonly TowerSupplyMonitor Monitor;
        public readonly BottleneckDetector Detector;

        public Harness(double threshold = 0.7, int consecutiveTicks = 60)
        {
            Monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
            Monitor.RegisterTower(new TowerSupplyRegistration(
                Tower, Position, ReloadTicks: 3, AmmoPerShot: 1, DamagePerShot: 10.0,
                Counter.AsFunc()));
            Detector = new BottleneckDetector(Clock, Monitor, threshold, consecutiveTicks);
        }

        /// <summary>Advances one tick; the delivery counter grows by the scheduled shots first.</summary>
        public void Step(int shotsThisTick = 0)
        {
            Clock.Advance();
            if (shotsThisTick > 0)
            {
                Counter.Add(shotsThisTick);
            }

            Monitor.Tick();
            Detector.Tick();
        }

        public void Drive(int ticks, int shotsEveryNTicks)
        {
            for (long tick = 1; tick <= ticks; tick++)
            {
                Step(tick % shotsEveryNTicks == 0 ? 1 : 0);
            }
        }
    }

    private sealed class DeliveryCounter
    {
        private long _shots;

        public void Add(int shots) => _shots += shots;

        public Func<long> AsFunc() => () => _shots;
    }

    [Fact] // legal — 60 consecutive under-threshold ticks arm exactly one event
    public void Consecutive_under_threshold_ticks_arm_one_event()
    {
        // [F-046] full stream (eff 1) 200 ticks, then a hard cut: the window
        // drains, efficiency falls under 0.7, the streak counts to 60 and
        // ONE event fires — never more while the cut lasts
        var h = new Harness();
        h.Drive(200, shotsEveryNTicks: 3); // eff 1
        Assert.Empty(h.Detector.Events);

        h.Drive(500, shotsEveryNTicks: 1_000); // cut: no deliveries at all
        Assert.Single(h.Detector.Events);

        SupplyBottleneckEvent evt = h.Detector.Events[0];
        Assert.Equal(1L, evt.Seq);
        Assert.True(evt.Efficiency < 0.7, $"event efficiency {evt.Efficiency} must be under the threshold");
        Assert.Equal(0.7, evt.Threshold, 9);
        Assert.Equal(Tower, evt.TowerId);
        Assert.Equal(Position, evt.TowerPosition);
        Assert.Equal(10.0, evt.DemandShotsPerSecond, 9); // reload 3, 1 shot/firing
        Assert.True(evt.DeliveryShotsPerSecond < 7.0, "delivery rate must have collapsed with the cut");
    }

    [Fact] // legal — the event fires exactly on the 60th streak tick
    public void Event_fires_on_the_sixtieth_consecutive_under_threshold_tick()
    {
        // [F-046] mirror-count in the test: count consecutive ticks the
        // monitor reads < 0.7 — the event tick must equal the 60th one
        var h = new Harness();
        h.Drive(200, shotsEveryNTicks: 3);

        long expectedTick = -1;
        int streak = 0;
        for (long tick = 201; tick <= 800 && expectedTick < 0; tick++)
        {
            h.Step();
            streak = h.Monitor.EfficiencyOf(Tower) < 0.7 ? streak + 1 : 0;
            if (streak == 60)
            {
                expectedTick = tick;
            }
        }

        Assert.True(expectedTick > 0, "efficiency never armed the detector within the window");
        Assert.Single(h.Detector.Events);
        Assert.Equal(expectedTick, h.Detector.Events[0].Tick);
    }

    [Fact] // legal — the no-alarm band sits above the threshold
    public void Healthy_and_mildly_thinned_streams_never_alarm()
    {
        // [F-046] band edges around the 0.7 default: eff 1.0 (period 3) and
        // eff 0.75 (period 4 ⇒ window 22.5/30) stay quiet for 400 ticks,
        // while eff 0.3 (period 10 ⇒ 9/30) is already a bottleneck
        var full = new Harness();
        full.Drive(400, shotsEveryNTicks: 3);
        Assert.Empty(full.Detector.Events);

        var mildOk = new Harness();
        mildOk.Drive(400, shotsEveryNTicks: 4); // 7.5/10 = 0.75 > 0.7
        Assert.Empty(mildOk.Detector.Events);

        var thin = new Harness();
        thin.Drive(400, shotsEveryNTicks: 10); // 3/10 = 0.3 < 0.7
        Assert.NotEmpty(thin.Detector.Events); // boundary witness for the band edge
    }

    [Fact] // legal — graded 0.2 stream alarms with the curve value in the payload
    public void Graded_zero_point_two_stream_alarms_with_curve_efficiency()
    {
        // [F-046] 断供 0.2 stream (2 发/s vs 10): after the window settles the
        // efficiency reads exactly 0.2 < 0.7 ⇒ the event carries ~0.2
        var h = new Harness();
        h.Drive(300, shotsEveryNTicks: 15);

        Assert.Single(h.Detector.Events);
        Assert.Equal(0.2, h.Detector.Events[0].Efficiency, 2);
    }

    [Fact] // legal — one event per episode: recovery re-arms the detector
    public void Recovery_rearms_and_a_second_cut_fires_a_second_event()
    {
        // [F-046] episode latch: cut #1 → event; restore to full → latch
        // clears (no event at recovery); cut #2 → a second event with seq 2
        var h = new Harness();
        h.Drive(200, shotsEveryNTicks: 3);
        h.Drive(300, shotsEveryNTicks: 1_000); // cut #1
        Assert.Single(h.Detector.Events);

        h.Drive(300, shotsEveryNTicks: 3); // full recovery — no new event
        Assert.Single(h.Detector.Events);

        h.Drive(300, shotsEveryNTicks: 1_000); // cut #2
        Assert.Equal(2, h.Detector.Events.Count);
        Assert.Equal(2L, h.Detector.Events[1].Seq);
        Assert.True(h.Detector.Events[1].Tick > h.Detector.Events[0].Tick);
    }

    [Fact] // legal — suggestion: the reported route break cell wins
    public void Suggestion_prefers_the_route_break_cell()
    {
        // [F-046] 建议=最短路径上的断点格: NotifyRouteBlocked feeds the wall
        // cell; the event payload carries it verbatim
        var h = new Harness();
        h.Monitor.RegisterRelay(new EntityId(9), new CellCoord(8, 8), () => 0);
        h.Drive(200, shotsEveryNTicks: 3);
        h.Monitor.NotifyRouteBlocked(Tower, new CellCoord(9, 2));
        h.Drive(300, shotsEveryNTicks: 1_000);

        Assert.Single(h.Detector.Events);
        SupplyBottleneckEvent evt = h.Detector.Events[0];
        Assert.Equal(SupplyBottleneckSuggestionKind.RouteBreakCell, evt.SuggestionKind);
        Assert.Equal(new CellCoord(9, 2), evt.SuggestedCell);
        Assert.Null(evt.SuggestedRelayId);
        Assert.Null(evt.SuggestedRelayStock);
    }

    [Fact] // legal — suggestion: without a break cell the lowest-stock relay is named
    public void Suggestion_falls_back_to_the_lowest_stock_relay()
    {
        // [F-046] 建议=最低库存中转站: no break reported, two relays — the
        // emptier one (the converter hopper, stock 1) is the suggestion
        var h = new Harness();
        h.Monitor.RegisterRelay(new EntityId(31), new CellCoord(7, 2), () => 1);
        h.Monitor.RegisterRelay(new EntityId(32), new CellCoord(2, 2), () => 40);
        h.Drive(200, shotsEveryNTicks: 3);
        h.Drive(300, shotsEveryNTicks: 1_000);

        Assert.Single(h.Detector.Events);
        SupplyBottleneckEvent evt = h.Detector.Events[0];
        Assert.Equal(SupplyBottleneckSuggestionKind.LowestStockRelay, evt.SuggestionKind);
        Assert.Equal(new EntityId(31), evt.SuggestedRelayId);
        Assert.Equal(new CellCoord(7, 2), evt.SuggestedCell);
        Assert.Equal(1L, evt.SuggestedRelayStock);
    }

    [Fact] // legal — suggestion: nothing registered ⇒ None kind with null cells
    public void Suggestion_is_none_without_break_or_relays()
    {
        // [F-046] suggestion fallback exhaustion
        var h = new Harness();
        h.Drive(200, shotsEveryNTicks: 3);
        h.Drive(300, shotsEveryNTicks: 1_000);

        Assert.Single(h.Detector.Events);
        SupplyBottleneckEvent evt = h.Detector.Events[0];
        Assert.Equal(SupplyBottleneckSuggestionKind.None, evt.SuggestionKind);
        Assert.Null(evt.SuggestedCell);
        Assert.Null(evt.SuggestedRelayId);
    }

    [Fact] // legal — the callback seam fires inline with the logged record
    public void Callback_receives_the_same_records_synchronously()
    {
        // [F-046] observability callback seam
        var seen = new List<SupplyBottleneckEvent>();
        var h = new Harness();
        h.Detector.BottleneckDetected += seen.Add;

        h.Drive(200, shotsEveryNTicks: 3);
        h.Drive(300, shotsEveryNTicks: 1_000);
        h.Drive(300, shotsEveryNTicks: 3);
        h.Drive(300, shotsEveryNTicks: 1_000);

        Assert.Equal(2, seen.Count);
        Assert.Equal(h.Detector.Events, seen); // same records, same order
        Assert.Equal(1L, seen[0].Seq);
        Assert.Equal(2L, seen[1].Seq);
    }

    [Fact] // legal — custom streak and threshold configurations arm proportionally
    public void Custom_streak_and_threshold_are_honoured()
    {
        // [F-046] threshold 0.5, streak 5: the 0.75 stream never arms; the
        // 0.4 stream (period 7.5 → use 8 ⇒ 3.75/10 = 0.375) arms fast
        var h = new Harness(threshold: 0.5, consecutiveTicks: 5);
        h.Drive(200, shotsEveryNTicks: 4); // eff 0.75 > 0.5 — quiet
        Assert.Empty(h.Detector.Events);

        h.Drive(200, shotsEveryNTicks: 8); // eff 0.375 < 0.5 — armed
        Assert.Single(h.Detector.Events);
        Assert.Equal(0.5, h.Detector.Events[0].Threshold, 9);
    }

    [Fact] // legal — replay hash follows the event log
    public void State_hash_tracks_emissions()
    {
        // [F-046] detector replay hash
        long RunOnce()
        {
            var h = new Harness();
            h.Drive(200, shotsEveryNTicks: 3);
            h.Drive(300, shotsEveryNTicks: 1_000);
            return h.Detector.ComputeStateHash();
        }

        long a = RunOnce();
        long b = RunOnce();
        Assert.Equal(a, b);

        var quiet = new Harness();
        quiet.Drive(500, shotsEveryNTicks: 3);
        Assert.NotEqual(a, quiet.Detector.ComputeStateHash());
    }

    [Theory] // illegal — constructor bounds
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Out_of_band_threshold_is_rejected(double threshold)
    {
        // [F-046] threshold bounds
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BottleneckDetector(new SteppedClock(), new TowerSupplyMonitor(), threshold));
    }

    [Fact] // illegal — zero-streak configuration
    public void Zero_consecutive_ticks_is_rejected()
    {
        // [F-046] streak bounds
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BottleneckDetector(new SteppedClock(), new TowerSupplyMonitor(), 0.7, consecutiveTicks: 0));
    }
}
