namespace EraCore.Balance;

/// <summary>
/// Wave count formula (v3.2 §6, 07 correction #26 bracketization):
/// max(0, min(40, (基数 + max(0, ⌊(波−起)/间隔⌋)) ÷ max(成长, 0.1))) × 数量乘子,
/// floored — add first, then divide, then clamp 0..40, then multiply by the
/// threat count multiplier, floor last. Pure static formula, stateless;
/// parameters come from data/waves/{era}.json at the call site (RL7).
/// </summary>
public static class WaveFormula
{
    /// <summary>
    /// Enemy count of one wave. Guards (T-036 task card): negative waves
    /// return the guard value 0; <paramref name="spacing"/> &lt;= 0 disables
    /// the ramp (increment 0) instead of dividing by zero;
    /// <paramref name="scaling"/> below 0.1 is lifted to 0.1 by the formula
    /// itself; a negative threat multiplier yields 0 (a wave never spawns
    /// negative enemies).
    /// </summary>
    /// <param name="wave">Wave number, 1-based; negative returns 0.</param>
    /// <param name="baseCount">基数 (base count) from the era wave table.</param>
    /// <param name="beginWave">起 (ramp start wave).</param>
    /// <param name="spacing">间隔 (waves per ramp step); &lt;= 0 → no ramp.</param>
    /// <param name="scaling">成长 (growth divisor), floored at 0.1.</param>
    /// <param name="threatMultiplier">数量乘子 from <see cref="Threat.Multipliers"/>.</param>
    public static int Count(
        int wave,
        int baseCount,
        int beginWave,
        int spacing,
        double scaling,
        double threatMultiplier)
    {
        if (wave < 0 || threatMultiplier < 0)
        {
            return 0;
        }

        int increment = spacing > 0
            ? Math.Max(0, IntMath.FloorDiv(wave - beginWave, spacing))
            : 0;

        double ramped = (baseCount + increment) / Math.Max(scaling, 0.1);
        double capped = Math.Clamp(ramped, 0.0, 40.0);
        return (int)Math.Floor(capped * threatMultiplier);
    }
}

/// <summary>
/// Integer math companions shared by the balance formulas (secondary type of
/// WaveFormula.cs). Floor division matches the ⌊⌋ notation of the frozen
/// formulas — it differs from C# <c>/</c> truncation for negative operands
/// (e.g. Elite.Count(7): ⌊−1/4⌋ = −1, not 0).
/// </summary>
internal static class IntMath
{
    /// <summary>Floor(a/b) for b &gt; 0.</summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
    }
}
