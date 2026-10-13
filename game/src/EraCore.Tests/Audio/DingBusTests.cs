using EraCore.Audio;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Table-driven tests of the ding dispatcher (T-060, C-25, F-112): the
/// six-event-class → three-tier map of v3.2 §10.3, the per-class 2 s
/// throttle in the K-1 tick domain, and the ding channel's independent
/// volume/mute switch.
/// </summary>
// [C-25][F-112] "叮"三级：事件类→三级映射 + 同类 2 s 节流 + 独立音量/静音
public class DingBusTests
{
    // ---- legal — the §10.3 mapping table ------------------------------------

    [Theory] // legal — all six event classes map to their §10.3 tier
    [InlineData(DingTrigger.WavePreview, DingTier.Info)]         // 情报: 波次预告
    [InlineData(DingTrigger.SystemReport, DingTier.Info)]        // 情报: 系统汇报
    [InlineData(DingTrigger.ResearchComplete, DingTier.Reward)]  // 奖励: 研究完成
    [InlineData(DingTrigger.ChestOpened, DingTier.Reward)]       // 奖励: 宝箱
    [InlineData(DingTrigger.DefenseLineLow, DingTier.Crisis)]    // 危机: 防线<40%
    [InlineData(DingTrigger.SupplyBottleneck, DingTier.Crisis)]  // 危机: 断供瓶颈
    public void Event_classes_map_to_tiers(DingTrigger trigger, DingTier expected)
    {
        Assert.Equal(expected, DingBus.TierOf(trigger));
        Assert.Equal(expected, DingBus.TierMap[trigger]);
    }

    [Fact] // legal — the map is total: every defined trigger has a tier, and all 3 tiers are reachable
    public void Map_is_total_over_triggers_and_tiers()
    {
        Assert.Equal(Enum.GetValues<DingTrigger>().Length, DingBus.MappedTriggerCount);
        Assert.Equal(6, DingBus.MappedTriggerCount);
        Assert.Equal(3, DingBus.TierMap.Values.Distinct().Count());
        Assert.All(Enum.GetValues<DingTrigger>(), t => Assert.True(DingBus.TierMap.ContainsKey(t)));
    }

    [Fact] // legal — the T-060 acceptance scenario: one event per class plays, a same-class repeat inside the window is intercepted
    public void Three_classes_play_once_and_repeat_is_throttled()
    {
        DingBus bus = new();
        List<DingCue> cues = new();
        bus.CueRaised += cues.Add;

        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.WavePreview, 0));      // 情报"叮"
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.ResearchComplete, 0)); // 奖励"叮叮"
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.DefenseLineLow, 0));   // 危机"叮——警告！"
        Assert.Equal(DingOutcome.Throttled, bus.Request(DingTrigger.WavePreview, 1));   // same class inside 2 s

        Assert.Equal(3, cues.Count);
        Assert.Equal(3, cues.Select(c => c.Tier).Distinct().Count());
        Assert.Equal(1, bus.ThrottledCount);

        // cue payload: class + tier + tick + volume snapshot
        Assert.Equal(DingTrigger.WavePreview, cues[0].Trigger);
        Assert.Equal(DingTier.Info, cues[0].Tier);
        Assert.Equal(0, cues[0].Tick);
        Assert.Equal(1.0, cues[0].Volume);
    }

    [Fact] // legal — different classes sharing a tier are NOT throttled against each other
    public void Different_classes_in_one_window_both_play()
    {
        DingBus bus = new();
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.WavePreview, 0));
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.SystemReport, 1)); // same Info tier, different class
        Assert.Equal(0, bus.ThrottledCount);
    }

    [Fact] // legal — a later window admits the same class again (episode latch, not a kill switch)
    public void Same_class_plays_again_after_the_window()
    {
        DingBus bus = new();
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.ChestOpened, 100));
        long windowEnd = 100 + DingBus.DefaultThrottleTicks;
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.ChestOpened, windowEnd));
        Assert.Equal(2, bus.Cues.Count);
    }

    // ---- boundary — throttle window edges and defaults -----------------------

    [Theory] // boundary — the window is exclusive: delta 60 ticks (2 s at 30 Hz) plays, 59 does not
    [InlineData(59, DingOutcome.Throttled)]
    [InlineData(60, DingOutcome.Played)]
    public void Throttle_window_edge(long delta, DingOutcome expected)
    {
        DingBus bus = new(); // default window = SimClock.TicksPerSecond * 2
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.ChestOpened, 100));
        Assert.Equal(expected, bus.Request(DingTrigger.ChestOpened, 100 + delta));
    }

    [Fact] // boundary — the default window is exactly 2 s of the frozen 30 Hz domain
    public void Default_window_is_two_seconds_of_tick_domain()
    {
        Assert.Equal(SimClock.TicksPerSecond * 2, DingBus.DefaultThrottleTicks);
        Assert.Equal(2, DingBus.ThrottleSeconds);
    }

    [Fact] // boundary — out-of-order ticks (earlier than the last play) fall inside the window
    public void Out_of_order_tick_is_throttled()
    {
        DingBus bus = new();
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.SystemReport, 50));
        Assert.Equal(DingOutcome.Throttled, bus.Request(DingTrigger.SystemReport, 49));
    }

    // ---- volume / mute (独立音量开关, v3.2 §10.3/§14) -------------------------

    [Theory] // legal — linear gain domain ends, cue carries the snapshot at play time
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Volume_is_carried_by_the_cue(double volume)
    {
        DingBus bus = new() { Volume = volume };
        DingCue? cue = null;
        bus.CueRaised += c => cue = c;

        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.ResearchComplete, 3));

        Assert.NotNull(cue);
        Assert.Equal(volume, cue!.Volume); // setter stores verbatim — exact compare is safe
    }

    [Theory] // illegal — non-finite or out-of-range volume is rejected and the old value survives
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Volume_outside_unit_range_is_rejected(double bad)
    {
        DingBus bus = new() { Volume = 0.25 };

        Assert.Throws<ArgumentOutOfRangeException>(() => bus.Volume = bad);
        Assert.Equal(0.25, bus.Volume);
    }

    [Fact] // legal — muted requests return Muted and never raise a cue
    public void Muted_bus_returns_muted_without_cues()
    {
        DingBus bus = new() { Muted = true };
        bus.CueRaised += _ => Assert.Fail("a muted bus must not raise cues");

        Assert.Equal(DingOutcome.Muted, bus.Request(DingTrigger.SupplyBottleneck, 5));
        Assert.Equal(DingOutcome.Muted, bus.Request(DingTrigger.WavePreview, 6));
        Assert.Empty(bus.Cues);
        Assert.Equal(0, bus.ThrottledCount);
    }

    [Fact] // boundary — a muted request does not open a throttle window
    public void Muted_request_leaves_throttle_state_untouched()
    {
        DingBus bus = new() { Muted = true };
        Assert.Equal(DingOutcome.Muted, bus.Request(DingTrigger.SupplyBottleneck, 5));

        bus.Muted = false;
        Assert.Equal(DingOutcome.Played, bus.Request(DingTrigger.SupplyBottleneck, 6)); // 1 tick later still plays
    }

    // ---- settings snapshot (T-055 settings-panel seam) ------------------------

    [Fact] // legal — snapshot round-trips volume + mute
    public void Settings_round_trip()
    {
        DingBus bus = new();
        bus.ApplySettings(new DingAudioSettings(Volume: 0.25, Muted: true));

        DingAudioSettings snapshot = bus.CurrentSettings;

        Assert.Equal(0.25, snapshot.Volume);
        Assert.True(snapshot.Muted);
    }

    [Fact] // illegal — null snapshot, or a snapshot with an invalid volume, is rejected
    public void Invalid_settings_are_rejected()
    {
        DingBus bus = new();
        Assert.Throws<ArgumentNullException>(() => bus.ApplySettings(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => bus.ApplySettings(new DingAudioSettings(Volume: 2.0, Muted: false)));
    }

    // ---- illegal — construction and mapping guards ----------------------------

    [Theory] // illegal — throttle window below 1 tick is rejected
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_throttle_window_is_rejected(int window)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DingBus(window));
    }

    [Fact] // illegal — an unmapped trigger value is rejected loudly, never silently dropped
    public void Undefined_trigger_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DingBus.TierOf((DingTrigger)99));

        DingBus bus = new();
        Assert.Throws<ArgumentException>(() => bus.Request((DingTrigger)99, 0));
    }
}
