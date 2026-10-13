namespace EraCore.Balance;

/// <summary>
/// Campaign pacing formulas (F-048): win waves, checkpoint and wave
/// interval. Frozen sources: 胜波 = v3.2 §7（10+⌊威胁度×10⌋）with the three
/// SP-9 代入示例 as anchors; checkpoint = ⌊胜波×0.75⌋ with a floor of 10
/// (SP table "检查点公式" verbatim list); interval = v3.2 §6
/// （波间隔 120 s × 间隔乘子）. Pure static formulas, stateless.
/// </summary>
public static class Campaign
{
    /// <summary>Base seconds between waves (v3.2 §6: 波间隔 120s×乘子).</summary>
    public const double IntervalBaseSeconds = 120.0;

    /// <summary>
    /// Waves needed to win a level: 10 + ⌊t×10⌋. SP-9 anchors: t=0.3 → 13,
    /// t=0.9 → 19, t=1.2 → 22.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="t"/> is negative — feed <see cref="Threat.ClampThreat"/>
    /// output (band [0.3, 1.2]).
    /// </exception>
    public static int WinWaves(double t)
    {
        if (t < 0)
        {
            throw new ArgumentException($"Threat must be non-negative (got {t}).", nameof(t));
        }

        return 10 + (int)Math.Floor(t * 10.0);
    }

    /// <summary>
    /// Checkpoint wave after a loss: max(10, ⌊winWaves×0.75⌋) — short
    /// campaigns (13 win waves → 9) restart from wave 10, longer ones keep
    /// three quarters of the progress.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="winWaves"/> is negative.
    /// </exception>
    public static int Checkpoint(int winWaves)
    {
        if (winWaves < 0)
        {
            throw new ArgumentException(
                $"Win waves must be non-negative (got {winWaves}).",
                nameof(winWaves));
        }

        return Math.Max(10, (int)Math.Floor(winWaves * 0.75));
    }

    /// <summary>
    /// Seconds between waves: 120 × the threat interval multiplier (already
    /// clamped to [0.5, 2] by <see cref="Threat.Multipliers"/>), i.e.
    /// 72~240 s across the threat band (v3.2 §6) and never outside
    /// 60~240 s even for unclamped t.
    /// </summary>
    public static double WaveIntervalSeconds(double t) =>
        IntervalBaseSeconds * Threat.Multipliers(t).Interval;
}
