using System.Text.Json;
using EraCore.Contracts.Content;

namespace EraCore.Legacy;

/// <summary>
/// Mapping parameter table of the C-20 legacy subsystem (T-074): the
/// spike③ recommended set (spike3-report §6) landed as content — A5 core
/// radius/margin, the F.7 移位 rung radius, and the new-continent
/// generation knobs (ratios, node counts, the terrain-让位 radius). Every
/// number is data (CS-2.3); the engine never re-hardcodes it.
/// </summary>
/// <param name="CoreRadiusCells">A5 core-zone Chebyshev radius (切比雪夫半径 10 格, v3.2 §7).</param>
/// <param name="BuildMarginCells">A5 built-area margin (已建区外扩 2 格).</param>
/// <param name="ReserveAreaCapFraction">Upper bound of the reserved new-continent core fraction (threat 让位 cap).</param>
/// <param name="ShiftRadiusCells">F.7 冲突降级 移位 rung radius (spike3 §6 推荐 2).</param>
/// <param name="MapSizeCells">New-continent side in cells.</param>
/// <param name="ForestRatio">Terrain draw ratio (GridWorld 口径).</param>
/// <param name="RockRatio">Terrain draw ratio.</param>
/// <param name="WaterRatio">Terrain draw ratio.</param>
/// <param name="ResourceNodeCount">Resource nodes placed on the new continent.</param>
/// <param name="TerrainYieldRadiusCells">Terrain 让位 radius: water/rock inside it become plain (spike3 §6 推荐 1).</param>
/// <param name="ResourceAvoidYieldZone">Resource nodes must not spawn inside the yield zone (spike3 §6 推荐 2).</param>
/// <param name="ThreatNodeCount">Threat nodes placed OUTSIDE the reserved core (让位演示旋钮, T-074).</param>
/// <param name="Orientations">Valid building facings (quarter turns, v3.2 §7).</param>
public sealed record RelicMappingParams(
    int CoreRadiusCells,
    int BuildMarginCells,
    double ReserveAreaCapFraction,
    int ShiftRadiusCells,
    int MapSizeCells,
    double ForestRatio,
    double RockRatio,
    double WaterRatio,
    int ResourceNodeCount,
    int TerrainYieldRadiusCells,
    bool ResourceAvoidYieldZone,
    int ThreatNodeCount,
    IReadOnlyList<int> Orientations);

/// <summary>
/// One era wonder (T-074, appendix F.3): the six fixed wonders, each the
/// hard transition precondition of its era and the carrier of one global
/// bonus. Loaded from <c>game/content/legacy/wonders.json</c>.
/// </summary>
/// <param name="Id">Registry id (<c>campaign:wonder-pyramid</c>).</param>
/// <param name="EraIndex">Era the wonder belongs to (1=E1 … 6=终章).</param>
/// <param name="TechNode">Tech node id that unlocks building it (content cross-reference).</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Aliases">Colloquial names (commander grammar seam).</param>
/// <param name="BonusKind">Bonus channel key (<c>resource_cap</c>/<c>research_cost</c>/…).</param>
/// <param name="BonusValue">Bonus magnitude (units in the content <c>_units</c> block).</param>
public sealed record WonderSpec(
    string Id,
    int EraIndex,
    string TechNode,
    string NameZh,
    string NameEn,
    IReadOnlyList<string> Aliases,
    string BonusKind,
    double BonusValue);

/// <summary>
/// K-2 registry entry of one wonder (T-074): wraps a <see cref="WonderSpec"/>
/// as an <see cref="IContentItem"/> under <see cref="ContentType.Wonder"/>
/// so grammar/UI lookups share the standard pipeline (CS-2.5).
/// </summary>
public sealed class WonderContentItem : IContentItem
{
    /// <summary>The wrapped content row.</summary>
    public WonderSpec Spec { get; }

    /// <summary>Creates the registry wrapper of one wonder row.</summary>
    public WonderContentItem(WonderSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
    }

    /// <inheritdoc />
    public string Id => Spec.Id;

    /// <inheritdoc />
    public ContentType ContentType => ContentType.Wonder;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases => Spec.Aliases;
}

/// <summary>
/// One knowledge-scroll row of the archaeology table (T-074, F-058/F-099):
/// reading the scroll restores one fragment of the system's lost memory and
/// grants a research-cost relief on ONE tech node (同一节点至多 1 张, F.3).
/// </summary>
/// <param name="Id">Scroll id (<c>legacy:scroll-mem-e1-siege</c>).</param>
/// <param name="TechNode">Tech node the relief applies to.</param>
/// <param name="ReliefShare">Relief share of the research formula total (fraction discount).</param>
/// <param name="MemoryZh">Restored memory fragment, Chinese (F-099 narrative payload).</param>
/// <param name="MemoryEn">Restored memory fragment, English.</param>
public sealed record KnowledgeScrollSpec(
    string Id,
    string TechNode,
    double ReliefShare,
    string MemoryZh,
    string MemoryEn);

/// <summary>One ancient-blueprint row (F-058 跨时代混合建筑解锁; first batch in content).</summary>
/// <param name="Id">Blueprint id.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Mix">The cross-era building mix the blueprint unlocks (era构件 ids).</param>
public sealed record AncientBlueprintSpec(
    string Id,
    string NameZh,
    string NameEn,
    IReadOnlyList<string> Mix);

/// <summary>Dig economics of one archaeology site (F-058 人力/能量/时长, 30 Hz drill domain).</summary>
/// <param name="ManpowerCost">Manpower points per dig.</param>
/// <param name="EnergyCost">Energy points per dig.</param>
/// <param name="MinDurationSeconds">Shortest possible dig (tier-less fallback lower bound).</param>
/// <param name="MaxDurationSeconds">Longest possible dig (tier-less fallback upper bound).</param>
/// <param name="TierDurationSeconds">Per-site-tier duration table (1..4 → seconds).</param>
/// <param name="DrillTicksPerSecond">Drill resolution, aligned to the K-1 30 Hz tick domain.</param>
public sealed record DigSpec(
    int ManpowerCost,
    int EnergyCost,
    int MinDurationSeconds,
    int MaxDurationSeconds,
    IReadOnlyDictionary<int, int> TierDurationSeconds,
    int DrillTicksPerSecond);

/// <summary>Restoration channel parameters (F-059/07 #28 + F.3 纪念馆光环).</summary>
/// <param name="WreckRepairCostFraction">Repair cost as a fraction of the wreck value (×0.3).</param>
/// <param name="NewEraConversionRate">New-era conversion multiplier of the repair cost (×0.8 叠加).</param>
/// <param name="FunctionalWeight">Draw weight of the 折算建筑 outcome.</param>
/// <param name="MemorialWeight">Draw weight of the 纪念馆 outcome.</param>
/// <param name="MemorialAuras">Memorial aura values by kind (取最大, 至多 3 座生效 — enforced by the boards).</param>
public sealed record RestorationSpec(
    double WreckRepairCostFraction,
    double NewEraConversionRate,
    double FunctionalWeight,
    double MemorialWeight,
    IReadOnlyDictionary<string, double> MemorialAuras);

/// <summary>
/// The archaeology table (T-074, F-058): dig economics, the closed
/// three-channel output table and the restoration parameters. Channel keys
/// are a closed vocabulary: <c>knowledge_scroll</c>/<c>ancient_blueprint</c>/<c>restoration</c>.
/// </summary>
/// <param name="Dig">Dig economics.</param>
/// <param name="Channels">Output channel weights in content order (cumulative walk is deterministic).</param>
/// <param name="Scrolls">Knowledge-scroll catalog.</param>
/// <param name="Blueprints">Ancient-blueprint catalog.</param>
/// <param name="Restoration">Restoration channel parameters.</param>
public sealed record ArchaeologyTable(
    DigSpec Dig,
    IReadOnlyList<(string Channel, double Weight)> Channels,
    IReadOnlyList<KnowledgeScrollSpec> Scrolls,
    IReadOnlyList<AncientBlueprintSpec> Blueprints,
    RestorationSpec Restoration);

/// <summary>
/// One 传承商店 shelf row (T-074, F-011/07 #23): a decorative blueprint
/// redeemable with civilization-fund units. Decorative only — no DPS/cost/
/// system-point/loot/research effect may ride on it (D21-7 family).
/// </summary>
/// <param name="Id">Item id (<c>legacy:shop-bp-gilded-granary</c>).</param>
/// <param name="Kind">Item kind (<c>decorative_blueprint</c>).</param>
/// <param name="PriceFund">Price in civilization-fund units.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Aliases">Colloquial names.</param>
public sealed record HeritageShopItem(
    string Id,
    string Kind,
    int PriceFund,
    string NameZh,
    string NameEn,
    IReadOnlyList<string> Aliases);

/// <summary>The 传承商店 shelf (T-074): id-sorted decorative redemption rows.</summary>
/// <param name="Items">Shelf rows sorted by id (deterministic order, K-2 family).</param>
public sealed record HeritageShopTable(IReadOnlyList<HeritageShopItem> Items);

/// <summary>One first-three schedule row of one target era (v3.2 §4: 关 1 新能源/关 2 新物流/关 3 考古+英雄+系统新能力).</summary>
/// <param name="Slot">1-based slot inside the new era.</param>
/// <param name="TargetEra">Era index the schedule belongs to — the jump's TARGET era (2 for the E1→E2 rows, 3 for the E2→E3 rows, T-117).</param>
/// <param name="LevelId">Campaign level id of the slot.</param>
/// <param name="Focus">Focus key (<c>new_energy</c>/<c>new_logistics</c>/<c>archaeology_hero_system</c>).</param>
public sealed record EraFirstThreeSlot(int Slot, int TargetEra, string LevelId, string Focus);

/// <summary>
/// The era-transition parameter table (T-074, F-003/F-011; E2→E3 rows =
/// T-117): hard preconditions, the 30% levy with the 3:1 useless-resource
/// fold, the warehouse-level gain, the per-era final-level ids and the
/// first-three schedules keyed by the jump's TARGET era.
/// </summary>
/// <param name="RequiresFinalLevel">终局关 captured is a hard precondition.</param>
/// <param name="RequiresWonder">Era wonder built is a hard precondition (附录 F.3).</param>
/// <param name="CarriedShare">Carried share of every pooled resource at the jump (0.3; 留存侧恰 70%).</param>
/// <param name="UselessFoldDivisor">Old-resource units per civilization-fund unit (3:1).</param>
/// <param name="WarehouseLevelGain">Warehouse levels gained per transition (v3.2 §5 自动 +1).</param>
/// <param name="PyramidBonusCarriesOver">Whether the pyramid +20% cap term survives the jump (relic city keeps the wonder standing).</param>
/// <param name="FinalLevels">Final-level id per era index (终局关; key = the era being LEFT).</param>
/// <param name="FirstThreeSchedule">Schedule rows of EVERY reachable target era's first three levels (rows carry <see cref="EraFirstThreeSlot.TargetEra"/>).</param>
public sealed record EraTransitionSpec(
    bool RequiresFinalLevel,
    bool RequiresWonder,
    double CarriedShare,
    int UselessFoldDivisor,
    int WarehouseLevelGain,
    bool PyramidBonusCarriesOver,
    IReadOnlyDictionary<int, string> FinalLevels,
    IReadOnlyList<EraFirstThreeSlot> FirstThreeSchedule)
{
    /// <summary>
    /// The schedule rows of one jump's target era, slot-ordered (T-117): the
    /// rows whose <see cref="EraFirstThreeSlot.TargetEra"/> equals
    /// <paramref name="targetEraIndex"/>. Empty when the table declares no
    /// rows for that era (the E3→E4+ rows are future content).
    /// </summary>
    public IReadOnlyList<EraFirstThreeSlot> ScheduleFor(int targetEraIndex) =>
        FirstThreeSchedule.Where(slot => slot.TargetEra == targetEraIndex)
            .OrderBy(slot => slot.Slot)
            .ToList();
}

/// <summary>
/// Strict loader of the C-20 legacy content files (T-074): parses and
/// validates the five JSON documents of <c>game/content/legacy/</c> —
/// relic_mapping / wonders / archaeology / heritage_shop / era_transition.
/// Annotation keys prefixed <c>_</c> are ignored (repo-wide convention);
/// malformed files throw — a red test, never a silent partial load
/// (same contract as <c>ScrollDataLoader</c>).
/// </summary>
public static class LegacyDataLoader
{
    /// <summary>Closed channel vocabulary of the archaeology table (F-058 三通道).</summary>
    public static readonly IReadOnlyList<string> ChannelKeys = new[]
    {
        "knowledge_scroll", "ancient_blueprint", "restoration",
    };

    /// <summary>Parses and validates the relic-mapping parameter document.</summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the field).</exception>
    public static RelicMappingParams ParseRelicMapping(string json)
    {
        JsonElement root = ParseRoot(json, nameof(ContentType.DropTable));
        JsonElement core = ObjectOf(root, "core_zone");
        JsonElement replay = ObjectOf(root, "replay");
        JsonElement continent = ObjectOf(root, "new_continent");
        JsonElement orientations = ArrayOf(root, "orientations_quarters");

        var facing = new List<int>();
        foreach (JsonElement element in orientations.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || element.GetInt32() < 0)
            {
                throw new InvalidOperationException("relic_mapping orientations_quarters must be non-negative integers.");
            }

            facing.Add(element.GetInt32());
        }

        if (facing.Count == 0 || facing.Distinct().Count() != facing.Count)
        {
            throw new InvalidOperationException("relic_mapping orientations_quarters must be a non-empty set of distinct facings.");
        }

        double capFraction = NumberOf(core, "reserve_area_cap_fraction");
        if (capFraction <= 0 || capFraction > 1)
        {
            throw new InvalidOperationException($"relic_mapping reserve_area_cap_fraction must be in (0,1] (got {capFraction}).");
        }

        var parameters = new RelicMappingParams(
            CoreRadiusCells: IntOf(core, "core_radius_cells", min: 1),
            BuildMarginCells: IntOf(core, "build_margin_cells", min: 0),
            ReserveAreaCapFraction: capFraction,
            ShiftRadiusCells: IntOf(replay, "shift_radius_cells", min: 0, max: 4),
            MapSizeCells: IntOf(continent, "map_size_cells", min: 1),
            ForestRatio: NumberOf(continent, "forest_ratio"),
            RockRatio: NumberOf(continent, "rock_ratio"),
            WaterRatio: NumberOf(continent, "water_ratio"),
            ResourceNodeCount: IntOf(continent, "resource_node_count", min: 0),
            TerrainYieldRadiusCells: IntOf(continent, "terrain_yield_radius_cells", min: 0),
            ResourceAvoidYieldZone: BoolOf(continent, "resource_avoid_yield_zone"),
            ThreatNodeCount: IntOf(continent, "threat_node_count", min: 0),
            Orientations: facing);
        if (parameters.ForestRatio < 0 || parameters.RockRatio < 0 || parameters.WaterRatio < 0
            || parameters.ForestRatio + parameters.RockRatio + parameters.WaterRatio > 1.0 + 1e-9)
        {
            throw new InvalidOperationException("relic_mapping terrain ratios must be non-negative and sum to at most 1.");
        }

        return parameters;
    }

    /// <summary>Parses and validates the wonders document (six fixed rows, F.3).</summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the row).</exception>
    public static IReadOnlyList<WonderSpec> ParseWonders(string json)
    {
        JsonElement root = ParseRoot(json, nameof(ContentType.Wonder));
        JsonElement items = ArrayOf(root, "items");
        var wonders = new List<WonderSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in items.EnumerateArray())
        {
            string id = StringOf(row, "id");
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Duplicate wonder id '{id}'.");
            }

            wonders.Add(new WonderSpec(
                id,
                IntOf(row, "era_index", min: 1, max: 6),
                StringOf(row, "tech_node"),
                StringOf(row, "name_zh"),
                StringOf(row, "name_en"),
                AliasesOf(row),
                StringOf(row, "bonus_kind"),
                NumberOf(row, "bonus_value")));
        }

        if (wonders.Count == 0)
        {
            throw new InvalidOperationException("The wonders document carries no rows (F.3 清单非空).");
        }

        var eraClaimed = new HashSet<int>();
        foreach (WonderSpec wonder in wonders)
        {
            if (!eraClaimed.Add(wonder.EraIndex))
            {
                throw new InvalidOperationException($"Era {wonder.EraIndex} carries more than one wonder (F.3: one per era).");
            }
        }

        return wonders;
    }

    /// <summary>Wraps parsed wonders as K-2 registry items sorted by id (deterministic registration).</summary>
    public static IReadOnlyList<WonderContentItem> WonderItems(IReadOnlyList<WonderSpec> wonders) =>
        wonders.OrderBy(w => w.Id, StringComparer.Ordinal).Select(w => new WonderContentItem(w)).ToList();

    /// <summary>Parses and validates the archaeology document (F.058 三通道).</summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the row).</exception>
    public static ArchaeologyTable ParseArchaeology(string json)
    {
        JsonElement root = ParseRoot(json, nameof(ContentType.DropTable));
        JsonElement dig = ObjectOf(root, "dig");
        JsonElement channels = ObjectOf(root, "channels");
        JsonElement scrolls = ArrayOf(root, "knowledge_scrolls");
        JsonElement blueprints = ArrayOf(root, "ancient_blueprints");
        JsonElement restoration = ObjectOf(root, "restoration");
        JsonElement auras = ObjectOf(restoration, "memorial_auras");

        var tierDurations = new Dictionary<int, int>();
        foreach (JsonProperty property in dig.GetProperty("tier_duration_seconds").EnumerateObject())
        {
            if (!int.TryParse(property.Name, out int tier) || property.Value.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException($"dig.tier_duration_seconds key '{property.Name}' must be a tier number with a numeric value.");
            }

            tierDurations[tier] = (int)property.Value.GetDouble();
        }

        var channelList = new List<(string Channel, double Weight)>();
        foreach (JsonProperty property in channels.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ChannelKeys.Contains(property.Name))
            {
                throw new InvalidOperationException($"Unknown archaeology channel '{property.Name}' (closed vocabulary — {string.Join('|', ChannelKeys)}).");
            }

            double weight = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetDouble() : -1;
            if (weight <= 0)
            {
                throw new InvalidOperationException($"Channel '{property.Name}' needs a positive weight (got {weight}).");
            }

            channelList.Add((property.Name, weight));
        }

        if (channelList.Count == 0)
        {
            throw new InvalidOperationException("The archaeology channels table is empty.");
        }

        var scrollList = new List<KnowledgeScrollSpec>();
        var scrollIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in scrolls.EnumerateArray())
        {
            string id = StringOf(row, "id");
            if (!scrollIds.Add(id))
            {
                throw new InvalidOperationException($"Duplicate knowledge scroll id '{id}'.");
            }

            double relief = NumberOf(row, "relief_share");
            if (relief is < 0 or >= 1)
            {
                throw new InvalidOperationException($"Knowledge scroll '{id}' relief_share must be in [0,1) (got {relief}).");
            }

            scrollList.Add(new KnowledgeScrollSpec(
                id, StringOf(row, "tech_node"), relief,
                StringOf(row, "memory_zh"), StringOf(row, "memory_en")));
        }

        var blueprintList = new List<AncientBlueprintSpec>();
        var blueprintIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in blueprints.EnumerateArray())
        {
            string id = StringOf(row, "id");
            if (!blueprintIds.Add(id))
            {
                throw new InvalidOperationException($"Duplicate ancient blueprint id '{id}'.");
            }

            var mix = new List<string>();
            foreach (JsonElement entry in row.GetProperty("mix").EnumerateArray())
            {
                mix.Add(entry.GetString()!);
            }

            if (mix.Count == 0)
            {
                throw new InvalidOperationException($"Ancient blueprint '{id}' has an empty mix.");
            }

            blueprintList.Add(new AncientBlueprintSpec(id, StringOf(row, "name_zh"), StringOf(row, "name_en"), mix));
        }

        var auraValues = new Dictionary<string, double>();
        foreach (JsonProperty property in auras.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            double value = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetDouble() : -1;
            if (value < 0 || value >= 1)
            {
                throw new InvalidOperationException($"Memorial aura '{property.Name}' must be in [0,1) (got {value}).");
            }

            auraValues[property.Name] = value;
        }

        if (auraValues.Count == 0)
        {
            throw new InvalidOperationException("restoration.memorial_auras is empty (F.3 光环目录非空).");
        }

        return new ArchaeologyTable(
            new DigSpec(
                IntOf(dig, "manpower_cost", min: 0),
                IntOf(dig, "energy_cost", min: 0),
                IntOf(dig, "min_duration_seconds", min: 0),
                IntOf(dig, "max_duration_seconds", min: 0),
                tierDurations,
                IntOf(dig, "drill_ticks_per_second", min: 1)),
            channelList,
            scrollList,
            blueprintList,
            new RestorationSpec(
                FractionOf(restoration, "wreck_repair_cost_fraction"),
                FractionOf(restoration, "new_era_conversion_rate"),
                NumberOf(restoration, "functional_weight"),
                NumberOf(restoration, "memorial_weight"),
                auraValues));
    }

    /// <summary>Parses and validates the heritage-shop document (F-011 传承商店).</summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the row).</exception>
    public static HeritageShopTable ParseHeritageShop(string json)
    {
        JsonElement root = ParseRoot(json, nameof(ContentType.DropTable));
        var items = new List<HeritageShopItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in ArrayOf(root, "items").EnumerateArray())
        {
            string id = StringOf(row, "id");
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Duplicate heritage shop item id '{id}'.");
            }

            items.Add(new HeritageShopItem(
                id,
                StringOf(row, "kind"),
                IntOf(row, "price_fund", min: 1),
                StringOf(row, "name_zh"),
                StringOf(row, "name_en"),
                AliasesOf(row)));
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("The heritage shop carries no rows.");
        }

        return new HeritageShopTable(items.OrderBy(item => item.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>Parses and validates the era-transition document (F-003/F-011).</summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the field).</exception>
    public static EraTransitionSpec ParseEraTransition(string json)
    {
        JsonElement root = ParseRoot(json, nameof(ContentType.Era));
        JsonElement preconditions = ObjectOf(root, "preconditions");
        JsonElement levy = ObjectOf(root, "levy");
        JsonElement warehouse = ObjectOf(root, "warehouse");
        JsonElement finalLevels = ObjectOf(root, "final_levels");

        double carriedShare = NumberOf(levy, "carried_share");
        if (carriedShare is < 0 or > 1)
        {
            throw new InvalidOperationException($"levy.carried_share must be in [0,1] (got {carriedShare}).");
        }

        var finals = new Dictionary<int, string>();
        foreach (JsonProperty property in finalLevels.EnumerateObject())
        {
            if (property.Name.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(property.Name, out int era) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"final_levels key '{property.Name}' must be an era number with a level-id string.");
            }

            finals[era] = property.Value.GetString()!;
        }

        var schedule = new List<EraFirstThreeSlot>();
        foreach (JsonElement row in ArrayOf(root, "first_three_schedule").EnumerateArray())
        {
            schedule.Add(new EraFirstThreeSlot(
                IntOf(row, "slot", min: 1, max: 3),
                IntOf(row, "target_era", min: 2, max: 6),
                StringOf(row, "level_id"),
                StringOf(row, "focus")));
        }

        if (schedule.Count == 0)
        {
            throw new InvalidOperationException("first_three_schedule carries no rows (v3.2 §4 前三关).");
        }

        // 槽位在 (target_era, slot) 上互异：同一目标时代的排期槽不得重复。
        if (schedule.Select(slot => (slot.TargetEra, slot.Slot)).Distinct().Count() != schedule.Count)
        {
            throw new InvalidOperationException("first_three_schedule (target_era, slot) pairs must be distinct (v3.2 §4 前三关).");
        }

        // 声明了排期的目标时代必须恰含槽 1..3（前三关=完整三行，防半截扩展）。
        foreach (int targetEra in schedule.Select(slot => slot.TargetEra).Distinct())
        {
            var slots = schedule.Where(slot => slot.TargetEra == targetEra).Select(slot => slot.Slot).OrderBy(s => s).ToList();
            if (!slots.SequenceEqual(new[] { 1, 2, 3 }))
            {
                throw new InvalidOperationException(
                    $"first_three_schedule of target era {targetEra} must be exactly slots 1..3 (got {string.Join(',', slots)}).");
            }
        }

        return new EraTransitionSpec(
            BoolOf(preconditions, "requires_final_level"),
            BoolOf(preconditions, "requires_wonder"),
            carriedShare,
            IntOf(levy, "useless_fold_divisor", min: 1),
            IntOf(warehouse, "level_gain_per_transition", min: 1),
            BoolOf(root, "pyramid_bonus_carries_over"),
            finals,
            schedule);
    }

    // ------------------------------------------------------------ JSON helpers

    private static JsonElement ParseRoot(string json, string contentType)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentNullException(nameof(json));
        }

        JsonElement root = JsonDocument.Parse(json).RootElement; // strict: malformed JSON throws (red test)
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("A legacy content document must be a JSON object.");
        }

        string declared = StringOf(root, "content_type");
        if (declared != contentType)
        {
            throw new InvalidOperationException($"Legacy document content_type must be '{contentType}' (got '{declared}').");
        }

        return root;
    }

    private static JsonElement ObjectOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException($"Legacy document lacks an '{name}' object.");

    private static JsonElement ArrayOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidOperationException($"Legacy document lacks an '{name}' array.");

    private static string StringOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Legacy content field '{name}' is missing or not a non-empty string.");
        }

        return value.GetString()!;
    }

    private static double NumberOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Legacy content field '{name}' is missing or not a number.");
        }

        return value.GetDouble();
    }

    private static bool BoolOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException($"Legacy content field '{name}' is missing or not a boolean.");
        }

        return value.GetBoolean();
    }

    private static double FractionOf(JsonElement parent, string name)
    {
        double value = NumberOf(parent, name);
        if (value is < 0 or > 1)
        {
            throw new InvalidOperationException($"Legacy content field '{name}' must be a fraction in [0,1] (got {value}).");
        }

        return value;
    }

    private static int IntOf(JsonElement parent, string name, int min, int max = int.MaxValue)
    {
        double value = NumberOf(parent, name);
        if (value < min || value > max || value != Math.Floor(value))
        {
            throw new InvalidOperationException($"Legacy content field '{name}' must be an integer in [{min},{max}] (got {value}).");
        }

        return (int)value;
    }

    private static IReadOnlyList<string> AliasesOf(JsonElement row)
    {
        var aliases = new List<string>();
        if (row.TryGetProperty("aliases", out JsonElement element) && element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement alias in element.EnumerateArray())
            {
                aliases.Add(alias.GetString()!);
            }
        }

        return aliases;
    }
}
