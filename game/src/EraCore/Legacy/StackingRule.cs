namespace EraCore.Legacy;

/// <summary>
/// The single choke point of every cost relief (T-074, F-059/F.3 叠加规则):
/// all reliefs stack MULTIPLICATIVELY and the total relief is capped at
/// 50% of the original cost — <c>减免后成本 = 原成本 × Π(1−份额)</c>, floored
/// at <c>原成本 × 50%</c>. Every relief source (knowledge scrolls, the era
/// wonder's research bonus, memorial auras, restoration discounts) MUST
/// merge here; no consumer may pre-combine shares on its own (单一咽喉,
/// same discipline as BudgetLedger for the two spending caps).
/// </summary>
public static class StackingRule
{
    /// <summary>Maximum total relief: the F.3 floor keeps 50% of the original cost.</summary>
    public const double MaxTotalReliefShare = 0.5;

    /// <summary>
    /// Validates one relief share (each source's own share, before stacking).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">share is outside [0,1).</exception>
    public static double ValidateShare(double share, string source)
    {
        if (!double.IsFinite(share) || share is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(share), share, $"Relief share of '{source}' must be in [0,1).");
        }

        return share;
    }

    /// <summary>
    /// The merged relief share after multiplicative stacking and the 50%
    /// cap: <c>min(1 − Π(1−份额), 0.5)</c>. This is the value consumers hand
    /// to the pricing seam (e.g. <c>TechTree.BeginResearch(node, reliefShare)</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException">shares is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any share is outside [0,1).</exception>
    public static double CombinedShare(IReadOnlyList<double> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        double keep = 1.0;
        foreach (double share in shares)
        {
            keep *= 1.0 - ValidateShare(share, "stacking");
        }

        return Math.Min(1.0 - keep, MaxTotalReliefShare);
    }

    /// <summary>
    /// Applies the merged relief to one cost:
    /// <c>max(原成本 × Π(1−份额), 原成本 × 50%)</c> (乘法叠加，总下限 50%).
    /// </summary>
    /// <exception cref="ArgumentNullException">shares is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any share outside [0,1), or originalCost negative.</exception>
    public static double Apply(double originalCost, IReadOnlyList<double> shares)
    {
        if (!double.IsFinite(originalCost) || originalCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalCost), originalCost, "Original cost must be finite and non-negative.");
        }

        return originalCost * (1.0 - CombinedShare(shares));
    }
}
