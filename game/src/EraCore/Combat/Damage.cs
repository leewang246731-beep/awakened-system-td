namespace EraCore.Combat;

/// <summary>Result of the burst-price check (F-028): did the tower pay for above-baseline DPS?</summary>
/// <param name="Allowed">True when the tower stays within the baseline or carries a positive burst price.</param>
/// <param name="Violation">Human-readable rule name; null when <paramref name="Allowed"/>.</param>
public sealed record BurstCostResult(bool Allowed, string? Violation);

/// <summary>
/// Damage engine, four branches (C-09 E1 thin version, T-038; F-029; design
/// v3.2 §6 L83 and appendix A inline formula). Pure static formulas,
/// stateless — same inputs always give the same outputs (determinism, 03
/// §1.2). The branches:
/// <list type="number">
/// <item>Direct: <c>max(d×decay − armor, 10%×d×decay)</c> — decay applies
/// BEFORE armor subtraction, armor clamps to ≥0, and the floor is 10% of the
/// DECAYED damage (v3.2 §6 symbol binding: d = undecayed single-shot damage,
/// 甲 = target armor).</item>
/// <item>Splash decay: <c>lerp(1 − min(dist/r, 1), 1, 0.4)</c> — full damage
/// at the impact center, never below 40% at the splash edge (溅射边缘保底
/// 40%, v3.2 §6). The result feeds branch 1 as its decay.</item>
/// <item>Pierce: reduces armor by a ratio (armorPen ∈ [0,1]) before branch 1
/// — the energy-line "穿甲" flag basis (SP-1a: interrupts shield regen;
/// ratios arrive from content, not from code).</item>
/// <item>Burst-price validation: a tower may only exceed the baseline DPS of
/// its era's standard line when it declares a positive burst price —
/// wind-up/charge/heat in ticks (v3.2 §6「爆发型必有前摇/充能/热量代价」;
/// anchor: 落石台 45-tick wind-up, SP-1b L37). Baselines come from the
/// content registry (RL7), never from code.</item>
/// </list>
/// </summary>
public static class Damage
{
    /// <summary>Floor ratio: 10% of the decayed damage (v3.2 §6 L83).</summary>
    public const double FloorRatio = 0.1;

    /// <summary>Splash edge floor: decay never drops below 40% of base (v3.2 §6).</summary>
    public const double SplashEdgeFloor = 0.4;

    /// <summary>
    /// Distance-driven share of the splash decay: lerp weight complement of
    /// <see cref="SplashEdgeFloor"/> (1 − 0.4), so decay(0) = 1 and decay(r) = 0.4.
    /// </summary>
    public const double SplashDistanceShare = 1.0 - SplashEdgeFloor;

    /// <summary>
    /// Direct-hit damage (branch 1). <paramref name="decay"/> multiplies d
    /// first; the clamped armor subtracts from the decayed value; the result
    /// never drops below <see cref="FloorRatio"/> × decayed damage. Decay 0
    /// yields 0 (both branches of the max collapse to 0).
    /// </summary>
    /// <param name="damage">Undecayed single-shot damage d (≥0).</param>
    /// <param name="decay">Decay factor in [0,1] (1 = point-blank direct hit).</param>
    /// <param name="armor">Target armor in points; negative values clamp to 0.</param>
    /// <exception cref="ArgumentException">damage negative, or decay outside [0,1].</exception>
    public static double Direct(double damage, double decay, int armor)
    {
        ValidateDecay(damage, decay);
        return DirectCore(damage, decay, armor);
    }

    /// <summary>
    /// Integer twin of the direct branch with decay = 1 (damage-formula
    /// unification, batch2 review F1): <c>max(d − armor, ceil(0.1×d))</c> —
    /// the ceil is preserved in pure integer math (<c>(d + 9) / 10</c>),
    /// armor clamps to ≥0 and can never fully negate a hit.
    /// <b>唯一权威整数入口，供确定性回放</b>（Unit.ApplyDamage delegates
    /// here; Units → Combat one-way, 03-decomposition §3.1; the d=1..30 ×
    /// armor=0..15 divergence grid in DamageTests pins this against the
    /// legacy ceil semantics so the twin can never drift from
    /// <see cref="Direct"/>).
    /// </summary>
    /// <param name="damage">Undecayed single-shot damage d (≥0).</param>
    /// <param name="armor">Target armor in points; negative values clamp to 0.</param>
    /// <exception cref="ArgumentException">damage negative.</exception>
    public static int DirectInt(int damage, int armor)
    {
        if (damage < 0)
        {
            throw new ArgumentException($"Damage must be non-negative (got {damage}).", nameof(damage));
        }

        int floor = (damage + 9) / 10; // ceil(FloorRatio × d) in pure integer math
        return Math.Max(damage - Math.Max(armor, 0), floor);
    }

    /// <summary>
    /// Splash decay factor (branch 2): <c>lerp(1 − min(dist/r, 1), 1, 0.4)</c>.
    /// Distance 0 → 1.0; distance ≥ radius → 0.4 (edge floor); in between it
    /// falls linearly. Feed the result into <see cref="Direct"/> as its decay.
    /// </summary>
    /// <param name="distanceCells">Distance from the impact center in cells (≥0).</param>
    /// <param name="radiusCells">Splash radius in cells (&gt;0).</param>
    /// <exception cref="ArgumentException">negative distance, or radius ≤ 0.</exception>
    public static double SplashDecay(double distanceCells, double radiusCells)
    {
        if (radiusCells <= 0)
        {
            throw new ArgumentException(
                $"Splash radius must be positive (got {radiusCells}).", nameof(radiusCells));
        }

        if (distanceCells < 0)
        {
            throw new ArgumentException(
                $"Splash distance must be non-negative (got {distanceCells}).", nameof(distanceCells));
        }

        double falloff = 1.0 - Math.Min(distanceCells / radiusCells, 1.0);
        return falloff * SplashDistanceShare + SplashEdgeFloor;
    }

    /// <summary>
    /// Armor-piercing hit (branch 3): the pierce ratio removes that share of
    /// the armor BEFORE branch 1 runs (armorPen 0.5 halves effective armor,
    /// 1.0 removes it entirely). Energy-line flag basis (SP-1a 穿甲).
    /// </summary>
    /// <param name="damage">Undecayed single-shot damage d (≥0).</param>
    /// <param name="decay">Decay factor in [0,1].</param>
    /// <param name="armor">Target armor in points (clamped ≥0 after the reduction).</param>
    /// <param name="armorPenRatio">Armor reduction ratio in [0,1].</param>
    /// <exception cref="ArgumentException">damage negative, decay outside [0,1], or armorPenRatio outside [0,1].</exception>
    public static double Pierce(double damage, double decay, int armor, double armorPenRatio)
    {
        ValidateDecay(damage, decay);
        if (armorPenRatio < 0 || armorPenRatio > 1)
        {
            throw new ArgumentException(
                $"Armor pierce ratio must lie in [0,1] (got {armorPenRatio}).", nameof(armorPenRatio));
        }

        double effectiveArmor = Math.Max(0.0, armor * (1.0 - armorPenRatio));
        return DirectCore(damage, decay, effectiveArmor);
    }

    /// <summary>
    /// Burst-price validation (branch 4): a tower whose registry DPS exceeds
    /// <paramref name="baselineDps"/> must declare a positive burst price
    /// (wind-up ticks today; charge/heat fields join the check as content
    /// grows them). The baseline is the era's standard single-target line DPS
    /// read from the registry (RL7) — e.g. E1 passes the wood-arrow tower's
    /// 10 DPS. Towers at or below the baseline never need a price.
    /// </summary>
    /// <param name="spec">Tower definition from the registry.</param>
    /// <param name="baselineDps">Standard-line DPS of the same era (≥0, from content).</param>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    /// <exception cref="ArgumentException">baselineDps negative.</exception>
    public static BurstCostResult ValidateBurstCost(TowerSpec spec, double baselineDps)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (baselineDps < 0)
        {
            throw new ArgumentException(
                $"Baseline DPS must be non-negative (got {baselineDps}).", nameof(baselineDps));
        }

        if (spec.Dps <= baselineDps)
        {
            return new BurstCostResult(true, null);
        }

        return HasPositiveBurstPrice(spec)
            ? new BurstCostResult(true, null)
            : new BurstCostResult(
                false,
                $"burst_price_missing:{spec.Id} — DPS {spec.Dps} exceeds baseline {baselineDps} without a positive burst wind-up/charge/heat trait (v3.2 §6)");
    }

    private static bool HasPositiveBurstPrice(TowerSpec spec) =>
        (spec.Traits?.BurstWindupTicks ?? 0) > 0;

    private static void ValidateDecay(double damage, double decay)
    {
        if (damage < 0)
        {
            throw new ArgumentException($"Damage must be non-negative (got {damage}).", nameof(damage));
        }

        if (decay < 0 || decay > 1)
        {
            throw new ArgumentException($"Decay must lie in [0,1] (got {decay}).", nameof(decay));
        }
    }

    private static double DirectCore(double damage, double decay, double armor)
    {
        double decayed = damage * decay;
        double armored = Math.Max(decayed - Math.Max(armor, 0.0), decayed * FloorRatio);
        return Math.Max(armored, 0.0);
    }
}
