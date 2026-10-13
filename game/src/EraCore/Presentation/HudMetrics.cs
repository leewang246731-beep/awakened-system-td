namespace EraCore.Presentation;

/// <summary>
/// The three常驻 HUD metrics of the system panel (T-055, F-111): wave number,
/// resource production rate and defense-line integrity. Immutable value type;
/// the Godot side renders it as three monospace digit fields.
/// </summary>
/// <param name="Wave">Current wave ordinal (0 = pre-first-wave build phase).</param>
/// <param name="ProductionPerSecond">Sum of live producer output (items/s).</param>
/// <param name="DefenseIntegrity">Tower HP ÷ tower max HP, 0..1.</param>
/// <param name="TowerCount">Placed weapon towers feeding the integrity ratio.</param>
public readonly record struct HudSnapshot(
    int Wave,
    double ProductionPerSecond,
    double DefenseIntegrity,
    int TowerCount);

/// <summary>
/// Pure computation + formatting of the HUD three metrics (T-055, F-111;
/// C-22 thin panel). Consumes REAL simulation state — the caller extracts
/// the raw figures from the live <c>Simulation</c> (wave counter, producer
/// rates, tower HP pairs) and this class owns every decision after that,
/// testable without Godot (D-01). Digit fields are formatted invariant and
/// zero-padded so the panel's monospace alignment holds regardless of locale.
/// </summary>
public static class HudMetrics
{
    /// <summary>
    /// Aggregates the raw simulation figures into one HUD snapshot.
    /// Defense integrity = Σ current HP ÷ Σ max HP over the given towers;
    /// an empty defense line reports full integrity with
    /// <paramref name="towerHealths"/>.Count == 0 towers (nothing standing
    /// means nothing damaged — the count is shown next to the ratio so the
    /// "0 towers / 100%" combination cannot mislead).
    /// </summary>
    /// <param name="currentWave">Wave ordinal from the wave spawner (≥ 0).</param>
    /// <param name="productionPerSecond">Summed producer output in items/s (≥ 0).</param>
    /// <param name="towerHealths">(current HP, max HP) per placed weapon tower.</param>
    /// <exception cref="ArgumentOutOfRangeException">Negative wave or rate.</exception>
    /// <exception cref="ArgumentException">A tower row with max HP ≤ 0, or current HP outside [0, max].</exception>
    public static HudSnapshot Compute(
        int currentWave,
        double productionPerSecond,
        IReadOnlyList<(int Hp, int MaxHp)> towerHealths)
    {
        ArgumentNullException.ThrowIfNull(towerHealths);

        if (currentWave < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentWave), currentWave, "wave ordinal must be >= 0.");
        }

        if (productionPerSecond < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productionPerSecond), productionPerSecond, "production rate must be >= 0.");
        }

        long hpSum = 0;
        long maxSum = 0;
        foreach ((int hp, int maxHp) in towerHealths)
        {
            if (maxHp <= 0)
            {
                throw new ArgumentException($"tower max HP must be positive, got {maxHp}.", nameof(towerHealths));
            }

            if (hp < 0 || hp > maxHp)
            {
                throw new ArgumentException($"tower HP {hp} outside [0,{maxHp}].", nameof(towerHealths));
            }

            hpSum += hp;
            maxSum += maxHp;
        }

        double integrity = maxSum == 0 ? 1d : (double)hpSum / maxSum;
        return new HudSnapshot(currentWave, productionPerSecond, integrity, towerHealths.Count);
    }

    /// <summary>Wave field, zero-padded to two digits ("<c>W00</c>".."<c>W99</c>", wider waves print full).</summary>
    public static string FormatWave(int wave) =>
        "W" + wave.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Production field, one decimal in items/s ("<c>1.5/s</c>").</summary>
    public static string FormatProduction(double itemsPerSecond) =>
        itemsPerSecond.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/s";

    /// <summary>Integrity field as an integer percent ("<c>100%</c>"), clamped into [0,100].</summary>
    public static string FormatIntegrity(double integrity) =>
        ((int)Math.Round(Math.Clamp(integrity, 0d, 1d) * 100d, MidpointRounding.AwayFromZero))
            .ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}
