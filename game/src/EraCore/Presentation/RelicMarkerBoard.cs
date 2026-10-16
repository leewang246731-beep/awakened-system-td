using EraCore.Contracts.Foundation;
using EraCore.Legacy;

namespace EraCore.Presentation;

/// <summary>
/// Kind of one new-era map relic marker (T-125, F-057/F-168): the
/// UI-visible face of the relic-mapping production channel.
/// </summary>
public enum RelicMarkerKind
{
    /// <summary>Standing relic (回放: 原位/移位) — a preserved building of the old era.</summary>
    StandingRelic = 0,

    /// <summary>Degraded ruin dig site (降级废墟的考古挖掘位).</summary>
    DigSite = 1,

    /// <summary>Memorial repair slot (纪念馆修复位 — unfilled dig site).</summary>
    MemorialSlot = 2,

    /// <summary>Core-reserve outline cell (A5 核心保留区边界的勾勒格).</summary>
    CoreReserveOutline = 3,
}

/// <summary>
/// One map marker decision (T-125): everything the new-era map UI draws
/// for one relic feature — kind, cell, display label and the texture
/// file stem under <c>textures/relic/</c>. Pure data derived from the
/// mapping outputs; the Godot side mirrors it, never re-derives it
/// (BeltRenderDecisions / ZoneLabels 先例：决策全在纯逻辑层, headless 可全测).
/// </summary>
/// <param name="Kind">Marker kind (drives texture + interaction).</param>
/// <param name="Cell">New-continent cell.</param>
/// <param name="Label">Display label (Chinese, content ids kept verbatim).</param>
/// <param name="Tier">Building tier 1..4 (0 for zone outline cells).</param>
/// <param name="TextureStem">Texture file stem under textures/relic/ (kebab-case, G2.5 naming rule).</param>
public sealed record RelicMarker(
    RelicMarkerKind Kind,
    CellCoord Cell,
    string Label,
    int Tier,
    string TextureStem);

/// <summary>
/// New-era map marker board (T-125): turns the relic-mapping production
/// outputs (continent + mapping result + dig-site plan) into the marker
/// plan the campaign map UI renders — standing relics with their
/// tier/type, dig sites of degraded ruins, memorial repair slots (dig
/// sites minus the slots already consumed by registered memorials), and
/// the outline of the reserved core zone. Deterministic pure function:
/// same inputs ⇒ same plan (row-major emission order).
/// </summary>
public static class RelicMarkerBoard
{
    /// <summary>Texture stem of the standing-relic marker.</summary>
    public const string StandingRelicTexture = "relic-standing";

    /// <summary>Texture stem of the dig-site marker.</summary>
    public const string DigSiteTexture = "relic-dig-site";

    /// <summary>Texture stem of the memorial-slot marker.</summary>
    public const string MemorialSlotTexture = "relic-memorial";

    /// <summary>Texture stem of the core-reserve outline tile.</summary>
    public const string CoreReserveTexture = "relic-reserve";

    /// <summary>
    /// Builds the full marker plan. Emission order (deterministic):
    /// standing relics in replay order, then dig sites in plan order,
    /// then unfilled memorial slots, then reserve-outline cells
    /// row-major. The reserve outline is the set of reserved cells with
    /// at least one 4-neighbour OUTSIDE the reserve — the drawn boundary,
    /// not all 441 cells of a radius-10 zone.
    /// </summary>
    /// <param name="continent">The production channel's continent.</param>
    /// <param name="mapping">The replayed mapping result.</param>
    /// <param name="digSites">The dig-site plan (may be null when the caller replays without sites).</param>
    /// <param name="memorialsRegistered">Memorials already standing (their repair slots are consumed).</param>
    /// <exception cref="ArgumentNullException">continent or mapping null.</exception>
    public static IReadOnlyList<RelicMarker> Build(
        NewContinent continent,
        RelicMappingResult mapping,
        RelicDigSitePlan? digSites = null,
        int memorialsRegistered = 0)
    {
        ArgumentNullException.ThrowIfNull(continent);
        ArgumentNullException.ThrowIfNull(mapping);
        var markers = new List<RelicMarker>();

        foreach (RelicRelayOutcome outcome in mapping.Outcomes)
        {
            if (outcome.Kind is RelayOutcomeKind.IntactHome or RelayOutcomeKind.IntactShifted
                && outcome.Cell is not null)
            {
                markers.Add(new RelicMarker(
                    RelicMarkerKind.StandingRelic,
                    outcome.Cell,
                    $"遗迹·{outcome.TypeId} T{outcome.Tier}",
                    outcome.Tier,
                    StandingRelicTexture));
            }
        }

        if (digSites is not null)
        {
            foreach (RelicDigSite site in digSites.Sites)
            {
                markers.Add(new RelicMarker(
                    RelicMarkerKind.DigSite,
                    site.Cell,
                    $"废墟·{site.TypeId} T{site.Tier}",
                    site.Tier,
                    DigSiteTexture));
            }

            for (int slot = memorialsRegistered; slot < digSites.Sites.Count; slot++)
            {
                RelicDigSite site = digSites.Sites[slot];
                markers.Add(new RelicMarker(
                    RelicMarkerKind.MemorialSlot,
                    site.Cell,
                    $"修复位·{site.TypeId} T{site.Tier}",
                    site.Tier,
                    MemorialSlotTexture));
            }
        }

        markers.AddRange(ReserveOutline(continent));
        return markers;
    }

    /// <summary>
    /// The reserve-zone outline cells: reserved cells (Chebyshev square of
    /// the anchor) whose 4-neighbourhood leaves the reserve — row-major
    /// order. This is the outline the UI strokes the 核心保留区 with.
    /// </summary>
    public static IReadOnlyList<RelicMarker> ReserveOutline(NewContinent continent)
    {
        ArgumentNullException.ThrowIfNull(continent);
        var reserve = new HashSet<CellCoord>(continent.ReserveCells);
        var outline = new List<RelicMarker>();
        foreach (CellCoord cell in continent.ReserveCells
                     .OrderBy(cell => cell.Y)
                     .ThenBy(cell => cell.X))
        {
            // 4-neighbour leaves the reserve ⇒ boundary cell (CampaignGrid 4 邻接同款几何)
            bool edge = (cell.X > 0 && !reserve.Contains(new CellCoord(cell.X - 1, cell.Y)))
                || (cell.Y > 0 && !reserve.Contains(new CellCoord(cell.X, cell.Y - 1)))
                || !reserve.Contains(new CellCoord(cell.X + 1, cell.Y))
                || !reserve.Contains(new CellCoord(cell.X, cell.Y + 1));
            if (edge)
            {
                outline.Add(new RelicMarker(
                    RelicMarkerKind.CoreReserveOutline,
                    cell,
                    "核心保留区",
                    Tier: 0,
                    CoreReserveTexture));
            }
        }

        return outline;
    }
}
