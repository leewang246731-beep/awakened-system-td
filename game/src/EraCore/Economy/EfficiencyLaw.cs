namespace EraCore.Economy;

/// <summary>
/// The §5.3 efficiency law of the production chain (C-06, T-034, F-021;
/// design v3.2 §5 L75): <c>效率 = min(供给率)</c> — a consumer runs at the
/// minimum of ALL its supply rates, and a dimension with no demand reads
/// 1 (需求≤0 恒 1). E1 thin composes exactly two dimensions:
/// <list type="bullet">
/// <item>supply rate of the input resources (F-020 input side),</item>
/// <item>energy satisfaction from <see cref="Energy.EnergyLedger.SatisfactionOf"/>
/// (C-08 seam — the ledger already applies 需求≤0 恒 1 per node).</item>
/// </list>
/// Pure function over two finite rates; the result is clamped to [0,1] so
/// over-supplied dimensions cannot leak above 1 (same clamp discipline as
/// the ledger's pool satisfaction). Rate-based proportional supply (断供
/// 按比例降速) lands with T-048; the E1 converter feeds binary supply
/// (inputs in the hopper = 1, starved = 0) through this same function.
/// </summary>
public static class EfficiencyLaw
{
    /// <summary>
    /// The min composition with the [0,1] clamp.
    /// <c>min(supplyRate, energySatisfaction)</c> — both arguments are
    /// themselves rates in [0,1]-ish; clamping keeps the contract total
    /// even when a caller feeds an over-supplied dimension.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// either rate is NaN or infinite — a non-finite rate is a wiring bug,
    /// not a slowdown.
    /// </exception>
    public static double Min(double supplyRate, double energySatisfaction)
    {
        if (!double.IsFinite(supplyRate))
        {
            throw new ArgumentException($"Supply rate must be finite, got {supplyRate}.", nameof(supplyRate));
        }

        if (!double.IsFinite(energySatisfaction))
        {
            throw new ArgumentException(
                $"Energy satisfaction must be finite, got {energySatisfaction}.", nameof(energySatisfaction));
        }

        return Math.Clamp(Math.Min(supplyRate, energySatisfaction), 0.0, 1.0);
    }
}
