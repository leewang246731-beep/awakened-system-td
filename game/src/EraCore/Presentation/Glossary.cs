using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Presentation;

/// <summary>
/// One glossary entry of the F1 help panel (T-093, F-124 术语表数据化):
/// term + bilingual definition + aliases + related-entry links. Parsed from
/// <c>game/content/ui/glossary.json</c> (RL7 — wording lives in content,
/// code only reads it).
/// </summary>
/// <param name="Id">Stable term id — the anchor predicate hints link to (<c>hints.json term</c>).</param>
/// <param name="TermZh">Chinese term (威胁度/系统点/信任度/效率…).</param>
/// <param name="TermEn">English term.</param>
/// <param name="DefZh">Chinese definition (one sentence, design-doc wording).</param>
/// <param name="DefEn">English definition.</param>
/// <param name="Aliases">Search aliases (zh/en mixed).</param>
/// <param name="Related">Ids of related entries the panel can jump to.</param>
public sealed record GlossaryEntry(
    string Id,
    string TermZh,
    string TermEn,
    string DefZh,
    string DefEn,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Related);

/// <summary>
/// Read-only glossary catalog parsed from one glossary.json document
/// (T-093, F-124). Parsing is strict and total: wrong content_type,
/// empty items, duplicate ids, empty terms/definitions or dangling
/// <c>related</c> ids all throw — a content defect surfaces at load,
/// never as a silently-blank help panel.
/// </summary>
public sealed class Glossary
{
    private const string ExpectedContentType = "Glossary";

    private readonly Dictionary<string, GlossaryEntry> _byId;
    private readonly List<GlossaryEntry> _ordered;

    private Glossary(List<GlossaryEntry> ordered)
    {
        _ordered = ordered;
        _byId = ordered.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
    }

    /// <summary>Entries in file order (the panel listing order).</summary>
    public IReadOnlyList<GlossaryEntry> Entries => _ordered;

    /// <summary>Entry count (smoke probe).</summary>
    public int Count => _ordered.Count;

    /// <summary>
    /// Parses one glossary.json document. Caller owns the IO (repo walk-up
    /// in the Godot host, file text in tests; EraCore stays IO-pure).
    /// </summary>
    /// <param name="json">Document text.</param>
    /// <param name="sourceName">Label for error messages (e.g. <c>ui/glossary.json</c>).</param>
    /// <exception cref="ArgumentException">Structure or value violations.</exception>
    public static Glossary Parse(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        GlossaryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, UiContentJsonContext.Default.GlossaryDocument);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{sourceName}: invalid JSON ({ex.Message}).", nameof(json));
        }

        if (document is null)
        {
            throw new ArgumentException($"{sourceName}: deserialized to null.", nameof(json));
        }

        if (document.ContentType != ExpectedContentType)
        {
            throw new ArgumentException(
                $"{sourceName}: content_type must be '{ExpectedContentType}', got '{document.ContentType}'.");
        }

        GlossaryEntryJson[]? items = document.Items;
        if (items is not { Length: > 0 })
        {
            throw new ArgumentException($"{sourceName}: items must be a non-empty array.");
        }

        var ordered = new List<GlossaryEntry>(items.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GlossaryEntryJson item in items)
        {
            string id = RequireNonBlank(item.Id, sourceName, "id");
            if (!seen.Add(id))
            {
                throw new ArgumentException($"{sourceName}: duplicate glossary id '{id}'.");
            }

            string termZh = RequireNonBlank(item.TermZh, sourceName, $"items[{id}].term_zh");
            string termEn = RequireNonBlank(item.TermEn, sourceName, $"items[{id}].term_en");
            string defZh = RequireNonBlank(item.DefZh, sourceName, $"items[{id}].def_zh");
            string defEn = RequireNonBlank(item.DefEn, sourceName, $"items[{id}].def_en");
            ordered.Add(new GlossaryEntry(
                id, termZh, termEn, defZh, defEn,
                item.Aliases ?? Array.Empty<string>(),
                item.Related ?? Array.Empty<string>()));
        }

        // Cross-link integrity: every related id must resolve (互链无悬空).
        var catalog = new Glossary(ordered);
        foreach (GlossaryEntry entry in ordered)
        {
            foreach (string related in entry.Related)
            {
                if (!catalog._byId.ContainsKey(related))
                {
                    throw new ArgumentException(
                        $"{sourceName}: items[{entry.Id}].related '{related}' does not resolve.");
                }
            }
        }

        return catalog;
    }

    /// <summary>Looks one entry up by id.</summary>
    /// <exception cref="ArgumentNullException">id is null.</exception>
    public bool TryGet(string id, out GlossaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_byId.TryGetValue(id, out GlossaryEntry? found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// Case-insensitive substring search over id / zh term / en term /
    /// aliases. A blank query returns every entry (the panel's initial
    /// listing); a query that matches nothing returns an empty list.
    /// </summary>
    /// <exception cref="ArgumentNullException">query is null.</exception>
    public IReadOnlyList<GlossaryEntry> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string needle = query.Trim();
        if (needle.Length == 0)
        {
            return _ordered;
        }

        var hits = new List<GlossaryEntry>();
        foreach (GlossaryEntry entry in _ordered)
        {
            if (entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.TermZh.Contains(needle, StringComparison.Ordinal)
                || entry.TermEn.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.Aliases.Any(alias => alias.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(entry);
            }
        }

        return hits;
    }

    private static string RequireNonBlank(string? value, string sourceName, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{sourceName}: {field} must be a non-blank string.");
        }

        return value;
    }
}

/// <summary>Root shape of glossary.json: content_type + items.</summary>
internal sealed record GlossaryDocument(string? ContentType, GlossaryEntryJson[]? Items);

/// <summary>Raw item shape of glossary.json (annotation keys ignored).</summary>
internal sealed record GlossaryEntryJson(
    string? Id,
    string? TermZh,
    string? TermEn,
    string? DefZh,
    string? DefEn,
    string[]? Aliases,
    string[]? Related);

/// <summary>
/// System.Text.Json source-generation context for the T-093 UI content
/// files (AOT red line 07 D-05): glossary / hints / zones / tower-name /
/// enemy-counter rows and the hint progress document all serialize
/// through this one generated context. One context = one declaration
/// with every [JsonSerializable] row inline (TowerSpecJsonContext
/// precedent — splitting a context across partial declarations breaks
/// the generator); the row records live beside their main types.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GlossaryDocument))]
[JsonSerializable(typeof(GlossaryEntryJson))]
[JsonSerializable(typeof(HintDocument))]
[JsonSerializable(typeof(HintEntryJson))]
[JsonSerializable(typeof(HintProgress))]
[JsonSerializable(typeof(EnemyCounterDocument))]
[JsonSerializable(typeof(EnemyCounterRowJson))]
[JsonSerializable(typeof(TowerNamesDocument))]
[JsonSerializable(typeof(TowerNameRowJson))]
[JsonSerializable(typeof(ZoneDocument))]
[JsonSerializable(typeof(ZoneEntryJson))]
[JsonSerializable(typeof(ZoneCellJson))]
[JsonSerializable(typeof(PersonaSettings))]
internal sealed partial class UiContentJsonContext : JsonSerializerContext;
