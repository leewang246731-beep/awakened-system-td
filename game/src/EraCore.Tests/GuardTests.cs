using EraCore;
using Xunit;

namespace EraCore.Tests;

public class GuardClampTests
{
    [Theory] // legal
    [InlineData(5, 0, 10, 5)]
    [InlineData(-3, -10, 10, -3)]
    public void Clamp_returns_value_when_in_range(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, Guard.Clamp(value, min, max));
    }

    [Theory] // boundary
    [InlineData(0, 0, 10, 0)]
    [InlineData(10, 0, 10, 10)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MinValue)]
    [InlineData(int.MaxValue, int.MinValue, int.MaxValue, int.MaxValue)]
    public void Clamp_keeps_exact_boundaries(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, Guard.Clamp(value, min, max));
    }

    [Theory] // illegal input, legal clamping semantics
    [InlineData(-1, 0, 10, 0)]
    [InlineData(11, 0, 10, 10)]
    [InlineData(int.MinValue, 0, 10, 0)]
    [InlineData(int.MaxValue, 0, 10, 10)]
    public void Clamp_pulls_out_of_range_back_to_bounds(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, Guard.Clamp(value, min, max));
    }

    [Theory] // illegal range definition
    [InlineData(5, 10, 0)]
    public void Clamp_rejects_inverted_range(int value, int min, int max)
    {
        Assert.ThrowsAny<ArgumentException>(() => Guard.Clamp(value, min, max));
    }
}

public class GuardEnsureInRangeTests
{
    [Theory] // legal
    [InlineData(5, 0, 10, 5)]
    [InlineData(-7, -10, 10, -7)]
    public void EnsureInRange_returns_value_when_in_range(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, Guard.EnsureInRange(value, min, max));
    }

    [Theory] // boundary
    [InlineData(0, 0, 10, 0)]
    [InlineData(10, 0, 10, 10)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MinValue)]
    [InlineData(int.MaxValue, int.MinValue, int.MaxValue, int.MaxValue)]
    public void EnsureInRange_accepts_exact_boundaries(int value, int min, int max, int expected)
    {
        Assert.Equal(expected, Guard.EnsureInRange(value, min, max));
    }

    [Theory] // illegal value -> ArgumentException (ArgumentOutOfRangeException derives from it)
    [InlineData(-1, 0, 10)]
    [InlineData(11, 0, 10)]
    [InlineData(int.MinValue, 0, 10)]
    [InlineData(int.MaxValue, 0, 10)]
    public void EnsureInRange_throws_ArgumentException_when_out_of_range(int value, int min, int max)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => Guard.EnsureInRange(value, min, max));
        Assert.Contains("inclusive range", ex.Message);
    }

    [Theory] // illegal range definition
    [InlineData(5, 10, 0)]
    public void EnsureInRange_rejects_inverted_range(int value, int min, int max)
    {
        Assert.ThrowsAny<ArgumentException>(() => Guard.EnsureInRange(value, min, max));
    }
}
