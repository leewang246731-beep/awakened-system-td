using System.Text.Json;

namespace EraCore.Narrative;

/// <summary>How one remnant is obtained (closed set): archaeology knowledge scroll / narrative beat.</summary>
public enum RemnantSourceKind
{
    /// <summary>考古知识残卷（scroll_id 挂接 legacy/archaeology.json knowledge_scrolls）。</summary>
    KnowledgeScroll = 0,

    /// <summary>叙事节点获得（beat_id 挂接钩子/演出/章节 id）。</summary>
    NarrativeBeat = 1,
}

/// <summary>
/// One knowledge remnant (T-089, F-098/F-099, §12.1+§8: 知识残卷=零丢失的
/// 记忆碎片): the archived memory text PLUS the E5 echo-whisper foreshadow
/// that grows with <see cref="EchoStage"/> (1→4 渐显). Reading a remnant IS
/// restoring Zero's memory of the previous epoch (考古进度=系统人格成长) —
/// the whisper layer is the E5 回声 personality seeding, laid down here so
/// the payoff chapter only ever reveals what E1/E2 already whispered.
/// </summary>
/// <param name="Id">Remnant id (scroll-family or narrative-family).</param>
/// <param name="SourceKind">Obtainment channel.</param>
/// <param name="ScrollId">The archaeology scroll id this remnant extends (knowledge_scroll rows).</param>
/// <param name="BeatId">The narrative beat id this remnant rides (narrative_beat rows).</param>
/// <param name="Era">Era the memory belongs to (1..6 — six-era domain, finale included).</param>
/// <param name="EchoStage">E5 foreshadow stage (1 faint static → 4 almost-clear whisper).</param>
/// <param name="MemoryZh">Zero's archive annotation of the memory fragment.</param>
/// <param name="EchoWhisperZh">The echo whisper shown at this stage.</param>
public sealed record KnowledgeRemnant(
    string Id,
    RemnantSourceKind SourceKind,
    string? ScrollId,
    string? BeatId,
    int Era,
    int EchoStage,
    string MemoryZh,
    string EchoWhisperZh);

/// <summary>
/// The remnant catalog: parses <c>game/content/narrative/remnants.json</c>
/// strictly. The archaeology linkage face (scroll ids) and the echo-stage
/// monotone progression are validated by the consistency tests against
/// <c>legacy/archaeology.json</c> (叙事与考古联动 single face).
/// </summary>
public sealed class RemnantCatalog
{
    /// <summary>Content schema URN of the remnant catalog.</summary>
    public const string SchemaUrn = "urn:eratd:content:narrative:remnants:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentType = "RemnantTable";

    /// <summary>Frozen table size (T-089 first batch 8 + T-120 E3 increment 3 + T-127 E4 increment 4 + T-136 E5/finale increments 7).</summary>
    public const int RemnantCount = 22; // ci-allow: R7 T-089 首批 8 条+T-120 E3 增量 3 条+T-127 E4 增量 4 条+T-136 E5 增量 4 条+终章增量 3 条=任务卡冻结结构数（叙事量纲，非内容平衡数值）

    /// <summary>
    /// Era ordinal ceiling of the remnant table: E1..E6 (six-era domain;
    /// T-136 终章增量首用 era 6——回声归位/记忆闭环拍).
    /// </summary>
    public const int MaxEraOrdinal = 6; // ci-allow: R7 T-136 残卷时代域 E1..E6=六时代体系冻结结构常量（章数纲，非内容平衡数值）

    /// <summary>Max echo stage (渐显带 1..4).</summary>
    public const int MaxEchoStage = 4; // ci-allow: R7 T-089 回声伏笔渐显 1..4 段=T-089 结构旋钮（叙事量纲）

    private readonly Dictionary<string, KnowledgeRemnant> _byId;

    private RemnantCatalog(IReadOnlyList<KnowledgeRemnant> items)
    {
        Items = items;
        _byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    /// <summary>All remnants in file order.</summary>
    public IReadOnlyList<KnowledgeRemnant> Items { get; }

    /// <summary>Remnant by id; null when absent.</summary>
    public KnowledgeRemnant? Find(string id) =>
        _byId.TryGetValue(id, out KnowledgeRemnant? remnant) ? remnant : null;

    /// <summary>Remnants linked to one archaeology scroll id (normally 0 or 1).</summary>
    public IReadOnlyList<KnowledgeRemnant> ByScroll(string scrollId) =>
        Items.Where(item => string.Equals(item.ScrollId, scrollId, StringComparison.Ordinal)).ToArray();

    /// <summary>Remnants whose whisper has reached one stage (the 渐显 slice).</summary>
    public IReadOnlyList<KnowledgeRemnant> AtEchoStage(int stage) =>
        Items.Where(item => item.EchoStage == stage).ToArray();

    /// <summary>
    /// Strict parser: exactly <see cref="RemnantCount"/> rows, unique ids,
    /// echo stages in 1..<see cref="MaxEchoStage"/> and non-decreasing along
    /// file order (渐显), source tables closed, knowledge_scroll rows carry
    /// a scroll id, narrative_beat rows carry a beat id, non-blank texts.
    /// </summary>
    /// <param name="json">Raw catalog JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="NarrativeSchemaException">Any schema violation.</exception>
    public static RemnantCatalog Parse(string json, string sourceName = "remnants")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NarrativeSchemaException($"{sourceName}: 残卷表为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NarrativeSchemaException($"{sourceName}: JSON 解析失败（{ex.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content_type", out JsonElement typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != ContentType)
            {
                throw new NarrativeSchemaException($"{sourceName}: content_type 须为 {ContentType}");
            }

            if (!root.TryGetProperty("items", out JsonElement itemsEl)
                || itemsEl.ValueKind != JsonValueKind.Array)
            {
                throw new NarrativeSchemaException($"{sourceName}: 缺 items 数组");
            }

            Require(
                itemsEl.GetArrayLength() == RemnantCount,
                $"{sourceName}: items 须恰好 {RemnantCount} 条（得到 {itemsEl.GetArrayLength()}——T-089 首批 8 条+T-120 E3 增量 3 条+T-127 E4 增量 4 条+T-136 E5/终章增量 7 条）");

            var items = new List<KnowledgeRemnant>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int lastStage = 0;
            foreach (JsonElement itemEl in itemsEl.EnumerateArray())
            {
                Require(itemEl.ValueKind == JsonValueKind.Object, $"{sourceName}: items 行须为对象");
                string id = RequireString(itemEl, "id", sourceName);
                Require(!id.Any(char.IsWhiteSpace), $"{sourceName}: id 含空白字符");
                Require(ids.Add(id), $"{sourceName}: id 重复（{id}）");

                string sourceKindName = RequireString(itemEl, "source_kind", sourceName);
                RemnantSourceKind sourceKind = sourceKindName switch
                {
                    "knowledge_scroll" => RemnantSourceKind.KnowledgeScroll,
                    "narrative_beat" => RemnantSourceKind.NarrativeBeat,
                    _ => throw new NarrativeSchemaException(
                        $"{sourceName}: 未登记 source_kind（{sourceKindName}，封闭表 knowledge_scroll/narrative_beat）"),
                };

                string? scrollId = OptionalString(itemEl, "scroll_id");
                string? beatId = OptionalString(itemEl, "beat_id");
                if (sourceKind == RemnantSourceKind.KnowledgeScroll)
                {
                    Require(!string.IsNullOrWhiteSpace(scrollId), $"{sourceName}: {id} 缺 scroll_id");
                }
                else
                {
                    Require(!string.IsNullOrWhiteSpace(beatId), $"{sourceName}: {id} 缺 beat_id");
                }

                int era = RequireInt(itemEl, "era", sourceName);
                Require(
                    era >= 1 && era <= MaxEraOrdinal,
                    $"{sourceName}: {id}.era 须 ∈[1,{MaxEraOrdinal}]（六时代域，T-136 终章增量首用 6）");
                int stage = RequireInt(itemEl, "echo_stage", sourceName);
                Require(
                    stage >= 1 && stage <= MaxEchoStage,
                    $"{sourceName}: {id}.echo_stage 须 ∈[1,{MaxEchoStage}]（得到 {stage}）");
                Require(
                    stage >= lastStage,
                    $"{sourceName}: {id}.echo_stage={stage} 低于前行 {lastStage}（渐显=沿文件序单调不降）");
                lastStage = stage;

                string memory = RequireString(itemEl, "memory_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(memory), $"{sourceName}: {id}.memory_zh 为空白");
                string whisper = RequireString(itemEl, "echo_whisper_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(whisper), $"{sourceName}: {id}.echo_whisper_zh 为空白");

                items.Add(new KnowledgeRemnant(
                    id, sourceKind, scrollId, beatId, era, stage, memory, whisper));
            }

            return new RemnantCatalog(items);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new NarrativeSchemaException(message);
        }
    }

    private static string RequireString(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new NarrativeSchemaException($"{source}: 缺字符串字段 {name}");
        }

        return value.GetString()!;
    }

    private static int RequireInt(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
        {
            throw new NarrativeSchemaException($"{source}: 缺整数字段 {name}");
        }

        return parsed;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
