using EraCore.Combat;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-027] tower upgrade numeric law (v3.2 §6 L82, RL7): the validator
// consumes ONLY registry JSON — the real era1 table must pass, deliberately
// broken content must be rejected. Anchors: DPS ×2/级 ±15%, cost ×3 strict,
// range +25% ±15% on the half-cell grid (取整 0.5), class bands splash ×0.8 /
// slow ×0.2 / support exempt (SP-1a, 07 D-15).
public class UpgradeLawTests
{
    // ------------------------------------------------------------ rounding

    public static TheoryData<double, double> RoundRows => new()
    {
        // legal — derived range lands on the half-cell grid
        { 6.25, 6.5 },  // wood arrow tier 2: 5.0 × 1.25 = 6.25 → 6.5 (midpoint away from zero)
        { 3.75, 4.0 },  // campfire tier 2: 3.0 × 1.25 = 3.75 → 4.0
        // boundary — values already on the grid or exactly representable stay put
        { 5.0, 5.0 },
        { 9.5, 9.5 },   // crossbow-line 9.5 is legal (07 correction #2)
        { 4.0, 4.0 },   // rockfall tier 2: 4.0 × 1.25 = 5.0 stays 5.0
        { 0.0, 0.0 },   // zero-range line stays zero
        { 6.3, 6.5 },   // off-grid input rounds to the nearest half cell
    };

    [Theory] // legal + boundary — 取整 0.5 (v3.2 §6)
    [MemberData(nameof(RoundRows))]
    public void Half_cell_rounding_follows_away_from_zero_midpoints(double value, double expected)
    {
        // [F-027] round(value/0.5)×0.5; 1e-6 tolerance (CS-3.3)
        Assert.Equal(expected, UpgradeLaw.RoundToHalfCell(value), 6);
    }

    [Theory] // illegal — negative range is rejected
    [InlineData(-0.5)]
    [InlineData(-3.0)]
    public void Rounding_rejects_negative_range(double value)
    {
        // [F-027] guard: range ≥ 0
        Assert.Throws<ArgumentException>(() => UpgradeLaw.RoundToHalfCell(value));
    }

    // ------------------------------------------------------------ registry declarations (real towers.json)

    [Fact] // legal — every E1 row declares the frozen growth law
    public void Era1_growth_declarations_all_pass()
    {
        // [F-027] RL7: run the validator over the registry's own upgrade
        // multiplier slots — DPS 2.0 ±15%, cost exactly 3.0, range 1.25 ±15%
        foreach (var spec in CombatContent.Towers)
        {
            var violations = UpgradeLaw.ValidateGrowthDeclaration(spec);
            Assert.Empty(violations);
        }
    }

    [Fact] // legal — projected tier-2 rows satisfy the step law end to end
    public void Era1_projected_tier2_steps_validate()
    {
        // [F-027] projection from registry multipliers, then validated as a
        // registry step: 30→90 cost, 10→20 dps, 5.0→6.5 range etc.
        foreach (var spec in CombatContent.Towers)
        {
            var projected = UpgradeLaw.ProjectNextTier(spec);
            Assert.Empty(UpgradeLaw.ValidateStep(spec, projected));
        }
    }

    [Fact] // legal — projected numbers carry the exact anchors
    public void Projection_carries_registry_anchors()
    {
        // [F-027] wood arrow tier 1 → 2: dps 10×2, cost 30×3, range 5.0→6.5;
        // rockfall: dps 20×2, cost 60×3, range 4.0→5.0 (all inputs from JSON)
        var arrow = UpgradeLaw.ProjectNextTier(CombatContent.Tower("era1:tower-wood-arrow"));
        Assert.Equal(2, arrow.Tier);
        Assert.Equal(20.0, arrow.Dps, 6);
        Assert.Equal(90, arrow.CostTotal);
        Assert.Equal(6.5, arrow.RangeCells, 6);

        var rockfall = UpgradeLaw.ProjectNextTier(CombatContent.Tower("era1:tower-rockfall"));
        Assert.Equal(40.0, rockfall.Dps, 6);
        Assert.Equal(180, rockfall.CostTotal);
        Assert.Equal(5.0, rockfall.RangeCells, 6);
    }

    [Fact] // boundary — zero-damage/zero-range lines (trap, campfire) project to zero
    public void Zero_lines_project_to_zero()
    {
        // [F-027] trap: dps 0→0, range 0→0; campfire: dps 0→0, range 3.0→4.0
        var trap = UpgradeLaw.ProjectNextTier(CombatContent.Tower("era1:tower-spike-trap"));
        Assert.Equal(0.0, trap.Dps, 6);
        Assert.Equal(0.0, trap.RangeCells, 6);
        Assert.Equal(45, trap.CostTotal);

        var campfire = UpgradeLaw.ProjectNextTier(CombatContent.Tower("era1:tower-campfire"));
        Assert.Equal(0.0, campfire.Dps, 6);
        Assert.Equal(4.0, campfire.RangeCells, 6);
    }

    [Fact] // illegal — projecting past max_tier is refused
    public void Projection_refuses_past_max_tier()
    {
        // [F-027] E1 lines cap at tier 2: projecting the projection throws
        var tier2 = UpgradeLaw.ProjectNextTier(CombatContent.Tower("era1:tower-wood-arrow"));
        Assert.Throws<InvalidOperationException>(() => UpgradeLaw.ProjectNextTier(tier2));
    }

    // ------------------------------------------------------------ step violations (synthetic JSON samples)

    [Fact] // legal — a hand-built lawful pair validates clean
    public void Lawful_pair_validates()
    {
        // [F-027] 10→20 dps (×2), 30→90 cost (×3 exact), 5.0→6.25→band+grid… use 6.5
        var (lower, upper) = CombatContent.LoadPair(
            CombatContent.PairCollectionJson("pierce", 10, 20, 30, 90, 5.0, 6.5));
        Assert.Empty(UpgradeLaw.ValidateStep(lower, upper));
    }

    [Fact] // illegal — each law violation is named and rejected
    public void Broken_step_pairs_are_rejected()
    {
        // [F-027] violation samples enter through the same JSON pipeline (RL7)
        var samples = new (double DpsLow, double DpsHigh, int CostLow, int CostHigh, double RangeLow, double RangeHigh, int TierHigh, string Expected)[]
        {
            // legal/boundary reference is covered above; these must all fail:
            (10, 25, 30, 90, 5.0, 6.5, 2, "dps_step"),   // ×2.5 above the band
            (10, 14, 30, 90, 5.0, 6.5, 2, "dps_step"),   // ×1.4 below the band
            (10, 20, 30, 94, 5.0, 6.5, 2, "cost_step"),  // strict ×3: 94 rejected
            (10, 20, 30, 84, 5.0, 6.5, 2, "cost_step"),  // 84 (=90×0.933) also rejected — no band
            (10, 20, 30, 90, 5.0, 8.0, 2, "range_step"), // ×1.6 outside +25% ±15%
            (10, 20, 30, 90, 5.0, 6.3, 2, "range_grid"), // off the 0.5 grid
            (10, 20, 30, 90, 5.0, 6.5, 3, "tier_step"),  // tier 1 → 3 refused
            (0, 5, 30, 90, 0.0, 0.0, 2, "dps_from_zero"), // zero-damage line grows DPS
            (10, 20, 30, 90, 0.0, 1.0, 2, "range_step"),  // zero-range line grows range
        };
        foreach (var s in samples)
        {
            var json = CombatContent.PairCollectionJson(
                "pierce", s.DpsLow, s.DpsHigh, s.CostLow, s.CostHigh, s.RangeLow, s.RangeHigh,
                tierLow: 1, tierHigh: s.TierHigh);
            var (lower, upper) = CombatContent.LoadPair(json);
            var violations = UpgradeLaw.ValidateStep(lower, upper);
            Assert.Contains(violations, v => v.StartsWith(s.Expected, StringComparison.Ordinal));
        }
    }

    [Fact] // illegal — declared multipliers outside the frozen law are flagged
    public void Broken_growth_declarations_are_rejected()
    {
        // [F-027] declared dps multiplier 2.5 (outside 2.0 ±15%) and cost
        // multiplier 3.1 (strict law) both flagged
        var json = CombatContent.PairCollectionJson(
            "pierce", 10, 20, 30, 90, 5.0, 6.5,
            dpsMultiplier: 2.5, costMultiplier: 3.1, rangeMultiplier: 1.5);
        var violations = UpgradeLaw.ValidateGrowthDeclaration(CombatContent.Load(json)[0]);
        Assert.Contains(violations, v => v.StartsWith("dps_multiplier", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith("cost_multiplier", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith("range_multiplier", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ class bands (SP-1a)

    [Fact] // legal — splash sits at 0.8× baseline, slow at 0.2× baseline
    public void Class_bands_accept_factor_conforming_dps()
    {
        // [F-026] AOE −20% 仅作用于 DPS (07 D-15); slow 0.2× (SP-1a)
        var splash = CombatContent.Load("""
            {"items":[{"id":"test:band-splash","class":"splash","tier":1,"max_tier":2,
              "cost_total":30,"dps":8.0,"damage_per_shot":8,"reload_ticks":30,
              "range_cells":5.0,"hp":100,"ammo_item":null,"ammo_per_shot":1}]}
            """)[0];
        Assert.Empty(UpgradeLaw.ValidateClassBand(splash, 10.0)); // 0.8×10 = 8 ∈ [6.8, 9.2]

        var slow = splash with { Id = "test:band-slow", Class = TowerClass.Slow, Dps = 2.0 };
        Assert.Empty(UpgradeLaw.ValidateClassBand(slow, 10.0));   // 0.2×10 = 2 ∈ [1.7, 2.3]
    }

    [Fact] // illegal — splash at full single-target DPS sits outside its band
    public void Class_band_rejects_splash_above_factor_band()
    {
        // [F-026] splash dps 10 vs baseline 10 → band [6.8, 9.2] → rejected
        var splash = CombatContent.Load("""
            {"items":[{"id":"test:band-hot-splash","class":"splash","tier":1,"max_tier":2,
              "cost_total":30,"dps":10.0,"damage_per_shot":10,"reload_ticks":30,
              "range_cells":5.0,"hp":100,"ammo_item":null,"ammo_per_shot":1}]}
            """)[0];
        var violations = UpgradeLaw.ValidateClassBand(splash, 10.0);
        Assert.Contains(violations, v => v.StartsWith("class_band:splash", StringComparison.Ordinal));

        // slow far above its 0.2 band is also rejected
        var slow = splash with { Id = "test:band-hot-slow", Class = TowerClass.Slow, Dps = 8.0 };
        Assert.Contains(
            UpgradeLaw.ValidateClassBand(slow, 10.0),
            v => v.StartsWith("class_band:slow", StringComparison.Ordinal));
    }

    [Fact] // boundary — support towers are exempt from the DPS band entirely
    public void Support_class_is_exempt_from_dps_band()
    {
        // [F-026] SP-1a L30: 支援零伤不入 DPS 验证带 — any dps passes, none judged
        var support = CombatContent.Load("""
            {"items":[{"id":"test:band-support","class":"support","tier":1,"max_tier":2,
              "cost_total":20,"dps":7.0,"damage_per_shot":7,"reload_ticks":30,
              "range_cells":3.0,"hp":80,"ammo_item":null,"ammo_per_shot":1}]}
            """)[0];
        Assert.Empty(UpgradeLaw.ValidateClassBand(support, 10.0));
        Assert.Empty(UpgradeLaw.ValidateClassBand(support, 0.0));
    }

    [Theory] // illegal — negative baselines are rejected
    [InlineData(-1.0)]
    [InlineData(-0.01)]
    public void Class_band_rejects_negative_baseline(double baseline)
    {
        // [F-026] guard: baseline ≥ 0
        Assert.Throws<ArgumentException>(
            () => UpgradeLaw.ValidateClassBand(CombatContent.Tower("era1:tower-wood-arrow"), baseline));
    }
}
