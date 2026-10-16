using EraCore.Contracts.Foundation;
using EraCore.World;

namespace EraCore.Legacy;

/// <summary>
/// One anchored dig site of a degraded ruin (T-125, F-057/F-168): the F.7
/// 冲突降级 rung leaves the building as an abstract-list archaeology
/// entry — this record gives that ruin a MAP PRESENCE on the new
/// continent, anchored where the building would have stood (anchor +
/// relative coordinate). The site is the dig target of the archaeology
/// service AND the in-place repair position when a dig rolls the
/// restoration channel as a memorial (废墟原位修复, 纪念馆修复位).
/// </summary>
/// <param name="BuildingId">Old building id (matches the abstract-list entry).</param>
/// <param name="TypeId">Old building type id.</param>
/// <param name="Tier">Old building tier (drives <see cref="ArchaeologyService.Quote"/>).</param>
/// <param name="Cell">Anchor cell on the new continent (in bounds).</param>
/// <param name="Cause">Conflict cause that degraded the building (terrain/resource/oob — the ruin's story).</param>
/// <param name="InCoreZone">A5 core-zone membership (persisted-set authority).</param>
/// <param name="OccupancyRegistered">Whether world occupancy was registered for the site — false only when the home cell is already held (a resource node occupies it; the ruin coexists, no double registration).</param>
public sealed record RelicDigSite(
    long BuildingId,
    string TypeId,
    int Tier,
    CellCoord Cell,
    string Cause,
    bool InCoreZone,
    bool OccupancyRegistered);

/// <summary>
/// The dig-site plan of one mapping pass (T-125): every degraded ruin with
/// an in-bounds home cell becomes an anchored dig site (entity ids from
/// <see cref="DigSiteEntityBase"/>, occupancy registered on free cells so
/// gameplay cannot build over a ruin); the memorial repair slots are the
/// site cells in value order (a restoration-channel memorial stands where
/// the ruin stood). Ruins without map presence (out of bounds, or home
/// cell held by a resource node) stay abstract-list-only — the plan
/// counts them so the no-loss ledger still partitions every building.
/// </summary>
/// <param name="Sites">Anchored dig sites, value order of the replay pass.</param>
/// <param name="UnanchoredRuinCount">Ruins without a map anchor (abstract-list only).</param>
public sealed record RelicDigSitePlan(
    IReadOnlyList<RelicDigSite> Sites,
    int UnanchoredRuinCount)
{
    /// <summary>Total degraded ruins this plan accounts for (anchored + unanchored).</summary>
    public int TotalRuins => Sites.Count + UnanchoredRuinCount;

    /// <summary>
    /// Memorial repair slots (纪念馆修复位): the site cells in value order —
    /// restoration-channel dig N repairs in place, memorial ordinal N+1
    /// anchors at slot N (registration order mirror of
    /// <see cref="MemorialAuraBoard"/>).
    /// </summary>
    public IReadOnlyList<CellCoord> MemorialRepairSlots =>
        Sites.Select(site => site.Cell).ToList();
}

/// <summary>
/// Dig-site generation (T-125, F-057/F-168 产品化): turns a replay pass's
/// degraded ruins into map entities on the SAME continent the transaction
/// generated — the production counterpart of the spike③ abstract list,
/// which held ruins as strings only. Pure function of (continent,
/// mapping result): same inputs ⇒ same plan (deterministic, CS-1.4 — no
/// randomness here at all).
/// </summary>
public static class RelicDigSites
{
    /// <summary>
    /// Entity-id base of dig sites (occupancy registrations on the new
    /// world) — disjoint from standing relics (800_000+) and resource
    /// nodes (900_000+).
    /// </summary>
    public const long DigSiteEntityBase = 700_000;

    /// <summary>
    /// Builds the dig-site plan over one mapping pass, registering world
    /// occupancy for every anchored site whose home cell is free (the
    /// resource-conflict rows share their cell with the node — no double
    /// registration, the ruin is still diggable).
    /// </summary>
    /// <exception cref="ArgumentNullException">continent or mapping is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The plan broke the ruin ledger (anchored + unanchored ≠ ruin count —
    /// never expected; guards the partition).
    /// </exception>
    public static RelicDigSitePlan Build(NewContinent continent, RelicMappingResult mapping)
    {
        ArgumentNullException.ThrowIfNull(continent);
        ArgumentNullException.ThrowIfNull(mapping);
        GridWorld world = continent.World;
        var sites = new List<RelicDigSite>();
        int unanchored = 0;
        foreach (RelicRelayOutcome outcome in mapping.Outcomes)
        {
            if (outcome.Kind != RelayOutcomeKind.RuinDowngraded)
            {
                continue;
            }

            CellCoord home = outcome.HomeCell
                ?? throw new InvalidOperationException(
                    $"Ruin outcome of building {outcome.BuildingId} carries no home cell — the replay pass predates the dig-site contract.");
            if (!world.Contains(home))
            {
                unanchored++; // oob: abstract-list only (spike3 坑 5 口径)
                continue;
            }

            bool registered = false;
            if (!world.IsOccupied(home))
            {
                world.RegisterOccupancy(new EntityId(DigSiteEntityBase + sites.Count + 1), new[] { home });
                registered = true;
            }

            sites.Add(new RelicDigSite(
                outcome.BuildingId, outcome.TypeId, outcome.Tier, home,
                outcome.Cause, outcome.InCoreZone, registered));
        }

        var plan = new RelicDigSitePlan(sites, unanchored);
        if (plan.TotalRuins != mapping.RuinCount)
        {
            throw new InvalidOperationException(
                $"Dig-site plan partition broken: anchored {sites.Count} + unanchored {unanchored} ≠ ruin count {mapping.RuinCount}.");
        }

        return plan;
    }
}
