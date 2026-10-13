using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// [F-049] elite line (C-19 前体): 1+⌊(波−8)/4⌋, multiplier pinned at 1 (v3.2 §6).
public class EliteTests
{
    [Theory] // legal — elite ladder
    [InlineData(8, 1)] // 1+⌊0/4⌋ = 1: first elite wave
    [InlineData(11, 1)] // 1+⌊3/4⌋ = 1
    [InlineData(12, 2)] // 1+⌊4/4⌋ = 2
    [InlineData(20, 4)] // 1+⌊12/4⌋ = 4: F.5 wave-20 anchor (4 攻城锤)
    [InlineData(30, 6)] // 1+⌊22/4⌋ = 6: F.5 wave-30 golden row
    [InlineData(100, 24)] // 1+⌊92/4⌋ = 24: deep-wave growth
    public void Count_follows_elite_ladder(int wave, int expected)
    {
        Assert.Equal(expected, Elite.Count(wave));
    }

    [Theory] // boundary — before wave 8 the floor goes negative and clamps to 0
    [InlineData(7, 0)] // 1+⌊−1/4⌋ = 1−1 = 0 (floor, not C# trunc: −1 ≠ 0)
    [InlineData(5, 0)] // 1+⌊−3/4⌋ = 1−1 = 0
    [InlineData(1, 0)] // 1+⌊−7/4⌋ = 1−2 = −1 → clamped to 0
    [InlineData(0, 0)]
    public void Count_is_zero_before_wave_eight(int wave, int expected)
    {
        Assert.Equal(expected, Elite.Count(wave));
    }

    [Theory] // illegal — negative waves return the guard value 0
    [InlineData(-1)]
    [InlineData(-50)]
    public void Count_returns_zero_for_negative_waves(int wave)
    {
        Assert.Equal(0, Elite.Count(wave));
    }
}
