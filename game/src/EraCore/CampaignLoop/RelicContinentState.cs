using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Legacy;

namespace EraCore.CampaignLoop;

/// <summary>
/// The campaign-shell view of the CURRENT era's relic continent (T-125,
/// F-057/F-168 正式版): after a committed era transition, the new
/// continent is NOT a test fixture — this state re-materializes it from
/// the persisted campaign document through the SAME production channel
/// the transaction used (<see cref="NewContinentFactory"/> + decoded
/// snapshot payload + <see cref="RelicMapper"/> + <see
/// cref="RelicDigSites"/>). The two-level-save architecture makes this
/// exact: the continent derives deterministically from (WorldSeed, era),
/// the relic-city snapshot travels as a layout payload, so the replayed
/// mapping/dig-site plan at load time equals the transaction-time one
/// byte for byte (A5: only persisted facts are read — the snapshot zone
/// set is decoded, never recomputed).
/// </summary>
public sealed class RelicContinentState
{
    /// <summary>E1 has no relic continent (nothing has transitioned yet).</summary>
    public const int EarliestRelicEra = 2;

    private RelicContinentState(
        int eraIndex,
        NewContinent? continent,
        RelicMappingResult? mapping,
        RelicDigSitePlan? digSites)
    {
        EraIndex = eraIndex;
        Continent = continent;
        Mapping = mapping;
        DigSites = digSites;
    }

    /// <summary>Current era index of the campaign document.</summary>
    public int EraIndex { get; }

    /// <summary>The re-materialized continent (null for E1 — no transition yet).</summary>
    public NewContinent? Continent { get; }

    /// <summary>The replayed mapping result (null for E1).</summary>
    public RelicMappingResult? Mapping { get; }

    /// <summary>The dig-site plan of degraded ruins (null for E1).</summary>
    public RelicDigSitePlan? DigSites { get; }

    /// <summary>True when the campaign document carries a relic continent (era ≥ 2).</summary>
    public bool HasContinent => Continent is not null;

    /// <summary>
    /// Re-materializes the relic continent of a persisted campaign
    /// document. For era 1 this returns the empty state (no continent);
    /// for era ≥ 2 the document MUST carry the previous era's relic-city
    /// snapshot payload (slot <c>legacy:relic-city-era{era-1}</c>, written
    /// by the transition's single campaign commit) — a missing or damaged
    /// payload is a corrupt save, not an empty continent.
    /// </summary>
    /// <param name="meta">The campaign document (K-7 CAMPAIGN domain read).</param>
    /// <param name="mapping">The relic-mapping parameter table.</param>
    /// <param name="rng">Random port — reseeded with the derived continent seed inside.</param>
    /// <exception cref="ArgumentNullException">Any argument null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Era below 1, or the document of an era ≥ 2 campaign lacks the
    /// relic-city snapshot slot of the era it left.
    /// </exception>
    public static RelicContinentState Rehydrate(
        ICampaignMeta meta, RelicMappingParams mapping, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(rng);
        if (meta.EraIndex < 1)
        {
            throw new InvalidOperationException($"Campaign document era {meta.EraIndex} is below E1.");
        }

        if (meta.EraIndex < EarliestRelicEra)
        {
            return new RelicContinentState(meta.EraIndex, continent: null, mapping: null, digSites: null);
        }

        string slotId = EraTransitionService.RelicCitySlotId(meta.EraIndex - 1);
        byte[]? payload = meta.LevelLayoutSnapshots
            .Where(layout => string.Equals(layout.LevelId, slotId, StringComparison.Ordinal))
            .Select(layout => layout.LayoutPayload)
            .FirstOrDefault();
        if (payload is null)
        {
            throw new InvalidOperationException(
                $"Campaign of era {meta.EraIndex} carries no '{slotId}' snapshot payload — the transition commit that produced it is missing.");
        }

        RelicCitySnapshot snapshot = RelicCityCodec.Decode(payload);
        NewContinent continent = NewContinentFactory.Create(mapping, meta.WorldSeed, meta.EraIndex, rng);
        RelicMappingResult result = new RelicMapper(mapping).Map(continent, snapshot);
        RelicDigSitePlan digSites = RelicDigSites.Build(continent, result);
        return new RelicContinentState(meta.EraIndex, continent, result, digSites);
    }
}
