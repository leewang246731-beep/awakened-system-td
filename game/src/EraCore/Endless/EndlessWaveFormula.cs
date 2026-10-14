using EraCore.Balance;

namespace EraCore.Endless;

/// <summary>
/// Endless wave-count extrapolation (T-080, F-006; 11-spec-tables SP-7a
/// 「越过 min(40) 封顶的外推」): waves 1..40 run the REGULAR frozen formula
/// verbatim (delegated — never re-implemented); from wave 41 on the count is
/// the SP-7a frozen extrapolation <c>（40+2×(w−40)）×数量乘子</c> — the
/// min(40) cap line keeps growing by +2 enemies per wave, still multiplied
/// by the threat count multiplier (5/3)×t, floored last. The extrapolated
/// segment carries an int-domain overflow guard that clamps at
/// <see cref="MaxCountClamp"/> (T-080 任务卡「clamp 10000」). Pure static
/// formula, stateless; the ≤40 segment's wave-shape parameters arrive from
/// the starting era's wave table at the call site (RL7).
/// </summary>
public static class EndlessWaveFormula
{
    /// <summary>
    /// Last wave of the regular capped segment: the min(40) cap of
    /// <see cref="WaveFormula.Count"/> (v3.2 §6) is in force through this
    /// wave and released from <c>+1</c> on (SP-7a「w&gt;40」).
    /// </summary>
    public const int RegularCapWaves = 40; // ci-allow: R7 T-080 v3.2 §6 min(40) 封顶口径（WaveFormula.Clamp(…,40.0) 的镜像常量）

    /// <summary>
    /// Extrapolation slope above the cap: +2 enemies per wave
    /// (SP-7a「线性延续（每 +1 波 +2 只）」).
    /// </summary>
    public const int CountGainPerWave = 2; // ci-allow: R7 T-080 11-spec-tables SP-7a「每 +1 波 +2 只」（冻结表行）

    /// <summary>
    /// Int-overflow guard of the extrapolated segment: counts are clamped at
    /// this ceiling before the int cast so any legal int wave yields a
    /// finite, replay-stable count (T-080 任务卡「int 溢出守卫 clamp
    /// 10000」——非平衡数值，工程上限).
    /// </summary>
    public const int MaxCountClamp = 10000; // ci-allow: R7 T-080 任务卡外推溢出守卫（工程上限，非平衡数值）

    /// <summary>
    /// Enemy count of one endless wave. Segment rule:
    /// <list type="bullet">
    /// <item>wave ≤ <see cref="RegularCapWaves"/> — EXACTLY
    /// <see cref="WaveFormula.Count"/> (the regular frozen formula, cap and
    /// all; the starting era's 基数/起/间隔/成长 drive it as usual);</item>
    /// <item>wave &gt; <see cref="RegularCapWaves"/> — SP-7a frozen
    /// extrapolation <c>floor(max(0, (40 + 2×(w−40))) × 数量乘子)</c>, then
    /// clamped at <see cref="MaxCountClamp"/>. The segment is deliberately
    /// independent of the era's ramp parameters: the 40-line IS the anchor
    /// the cap froze, so the release continues from IT (the join can step up
    /// when an early era's wave 40 still sits under the cap — the frozen
    /// formula's own behaviour, documented not smoothed).</item>
    /// </list>
    /// Guards mirror <see cref="WaveFormula.Count"/>: negative waves and
    /// negative (or NaN) threat multipliers return 0.
    /// </summary>
    /// <param name="wave">Wave number, 1-based; negative returns 0.</param>
    /// <param name="baseCount">基数 (base count) of the starting era's wave table (≤40 segment only).</param>
    /// <param name="beginWave">起 (ramp start wave) (≤40 segment only).</param>
    /// <param name="spacing">间隔 (waves per ramp step); ≤ 0 → no ramp (≤40 segment only).</param>
    /// <param name="scaling">成长 (growth divisor), floored at 0.1 (≤40 segment only).</param>
    /// <param name="threatMultiplier">数量乘子 from <see cref="Threat.Multipliers"/> ((5/3)×t).</param>
    public static int Count(
        int wave,
        int baseCount,
        int beginWave,
        int spacing,
        double scaling,
        double threatMultiplier)
    {
        if (wave < 0 || !(threatMultiplier >= 0))
        {
            return 0;
        }

        if (wave <= RegularCapWaves)
        {
            // Regular segment: the frozen formula owns it, cap included.
            return WaveFormula.Count(wave, baseCount, beginWave, spacing, scaling, threatMultiplier);
        }

        // Extrapolated segment (SP-7a): (40 + 2×(w−40)) × 数量乘子, floored,
        // then the task-card overflow clamp. The arithmetic runs in double
        // space so extreme int waves cannot overflow before the guard.
        double uncapped = (RegularCapWaves + (CountGainPerWave * (double)(wave - RegularCapWaves))) * threatMultiplier;
        double floored = Math.Floor(Math.Max(0.0, uncapped));
        return (int)Math.Min(floored, MaxCountClamp);
    }

    /// <summary>
    /// Elite-line count of one endless wave: <see cref="Elite.Count"/>
    /// <c>1+⌊(波−8)/4⌋</c> verbatim — the regular formula never caps the
    /// elite line, so the endless segment simply keeps calling it
    /// (SP-7a 精英线「不封顶」= the existing formula already satisfies it).
    /// </summary>
    /// <param name="wave">Wave number; negative returns 0 (Elite guard).</param>
    public static int EliteCount(int wave) => Elite.Count(wave);
}
