namespace EraCore.Economy;

/// <summary>
/// Growth-law validator of the harvest building ladder (C-06 E1 thin,
/// T-034, F-019; design v3.2 §5 L74, red line RL7): the engine never
/// invents harvest numbers — it projects them from the frozen law and
/// validates declared tier specs against it:
/// <list type="bullet">
/// <item>output ×1.8 per tier exactly (产量 ×1.8/级);</item>
/// <item>energy cost ×2.7 per tier exactly (能耗 ×2.7/级);</item>
/// <item>footprint (and seats) never change with tier — 单位面积产量恒定:
/// upgrading buys rate, never area;</item>
/// <item>tier 1 output anchor 0.5 件/s/坐席 (T1 采集 0.5 件/s/坐席);</item>
/// <item>tier band 1..<see cref="MaxDepthOfLadder"/> = 3 (采集 2~3 级/时代;
/// E1 ships tier 1 only — tiers 2/3 exist as law projections for later
/// tasks, marked [NEW-T-034] where v3.2 pins no absolute value).</item>
/// </list>
/// Every check consumes plain values — content-side tier tables (F-023,
/// T-079) deserialize into <see cref="TierSpec"/> rows and run through the
/// same validator, so no design number is hardcoded here except the frozen
/// law constants themselves (same standing as
/// <see cref="Combat.UpgradeLaw"/> and the Balance formula library).
/// Violations come back as a list of rule strings; an empty list passes.
/// </summary>
public static class HarvestLaw
{
    /// <summary>
    /// Frozen tier-1 anchor: 0.5 items/s per seat (v3.2 §5 L74:
    /// T1 采集 0.5 件/s/坐席).
    /// </summary>
    public const double Tier1OutputPerSeatPerSecond = 0.5;

    /// <summary>
    /// Frozen tier-1 energy cost anchor per seat.
    /// [NEW-T-034] v3.2 pins only the ×2.7/级 law, not the T1 base; 1.0
    /// energy point/s/seat keeps one labor camp (10/s,
    /// <see cref="Energy.E1EnergyAnchors.LaborCampOutputPerSecond"/>) well
    /// above a thin E1 economy's harvest demand.
    /// </summary>
    public const double Tier1EnergyPerSeatPerSecond = 1.0;

    /// <summary>Frozen output ladder: ×1.8 per tier (v3.2 §5 L74 产量 ×1.8/级).</summary>
    public const double OutputMultiplierPerTier = 1.8;

    /// <summary>Frozen energy ladder: ×2.7 per tier (v3.2 §5 L74 能耗 ×2.7/级).</summary>
    public const double EnergyMultiplierPerTier = 2.7;

    /// <summary>
    /// Inclusive tier ceiling: 采集 2~3 级/时代 (v3.2 §5 L74) — 3 is the
    /// deepest harvest tier any era may declare.
    /// </summary>
    public const int MaxTier = 3;

    /// <summary>
    /// Comparison tolerance for projected-value equality: content rows are
    /// decimal JSON numbers, projections are IEEE doubles; 1e-9 absorbs the
    /// round-trip without ever accepting a wrong multiplier (a ×1.7 step is
    /// off by 0.1).
    /// </summary>
    public const double EqualityTolerance = 1e-9;

    /// <summary>
    /// One declared harvest tier row (content-table shape, F-023). Output
    /// and energy are totals for the whole building (<see cref="Seats"/>
    /// seats), footprint is grid cells — the validator checks the totals
    /// against the law projections per seat.
    /// </summary>
    /// <param name="Tier">Harvest tier, 1..<see cref="MaxTier"/>.</param>
    /// <param name="Seats">Work seats of the building, ≥ 1 (坐席).</param>
    /// <param name="OutputItemsPerSecond">Declared total output in 件/s.</param>
    /// <param name="EnergyCostPerSecond">Declared total energy cost in points/s.</param>
    /// <param name="FootprintCells">Occupied grid cells, ≥ 1, tier-invariant.</param>
    public sealed record TierSpec(
        int Tier,
        int Seats,
        double OutputItemsPerSecond,
        double EnergyCostPerSecond,
        int FootprintCells)
    {
        /// <summary>
        /// Default footprint of the E1 thin harvester: one grid cell
        /// (1 cell = 32 px, K-1 CellCoord). [NEW-T-034]
        /// </summary>
        public const int DefaultFootprintCells = 1;

        /// <summary>
        /// Builds the law-projected spec for <paramref name="tier"/> and
        /// <paramref name="seats"/> — the numbers the ladder must equal.
        /// </summary>
        public static TierSpec FromLaw(int tier, int seats, int footprintCells = DefaultFootprintCells) =>
            new(
                tier,
                seats,
                ProjectedOutputPerSeatPerSecond(tier) * seats,
                ProjectedEnergyPerSeatPerSecond(tier) * seats,
                footprintCells);
    }

    /// <summary>
    /// Law projection: tier-1 anchor × output multiplier^(tier−1), per
    /// seat, in 件/s. Tiers 2/3 values (0.9 / 1.62) are [NEW-T-034]
    /// projections — v3.2 pins the law and the T1 base only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// tier outside 1..<see cref="MaxTier"/>.
    /// </exception>
    public static double ProjectedOutputPerSeatPerSecond(int tier)
    {
        EnsureTierInBand(tier);
        return Tier1OutputPerSeatPerSecond * Math.Pow(OutputMultiplierPerTier, tier - 1);
    }

    /// <summary>
    /// Law projection: tier-1 energy anchor × energy multiplier^(tier−1),
    /// per seat, in energy points/s. Tiers 2/3 values (2.7 / 7.29) are
    /// [NEW-T-034] projections.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// tier outside 1..<see cref="MaxTier"/>.
    /// </exception>
    public static double ProjectedEnergyPerSeatPerSecond(int tier)
    {
        EnsureTierInBand(tier);
        return Tier1EnergyPerSeatPerSecond * Math.Pow(EnergyMultiplierPerTier, tier - 1);
    }

    /// <summary>
    /// Validates one declared tier row against the frozen law: tier inside
    /// the band, seats ≥ 1, footprint ≥ 1, finite totals, and both totals
    /// equal to the law projections for that tier/seats pair.
    /// </summary>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    public static IReadOnlyList<string> ValidateDeclaration(TierSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var violations = new List<string>();

        if (spec.Tier < 1 || spec.Tier > MaxTier)
        {
            violations.Add($"tier:{spec.Tier} outside the harvest band [1,{MaxTier}] (采集 2~3 级/时代)");
        }

        if (spec.Seats < 1)
        {
            violations.Add($"seats:{spec.Seats} must be at least 1 (坐席)");
        }

        if (spec.FootprintCells < 1)
        {
            violations.Add($"footprint:{spec.FootprintCells} must be at least 1 cell");
        }

        if (!double.IsFinite(spec.OutputItemsPerSecond) || !double.IsFinite(spec.EnergyCostPerSecond))
        {
            violations.Add($"non-finite totals for tier {spec.Tier} (output/energy must be finite)");
            return violations;
        }

        if (spec.Tier >= 1 && spec.Tier <= MaxTier && spec.Seats >= 1)
        {
            double expectedOutput = ProjectedOutputPerSeatPerSecond(spec.Tier) * spec.Seats;
            if (Math.Abs(spec.OutputItemsPerSecond - expectedOutput) > EqualityTolerance)
            {
                violations.Add(
                    $"output:{spec.Tier}={spec.OutputItemsPerSecond} must equal the law projection {expectedOutput} (×{OutputMultiplierPerTier}/级, T1 {Tier1OutputPerSeatPerSecond}/坐席)");
            }

            double expectedEnergy = ProjectedEnergyPerSeatPerSecond(spec.Tier) * spec.Seats;
            if (Math.Abs(spec.EnergyCostPerSecond - expectedEnergy) > EqualityTolerance)
            {
                violations.Add(
                    $"energy:{spec.Tier}={spec.EnergyCostPerSecond} must equal the law projection {expectedEnergy} (×{EnergyMultiplierPerTier}/级)");
            }
        }

        return violations;
    }

    /// <summary>
    /// Validates one registry tier step <paramref name="lower"/> →
    /// <paramref name="upper"/> against the frozen law: tier increases by
    /// exactly 1 and stays inside the band; output ratio is exactly ×1.8
    /// and energy ratio exactly ×2.7 (no band — the multipliers are the
    /// frozen anchors, unlike the ±15% bands of the tower ladder);
    /// footprint and seats are unchanged (单位面积产量恒定 — an upgrade
    /// buys rate, never area or seats).
    /// </summary>
    /// <exception cref="ArgumentNullException">lower or upper is null.</exception>
    public static IReadOnlyList<string> ValidateStep(TierSpec lower, TierSpec upper)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        var violations = new List<string>();

        if (upper.Tier != lower.Tier + 1)
        {
            violations.Add($"tier_step:{lower.Tier}->{upper.Tier} must be exactly +1");
        }

        if (upper.Tier > MaxTier)
        {
            violations.Add($"tier_ceiling:{upper.Tier} exceeds the harvest tier ceiling {MaxTier}");
        }

        if (upper.Seats != lower.Seats)
        {
            violations.Add($"seats_step:{lower.Seats}->{upper.Seats} must not change with tier");
        }

        if (upper.FootprintCells != lower.FootprintCells)
        {
            violations.Add(
                $"footprint_step:{lower.FootprintCells}->{upper.FootprintCells} must not change with tier (单位面积产量恒定)");
        }

        if (lower.OutputItemsPerSecond > 0 && upper.OutputItemsPerSecond > 0)
        {
            double ratio = upper.OutputItemsPerSecond / lower.OutputItemsPerSecond;
            if (Math.Abs(ratio - OutputMultiplierPerTier) > EqualityTolerance)
            {
                violations.Add(
                    $"output_step:{lower.OutputItemsPerSecond}->{upper.OutputItemsPerSecond} ratio {ratio} must equal ×{OutputMultiplierPerTier} exactly");
            }
        }
        else
        {
            violations.Add($"output_step:{lower.Tier}->{upper.Tier} needs two positive outputs to compare");
        }

        if (lower.EnergyCostPerSecond > 0 && upper.EnergyCostPerSecond > 0)
        {
            double ratio = upper.EnergyCostPerSecond / lower.EnergyCostPerSecond;
            if (Math.Abs(ratio - EnergyMultiplierPerTier) > EqualityTolerance)
            {
                violations.Add(
                    $"energy_step:{lower.EnergyCostPerSecond}->{upper.EnergyCostPerSecond} ratio {ratio} must equal ×{EnergyMultiplierPerTier} exactly");
            }
        }
        else
        {
            violations.Add($"energy_step:{lower.Tier}->{upper.Tier} needs two positive energy costs to compare");
        }

        return violations;
    }

    private static void EnsureTierInBand(int tier)
    {
        if (tier < 1 || tier > MaxTier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tier), tier, $"Harvest tier must be inside [1,{MaxTier}] (采集 2~3 级/时代).");
        }
    }
}
