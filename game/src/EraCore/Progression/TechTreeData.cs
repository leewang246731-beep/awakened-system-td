using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.Progression;

/// <summary>
/// Branch membership of a tech node (T-073, F-052 系统能力独立旁支): the
/// <c>system</c> branch is the skippable side branch — no <c>main</c> node
/// may depend on it, so a player who closes the system (F-097) can still
/// unlock every main-line node. JSON spellings <c>main</c>/<c>system</c>.
/// </summary>
[JsonConverter(typeof(TechBranchJsonConverter))]
public enum TechBranch
{
    /// <summary>Main line: tower unlocks, scroll unlocks, game-system upgrades.</summary>
    Main = 0,

    /// <summary>Independent system-capability side branch (F-052).</summary>
    System = 1,
}

/// <summary>
/// String converter binding <see cref="TechBranch"/> to the lowercase JSON
/// spellings of techtree.json <c>branch</c> (AOT-safe generic form, 07 D-05;
/// same pattern as <c>TowerClassJsonConverter</c>).
/// </summary>
public sealed class TechBranchJsonConverter : JsonStringEnumConverter<TechBranch>
{
    /// <summary>Camel-case policy: main/system; reading stays case-insensitive.</summary>
    public TechBranchJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Kind of a tech node (T-073 task card: 塔解锁/系统升级/卷轴解锁三类), a
/// content taxonomy label only — the engine treats it as data, hosts filter
/// on it. JSON snake_case spellings <c>tower_unlock</c>/
/// <c>system_upgrade</c>/<c>scroll_unlock</c>.
/// </summary>
[JsonConverter(typeof(TechNodeKindJsonConverter))]
public enum TechNodeKind
{
    /// <summary>Unlocks a buildable tower (unlocks[] carries the tower id).</summary>
    TowerUnlock = 0,

    /// <summary>Upgrades a game system or a system capability (unlocks[] carries a capability id).</summary>
    SystemUpgrade = 1,

    /// <summary>Unlocks a guardian scroll (unlocks[] carries the scroll id, appendix F.1).</summary>
    ScrollUnlock = 2,
}

/// <summary>
/// String converter binding <see cref="TechNodeKind"/> to the snake_case
/// JSON spellings of techtree.json <c>kind</c> (AOT-safe, 07 D-05).
/// </summary>
public sealed class TechNodeKindJsonConverter : JsonStringEnumConverter<TechNodeKind>
{
    /// <summary>Snake-case policy: tower_unlock/system_upgrade/scroll_unlock.</summary>
    public TechNodeKindJsonConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// One material line of a node's 材料依赖 gate (T-073): the resource and
/// amount that must be present in the campaign pool before research may
/// start (consumed at start — the commitment model; see
/// <see cref="TechTree.BeginResearch"/>).
/// </summary>
/// <param name="Resource">Required resource (campaign pool key).</param>
/// <param name="Amount">Required amount, &gt; 0.</param>
public sealed record TechMaterial(ResourceKey Resource, double Amount);

/// <summary>
/// One line of a node's research-payment mix: the share of the formula
/// total charged to one resource. The mix exists because a single-resource
/// charge of the frozen magnitudes (e.g. the F.5 arrow anchor ≈3013)
/// cannot sit under the E1 per-resource pool cap (2000, appendix F.3) —
/// the total is split by shares instead.
/// </summary>
/// <param name="Resource">Payment resource (campaign pool key).</param>
/// <param name="Share">Share of the total in (0, 1]; the mix sums to 1.</param>
public sealed record TechCostLine(ResourceKey Resource, double Share);

/// <summary>
/// One validated technology node (T-073, F-050): the triple gate lives in
/// <see cref="Prerequisites"/> (拓扑前置) + <see cref="Materials"/> (材料依赖)
/// + <see cref="BoundLevelIds"/> (关卡绑定), the cost-formula input in
/// <see cref="CostSum"/> (Σ成本) and <see cref="CostMix"/> (研究支付分摊).
/// Produced by <see cref="TechTreeData.Parse"/> — never constructed by
/// hosts directly, so validation is total.
/// </summary>
/// <param name="Id">Content-registry id (<c>campaign:tech-e1-arrow</c>).</param>
/// <param name="EraIndex">Era the node belongs to (1=E1 … 6=终章; research also needs the campaign era to have reached it).</param>
/// <param name="Branch">Main line vs the independent system side branch (F-052).</param>
/// <param name="Kind">Tower unlock / system upgrade / scroll unlock.</param>
/// <param name="Prerequisites">Topology-prerequisite node ids (all must be unlocked).</param>
/// <param name="Materials">Material dependency lines (pool presence gate, consumed at research start).</param>
/// <param name="BoundLevelIds">Level ids that must be captured (占领) before the node is researchable; empty = unbound.</param>
/// <param name="CostSum">Σ成本 of the cost formula (tower nodes: towers.json cost_total; others: declared weight).</param>
/// <param name="CostMix">Payment split of the formula total across resources (shares sum to 1).</param>
/// <param name="Unlocks">Content ids the node unlocks when paid off (towers/scrolls/capabilities — opaque to the engine).</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
public sealed record TechNodeSpec(
    string Id,
    int EraIndex,
    TechBranch Branch,
    TechNodeKind Kind,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<TechMaterial> Materials,
    IReadOnlyList<string> BoundLevelIds,
    long CostSum,
    IReadOnlyList<TechCostLine> CostMix,
    IReadOnlyList<string> Unlocks,
    string? NameZh = null,
    string? NameEn = null);

/// <summary>
/// JSON shape of one techtree.json item (snake_case, underscore keys
/// ignored — K-2 annotation convention). Strings stay strings here except
/// the enum-typed taxonomy fields (bound by their converters);
/// <see cref="TechTreeData.Parse"/> materializes <see cref="ResourceKey"/>s
/// and validates the graph.
/// </summary>
public sealed record TechNodeDto(
    string? Id,
    string? ContentType,
    int? EraIndex,
    TechBranch? Branch,
    TechNodeKind? Kind,
    string? NameZh,
    string? NameEn,
    IReadOnlyList<string>? Aliases,
    IReadOnlyList<string>? Prerequisites,
    IReadOnlyList<TechMaterialDto>? Materials,
    [property: JsonPropertyName("bound_levels")] IReadOnlyList<string>? BoundLevelIds,
    long? CostSum,
    IReadOnlyList<TechCostLineDto>? CostMix,
    IReadOnlyList<string>? Unlocks);

/// <summary>JSON shape of one materials[] line of techtree.json.</summary>
/// <param name="Resource">Resource id string.</param>
/// <param name="Amount">Required amount.</param>
public sealed record TechMaterialDto(string? Resource, double? Amount);

/// <summary>JSON shape of one cost_mix[] line of techtree.json.</summary>
/// <param name="Resource">Payment resource id string.</param>
/// <param name="Share">Share of the total.</param>
public sealed record TechCostLineDto(string? Resource, double? Share);

/// <summary>Root shape of techtree.json: content_type + items.</summary>
/// <param name="ContentType">K-2 content kind declaration ("Tech").</param>
/// <param name="Items">Node definitions.</param>
public sealed record TechTreeDocument(string? ContentType, IReadOnlyList<TechNodeDto>? Items);

/// <summary>
/// Loader + validator of the tech-tree content document (T-073): parses
/// techtree.json, materializes resource keys and rejects every structural
/// defect (duplicate ids, dangling prerequisites, non-positive materials,
/// bad cost mixes, eras outside 1..6) with <see cref="ArgumentException"/>
/// — a damaged tree never reaches the engine.
/// </summary>
public static class TechTreeData
{
    /// <summary>
    /// Parses and validates a tech-tree document. Unknown keys are ignored
    /// (mod extension seam, K-2); damaged payloads throw
    /// <see cref="JsonException"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="JsonException">Payload is not valid JSON for the shape.</exception>
    /// <exception cref="ArgumentException">Structural validation failed (message names the node and defect).</exception>
    public static IReadOnlyList<TechNodeSpec> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        TechTreeDocument document = JsonSerializer.Deserialize(
            json, TechTreeJsonContext.Default.TechTreeDocument)
            ?? throw new JsonException("Tech tree document deserialized to null.");

        var nodes = new List<TechNodeSpec>();
        var seen = new HashSet<string>();
        foreach (TechNodeDto dto in document.Items ?? Array.Empty<TechNodeDto>())
        {
            TechNodeSpec spec = ParseNode(dto, seen);
            _ = seen.Add(spec.Id);
            nodes.Add(spec);
        }

        ValidateGraph(nodes);
        return nodes;
    }

    private static TechNodeSpec ParseNode(TechNodeDto dto, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new ArgumentException("A tech node is missing its id.");
        }

        string id = dto.Id.Trim();
        if (!seen.Add($"dup:{id}"))
        {
            throw new ArgumentException($"Duplicate tech node id {id}.");
        }

        int era = dto.EraIndex ?? 0;
        if (era is < 1 or > 6)
        {
            throw new ArgumentException($"Tech node {id} carries era_index {era} — must be 1..6 (appendix A: E1=1 … 终章=6).");
        }

        if (dto.CostSum is < 0)
        {
            throw new ArgumentException($"Tech node {id} carries a negative cost_sum {dto.CostSum}.");
        }

        var materials = new List<TechMaterial>();
        foreach (TechMaterialDto? line in dto.Materials ?? Array.Empty<TechMaterialDto>())
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Resource))
            {
                throw new ArgumentException($"Tech node {id} has a material line without a resource id.");
            }

            double amount = line.Amount ?? -1.0;
            if (!double.IsFinite(amount) || amount <= 0)
            {
                throw new ArgumentException($"Tech node {id} material {line.Resource} needs a positive finite amount (got {amount}).");
            }

            materials.Add(new TechMaterial(new ResourceKey(line.Resource.Trim()), amount));
        }

        var costMix = new List<TechCostLine>();
        double shareSum = 0.0;
        var mixResources = new HashSet<string>(StringComparer.Ordinal);
        foreach (TechCostLineDto? line in dto.CostMix ?? Array.Empty<TechCostLineDto>())
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Resource))
            {
                throw new ArgumentException($"Tech node {id} has a cost-mix line without a resource id.");
            }

            double share = line.Share ?? -1.0;
            if (!double.IsFinite(share) || share <= 0 || share > 1.0)
            {
                throw new ArgumentException($"Tech node {id} cost-mix {line.Resource} needs a share in (0, 1] (got {share}).");
            }

            if (!mixResources.Add(line.Resource.Trim()))
            {
                throw new ArgumentException($"Tech node {id} cost-mix lists {line.Resource} twice.");
            }

            shareSum += share;
            costMix.Add(new TechCostLine(new ResourceKey(line.Resource.Trim()), share));
        }

        if (costMix.Count == 0)
        {
            throw new ArgumentException($"Tech node {id} is missing cost_mix (研究支付分摊).");
        }

        if (Math.Abs(shareSum - 1.0) > 1e-6)
        {
            throw new ArgumentException($"Tech node {id} cost-mix shares sum to {shareSum} — must sum to 1.");
        }

        return new TechNodeSpec(
            id,
            era,
            dto.Branch ?? TechBranch.Main,
            dto.Kind ?? TechNodeKind.SystemUpgrade,
            TrimList(dto.Prerequisites),
            materials,
            TrimList(dto.BoundLevelIds),
            dto.CostSum ?? 0,
            costMix,
            TrimList(dto.Unlocks),
            dto.NameZh,
            dto.NameEn);
    }

    private static IReadOnlyList<string> TrimList(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<string>();
        }

        var trimmed = new List<string>(values.Count);
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                trimmed.Add(value.Trim());
            }
        }

        return trimmed;
    }

    private static void ValidateGraph(IReadOnlyList<TechNodeSpec> nodes)
    {
        var byId = nodes.ToDictionary(node => node.Id, node => node);
        foreach (TechNodeSpec node in nodes)
        {
            foreach (string prerequisite in node.Prerequisites)
            {
                if (!byId.ContainsKey(prerequisite))
                {
                    throw new ArgumentException($"Tech node {node.Id} lists unknown prerequisite {prerequisite}.");
                }
            }

            foreach (string levelId in node.BoundLevelIds)
            {
                if (string.IsNullOrWhiteSpace(levelId))
                {
                    throw new ArgumentException($"Tech node {node.Id} lists a blank bound level id.");
                }
            }
        }

        RejectCycles(nodes, byId);
    }

    /// <summary>
    /// Topology cycles are structural corruption (a node waiting on itself
    /// forever): DFS three-colour walk, throws on the first back edge.
    /// </summary>
    private static void RejectCycles(IReadOnlyList<TechNodeSpec> nodes, Dictionary<string, TechNodeSpec> byId)
    {
        var colour = new Dictionary<string, int>(); // 0=white 1=grey 2=black
        foreach (TechNodeSpec node in nodes)
        {
            Visit(node.Id);
        }

        void Visit(string id)
        {
            colour.TryGetValue(id, out int state);
            switch (state)
            {
                case 1:
                    throw new ArgumentException($"Tech tree contains a prerequisite cycle through {id}.");
                case 2:
                    return;
            }

            colour[id] = 1;
            foreach (string prerequisite in byId[id].Prerequisites)
            {
                Visit(prerequisite);
            }

            colour[id] = 2;
        }
    }
}

/// <summary>
/// System.Text.Json source-generation context for tech-tree content (AOT
/// red line, 07 D-05). Kept in this file as a secondary type so its
/// coverage rides on TechTreeDataTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TechTreeDocument))]
[JsonSerializable(typeof(TechNodeDto))]
[JsonSerializable(typeof(TechMaterialDto))]
[JsonSerializable(typeof(TechCostLineDto))]
[JsonSerializable(typeof(TechBranch))]
[JsonSerializable(typeof(TechNodeKind))]
public sealed partial class TechTreeJsonContext : JsonSerializerContext;
