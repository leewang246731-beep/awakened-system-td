using EraCore.Combat;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-029] damage engine four branches (v3.2 §6 L83 + appendix A):
// direct max(d×decay−甲, 10%×d×decay), splash decay lerp edge floor 40%,
// pierce armor-ratio reduction, burst-price validation (F-028).
public class DamageTests
{
    // ------------------------------------------------------------ branch 1: direct

    public static TheoryData<double, double, int, double> DirectRows => new()
    {
        // legal — armor eats part of the decayed damage
        { 100, 1.0, 30, 70 },      // 100×1 − 30
        { 100, 0.5, 20, 30 },      // decay FIRST (100×0.5=50), then −20
        { 17, 1.0, 4, 13 },        // F.5 anchor shape: 弩塔单发 17 vs 勇士甲 4 → 13
        // boundary — armor equals decayed damage: floor binds
        { 100, 1.0, 100, 10 },     // max(0, 10%×100) → 10% floor
        // boundary — armor exceeds decayed damage: floor still binds
        { 100, 1.0, 250, 10 },     // max(−150, 10) → 10
        { 60, 0.5, 40, 3 },        // decayed 30, floor 3 beats 30−40 = −10
        // boundary — decay 1 keeps d intact; decay 0 zeroes everything
        { 55, 1.0, 0, 55 },        // pristine hit
        { 55, 0.0, 30, 0 },        // 0×anything → both max arms are 0
        // boundary — zero damage stays zero
        { 0, 1.0, 30, 0 },
    };

    [Theory] // legal + boundary — decay applies before armor; floor is 10% of DECAYED damage
    [MemberData(nameof(DirectRows))]
    public void Direct_applies_decay_before_armor_with_ten_percent_floor(
        double d, double decay, int armor, double expected)
    {
        // [F-029] max(d×decay − armor, 10%×d×decay)（先衰减后减甲）
        // Pure arithmetic: 1e-6 tolerance (CS-3.3, tolerance source = formula exactness).
        Assert.Equal(expected, Damage.Direct(d, decay, armor), 6);
    }

    [Fact] // boundary — armor clamps to 0 from below (v3.2 §6 甲 clamp≥0)
    public void Negative_armor_clamps_to_zero()
    {
        // [F-029] −30 armor must behave like 0 armor, not like a buff
        Assert.Equal(Damage.Direct(100, 1.0, 0), Damage.Direct(100, 1.0, -30), 9);
        Assert.Equal(100.0, Damage.Direct(100, 1.0, -30), 6);
    }

    [Theory] // illegal — negative damage and out-of-range decay are rejected
    [InlineData(-1, 1.0)]
    [InlineData(-0.01, 0.5)]
    public void Direct_rejects_negative_damage(double d, double decay)
    {
        // [F-029] guard: d ≥ 0
        Assert.Throws<ArgumentException>(() => Damage.Direct(d, decay, 0));
    }

    [Theory] // illegal — decay outside [0,1] is rejected
    [InlineData(100, -0.1)]
    [InlineData(100, 1.01)]
    [InlineData(100, 2.0)]
    public void Direct_rejects_decay_outside_unit_interval(double d, double decay)
    {
        // [F-029] guard: decay ∈ [0,1]
        Assert.Throws<ArgumentException>(() => Damage.Direct(d, decay, 0));
    }

    // ------------------------------------------------------------ branch 2: splash decay

    public static TheoryData<double, double, double> SplashRows => new()
    {
        // legal — linear falloff between center and edge
        { 0.0, 3.0, 1.0 },   // impact center: full damage
        { 1.5, 3.0, 0.7 },   // half radius: lerp(0.5, 1, 0.4) = 0.7
        { 0.75, 3.0, 0.85 }, // quarter radius
        // boundary — at and past the radius the edge floor binds
        { 3.0, 3.0, 0.4 },   // 溅射边缘保底 40% (v3.2 §6)
        { 4.5, 3.0, 0.4 },   // beyond: clamped at 40%
        { 99.0, 3.0, 0.4 },
        // boundary — far inside a huge radius stays near full damage
        { 5.0, 1000.0, 0.997 }, // falloff 0.995 → 0.995×0.6 + 0.4 = 0.997
    };

    [Theory] // legal + boundary — lerp(1−min(dist/r,1), 1, 0.4)
    [MemberData(nameof(SplashRows))]
    public void Splash_decay_lerps_down_to_forty_percent_edge_floor(
        double dist, double radius, double expected)
    {
        // [F-029] splash decay feeds Direct as its decay factor
        Assert.Equal(expected, Damage.SplashDecay(dist, radius), 6);
    }

    [Fact] // legal — a full splash chain composes: decay then direct formula
    public void Splash_chain_composes_with_direct_branch()
    {
        // [F-029] 落石台-style hit: d 60 at half radius (decay 0.7) vs armor 8 → 60×0.7−8 = 34
        double decay = Damage.SplashDecay(1.5, 3.0);
        Assert.Equal(34.0, Damage.Direct(60, decay, 8), 6);
    }

    [Theory] // illegal — negative distance or non-positive radius are rejected
    [InlineData(-0.1, 3.0)]
    [InlineData(-5.0, 3.0)]
    public void Splash_rejects_negative_distance(double dist, double radius)
    {
        // [F-029] guard: distance ≥ 0
        Assert.Throws<ArgumentException>(() => Damage.SplashDecay(dist, radius));
    }

    [Theory] // illegal — radius must be positive
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void Splash_rejects_non_positive_radius(double radius)
    {
        // [F-029] guard: radius > 0
        Assert.Throws<ArgumentException>(() => Damage.SplashDecay(1.0, radius));
    }

    // ------------------------------------------------------------ branch 3: pierce

    public static TheoryData<double, int, int, double> PierceRows => new()
    {
        // legal — the ratio removes that share of armor before the direct branch
        { 0.0, 30, 100, 70 },  // no pierce: identical to Direct(100,1,30)
        { 0.5, 30, 100, 85 },  // armor 30 → 15 effective: 100−15
        { 1.0, 30, 100, 100 }, // full pierce: armor gone
        // boundary — pierce through overwhelming armor: the floor binds
        { 0.5, 250, 60, 6 },   // effective armor 125 > decayed 60 → floor 10%×60 = 6
        { 1.0, 250, 100, 100 }, // full pierce removes the floor-binding armor entirely
        // boundary — zero armor with any ratio equals the plain hit
        { 0.75, 0, 80, 80 },
    };

    [Theory] // legal + boundary — armorPen reduces armor by ratio, then the direct formula runs
    [MemberData(nameof(PierceRows))]
    public void Pierce_reduces_armor_by_ratio_then_direct(
        double pen, int armor, int d, double expected)
    {
        // [F-029] 穿甲比率减甲, expected values hand-computed from the frozen
        // formula; 1e-6 tolerance (CS-3.3)
        Assert.Equal(expected, Damage.Pierce(d, 1.0, armor, pen), 6);
    }

    [Fact] // legal — pierce 0 is exactly the direct branch
    public void Pierce_zero_ratio_equals_direct()
    {
        // [F-029] branch consistency: Pierce(d, decay, armor, 0) == Direct(d, decay, armor)
        Assert.Equal(
            Damage.Direct(77.5, 0.8, 12),
            Damage.Pierce(77.5, 0.8, 12, 0.0), 9);
    }

    [Theory] // illegal — armorPen outside [0,1] is rejected
    [InlineData(-0.01)]
    [InlineData(-1.0)]
    [InlineData(1.01)]
    [InlineData(2.0)]
    public void Pierce_rejects_ratio_outside_unit_interval(double pen)
    {
        // [F-029] guard: armorPen ∈ [0,1]
        Assert.Throws<ArgumentException>(() => Damage.Pierce(100, 1.0, 30, pen));
    }

    // ------------------------------------------------------------ branch 4: burst price

    [Fact] // legal — E1 registry rows pass with the era baseline (RL7: numbers from JSON)
    public void Era1_towers_pass_burst_price_check_against_wood_arrow_baseline()
    {
        // [F-028] baseline = the standard single-target line DPS read from the
        // registry (wood arrow, 10); rockfall exceeds it and pays 45 wind-up ticks
        double baseline = CombatContent.Tower("era1:tower-wood-arrow").Dps;
        foreach (var spec in CombatContent.Towers)
        {
            var result = Damage.ValidateBurstCost(spec, baseline);
            Assert.True(result.Allowed, $"{spec.Id}: {result.Violation}");
        }
    }

    [Fact] // illegal — above-baseline DPS without a wind-up is rejected
    public void Above_baseline_dps_without_burst_price_is_rejected()
    {
        // [F-028] rockfall numbers WITHOUT the wind-up trait: d 60 / reload 90 /
        // dps 20 > baseline 10, no 前摇 → violation naming the tower
        var naked = CombatContent.Load("""
            {"items":[{"id":"test:windless-rockfall","class":"physical","tier":1,"max_tier":2,
              "cost_total":60,"dps":20.0,"damage_per_shot":60,"reload_ticks":90,
              "range_cells":4.0,"hp":150,"ammo_item":null,"ammo_per_shot":1}]}
            """)[0];
        var result = Damage.ValidateBurstCost(naked, 10.0);
        Assert.False(result.Allowed);
        Assert.Contains("test:windless-rockfall", result.Violation);
    }

    [Fact] // boundary — at-baseline DPS needs no burst price at all
    public void At_baseline_dps_needs_no_burst_price()
    {
        // [F-028] wood arrow itself: dps == baseline (no traits) → allowed
        var result = Damage.ValidateBurstCost(CombatContent.Tower("era1:tower-wood-arrow"), 10.0);
        Assert.True(result.Allowed);
        Assert.Null(result.Violation);
    }

    [Theory] // illegal — negative baselines are rejected
    [InlineData(-1.0)]
    [InlineData(-0.01)]
    public void Burst_check_rejects_negative_baseline(double baseline)
    {
        // [F-028] guard: baseline ≥ 0
        Assert.Throws<ArgumentException>(
            () => Damage.ValidateBurstCost(CombatContent.Tower("era1:tower-wood-arrow"), baseline));
    }
}
