namespace EraCore;

/// <summary>
/// Reusable pure validation/clamp helpers shared by the whole project.
/// Pure functions only: no I/O, no engine types, no hidden state (D-01).
/// </summary>
public static class Guard
{
    /// <summary>
    /// Clamps <paramref name="value"/> into the inclusive range [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static int Clamp(int value, int min, int max)
    {
        if (min > max)
        {
            throw new ArgumentException($"min ({min}) must not be greater than max ({max}).", nameof(min));
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    /// <summary>
    /// Returns <paramref name="value"/> when it lies inside the inclusive range
    /// [<paramref name="min"/>, <paramref name="max"/>]; otherwise throws
    /// <see cref="ArgumentException"/> (as <see cref="ArgumentOutOfRangeException"/>).
    /// </summary>
    public static int EnsureInRange(int value, int min, int max, string paramName = "value")
    {
        if (min > max)
        {
            throw new ArgumentException($"min ({min}) must not be greater than max ({max}).", nameof(min));
        }

        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"Value must be in the inclusive range [{min}, {max}].");
        }

        return value;
    }
}
