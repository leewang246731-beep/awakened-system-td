using EraCore.Economy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-019] Harvest ladder law (T-034): tier projections 0.5×1.8^(t−1)
// output / 1.0×2.7^(t−1) energy per seat, tier band [1,3], footprint and
// seats tier-invariant (v3.2 §5 L74). Table rows for tiers 2/3 are
// [NEW-T-034] projections — v3.2 pins the law and the T1 base only.
// [C-06][F-019]
public class HarvestLawTests
{
    // ---- 公式组 1/6：采集速率与升级律数值表 -----------------------------

    [Theory] // legal — per-seat output ladder 0.5 × 1.8^(tier−1), tiers 2/3 [NEW-T-034]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.9)]
    [InlineData(3, 1.62)]
    public void ProjectedOutputPerSeatPerSecond_matches_the_ladder_table(int tier, double expected)
    {
        // tolerance 1e-9: same standing as HarvestLaw.EqualityTolerance —
        // decimal table rows vs IEEE pow projections
        Assert.Equal(expected, HarvestLaw.ProjectedOutputPerSeatPerSecond(tier), 9);
    }

    [Theory] // legal — per-seat energy ladder 1.0 × 2.7^(tier−1), tiers 2/3 [NEW-T-034]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.7)]
    [InlineData(3, 7.29)]
    public void ProjectedEnergyPerSeatPerSecond_matches_the_ladder_table(int tier, double expected)
    {
        Assert.Equal(expected, HarvestLaw.ProjectedEnergyPerSeatPerSecond(tier), 9);
    }

    [Theory] // legal — seats scale both totals linearly (T1 0.5 件/s/坐席)
    [InlineData(1, 0.5)]
    [InlineData(2, 1.0)]
    [InlineData(3, 1.5)]
    public void FromLaw_scales_totals_per_seat_at_tier1(int seats, double expectedOutput)
    {
        HarvestLaw.TierSpec spec = HarvestLaw.TierSpec.FromLaw(1, seats);

        Assert.Equal(expectedOutput, spec.OutputItemsPerSecond, 9);
        Assert.Equal(1.0 * seats, spec.EnergyCostPerSecond, 9);
    }

    [Theory] // boundary — the whole tier table validates clean against the law (F-019 验收)
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public void ValidateDeclaration_accepts_every_law_projected_row(int tier, int seats)
    {
        HarvestLaw.TierSpec spec = HarvestLaw.TierSpec.FromLaw(tier, seats);

        Assert.Empty(HarvestLaw.ValidateDeclaration(spec));
    }

    [Theory] // legal — registry steps 1→2 and 2→3 validate clean (×1.8 / ×2.7 exact)
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void ValidateStep_accepts_the_law_steps(int lowerTier, int upperTier)
    {
        HarvestLaw.TierSpec lower = HarvestLaw.TierSpec.FromLaw(lowerTier, 1);
        HarvestLaw.TierSpec upper = HarvestLaw.TierSpec.FromLaw(upperTier, 1);

        Assert.Empty(HarvestLaw.ValidateStep(lower, upper));
    }

    // ---- 升级律非法输入 ---------------------------------------------------

    [Theory] // illegal — tier outside [1,3] rejected by the projections
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void Projections_reject_tiers_outside_the_band(int tier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HarvestLaw.ProjectedOutputPerSeatPerSecond(tier));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarvestLaw.ProjectedEnergyPerSeatPerSecond(tier));
    }

    [Fact] // illegal — a declared row with the wrong output multiplier is rejected
    public void ValidateDeclaration_rejects_wrong_output_multiplier()
    {
        var spec = new HarvestLaw.TierSpec(
            Tier: 2, Seats: 1, OutputItemsPerSecond: 0.5 * 1.7, EnergyCostPerSecond: 2.7, FootprintCells: 1);

        IReadOnlyList<string> violations = HarvestLaw.ValidateDeclaration(spec);

        Assert.Contains(violations, v => v.StartsWith("output:", StringComparison.Ordinal));
    }

    [Fact] // illegal — a declared row with the wrong energy multiplier is rejected
    public void ValidateDeclaration_rejects_wrong_energy_multiplier()
    {
        var spec = new HarvestLaw.TierSpec(
            Tier: 2, Seats: 1, OutputItemsPerSecond: 0.9, EnergyCostPerSecond: 1.0 * 2.5, FootprintCells: 1);

        IReadOnlyList<string> violations = HarvestLaw.ValidateDeclaration(spec);

        Assert.Contains(violations, v => v.StartsWith("energy:", StringComparison.Ordinal));
    }

    [Fact] // illegal — tier 4 declaration is out of band (采集 2~3 级/时代)
    public void ValidateDeclaration_rejects_tier_above_the_ceiling()
    {
        var spec = new HarvestLaw.TierSpec(
            Tier: 4, Seats: 1, OutputItemsPerSecond: 0.5 * 1.8 * 1.8 * 1.8, EnergyCostPerSecond: 1.0 * 2.7 * 2.7 * 2.7, FootprintCells: 1);

        Assert.Contains(HarvestLaw.ValidateDeclaration(spec), v => v.StartsWith("tier:", StringComparison.Ordinal));
    }

    [Fact] // illegal — non-finite totals rejected (NaN sneaks past ratio checks)
    public void ValidateDeclaration_rejects_non_finite_totals()
    {
        var spec = new HarvestLaw.TierSpec(
            Tier: 1, Seats: 1, OutputItemsPerSecond: double.NaN, EnergyCostPerSecond: 1.0, FootprintCells: 1);

        Assert.Contains(
            HarvestLaw.ValidateDeclaration(spec), v => v.Contains("non-finite", StringComparison.Ordinal));
    }

    [Fact] // illegal — an upgrade that buys footprint violates 单位面积产量恒定
    public void ValidateStep_rejects_footprint_growth()
    {
        HarvestLaw.TierSpec lower = HarvestLaw.TierSpec.FromLaw(1, 1);
        var upper = HarvestLaw.TierSpec.FromLaw(2, 1) with { FootprintCells = 2 };

        Assert.Contains(
            HarvestLaw.ValidateStep(lower, upper), v => v.StartsWith("footprint_step:", StringComparison.Ordinal));
    }

    [Fact] // illegal — seats changing with tier breaks the per-seat law reading
    public void ValidateStep_rejects_seat_growth()
    {
        HarvestLaw.TierSpec lower = HarvestLaw.TierSpec.FromLaw(1, 1);
        HarvestLaw.TierSpec upper = HarvestLaw.TierSpec.FromLaw(2, 2);

        Assert.Contains(HarvestLaw.ValidateStep(lower, upper), v => v.StartsWith("seats_step:", StringComparison.Ordinal));
    }

    [Theory] // illegal — non-+1 tier steps rejected
    [InlineData(1, 3)]
    [InlineData(3, 5)]
    public void ValidateStep_rejects_tier_jumps(int lowerTier, int upperTier)
    {
        HarvestLaw.TierSpec lower = HarvestLaw.TierSpec.FromLaw(lowerTier, 1);
        HarvestLaw.TierSpec upper = HarvestLaw.TierSpec.FromLaw(Math.Min(upperTier, HarvestLaw.MaxTier), 1);

        Assert.Contains(HarvestLaw.ValidateStep(lower, upper), v => v.StartsWith("tier_step:", StringComparison.Ordinal));
    }

    [Fact] // boundary — a 3→4 step hits the ceiling even with correct multipliers
    public void ValidateStep_flags_the_tier_ceiling()
    {
        HarvestLaw.TierSpec tier3 = HarvestLaw.TierSpec.FromLaw(3, 1);
        var tier4 = new HarvestLaw.TierSpec(
            Tier: 4, Seats: 1,
            OutputItemsPerSecond: HarvestLaw.ProjectedOutputPerSeatPerSecond(3) * HarvestLaw.OutputMultiplierPerTier,
            EnergyCostPerSecond: HarvestLaw.ProjectedEnergyPerSeatPerSecond(3) * HarvestLaw.EnergyMultiplierPerTier,
            FootprintCells: 1);

        Assert.Contains(
            HarvestLaw.ValidateStep(tier3, tier4), v => v.StartsWith("tier_ceiling:", StringComparison.Ordinal));
    }

    [Fact] // illegal — null specs rejected
    public void Validators_reject_null_specs()
    {
        Assert.Throws<ArgumentNullException>(() => HarvestLaw.ValidateDeclaration(null!));
        Assert.Throws<ArgumentNullException>(() => HarvestLaw.ValidateStep(null!, HarvestLaw.TierSpec.FromLaw(1, 1)));
        Assert.Throws<ArgumentNullException>(() => HarvestLaw.ValidateStep(HarvestLaw.TierSpec.FromLaw(1, 1), null!));
    }

    [Fact] // legal — frozen anchors carry the v3.2 §5 L74 values verbatim
    public void Frozen_constants_match_the_design_anchors()
    {
        Assert.Equal(0.5, HarvestLaw.Tier1OutputPerSeatPerSecond);
        Assert.Equal(1.8, HarvestLaw.OutputMultiplierPerTier);
        Assert.Equal(2.7, HarvestLaw.EnergyMultiplierPerTier);
        Assert.Equal(3, HarvestLaw.MaxTier);
    }
}
