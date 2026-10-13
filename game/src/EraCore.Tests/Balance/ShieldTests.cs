using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// [F-049] shield line (C-19 前体): 10+8×wave from startWave on, 0 before —
// 护盾 E3 起 (v3.2 §6), so E1/E2 tables stay at 0.
public class ShieldTests
{
    [Theory] // legal — 10+8×wave once the era has shields
    [InlineData(0, 0, 10)] // earliest possible: start at once, 10+0
    [InlineData(6, 6, 58)] // first shielded wave: 10+8×6
    [InlineData(10, 1, 90)] // 10+8×10
    [InlineData(31, 31, 258)] // E3-style first wave: 10+8×31
    public void At_grows_eight_per_wave(int wave, int startWave, int expected)
    {
        Assert.Equal(expected, Shield.At(wave, startWave));
    }

    [Theory] // legal — waves before startWave carry no shield at all
    [InlineData(5, 6)]
    [InlineData(0, 1)]
    [InlineData(29, 31)] // E2 golden table max wave: shield is E3 起
    [InlineData(30, 31)]
    public void At_is_zero_before_start_wave(int wave, int startWave)
    {
        Assert.Equal(0, Shield.At(wave, startWave));
    }

    [Theory] // illegal — negative start wave is rejected
    [InlineData(-1)]
    [InlineData(-100)]
    public void At_rejects_negative_start_wave(int startWave)
    {
        Assert.Throws<ArgumentException>(() => Shield.At(10, startWave));
    }

    [Theory] // illegal — negative waves return the guard value 0
    [InlineData(-1)]
    [InlineData(-10)]
    public void At_returns_zero_for_negative_waves(int wave)
    {
        Assert.Equal(0, Shield.At(wave, 0));
    }
}
