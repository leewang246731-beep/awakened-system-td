using EraCore.Audio;
using EraCore.IO;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the one-time narration book (T-092, F-138 一次性旁白): first-play
/// semantics, replay suppression, D-03 persistence round-trips, tolerant
/// loading, and the closed-id guards.
/// </summary>
// [C-25][F-138] 一次性旁白：一次性标记+入存档（合法/边界/非法）
public class NarrationBookTests : IDisposable
{
    private const string LegalJson = """
        {
          "content_type": "NarrationTable",
          "rows": [
            { "id": "e1.open.narrator", "kind": "opening", "stinger": "kenney-jingles-pizzicato-07.ogg" },
            { "id": "e1.end.handover", "kind": "victory", "stinger": "kenney-jingles-steel-02.ogg" },
            { "id": "e2.open.narrator", "kind": "opening", "stinger": "kenney-jingles-pizzicato-07.ogg" },
            { "id": "e2.end.zero", "kind": "victory", "stinger": "kenney-jingles-steel-02.ogg" }
          ]
        }
        """;

    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private static NarrationTable Table() => NarrationTable.Parse(LegalJson);

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — the E1/E2 opening+victory anchors parse with their stingers
    public void Legal_table_parses()
    {
        NarrationTable table = Table();

        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(NarrationKind.Opening, table.Find("e1.open.narrator")!.Kind);
        Assert.Equal(NarrationKind.Victory, table.Find("e1.end.handover")!.Kind);
        Assert.Equal("kenney-jingles-pizzicato-07.ogg", table.Find("e2.open.narrator")!.StingerFile);
        Assert.Null(table.Find("e3.open.narrator")); // closed set — E3 lands with its chapter
    }

    [Fact] // legal — first mark plays, every later mark stays silent
    public void MarkPlayed_fires_exactly_once()
    {
        var book = new NarrationBook(Table());

        Assert.False(book.HasPlayed("e1.open.narrator"));
        Assert.True(book.MarkPlayed("e1.open.narrator")); // the one audible play
        Assert.True(book.HasPlayed("e1.open.narrator"));
        Assert.False(book.MarkPlayed("e1.open.narrator")); // replay suppressed
        Assert.False(book.MarkPlayed("e1.open.narrator"));
        Assert.Equal(1, book.PlayedCount);

        Assert.True(book.MarkPlayed("e1.end.handover")); // other anchors unaffected
        Assert.Equal(2, book.PlayedCount);
    }

    [Fact] // legal — flags persist through the D-03 store and reload
    public void Flags_persist_and_reload()
    {
        var store = new DevFileStore(_root);
        var book = new NarrationBook(Table());
        _ = book.MarkPlayed("e1.open.narrator");
        _ = book.MarkPlayed("e2.end.zero");

        book.Save(store);
        Assert.True(store.Exists(NarrationBook.RelativePath));

        NarrationBook reloaded = NarrationBook.Load(store, Table());
        Assert.True(reloaded.HasPlayed("e1.open.narrator"));
        Assert.True(reloaded.HasPlayed("e2.end.zero"));
        Assert.False(reloaded.HasPlayed("e1.end.handover"));
        Assert.False(reloaded.MarkPlayed("e1.open.narrator")); // still one-time after reload
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — a missing file yields an empty book (fresh save replays)
    public void Missing_file_loads_empty()
    {
        NarrationBook book = NarrationBook.Load(new DevFileStore(_root), Table());

        Assert.Equal(0, book.PlayedCount);
        Assert.True(book.MarkPlayed("e1.open.narrator"));
    }

    [Fact] // boundary — a corrupt preference file never blocks startup
    public void Corrupt_file_loads_empty()
    {
        var store = new DevFileStore(_root);
        store.WriteAllBytes(NarrationBook.RelativePath, "{ not valid json"u8.ToArray());

        NarrationBook book = NarrationBook.Load(store, Table());

        Assert.Equal(0, book.PlayedCount);
    }

    [Fact] // boundary — stale ids from an older table are dropped on load
    public void Stale_ids_are_dropped()
    {
        var store = new DevFileStore(_root);
        store.WriteAllBytes(
            NarrationBook.RelativePath,
            """{"PlayedIds":["e9.open.narrator","e1.end.handover"]}"""u8.ToArray());

        NarrationBook book = NarrationBook.Load(store, Table());

        Assert.Equal(1, book.PlayedCount); // only the registered anchor survived
        Assert.True(book.HasPlayed("e1.end.handover"));
    }

    [Fact] // boundary — saved flags are sorted (stable file diffs across runs)
    public void Saved_flags_are_sorted()
    {
        var store = new DevFileStore(_root);
        var book = new NarrationBook(Table());
        _ = book.MarkPlayed("e2.end.zero");
        _ = book.MarkPlayed("e1.open.narrator");

        book.Save(store);

        string json = System.Text.Encoding.UTF8.GetString(store.ReadAllBytes(NarrationBook.RelativePath));
        Assert.Contains("""{"PlayedIds":["e1.open.narrator","e2.end.zero"]}""", json);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — ids outside the anchor table are rejected (closed set)
    public void Unknown_id_rejected()
    {
        var book = new NarrationBook(Table());

        Assert.Throws<ArgumentException>(() => book.HasPlayed("e1.open.system"));
        Assert.Throws<ArgumentException>(() => book.MarkPlayed("e3.open.narrator"));
    }

    [Theory] // illegal — schema violations: marker, kinds, stinger names, counts
    [InlineData("content_type", "WrongMarker")]
    [InlineData("kind", "intermission")]
    [InlineData("stinger", "Jingles PIZZI07.OGG")]
    [InlineData("rows", "one row dropped")]
    public void Illegal_schema_rejected(string key, string value)
    {
        string json = key switch
        {
            "content_type" => LegalJson.Replace("\"NarrationTable\"", $"\"{value}\""),
            "kind" => LegalJson.Replace("\"kind\": \"opening\"", $"\"kind\": \"{value}\""),
            "stinger" => LegalJson.Replace("kenney-jingles-pizzicato-07.ogg", value),
            "rows" => LegalJson.Replace(
                "    { \"id\": \"e1.open.narrator\", \"kind\": \"opening\", \"stinger\": \"kenney-jingles-pizzicato-07.ogg\" },\n",
                ""),
            _ => throw new InvalidOperationException(key),
        };

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => NarrationTable.Parse(json));
    }

    [Fact] // illegal — duplicate ids are rejected
    public void Duplicate_id_rejected()
    {
        string json = LegalJson.Replace("\"e2.end.zero\"", "\"e1.end.handover\"");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => NarrationTable.Parse(json));
    }

    [Fact] // illegal — null table/store are rejected on construction and load
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new NarrationBook(null!));
        Assert.Throws<ArgumentNullException>(() => NarrationBook.Load(new DevFileStore(_root), null!));
        Assert.Throws<ArgumentNullException>(() => NarrationBook.Load(null!, Table()));

        var book = new NarrationBook(Table());
        Assert.Throws<ArgumentNullException>(() => book.Save(null!));
    }
}
