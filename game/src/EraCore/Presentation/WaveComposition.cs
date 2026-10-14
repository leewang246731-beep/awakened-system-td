using System.Globalization;

namespace EraCore.Presentation;

/// <summary>
/// One species row of the wave-composition HUD metric (T-075 full口径,
/// F-111/§10.2): enemy type (content id) × live count × armor overview
/// (min~max across the observed units of that species).
/// </summary>
/// <param name="EnemyId">Enemy content id (K-2 registry, e.g. "era1:enemy-beastling").</param>
/// <param name="Count">Units of this species (current wave) or projected units (preview).</param>
/// <param name="ArmorMin">Lowest armor observed/projected for the species (≥ 0).</param>
/// <param name="ArmorMax">Highest armor observed/projected for the species (≥ ArmorMin).</param>
public sealed record WaveCompositionRow(string EnemyId, int Count, int ArmorMin, int ArmorMax);

/// <summary>
/// The full third常驻 HUD metric (T-075 DEBT repayment, v1.5 F8): current
/// wave composition aggregated from the sim snapshot's units PLUS the
/// next-wave preview projected read-only from the WaveSpawner plan.
/// </summary>
/// <param name="Wave">Wave ordinal of the aggregated (current) wave (≥ 0).</param>
/// <param name="CurrentWave">Species rows live on the field right now.</param>
/// <param name="NextWave">Wave ordinal of the previewed wave (≥ 1).</param>
/// <param name="NextWavePreview">Species rows the spawner plan projects for the next wave.</param>
public sealed record WaveCompositionView(
    int Wave,
    IReadOnlyList<WaveCompositionRow> CurrentWave,
    int NextWave,
    IReadOnlyList<WaveCompositionRow> NextWavePreview);

/// <summary>
/// Pure aggregation + formatting of the wave-composition HUD metric
/// (T-075, F-111 全口径; DEBT v1.5 F8). No engine types (D-01): the caller
/// extracts (content id, armor) pairs from the K-6 snapshot units — or the
/// WaveSpawner plan numbers for the preview — and this class owns every
/// grouping/ordering/formatting decision after that, testable without
/// Godot. Row order is deterministic (species id ascending) so HUD text and
/// smoke output never shuffle between runs.
/// </summary>
public static class WaveComposition
{
    /// <summary>
    /// Aggregates live snapshot units into per-species rows (类型×数量×护甲
    /// 概览). Pure function; 30 lines by task-card contract.
    /// </summary>
    /// <param name="units">(content id, armor) per live unit — the snapshot projection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="units"/> is null.</exception>
    /// <exception cref="ArgumentException">A blank content id.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative armor value.</exception>
    public static IReadOnlyList<WaveCompositionRow> Aggregate(IEnumerable<(string EnemyId, int Armor)> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var bySpecies = new SortedDictionary<string, (int Count, int Min, int Max)>();
        foreach ((string enemyId, int armor) in units)
        {
            if (string.IsNullOrWhiteSpace(enemyId))
            {
                throw new ArgumentException("unit content id must be non-blank.", nameof(units));
            }

            if (armor < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(units), armor, "armor must be >= 0.");
            }

            if (bySpecies.TryGetValue(enemyId, out (int Count, int Min, int Max) row))
            {
                bySpecies[enemyId] = (row.Count + 1, Math.Min(row.Min, armor), Math.Max(row.Max, armor));
            }
            else
            {
                bySpecies[enemyId] = (1, armor, armor);
            }
        }

        return bySpecies.Select(pair =>
            new WaveCompositionRow(pair.Key, pair.Value.Count, pair.Value.Min, pair.Value.Max)).ToList();
    }

    /// <summary>
    /// Projects the read-only next-wave preview from the WaveSpawner plan
    /// numbers: the regular count is split evenly across the roster (the
    /// exact per-species cycling offset lives in the spawner's live spawn
    /// counter and is unknowable from a read-only projection — the even
    /// split is the canonical estimate the task card freezes), then the
    /// elite line and boss are appended as their own rows. Armor comes from
    /// the content catalog (threat scales hp, never armor — v3.2 §6).
    /// </summary>
    /// <param name="roster">Regular-line roster ids (cycled deterministically at spawn).</param>
    /// <param name="regularCount">Regular-line total of the previewed wave (WaveFormula.Count output).</param>
    /// <param name="eliteEnemyId">Elite species id; null/blank = no elite line.</param>
    /// <param name="eliteCount">Elite-line total of the previewed wave.</param>
    /// <param name="bossEnemyId">Boss species id; null/blank = no boss batch.</param>
    /// <param name="armorOf">Armor lookup into the enemy catalog (pure data access).</param>
    /// <exception cref="ArgumentNullException"><paramref name="roster"/> or <paramref name="armorOf"/> is null.</exception>
    /// <exception cref="ArgumentException">Empty roster, a blank roster id, or a negative count.</exception>
    public static IReadOnlyList<WaveCompositionRow> Preview(
        IReadOnlyList<string> roster,
        int regularCount,
        string? eliteEnemyId,
        int eliteCount,
        string? bossEnemyId,
        Func<string, int> armorOf)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(armorOf);
        if (roster.Count == 0)
        {
            throw new ArgumentException("roster must not be empty.", nameof(roster));
        }

        foreach (string id in roster)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("roster entries must be non-blank ids.", nameof(roster));
            }
        }

        if (regularCount < 0 || eliteCount < 0)
        {
            throw new ArgumentException($"counts must be >= 0 (regular {regularCount}, elite {eliteCount}).");
        }

        var rows = new List<WaveCompositionRow>();
        int baseShare = regularCount / roster.Count;
        int remainder = regularCount % roster.Count;
        for (int index = 0; index < roster.Count; index++)
        {
            int count = baseShare + (index < remainder ? 1 : 0);
            if (count > 0)
            {
                int armor = armorOf(roster[index]);
                rows.Add(new WaveCompositionRow(roster[index], count, armor, armor));
            }
        }

        if (!string.IsNullOrWhiteSpace(eliteEnemyId) && eliteCount > 0)
        {
            int eliteArmor = armorOf(eliteEnemyId);
            rows.Add(new WaveCompositionRow(eliteEnemyId, eliteCount, eliteArmor, eliteArmor));
        }

        if (!string.IsNullOrWhiteSpace(bossEnemyId))
        {
            int bossArmor = armorOf(bossEnemyId);
            rows.Add(new WaveCompositionRow(bossEnemyId, 1, bossArmor, bossArmor));
        }

        return rows;
    }

    /// <summary>One species line, name-resolved: "<c>兽崽×3(甲0)</c>" / "<c>兽崽×3(甲0~2)</c>" (invariant digits).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="nameOf"/> is null.</exception>
    public static string FormatRow(WaveCompositionRow row, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(nameOf);
        string armor = row.ArmorMin == row.ArmorMax
            ? row.ArmorMin.ToString(CultureInfo.InvariantCulture)
            : row.ArmorMin.ToString(CultureInfo.InvariantCulture)
                + "~" + row.ArmorMax.ToString(CultureInfo.InvariantCulture);
        return $"{nameOf(row.EnemyId)}×{row.Count.ToString(CultureInfo.InvariantCulture)}(甲{armor})";
    }

    /// <summary>Full composition line (space-joined rows); empty input renders "<c>—</c>" (no contact yet).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> or <paramref name="nameOf"/> is null.</exception>
    public static string FormatSummary(IReadOnlyList<WaveCompositionRow> rows, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(nameOf);
        return rows.Count == 0
            ? "—"
            : string.Join(" ", rows.Select(row => FormatRow(row, nameOf)));
    }

    /// <summary>
    /// Compact next-wave digest for the cramped HUD field:
    /// "<c>12敌·3种·甲0~3</c>" — totals over the preview rows; empty renders
    /// "<c>—</c>". Invariant digits, so the monospace field never drifts by locale.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public static string FormatDigest(IReadOnlyList<WaveCompositionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return "—";
        }

        int total = rows.Sum(row => row.Count);
        int min = rows.Min(row => row.ArmorMin);
        int max = rows.Max(row => row.ArmorMax);
        string armor = min == max
            ? min.ToString(CultureInfo.InvariantCulture)
            : min.ToString(CultureInfo.InvariantCulture)
                + "~" + max.ToString(CultureInfo.InvariantCulture);
        return $"{total.ToString(CultureInfo.InvariantCulture)}敌"
            + $"·{rows.Count.ToString(CultureInfo.InvariantCulture)}种·甲{armor}";
    }
}
