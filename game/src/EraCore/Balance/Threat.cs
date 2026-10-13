namespace EraCore.Balance;

/// <summary>
/// Threat level (F-047): pure static formula library, no state, no ports.
/// Frozen sources: threat clamp = v3.2 §7 / SP-9（邻接敌营判定口径）;
/// multiplier triple = v3.2 appendix F.2（威胁乘子映射表）.
/// </summary>
public static class Threat
{
    /// <summary>Lower clamp bound of the threat band (v3.2 §7).</summary>
    public const double Min = 0.3;

    /// <summary>Upper clamp bound of the threat band (v3.2 §7).</summary>
    public const double Max = 1.2;

    /// <summary>
    /// Threat level from the number of adjacent enemy camps on the campaign
    /// map (SP-9: hex 6-neighbour geometry ∩ hostile corridor edges):
    /// clamp(0.3 + 0.3×n, 0.3, 1.2). Adjacency count 3 lands exactly on the
    /// 1.2 cap; larger counts clamp (SP-9 围城 example: 5 → 1.2).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="adjacentCamps"/> is negative — a camp count cannot be.
    /// </exception>
    public static double ClampThreat(int adjacentCamps)
    {
        if (adjacentCamps < 0)
        {
            throw new ArgumentException(
                $"Adjacent camp count must be non-negative (got {adjacentCamps}).",
                nameof(adjacentCamps));
        }

        return Math.Clamp(0.3 + (0.3 * adjacentCamps), Min, Max);
    }

    /// <summary>
    /// Threat multiplier triple for level <paramref name="t"/>
    /// (v3.2 appendix F.2): Count = (5/3)t, Hp = 0.5+(t−0.3)/0.9,
    /// Interval = clamp(2−1.4(t−0.3)/0.9, 0.5, 2). The band [0.3, 1.2] is
    /// where the F.2 table lives; outside it only Interval clamps — Count
    /// and Hp extrapolate linearly (documented behaviour, callers feed
    /// <see cref="ClampThreat"/> output in practice).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="t"/> is negative — threat is a non-negative level.
    /// </exception>
    public static ThreatMultipliers Multipliers(double t)
    {
        if (t < 0)
        {
            throw new ArgumentException($"Threat must be non-negative (got {t}).", nameof(t));
        }

        return new ThreatMultipliers(
            Count: (5.0 * t) / 3.0,
            Hp: 0.5 + ((t - 0.3) / 0.9),
            Interval: Math.Clamp(2.0 - ((1.4 * (t - 0.3)) / 0.9), 0.5, 2.0));
    }
}
