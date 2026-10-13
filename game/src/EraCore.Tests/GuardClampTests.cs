using EraCore;
using Xunit;

namespace EraCore.Tests;

// [C-01] K-1 领域原语共享校验助手（合法/边界/非法三行齐备）
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
