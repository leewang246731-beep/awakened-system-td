using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// [F-048] win waves / checkpoint / wave interval: v3.2 §7+§6 with the three
// SP-9 代入示例 as frozen anchors. Float tolerance 1e-9 except where noted —
// expected values are decimal literals of frozen formula outputs.
public class CampaignTests
{
    [Theory] // legal — SP-9 代入示例 anchors for 胜波 10+⌊t×10⌋
    [InlineData(0.3, 13)] // 孤关
    [InlineData(0.9, 19)] // 双面受敌
    [InlineData(1.2, 22)] // 围城
    public void WinWaves_matches_SP9_examples(double t, int expected)
    {
        Assert.Equal(expected, Campaign.WinWaves(t));
    }

    [Theory] // legal + boundary — interior points and the band edges
    [InlineData(0.75, 17)] // 10+⌊7.5⌋: F.5 benchmark threat
    [InlineData(0.31, 13)] // 10+⌊3.1⌋
    [InlineData(0.39, 13)] // 10+⌊3.9⌋ (floor boundary: 3.9 floors to 3)
    public void WinWaves_floors_threat_times_ten(double t, int expected)
    {
        Assert.Equal(expected, Campaign.WinWaves(t));
    }

    [Theory] // illegal — negative threat rejected
    [InlineData(-0.1)]
    [InlineData(-2.0)]
    public void WinWaves_rejects_negative_threat(double t)
    {
        Assert.Throws<ArgumentException>(() => Campaign.WinWaves(t));
    }

    [Theory] // legal — checkpoint max(10, ⌊winWaves×0.75⌋)
    [InlineData(13, 10)] // ⌊9.75⌋=9 → floor lifts to 10 (SP-9 孤关 restart)
    [InlineData(19, 14)] // ⌊14.25⌋=14 (SP-9 双面受敌)
    [InlineData(22, 16)] // ⌊16.5⌋=16 (SP-9 围城)
    [InlineData(40, 30)] // ⌊30⌋=30
    [InlineData(11, 10)] // ⌊8.25⌋=8 → 10
    public void Checkpoint_keeps_three_quarters_with_floor_ten(int winWaves, int expected)
    {
        Assert.Equal(expected, Campaign.Checkpoint(winWaves));
    }

    [Fact] // boundary — the floor of 10 turns on exactly when ⌊winWaves×0.75⌋ reaches 10
    public void Checkpoint_floor_boundary()
    {
        Assert.Equal(10, Campaign.Checkpoint(13)); // ⌊9.75⌋ = 9 < 10 → 10
        Assert.Equal(10, Campaign.Checkpoint(14)); // ⌊10.5⌋ = 10 → 10 (max equal)
        Assert.Equal(11, Campaign.Checkpoint(15)); // ⌊11.25⌋ = 11 > 10 → 11
    }

    [Theory] // illegal — negative win waves rejected
    [InlineData(-1)]
    [InlineData(-30)]
    public void Checkpoint_rejects_negative_win_waves(int winWaves)
    {
        Assert.Throws<ArgumentException>(() => Campaign.Checkpoint(winWaves));
    }

    [Theory] // legal — interval 120s × interval multiplier (band 72~240 s, v3.2 §6)
    [InlineData(0.3, 240.0)] // 120×2.0
    [InlineData(0.75, 156.0)] // 120×1.3: F.5 benchmark threat
    [InlineData(1.2, 72.0)] // 120×0.6
    public void WaveIntervalSeconds_spans_the_threat_band(double t, double expected)
    {
        Assert.Equal(expected, Campaign.WaveIntervalSeconds(t), precision: 3);
    }

    [Theory] // boundary — clamp 外 threat: interval multiplier clamps to [0.5, 2]
    [InlineData(0.0, 240.0)] // interval mult pinned at 2.0
    [InlineData(2.0, 60.0)] // interval mult pinned at 0.5
    public void WaveIntervalSeconds_never_leaves_sixty_to_two_forty(double t, double expected)
    {
        Assert.Equal(expected, Campaign.WaveIntervalSeconds(t), precision: 3);
    }

    [Theory] // illegal — negative threat rejected (shared guard with Threat.Multipliers)
    [InlineData(-0.1)]
    public void WaveIntervalSeconds_rejects_negative_threat(double t)
    {
        Assert.Throws<ArgumentException>(() => Campaign.WaveIntervalSeconds(t));
    }
}
