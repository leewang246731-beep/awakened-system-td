namespace EraCore.Procedural;

/// <summary>
/// Parsed <c>game/content/campaign/procedural.json</c> — the 战役外程序关
/// entry data slot (T-140): the campaign-side registry row that pins WHERE
/// procedural levels plug in (the standalone entry block), the generator's
/// constraint defaults and the difficulty tier ladder (RL7 — every pacing
/// number lives here, the EraCore generator carries none). Pure data +
/// validation, host-owned IO (the test harness parses the file, the
/// presentation host does the same at boot).
/// </summary>
/// <param name="Id">Entry id (e.g. "campaign:procedural-entry").</param>
/// <param name="NameZh">Entry label (Chinese).</param>
/// <param name="NameEn">Entry label (English).</param>
/// <param name="Eras">Era tags the entry is open to (subset check against the loaded era packs — 动态枚举纪律).</param>
/// <param name="DefaultTierId">Tier id a bare entry request falls back to; must exist in <paramref name="Tiers"/>.</param>
/// <param name="DurationBandMinutesMin">Acceptance band lower bound (minutes) — the ≥10 局中位 gate the duration chain asserts against.</param>
/// <param name="DurationBandMinutesMax">Acceptance band upper bound (minutes).</param>
/// <param name="Constraints">Constraint set handed to every generation under this entry.</param>
/// <param name="Tiers">Difficulty tier ladder (threat derived per tier via the §7 formula).</param>
public sealed record ProceduralCampaignEntry(
    string Id,
    string NameZh,
    string NameEn,
    IReadOnlyList<string> Eras,
    string DefaultTierId,
    int DurationBandMinutesMin,
    int DurationBandMinutesMax,
    ProceduralConstraints Constraints,
    IReadOnlyList<ProceduralTier> Tiers)
{
    /// <summary>
    /// Validates the entry: non-blank ids/labels, a non-empty era tag list,
    /// a positive duration band, constraints that self-validate, ≥1 tier,
    /// unique tier ids, the default tier present, slots unique, and every
    /// tier self-validating.
    /// </summary>
    /// <exception cref="ArgumentException">Any rule above is violated.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(NameZh)
            || string.IsNullOrWhiteSpace(NameEn))
        {
            throw new ArgumentException("Entry id and labels must not be blank.");
        }

        ArgumentNullException.ThrowIfNull(Eras);
        if (Eras.Count == 0 || Eras.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The entry era list must be non-empty and blank-free.");
        }

        if (DurationBandMinutesMin < 1 || DurationBandMinutesMax < DurationBandMinutesMin)
        {
            throw new ArgumentException(
                $"The duration band is malformed ({DurationBandMinutesMin}..{DurationBandMinutesMax} minutes).");
        }

        Constraints.Validate();
        ArgumentNullException.ThrowIfNull(Tiers);
        if (Tiers.Count == 0)
        {
            throw new ArgumentException("The tier ladder must not be empty.");
        }

        foreach (ProceduralTier tier in Tiers)
        {
            tier.Validate();
        }

        if (Tiers.Select(tier => tier.Id).Distinct(StringComparer.Ordinal).Count() != Tiers.Count)
        {
            throw new ArgumentException("Tier ids must be unique.");
        }

        if (Tiers.All(tier => tier.Id != DefaultTierId))
        {
            throw new ArgumentException($"Default tier '{DefaultTierId}' is not in the ladder.");
        }

        if (Tiers.Select(tier => tier.Slot).Distinct().Count() != Tiers.Count)
        {
            throw new ArgumentException("Tier slots must be unique.");
        }
    }

    /// <summary>
    /// Tier row by id; null when absent.
    /// </summary>
    public ProceduralTier? TryGetTier(string tierId) =>
        Tiers.FirstOrDefault(tier => tier.Id == tierId);

    /// <summary>
    /// Resolves the era tags against the loaded era content packs
    /// (动态枚举纪律 — the entry never hardcodes the era domain): a tag is
    /// open when a pack carries it. Returns the open subset in entry order;
    /// an empty result means the host has none of the entry's eras loaded
    /// (the entry stays hidden, not broken).
    /// </summary>
    public IReadOnlyList<string> OpenEras(IReadOnlyList<ProceduralEraContent> loadedEras)
    {
        ArgumentNullException.ThrowIfNull(loadedEras);
        return Eras.Where(tag => loadedEras.Any(pack => pack.EraId.StartsWith(
            tag + ":", StringComparison.Ordinal))).ToArray();
    }
}
