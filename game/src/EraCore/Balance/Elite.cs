namespace EraCore.Balance;

/// <summary>
/// Elite line (v3.2 §6): elite count is independent of the regular wave
/// formula — 1 + ⌊(波−8)/4⌋ with the elite count multiplier pinned at 1.
/// Elites first appear at wave 8; the floor keeps earlier waves at 0.
/// Pure static formula, stateless.
/// </summary>
public static class Elite
{
    /// <summary>
    /// Elite count of one wave; negative waves return the guard value 0.
    /// The result is clamped at 0 from below (waves 1..7 floor to −2..−1
    /// before the clamp).
    /// </summary>
    public static int Count(int wave)
    {
        if (wave < 0)
        {
            return 0;
        }

        return Math.Max(0, 1 + IntMath.FloorDiv(wave - 8, 4));
    }
}
