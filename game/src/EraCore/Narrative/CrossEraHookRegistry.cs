using System.Text.Json;

namespace EraCore.Narrative;

/// <summary>Where a hook's payoff lands (closed set): system memory / advisor private line layer / system voice.</summary>
public enum HookLayer
{
    /// <summary>零的记忆归档面（残卷/壁画/图纸——叙事与考古联动）。</summary>
    SystemMemory = 0,

    /// <summary>军师私人台词层（E4 卫星信号双载体之一：台词层）。</summary>
    AdvisorPrivate = 1,

    /// <summary>系统声音面（E5 回声低语——与 echo_stage 渐显线汇合）。</summary>
    SystemVoice = 2,
}

/// <summary>Cross-era recovery channel of one hook (closed set).</summary>
public enum HookRecoverKind
{
    /// <summary>E2 考古通道（channel 必须是 archaeology.json 已登记通道）。</summary>
    ArchaeologyChannel = 0,

    /// <summary>章节推进回收（era3+ 未交付章的登记位）。</summary>
    ChapterProgress = 1,
}

/// <summary>Recovery declaration: target era + channel.</summary>
public sealed record HookRecover(HookRecoverKind Kind, int Era, string? Channel);

/// <summary>
/// One cross-era suspense hook (T-089, F-098, §12.1 谱系线): E1 壁画飞艇 /
/// E2 预言残页 / E3 不该存在图纸 / E4 卫星信号 / E5 回声低语. The row
/// registers WHERE the hook is planted (level + anchor), the teaser the
/// player sees, and WHERE it pays off — the linkage face consumed by the
/// archaeology/narrative consistency test (钩子-考古联动).
/// </summary>
/// <param name="Id">Hook id (<c>narrative:hook-e1-mural-airship</c>…).</param>
/// <param name="Ordinal">1..5, one per era.</param>
/// <param name="TitleZh">Hook title.</param>
/// <param name="PlantedEra">Era the hook is planted in (1..5).</param>
/// <param name="PlantedLevelId">Planted level id (null for unshipped-era registry rows).</param>
/// <param name="PlantedAnchorZh">In-level anchor description.</param>
/// <param name="TeaserZh">The teaser text shown at the plant.</param>
/// <param name="Recover">Recovery declaration.</param>
/// <param name="PayoffZh">Payoff text (回收方向写死：奖励使用者，不搞背叛骗局).</param>
/// <param name="Layer">Which surface carries the hook.</param>
/// <param name="LinkedRemnant">Linked remnant id in remnants.json (null when none).</param>
public sealed record CrossEraHook(
    string Id,
    int Ordinal,
    string TitleZh,
    int PlantedEra,
    string? PlantedLevelId,
    string PlantedAnchorZh,
    string TeaserZh,
    HookRecover Recover,
    string PayoffZh,
    HookLayer Layer,
    string? LinkedRemnant);

/// <summary>
/// Registry of the five cross-era hooks: parses
/// <c>game/content/narrative/hooks.json</c> strictly. Every planted level
/// reference, archaeology channel and remnant link is validated against the
/// live content files by the consistency tests (single linkage face — the
/// registry is the ONE place the 埋点/回收 wiring lives).
/// </summary>
public sealed class CrossEraHookRegistry
{
    /// <summary>Content schema URN of the hook registry.</summary>
    public const string SchemaUrn = "urn:eratd:content:narrative:hooks:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentType = "CrossEraHookTable";

    /// <summary>Frozen hook count (§12: five cross-era hooks E1..E5).</summary>
    public const int HookCount = 5; // ci-allow: R7 T-089 五条跨时代钩子=v3.2 §12 谱系冻结结构数（叙事量纲，非内容平衡数值）

    private CrossEraHookRegistry(IReadOnlyList<CrossEraHook> items)
    {
        Items = items;
    }

    /// <summary>All hooks in ordinal order.</summary>
    public IReadOnlyList<CrossEraHook> Items { get; }

    /// <summary>Hooks planted in one era.</summary>
    public IReadOnlyList<CrossEraHook> PlantedIn(int era) =>
        Items.Where(hook => hook.PlantedEra == era).ToArray();

    /// <summary>
    /// Strict parser: exactly <see cref="HookCount"/> rows, ordinals 1..5,
    /// one planted era per row covering E1..E5 exactly once, non-blank
    /// texts, closed layer/recover tables, archaeology recoveries carry a
    /// channel, and no duplicate ids.
    /// </summary>
    /// <param name="json">Raw registry JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="NarrativeSchemaException">Any schema violation.</exception>
    public static CrossEraHookRegistry Parse(string json, string sourceName = "cross-era hooks")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NarrativeSchemaException($"{sourceName}: 钩子表为空");
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
                itemsEl.GetArrayLength() == HookCount,
                $"{sourceName}: items 须恰好 {HookCount} 条（得到 {itemsEl.GetArrayLength()}——E1~E5 每时代一条）");

            var hooks = new List<CrossEraHook>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var erasSeen = new HashSet<int>();
            foreach (JsonElement hookEl in itemsEl.EnumerateArray())
            {
                Require(hookEl.ValueKind == JsonValueKind.Object, $"{sourceName}: items 行须为对象");
                string id = RequireString(hookEl, "id", sourceName);
                Require(!id.Any(char.IsWhiteSpace), $"{sourceName}: id 含空白字符");
                Require(ids.Add(id), $"{sourceName}: id 重复（{id}）");
                int ordinal = RequireInt(hookEl, "ordinal", sourceName);
                Require(ordinal >= 1 && ordinal <= HookCount, $"{sourceName}: {id}.ordinal 须 ∈[1,{HookCount}]");
                string title = RequireString(hookEl, "title_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(title), $"{sourceName}: {id}.title_zh 为空白");
                int plantedEra = RequireInt(hookEl, "planted_era", sourceName);
                Require(plantedEra >= 1 && plantedEra <= HookCount, $"{sourceName}: {id}.planted_era 须 ∈[1,5]");
                Require(erasSeen.Add(plantedEra), $"{sourceName}: planted_era {plantedEra} 重复（每时代一条）");

                string? levelId = OptionalString(hookEl, "planted_level_id");
                if (levelId is not null)
                {
                    Require(!levelId.Any(char.IsWhiteSpace), $"{sourceName}: {id}.planted_level_id 含空白");
                }

                string anchor = RequireString(hookEl, "planted_anchor_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(anchor), $"{sourceName}: {id}.planted_anchor_zh 为空白");
                string teaser = RequireString(hookEl, "teaser_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(teaser), $"{sourceName}: {id}.teaser_zh 为空白");

                JsonElement recoverEl = RequireObject(hookEl, "recover", sourceName, id);
                string recoverKindName = RequireString(recoverEl, "kind", sourceName);
                HookRecoverKind recoverKind = recoverKindName switch
                {
                    "archaeology_channel" => HookRecoverKind.ArchaeologyChannel,
                    "chapter_progress" => HookRecoverKind.ChapterProgress,
                    _ => throw new NarrativeSchemaException(
                        $"{sourceName}: 未登记 recover.kind（{recoverKindName}，封闭表 archaeology_channel/chapter_progress）"),
                };
                int recoverEra = RequireInt(recoverEl, "era", sourceName);
                Require(recoverEra >= 1 && recoverEra <= HookCount, $"{sourceName}: {id}.recover.era 须 ∈[1,5]");
                string? channel = OptionalString(recoverEl, "channel");
                if (recoverKind == HookRecoverKind.ArchaeologyChannel)
                {
                    Require(
                        !string.IsNullOrWhiteSpace(channel),
                        $"{sourceName}: {id} 的 archaeology_channel 必须带 channel（archaeology.json 通道名）");
                }

                string payoff = RequireString(hookEl, "payoff_zh", sourceName);
                Require(!string.IsNullOrWhiteSpace(payoff), $"{sourceName}: {id}.payoff_zh 为空白");

                string layerName = RequireString(hookEl, "layer", sourceName);
                HookLayer layer = layerName switch
                {
                    "system_memory" => HookLayer.SystemMemory,
                    "advisor_private" => HookLayer.AdvisorPrivate,
                    "system_voice" => HookLayer.SystemVoice,
                    _ => throw new NarrativeSchemaException(
                        $"{sourceName}: 未登记 layer（{layerName}，封闭表 system_memory/advisor_private/system_voice）"),
                };

                string? linkedRemnant = OptionalString(hookEl, "linked_remanent");
                hooks.Add(new CrossEraHook(
                    id, ordinal, title, plantedEra, levelId, anchor, teaser,
                    new HookRecover(recoverKind, recoverEra, channel), payoff, layer, linkedRemnant));
            }

            return new CrossEraHookRegistry(hooks);
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

    private static JsonElement RequireObject(JsonElement parent, string name, string source, string id)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new NarrativeSchemaException($"{source}: {id} 缺对象字段 {name}");
        }

        return value;
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
