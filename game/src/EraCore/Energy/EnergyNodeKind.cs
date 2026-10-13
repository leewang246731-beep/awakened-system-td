namespace EraCore.Energy;

/// <summary>
/// Energy node taxonomy for the E1 manpower era (C-08, T-033, F-009).
/// E1 set per <c>game/content/era1/era.json</c> (T-049 anchors):
/// tier-1 labor camp = 10 energy points/s, tier-2 beast pen = 40 energy
/// points/s (ladder multiplier ×4, inside the v3.2 §4 L65 band [3,5]).
/// Consumers (towers/facilities) carry no era-table demand yet — their
/// per-node demand is registered explicitly with
/// <see cref="EnergyLedger.RegisterConsumer"/>; era-table wiring arrives
/// with the C-06 economy (T-034) and tower tables (T-049 follow-up).
/// </summary>
public enum EnergyNodeKind
{
    /// <summary>
    /// Labor camp (劳工营地) — E1 tier-1 manpower producer, 10 points/s
    /// (era1.json <c>energy.levels[0].output</c>).
    /// </summary>
    LaborCamp = 1,

    /// <summary>
    /// Beast pen (畜栏) — E1 tier-2 manpower producer, 40 points/s
    /// (era1.json <c>energy.levels[1].output</c>).
    /// </summary>
    BeastPen = 2,

    /// <summary>
    /// Facility — generic E1 consumer node (towers and production
    /// facilities); demand is per-node, see
    /// <see cref="EnergyLedger.RegisterConsumer"/>.
    /// </summary>
    Facility = 3,
}

/// <summary>
/// Anchor table companion of <see cref="EnergyNodeKind"/> (same pattern as
/// <c>World.TerrainInfo</c>). Producer outputs mirror
/// <c>game/content/era1/era.json</c> <c>energy.levels</c> — the equality is
/// enforced by <c>EraCore.Tests.Energy.EnergyNodeKindTests</c> against the
/// JSON itself, so engine constants and the content table cannot drift
/// apart. The ladder band [3,5] is v3.2 §4 L65 (能源阶梯 ×3~5/级).
/// </summary>
public static class E1EnergyAnchors
{
    /// <summary>
    /// Tier-1 labor camp output in energy points per second
    /// (era1.json energy.levels[0].output, v3.2 §4 E1 行+T-049 锚点).
    /// </summary>
    public const double LaborCampOutputPerSecond = 10.0;

    /// <summary>
    /// Tier-2 beast pen output in energy points per second
    /// (era1.json energy.levels[1].output, T-049 锚点).
    /// </summary>
    public const double BeastPenOutputPerSecond = 40.0;

    /// <summary>
    /// Inclusive lower bound of the per-tier energy ladder growth band
    /// (v3.2 §4 L65: 能源阶梯 ×3~5/级; era1.json energy.ladder_multiplier_min).
    /// </summary>
    public const double LadderMultiplierMin = 3.0;

    /// <summary>
    /// Inclusive upper bound of the per-tier energy ladder growth band
    /// (v3.2 §4 L65; era1.json energy.ladder_multiplier_max).
    /// </summary>
    public const double LadderMultiplierMax = 5.0;

    /// <summary>
    /// Whether <paramref name="kind"/> is a producer node kind whose output
    /// is pinned by the era anchor table.
    /// </summary>
    public static bool IsProducer(EnergyNodeKind kind) =>
        kind is EnergyNodeKind.LaborCamp or EnergyNodeKind.BeastPen;

    /// <summary>
    /// Anchor output of a producer kind in energy points per second.
    /// Consumer or undefined kinds are rejected with
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static double OutputPerSecond(EnergyNodeKind kind) => kind switch
    {
        EnergyNodeKind.LaborCamp => LaborCampOutputPerSecond,
        EnergyNodeKind.BeastPen => BeastPenOutputPerSecond,
        _ => throw new ArgumentException(
            $"Kind {kind} is not an E1 producer; consumers register an explicit demand.", nameof(kind)),
    };

    /// <summary>
    /// E1 ladder growth from tier 1 to tier 2 (10 → 40 = ×4). Value, not a
    /// constant, so the arithmetic stays checkable against the band.
    /// </summary>
    public static double L1ToL2Multiplier => BeastPenOutputPerSecond / LaborCampOutputPerSecond;

    /// <summary>
    /// Asserts the E1 ladder multiplier sits inside the v3.2 §4 L65 band
    /// [3,5]. Called by the <see cref="EnergyLedger"/> constructor so an
    /// anchor regression fails fast instead of silently shipping a broken
    /// ladder (T-033 验收: 阶梯倍率 [3,5] 断言绿).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The tier-2/tier-1 output ratio left the [3,5] band.
    /// </exception>
    public static void EnsureLadderInBand()
    {
        double multiplier = L1ToL2Multiplier;
        if (multiplier < LadderMultiplierMin || multiplier > LadderMultiplierMax)
        {
            throw new InvalidOperationException(
                $"E1 energy ladder multiplier {multiplier:0.###} is outside the v3.2 band "
                + $"[{LadderMultiplierMin},{LadderMultiplierMax}].");
        }
    }
}
