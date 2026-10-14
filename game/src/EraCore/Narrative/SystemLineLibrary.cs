using System.Text.Json;

namespace EraCore.Narrative;

/// <summary>
/// One system prompt template of the 15-line library (T-089, F-105,
/// §12.1 台词库): the raw template text with <c>【槽位】</c> slots,
/// its sound-cue slot, and the slot names derived from the text.
/// </summary>
/// <param name="Key">Stable template key (<c>sys-detect</c>…).</param>
/// <param name="Text">Template text carrying 【槽位】 slots.</param>
/// <param name="Cue">Optional ding cue slot.</param>
/// <param name="Slots">Slot names in first-occurrence order (may repeat-free).</param>
public sealed record SystemLineTemplate(
    string Key,
    string Text,
    string? Cue,
    IReadOnlyList<string> Slots);

/// <summary>
/// The 15-template system prompt library (F-105): parses
/// <c>game/content/narrative/system-lines.json</c> strictly and resolves a
/// template into a final line by filling every <c>【槽位】</c> slot.
/// The 40% defense-integrity alarm row is the F-111 HUD linkage face —
/// the consistency test pins it to <c>OffModeDowngrade.PersonaAlarmLine</c>
/// so the wording cannot drift from the alarm threshold it narrates.
/// </summary>
public sealed class SystemLineLibrary
{
    /// <summary>Content schema URN of the template library.</summary>
    public const string SchemaUrn = "urn:eratd:content:narrative:system-lines:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentType = "SystemLineLibrary";

    /// <summary>Frozen library size (v3.2 §12.1: 15 条模板入文案库).</summary>
    public const int TemplateCount = 15; // ci-allow: R7 T-089 15 条模板=v3.2 §12.1 冻结规格数（文案量纲，非内容平衡数值）

    /// <summary>The 40% alarm key (F-105/F-111 linkage anchor).</summary>
    public const string IntegrityAlarmKey = "sys-alarm-integrity";

    private static readonly IReadOnlySet<string> SoundCues =
        new HashSet<string> { "ding_info", "ding_reward", "ding_crisis" };

    private readonly Dictionary<string, SystemLineTemplate> _byKey;

    private SystemLineLibrary(IReadOnlyList<SystemLineTemplate> items)
    {
        Items = items;
        _byKey = items.ToDictionary(template => template.Key, StringComparer.Ordinal);
    }

    /// <summary>All templates in file order.</summary>
    public IReadOnlyList<SystemLineTemplate> Items { get; }

    /// <summary>Template by key; null when absent.</summary>
    public SystemLineTemplate? Find(string key) =>
        _byKey.TryGetValue(key, out SystemLineTemplate? template) ? template : null;

    /// <summary>
    /// Strict parser: exactly <see cref="TemplateCount"/> rows, unique keys,
    /// non-blank texts, cue in the closed set, and at least one slot
    /// per template that declares slots in its text (slots are derived, not
    /// declared — a text without 【】 simply has none).
    /// </summary>
    /// <param name="json">Raw library JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="NarrativeSchemaException">Any schema violation.</exception>
    public static SystemLineLibrary Parse(string json, string sourceName = "system lines")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NarrativeSchemaException($"{sourceName}: 台词库文件为空");
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
                throw new NarrativeSchemaException(
                    $"{sourceName}: content_type 须为 {ContentType}");
            }

            if (!root.TryGetProperty("items", out JsonElement itemsEl)
                || itemsEl.ValueKind != JsonValueKind.Array)
            {
                throw new NarrativeSchemaException($"{sourceName}: 缺 items 数组");
            }

            Require(
                itemsEl.GetArrayLength() == TemplateCount,
                $"{sourceName}: items 须恰好 {TemplateCount} 条（得到 {itemsEl.GetArrayLength()}——§12.1 冻结 15 条）");

            var items = new List<SystemLineTemplate>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement itemEl in itemsEl.EnumerateArray())
            {
                Require(itemEl.ValueKind == JsonValueKind.Object, $"{sourceName}: items 行须为对象");
                string key = RequireString(itemEl, "key", sourceName);
                Require(!key.Any(char.IsWhiteSpace), $"{sourceName}: key 含空白字符");
                Require(keys.Add(key), $"{sourceName}: key 重复（{key}）");
                string text = RequireString(itemEl, "text", sourceName);
                Require(!string.IsNullOrWhiteSpace(text), $"{sourceName}: {key}.text 为空白");
                string? cue = null;
                if (itemEl.TryGetProperty("cue", out JsonElement cueEl))
                {
                    Require(cueEl.ValueKind == JsonValueKind.String, $"{sourceName}: {key}.cue 须为字符串");
                    cue = cueEl.GetString();
                    Require(
                        SoundCues.Contains(cue!),
                        $"{sourceName}: {key}.cue 未登记（{cue}，封闭表 {string.Join("/", SoundCues)}）");
                }

                items.Add(new SystemLineTemplate(key, text, cue, SlotsOf(text)));
            }

            return new SystemLineLibrary(items);
        }
    }

    /// <summary>
    /// Resolves one template: fills every <c>【槽位】</c> with the provided
    /// values. A missing slot value is an error (never a silently unfilled
    /// slot — same strictness as the schema parsers).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown key or a missing slot value.</exception>
    public string Resolve(string key, IReadOnlyDictionary<string, string> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        SystemLineTemplate? template = Find(key)
            ?? throw new ArgumentException($"未知台词模板 key（{key}）", nameof(key));

        if (template.Slots.Count == 0)
        {
            return template.Text;
        }

        string resolved = template.Text;
        foreach (string slot in template.Slots)
        {
            if (!slots.TryGetValue(slot, out string? value) || string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    $"模板 {key} 槽位 {slot} 缺值（值表：{string.Join("/", slots.Keys)}）", nameof(slots));
            }

            resolved = resolved.Replace($"【{slot}】", value);
        }

        return resolved;
    }

    /// <summary>Derives slot names from the 【】 marks, in order, deduplicated.</summary>
    internal static IReadOnlyList<string> SlotsOf(string text)
    {
        var slots = new List<string>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            int open = text.IndexOf('【', cursor);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf('】', open + 1);
            if (close < 0)
            {
                throw new NarrativeSchemaException($"模板槽位未闭合（{text}——『【』缺『】』）");
            }

            string slot = text[(open + 1)..close];
            if (slot.Length == 0)
            {
                throw new NarrativeSchemaException($"模板槽位名为空（{text}）");
            }

            if (!slots.Contains(slot, StringComparer.Ordinal))
            {
                slots.Add(slot);
            }

            cursor = close + 1;
        }

        return slots;
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
}
