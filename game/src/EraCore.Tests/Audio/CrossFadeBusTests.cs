using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the equal-power crossfade state machine (T-092, F-135 三态淡入
/// 淡出): envelope math (cos/sin quarter-circle, from² + to² = 1), transition
/// lifecycle, no-op and re-target semantics, and the guards.
/// </summary>
// [C-25][F-135] 三态等功率淡入淡出状态机
public class CrossFadeBusTests
{
    private const int FadeTicks = 60;

    // ---------------------------------------------------------------- legal

    [Fact] // legal — a fresh bus rests at unit gain on its initial state
    public void Fresh_bus_rests_on_initial_state()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);

        Assert.Equal(BgmLayerState.Calm, bus.Current);
        Assert.False(bus.IsFading(0));
        Assert.Equal(1.0, bus.GainOf(BgmLayerState.Calm, 0));
        Assert.Equal(0.0, bus.GainOf(BgmLayerState.Combat, 0));
        Assert.Equal(0.0, bus.GainOf(BgmLayerState.Boss, 0));
    }

    [Fact] // legal — equal-power property holds along the whole fade
    public void Equal_power_holds_along_the_fade()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus.Request(BgmLayerState.Combat, tick: 100);

        for (int t = 100; t <= 100 + FadeTicks; t++)
        {
            CrossFadeGains gains = bus.GainsAt(t);

            Assert.Equal(BgmLayerState.Calm, gains.From);
            Assert.Equal(BgmLayerState.Combat, gains.To);
            Assert.InRange(gains.FromGain, 0.0, 1.0);
            Assert.InRange(gains.ToGain, 0.0, 1.0);
            Assert.Equal(1.0, (gains.FromGain * gains.FromGain) + (gains.ToGain * gains.ToGain), 6); // cos²+sin²=1
        }
    }

    [Fact] // legal — midpoint is the equal-loudness crossing (√½ each)
    public void Midpoint_is_equal_loudness()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus.Request(BgmLayerState.Combat, tick: 0);

        CrossFadeGains mid = bus.GainsAt(FadeTicks / 2);

        Assert.Equal(Math.Sqrt(0.5), mid.FromGain, 3);
        Assert.Equal(Math.Sqrt(0.5), mid.ToGain, 3);
    }

    [Fact] // legal — the fade completes and the target becomes current
    public void Fade_completes()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        Assert.True(bus.Request(BgmLayerState.Boss, tick: 10));

        Assert.True(bus.IsFading(10 + FadeTicks - 1)); // last in-window tick
        Assert.False(bus.IsFading(10 + FadeTicks));    // completion tick
        CrossFadeGains end = bus.GainsAt(10 + FadeTicks);

        Assert.Equal(BgmLayerState.Boss, bus.Current);
        Assert.Equal(0.0, end.FromGain, 9);
        Assert.Equal(1.0, end.ToGain, 9);
    }

    [Fact] // legal — a same-target request is a no-op even mid-fade
    public void Same_target_request_is_noop()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus.Request(BgmLayerState.Combat, tick: 0);

        Assert.False(bus.Request(BgmLayerState.Combat, tick: 10));
        Assert.True(bus.IsFading(10)); // original fade untouched
        Assert.Equal(BgmLayerState.Calm, bus.GainsAt(10).From);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — re-target mid-fade restarts from the DOMINANT state
    public void Retarget_midfade_restarts_from_dominant()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus.Request(BgmLayerState.Combat, tick: 0);

        // at 25% progress calm is still dominant → the new fade fades CALM out
        Assert.True(bus.Request(BgmLayerState.Boss, tick: 15));
        CrossFadeGains restarted = bus.GainsAt(15);

        Assert.Equal(BgmLayerState.Calm, restarted.From);
        Assert.Equal(BgmLayerState.Boss, restarted.To);
        Assert.Equal(1.0, restarted.FromGain, 9);
        Assert.Equal(0.0, restarted.ToGain, 9);

        // past the midpoint the destination is dominant → re-target fades COMBAT out
        var bus2 = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus2.Request(BgmLayerState.Combat, tick: 0);
        Assert.True(bus2.Request(BgmLayerState.Boss, tick: 45));
        Assert.Equal(BgmLayerState.Combat, bus2.GainsAt(45).From);
    }

    [Fact] // boundary — a tick before the start (clock skew) clamps to progress 0
    public void Skewed_tick_clamps_to_start()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        _ = bus.Request(BgmLayerState.Combat, tick: 100);

        CrossFadeGains gains = bus.GainsAt(50); // before the fade window

        Assert.Equal(1.0, gains.FromGain, 9);
        Assert.Equal(0.0, gains.ToGain, 9);
    }

    [Fact] // boundary — a one-tick fade window is legal (fastest possible morph)
    public void One_tick_window_works()
    {
        var bus = new CrossFadeBus(BgmLayerState.Calm, 1);
        _ = bus.Request(BgmLayerState.Boss, tick: 5);

        Assert.False(bus.IsFading(6));
        Assert.Equal(1.0, bus.GainOf(BgmLayerState.Boss, 6));
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — non-positive fade windows are rejected
    [InlineData(0)]
    [InlineData(-1)]
    public void Illegal_window_rejected(int fadeTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CrossFadeBus(BgmLayerState.Calm, fadeTicks));
    }

    [Theory] // illegal — undefined states are rejected on construction and request
    [InlineData(99)]
    [InlineData(-1)]
    public void Illegal_state_rejected(int raw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CrossFadeBus((BgmLayerState)raw, FadeTicks));

        var bus = new CrossFadeBus(BgmLayerState.Calm, FadeTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.Request((BgmLayerState)raw, 0));
    }
}
