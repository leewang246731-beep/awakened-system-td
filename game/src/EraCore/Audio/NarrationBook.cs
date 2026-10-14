using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;

namespace EraCore.Audio;

/// <summary>Kind of one one-time narration anchor (T-092, F-138).</summary>
public enum NarrationKind
{
    /// <summary>章节开场白 — plays once when the chapter's opening narrator line shows.</summary>
    Opening = 0,

    /// <summary>胜利交底 — plays once when the victory handover line shows.</summary>
    Victory = 1,
}

/// <summary>
/// One one-time narration row (T-092, F-138): the line key from the story
/// chapter (T-089 content), its kind, and the CC0 stinger file that accents
/// it once per save.
/// </summary>
/// <param name="Id">Line key, e.g. <c>e1.open.narrator</c> (must exist in the chapter file).</param>
/// <param name="Kind">Opening or victory anchor.</param>
/// <param name="StingerFile">File name under <c>res://audio/cc0/</c> (ledger-documented CC0 asset).</param>
public sealed record NarrationCue(string Id, NarrationKind Kind, string StingerFile);

/// <summary>
/// Strict parser of the narration anchor table (schema
/// <c>urn:eratd:content:audio:narration:1</c>): closed marker, one opening +
/// one victory row per era of the thin slice (E1/E2), unique ids,
/// kebab-case stinger file names.
/// </summary>
public sealed class NarrationTable
{
    /// <summary>Content schema URN of the narration anchor table.</summary>
    public const string SchemaUrn = "urn:eratd:content:audio:narration:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentTypeMarker = "NarrationTable";

    /// <summary>Frozen anchor count: opening + victory × E1/E2 (T-092 card narrated=2 per era pairing).</summary>
    public const int RowCount = 4; // ci-allow: R7 T-092 冻结口径=E1/E2 各开场+胜利两锚（F-138 薄版；E3 起扩表）

    private readonly Dictionary<string, NarrationCue> _byId;

    private NarrationTable(IReadOnlyList<NarrationCue> rows)
    {
        Rows = rows;
        _byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
    }

    /// <summary>All anchors in file order.</summary>
    public IReadOnlyList<NarrationCue> Rows { get; }

    /// <summary>Anchor by line key; null when absent.</summary>
    public NarrationCue? Find(string id) =>
        _byId.TryGetValue(id, out NarrationCue? cue) ? cue : null;

    /// <summary>
    /// Strict parse: closed marker, exactly <see cref="RowCount"/> rows,
    /// unique ids, closed kinds, kebab-case <c>.ogg</c> stinger names.
    /// </summary>
    /// <param name="json">Raw table JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="AudioSchemaException">Any schema violation.</exception>
    public static NarrationTable Parse(string json, string sourceName = "narration")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AudioSchemaException($"{sourceName}: 根必须是对象");
        }

        if (!root.TryGetProperty("content_type", out JsonElement marker) ||
            marker.ValueKind != JsonValueKind.String ||
            marker.GetString() != ContentTypeMarker)
        {
            throw new AudioSchemaException($"{sourceName}: content_type 必须为 {ContentTypeMarker}");
        }

        if (!root.TryGetProperty("rows", out JsonElement rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少 rows 数组");
        }

        if (rowsEl.GetArrayLength() != RowCount)
        {
            throw new AudioSchemaException($"{sourceName}: rows 必须 {RowCount} 行（E1/E2 × 开场/胜利）");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<NarrationCue>(RowCount);
        foreach (JsonElement row in rowsEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new AudioSchemaException($"{sourceName}: row 必须是对象");
            }

            if (!row.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                throw new AudioSchemaException($"{sourceName}: 缺少字符串字段 id");
            }

            string id = idEl.GetString()!;
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                throw new AudioSchemaException($"{sourceName}: id 缺失或重复 {id}");
            }

            if (!row.TryGetProperty("kind", out JsonElement kindEl) || kindEl.ValueKind != JsonValueKind.String)
            {
                throw new AudioSchemaException($"{sourceName}: {id} 缺少字符串字段 kind");
            }

            NarrationKind kind = Enum.TryParse<NarrationKind>(kindEl.GetString(), ignoreCase: true, out NarrationKind parsed) &&
                Enum.IsDefined(parsed)
                    ? parsed
                    : throw new AudioSchemaException($"{sourceName}: {id} 未登记 kind {kindEl.GetString()}");

            if (!row.TryGetProperty("stinger", out JsonElement stingerEl) || stingerEl.ValueKind != JsonValueKind.String)
            {
                throw new AudioSchemaException($"{sourceName}: {id} 缺少字符串字段 stinger");
            }

            string stinger = stingerEl.GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(stinger, @"^[a-z0-9]+(-[a-z0-9]+)*\.ogg$"))
            {
                throw new AudioSchemaException($"{sourceName}: {id} stinger={stinger} 非 kebab-case .ogg 文件名");
            }

            rows.Add(new NarrationCue(id, kind, stinger));
        }

        return new NarrationTable(rows);
    }
}

/// <summary>Persisted payload of the one-time narration flags (sorted on save for stable diffs).</summary>
/// <param name="PlayedIds">Line keys already played once on this save.</param>
public sealed record NarrationFlags(IReadOnlyList<string> PlayedIds)
{
    /// <summary>Empty flags — fresh save.</summary>
    public static NarrationFlags Empty { get; } = new(Array.Empty<string>());
}

/// <summary>
/// System.Text.Json source-generation context for the narration flags (AOT
/// red line 07 D-05 — mirrors <c>PresentationJsonContext</c>): the only
/// serialization entry point of the audio persistence slice.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(NarrationFlags))]
public sealed partial class AudioJsonContext : JsonSerializerContext
{
}

/// <summary>
/// One-time narration book (T-092, F-138, §14 一次性旁白): marks which
/// narration anchors have already played on this save and persists the
/// flags through the D-03 store (<c>settings/narration.json</c>, the
/// <c>PanelSettingsStore</c> precedent — a preference must never block
/// startup). Pure state: the adapter asks <see cref="MarkPlayed"/> when a
/// narration line is about to show and plays stinger + tone only on the
/// first true.
/// </summary>
public sealed class NarrationBook
{
    /// <summary>Flags file path relative to the injected data root.</summary>
    public const string RelativePath = "settings/narration.json";

    private readonly NarrationTable _table;
    private readonly HashSet<string> _played = new(StringComparer.Ordinal);

    /// <summary>Creates an empty book over one anchor table.</summary>
    public NarrationBook(NarrationTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <summary>The anchor table (closed id set of this book).</summary>
    public NarrationTable Table => _table;

    /// <summary>Number of anchors already played.</summary>
    public int PlayedCount => _played.Count;

    /// <summary>True when the anchor has played on this save.</summary>
    /// <exception cref="ArgumentException">id is not a registered anchor (closed set).</exception>
    public bool HasPlayed(string id) => _played.Contains(Resolve(id).Id);

    /// <summary>
    /// Marks one anchor played; returns true only on the FIRST mark — the
    /// adapter plays the one-time narration exactly then.
    /// </summary>
    /// <exception cref="ArgumentException">id is not a registered anchor.</exception>
    public bool MarkPlayed(string id)
    {
        NarrationCue cue = Resolve(id);
        return _played.Add(cue.Id);
    }

    /// <summary>Writes the flags through the D-03 store (sorted — stable diffs).</summary>
    /// <exception cref="ArgumentNullException">store is null.</exception>
    public void Save(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var flags = new NarrationFlags(_played.Order(StringComparer.Ordinal).ToArray());
        store.WriteAllBytes(RelativePath, JsonSerializer.SerializeToUtf8Bytes(flags, AudioJsonContext.Default.NarrationFlags));
    }

    /// <summary>
    /// Loads the flags; a missing or corrupt file yields an empty book (a
    /// narration preference replays at worst — never a startup failure).
    /// Unknown ids in the file are dropped (stale rows from older tables).
    /// </summary>
    /// <exception cref="ArgumentNullException">store or table is null.</exception>
    public static NarrationBook Load(IFileStore store, NarrationTable table)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(table);

        var book = new NarrationBook(table);
        if (!store.Exists(RelativePath))
        {
            return book;
        }

        try
        {
            NarrationFlags? flags = JsonSerializer.Deserialize(
                store.ReadAllBytes(RelativePath), AudioJsonContext.Default.NarrationFlags);
            if (flags is null)
            {
                return book;
            }

            foreach (string id in flags.PlayedIds)
            {
                if (table.Find(id) is not null)
                {
                    book._played.Add(id); // closed-set filter — stale rows dropped
                }
            }

            return book;
        }
        catch (JsonException)
        {
            return new NarrationBook(table); // corrupt preference file — empty book, never a startup failure
        }
    }

    private NarrationCue Resolve(string id) =>
        _table.Find(id) ?? throw new ArgumentException(
            $"未登记的一次性旁白 id {id}（封闭表 {string.Join("/", _table.Rows.Select(row => row.Id))}）", nameof(id));
}
