namespace EraCore.Combat;

/// <summary>
/// Growth-law validator of the tower upgrade system (C-09 E1, T-038; F-027;
/// design v3.2 §6 L82, red line RL7): the engine never invents upgrade
/// numbers — it projects them from the registry's own
/// <see cref="TowerGrowthSpec"/> declarations and validates tier steps
/// against the frozen law:
/// <list type="bullet">
/// <item>DPS ×2 per tier inside a ±15% band;</item>
/// <item>total cost ×3 per tier STRICTLY (no band — 07 D-15 restored the
/// 90→270→810→2430 ladder);</item>
/// <item>range +25% per tier inside a ±15% design band, and derived range
/// values live on the 0.5-cell grid (取整 0.5, v3.2 §6 — values like the
/// crossbow line's 9.5 are legal); CLASS-CHANGING steps (e.g. E2 pierce T3
/// → splash T4 火油 7.5) are specialization transitions whose upper range
/// is its own F.5 anchor — the band governs same-class steps only, the
/// half-cell grid always applies;</item>
/// <item>class DPS bands relative to the same-tier single-target baseline
/// (SP-1a): splash ×0.8 (AOE −20% applies to DPS only, 07 D-15), slow ×0.2,
/// support exempt from the band entirely, and F.5 specialization lines
/// (<c>tier_line:"specialization"</c>) exempt — their niche is armor
/// interaction, not ladder conformity.</item>
/// </list>
/// Every check consumes <see cref="TowerSpec"/> values deserialized from
/// content JSON — no design number is hardcoded here except the frozen law
/// constants themselves (same standing as the Balance formula library).
/// Violations come back as a list of rule strings; an empty list passes.
/// </summary>
public static class UpgradeLaw
{
    /// <summary>DPS band half-width per tier step: ±15% (v3.2 §6).</summary>
    public const double DpsBand = 0.15;

    /// <summary>Range band half-width per tier step: ±15% design band (07 correction #2).</summary>
    public const double RangeBand = 0.15;

    /// <summary>Class-band half-width: ±15% around the class factor (SP-1a).</summary>
    public const double ClassBand = 0.15;

    /// <summary>Frozen DPS anchor: ×2 per tier (v3.2 §6).</summary>
    public const double DpsLawMultiplier = 2.0;

    /// <summary>Frozen cost anchor: ×3 per tier, strict (v3.2 §6 / 07 D-15).</summary>
    public const double CostLawMultiplier = 3.0;

    /// <summary>Frozen range anchor: +25% per tier (v3.2 §6).</summary>
    public const double RangeLawMultiplier = 1.25;

    /// <summary>Splash-class DPS band factor: single-target ×0.8 (AOE −20% on DPS only, 07 D-15).</summary>
    public const double SplashDpsFactor = 0.8;

    /// <summary>Slow-class DPS band factor (SP-1a [NEW-原型校准]).</summary>
    public const double SlowDpsFactor = 0.2;

    /// <summary>Derived range grid: half a cell (v3.2 §6 取整 0.5).</summary>
    public const double RangeCellGrid = 0.5;

    /// <summary>
    /// F.5 line vocabulary for rows balanced outside the standard ×3 ladder
    /// (<c>tier_line:"specialization"</c>, e.g. the E2 onager 700|80|11) —
    /// such rows are exempt from the same-tier class DPS band; their niche
    /// is armor interaction (F.5 第 20 波验算), not ladder conformity.
    /// </summary>
    public const string SpecializationLine = "specialization";

    /// <summary>
    /// Rounds a derived range onto the half-cell grid
    /// (<c>round(value / 0.5) × 0.5</c>, midpoints away from zero —
    /// 6.25 → 6.5, 3.75 → 4.0).
    /// </summary>
    public static double RoundToHalfCell(double rangeCells)
    {
        if (rangeCells < 0)
        {
            throw new ArgumentException(
                $"Range must be non-negative (got {rangeCells}).", nameof(rangeCells));
        }

        return Math.Round(rangeCells / RangeCellGrid, MidpointRounding.AwayFromZero) * RangeCellGrid;
    }

    /// <summary>
    /// Validates one tower's declared <c>tier_growth</c> block against the
    /// frozen law anchors: DPS multiplier within 2.0 ±15%, cost multiplier
    /// exactly 3.0, range multiplier within 1.25 ±15%. Run over every item of
    /// towers.json (RL7: the registry numbers themselves are the object under
    /// test).
    /// </summary>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    public static IReadOnlyList<string> ValidateGrowthDeclaration(TowerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var violations = new List<string>();
        if (spec.TierGrowth is null)
        {
            return new[] { $"tier_growth_missing:{spec.Id}" };
        }

        var growth = spec.TierGrowth;
        WithinBand(
            violations, growth.DpsMultiplierPerTier, DpsLawMultiplier, DpsBand,
            $"dps_multiplier:{spec.Id}");
        if (Math.Abs(growth.CostMultiplierPerTier - CostLawMultiplier) > 1e-9)
        {
            violations.Add(
                $"cost_multiplier:{spec.Id}={growth.CostMultiplierPerTier} must equal {CostLawMultiplier} exactly (strict ladder)");
        }

        WithinBand(
            violations, growth.RangeMultiplierPerTier, RangeLawMultiplier, RangeBand,
            $"range_multiplier:{spec.Id}");
        return violations;
    }

    /// <summary>
    /// Projects the next tier of <paramref name="spec"/> from its own
    /// registry-declared growth multipliers: DPS × dps multiplier, total cost
    /// × cost multiplier (exact), range × range multiplier rounded onto the
    /// half-cell grid; tier +1; everything else (hp, reload, traits) carries
    /// over unchanged — the growth law governs exactly those three numbers.
    /// </summary>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="spec"/> sits at its <c>max_tier</c> ceiling, or its
    /// tier_growth block is missing.
    /// </exception>
    public static TowerSpec ProjectNextTier(TowerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.TierGrowth is null)
        {
            throw new InvalidOperationException(
                $"{spec.Id} declares no tier_growth block; cannot project tier {spec.Tier + 1}.");
        }

        if (spec.Tier >= spec.MaxTier)
        {
            throw new InvalidOperationException(
                $"{spec.Id} is at its max tier {spec.MaxTier}; no upgrade slot left.");
        }

        var growth = spec.TierGrowth;
        return spec with
        {
            Tier = spec.Tier + 1,
            Dps = spec.Dps * growth.DpsMultiplierPerTier,
            CostTotal = (int)Math.Round(
                spec.CostTotal * growth.CostMultiplierPerTier, MidpointRounding.AwayFromZero),
            RangeCells = RoundToHalfCell(spec.RangeCells * growth.RangeMultiplierPerTier),
        };
    }

    /// <summary>
    /// Validates one registry tier step <paramref name="lower"/> →
    /// <paramref name="upper"/> against the frozen law. Tier must increase by
    /// exactly 1; the DPS ratio must sit inside dps-multiplier ±15% (a
    /// zero-damage line must stay zero — control/aura towers); the total cost
    /// must equal lower × cost multiplier EXACTLY (strict ladder, no band);
    /// the range must sit inside range-multiplier ±15% AND on the half-cell
    /// grid; <c>max_tier</c> must agree between the rows.
    /// </summary>
    /// <exception cref="ArgumentNullException">lower or upper is null.</exception>
    public static IReadOnlyList<string> ValidateStep(TowerSpec lower, TowerSpec upper)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        var violations = new List<string>();
        string id = lower.Id;

        if (upper.Tier != lower.Tier + 1)
        {
            violations.Add($"tier_step:{id} {lower.Tier}->{upper.Tier} must be exactly +1");
        }

        if (upper.MaxTier != lower.MaxTier)
        {
            violations.Add($"max_tier_mismatch:{id} {lower.MaxTier}->{upper.MaxTier}");
        }

        if (lower.TierGrowth is null)
        {
            violations.Add($"tier_growth_missing:{id}");
            return violations;
        }

        var growth = lower.TierGrowth;

        // DPS: ratio band around the declared multiplier (zero lines stay zero).
        if (lower.Dps <= 0)
        {
            if (upper.Dps != 0)
            {
                violations.Add(
                    $"dps_from_zero:{id} a zero-damage line cannot grow a nonzero DPS ({upper.Dps}) — SP-1a support/slow discipline");
            }
        }
        else
        {
            WithinBand(
                violations, upper.Dps / lower.Dps, growth.DpsMultiplierPerTier, DpsBand,
                $"dps_step:{id} {lower.Dps}->{upper.Dps}");
        }

        // Cost: exact ×multiplier, no band (07 D-15 strict ladder).
        double expectedCost = lower.CostTotal * growth.CostMultiplierPerTier;
        if (Math.Abs(upper.CostTotal - expectedCost) > 1e-6)
        {
            violations.Add(
                $"cost_step:{id} {lower.CostTotal}->{upper.CostTotal} must equal {expectedCost} exactly (×{growth.CostMultiplierPerTier} strict)");
        }

        // Range: ±15% design band around the declared multiplier, half-cell grid.
        // A zero-range line (contact-only, e.g. the spike trap) must stay zero.
        // CLASS-CHANGING STEPS (T-065, e.g. the E2 standard line's pierce T3
        // 连弩 9.5 → splash T4 火油 7.5) are specialization transitions, not
        // pure growth steps: SP-1b assigns each row its own class and F.5
        // freezes the terminal row's range as its own anchor, so the +25%
        // band governs same-class steps only — the half-cell grid still
        // applies to every row. DPS and cost laws are class-blind (both stay
        // enforced on class-changing steps).
        bool classChanges = lower.Class != upper.Class;
        if (lower.RangeCells <= 0)
        {
            if (upper.RangeCells != 0)
            {
                violations.Add(
                    $"range_step:{id} a zero-range line cannot grow range {lower.RangeCells}->{upper.RangeCells}");
            }
        }
        else if (!classChanges)
        {
            WithinBand(
                violations, upper.RangeCells / lower.RangeCells, growth.RangeMultiplierPerTier, RangeBand,
                $"range_step:{id} {lower.RangeCells}->{upper.RangeCells}");
        }

        if (Math.Abs(upper.RangeCells / RangeCellGrid
                - Math.Round(upper.RangeCells / RangeCellGrid, MidpointRounding.AwayFromZero)) > 1e-6)
        {
            violations.Add($"range_grid:{id} {upper.RangeCells} is off the {RangeCellGrid}-cell grid");
        }

        return violations;
    }

    /// <summary>
    /// Validates one tower's DPS against its class band
    /// (SP-1a 按塔类验证带): expected = <paramref name="baselineDps"/> × class
    /// factor (splash 0.8, slow 0.2, others 1.0) with a ±15% band. Support
    /// towers are exempt (SP-1a L30: 支援零伤不入 DPS 验证带) — any DPS
    /// passes, the class simply is not judged on damage. The baseline is the
    /// same-tier single-target line DPS read from the registry (RL7).
    /// </summary>
    /// <param name="spec">Tower definition from the registry.</param>
    /// <param name="baselineDps">Same-tier single-target line DPS (≥0, from content).</param>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    /// <exception cref="ArgumentException">baselineDps negative.</exception>
    public static IReadOnlyList<string> ValidateClassBand(TowerSpec spec, double baselineDps)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (baselineDps < 0)
        {
            throw new ArgumentException(
                $"Baseline DPS must be non-negative (got {baselineDps}).", nameof(baselineDps));
        }

        if (spec.Class == TowerClass.Support)
        {
            return Array.Empty<string>(); // SP-1a L30: support stays out of the DPS band
        }

        // F.5 specialization lines (tier_line:"specialization", e.g. the E2
        // onager) sit off the standard ladder by design — their balance
        // niche is armor interaction (F.5 第 20 波验算: 单发 160 对甲 12 保
        // 92.5% 有效 DPS), so the same-tier class band does not judge them.
        if (string.Equals(spec.TierLine, SpecializationLine, StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        double factor = spec.Class switch
        {
            TowerClass.Splash => SplashDpsFactor,
            TowerClass.Slow => SlowDpsFactor,
            _ => 1.0,
        };

        var violations = new List<string>();
        WithinBand(
            violations, spec.Dps, baselineDps * factor, ClassBand,
            $"class_band:{spec.Class.ToString().ToLowerInvariant()}:{spec.Id}");
        return violations;
    }

    private static void WithinBand(
        List<string> violations, double value, double anchor, double band, string label)
    {
        double low = anchor * (1.0 - band);
        double high = anchor * (1.0 + band);
        if (value < low - 1e-9 || value > high + 1e-9)
        {
            violations.Add(
                $"{label}={value} outside band [{low.ToString("0.####")}, {high.ToString("0.####")}] (anchor {anchor} ±{band:P0})");
        }
    }
}
