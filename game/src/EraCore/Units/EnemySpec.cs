using System.Text.Json;

namespace EraCore.Units;

/// <summary>
/// One enemy definition deserialized from content data (K-2 registry file
/// game/content/era1/enemies.json, schema urn:eratd:content:enemy:1; SP-3a
/// anchor table). Field units follow the file's own _units annotations:
/// hp/armor in points, speed in px per tick at the K-1 30 Hz tick domain
/// (1 cell = 32 px). Pure data record, immutable.
/// </summary>
/// <param name="Id">Content id, e.g. "era1:enemy-boss" (K-2 namespace).</param>
/// <param name="SpId">Spec-table row id, e.g. "e1_boss" (SP-3a # column).</param>
/// <param name="NameZh">Chinese display name (logging/UI only).</param>
/// <param name="IsBoss">Whether the row is a boss (is_boss in the file).</param>
/// <param name="Hp">Base hit points (> 0).</param>
/// <param name="Armor">Armor points (≥ 0).</param>
/// <param name="SpeedPxPerTick">Movement speed in px per 30 Hz tick (> 0).</param>
/// <param name="TrampleRadiusCells">Trample aura radius in cells; 0 = no trample.</param>
/// <param name="CanvasSizePx">Render canvas size in px (64~96 for bosses).</param>
public sealed record EnemySpec(
    string Id,
    string SpId,
    string NameZh,
    bool IsBoss,
    int Hp,
    int Armor,
    double SpeedPxPerTick,
    double TrampleRadiusCells,
    int CanvasSizePx);

/// <summary>
/// Read-only catalog of enemy definitions parsed from one enemies.json
/// document (C-10 E1 thin, T-039). Parsing is strict and total: malformed
/// JSON, a wrong content_type, missing required fields, duplicate ids or
/// non-positive hp/speed all throw — a content defect must surface as a
/// load failure, never as a silently-zeroed spec. Keys starting with
/// underscore are annotation keys and ignored, per the loader convention
/// declared inside the content files themselves.
/// </summary>
public sealed class EnemySpecCatalog
{
    private readonly Dictionary<string, EnemySpec> _byId;

    private EnemySpecCatalog(Dictionary<string, EnemySpec> byId, int tickRateHz, int cellSizePx)
    {
        _byId = byId;
        TickRateHz = tickRateHz;
        CellSizePx = cellSizePx;
    }

    /// <summary>Tick rate the file's speeds are expressed against (K-1: 30).</summary>
    public int TickRateHz { get; }

    /// <summary>Cell size in px the file's radii are expressed against (1 cell = 32 px).</summary>
    public int CellSizePx { get; }

    /// <summary>Number of enemy definitions in the catalog.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// Parses one enemies.json document. Caller owns the IO (IFileStore /
    /// test harness reads the text; EraCore stays IO-pure).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Structure or value violations: wrong content_type, missing required
    /// field, duplicate id, non-positive hp/speed, negative armor.
    /// </exception>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static EnemySpecCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Enemy catalog JSON must not be null or blank.", nameof(json));
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        RequireObject(root, "root");
        RequireContentType(root, "Enemy", "root");

        int tickRateHz = ReadInt(root, "tick_rate_hz", "root");
        int cellSizePx = ReadInt(root, "cell_size_px", "root");
        if (tickRateHz <= 0)
        {
            throw new ArgumentException($"tick_rate_hz must be positive (got {tickRateHz}).", nameof(json));
        }

        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"cell_size_px must be positive (got {cellSizePx}).", nameof(json));
        }

        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Enemy catalog needs an 'items' array.", nameof(json));
        }

        var byId = new Dictionary<string, EnemySpec>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            RequireObject(item, "items[]");
            RequireContentType(item, "Enemy", "items[]");
            string id = ReadString(item, "id", "items[]");
            if (!byId.TryAdd(id, ParseItem(item, id)))
            {
                throw new ArgumentException($"Duplicate enemy id '{id}'.", nameof(json));
            }
        }

        if (byId.Count == 0)
        {
            throw new ArgumentException("Enemy catalog 'items' must not be empty.", nameof(json));
        }

        return new EnemySpecCatalog(byId, tickRateHz, cellSizePx);
    }

    /// <summary>Looks up one definition by content id.</summary>
    /// <exception cref="KeyNotFoundException">The id is not in the catalog.</exception>
    public EnemySpec Get(string id)
    {
        if (!_byId.TryGetValue(id, out EnemySpec? spec))
        {
            throw new KeyNotFoundException($"Enemy id '{id}' is not in the catalog.");
        }

        return spec;
    }

    /// <summary>Non-throwing lookup variant.</summary>
    public bool TryGet(string id, out EnemySpec spec) => _byId.TryGetValue(id, out spec!);

    private static EnemySpec ParseItem(JsonElement item, string id)
    {
        string spId = ReadString(item, "sp_id", id);
        string nameZh = ReadString(item, "name_zh", id);
        bool isBoss = item.TryGetProperty("is_boss", out JsonElement bossFlag)
            && bossFlag.ValueKind == JsonValueKind.True;
        int hp = ReadInt(item, "hp", id);
        int armor = item.TryGetProperty("armor", out JsonElement armorEl)
            ? armorEl.GetInt32()
            : 0;
        double speed = ReadDouble(item, "speed_px_per_tick", id);
        int canvas = item.TryGetProperty("canvas_size_px", out JsonElement canvasEl)
            ? canvasEl.GetInt32()
            : 32;
        double trampleRadius = 0;
        if (item.TryGetProperty("traits", out JsonElement traits)
            && traits.ValueKind == JsonValueKind.Object
            && traits.TryGetProperty("trample", out JsonElement trample)
            && trample.ValueKind == JsonValueKind.Object
            && trample.TryGetProperty("radius_cells", out JsonElement radius))
        {
            trampleRadius = radius.GetDouble();
        }

        if (hp <= 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have positive hp (got {hp}).", nameof(id));
        }

        if (armor < 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have non-negative armor (got {armor}).", nameof(id));
        }

        if (speed <= 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have positive speed (got {speed}).", nameof(id));
        }

        if (trampleRadius < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative trample radius (got {trampleRadius}).", nameof(id));
        }

        return new EnemySpec(id, spId, nameZh, isBoss, hp, armor, speed, trampleRadius, canvas);
    }

    private static void RequireObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Enemy catalog {where} must be a JSON object.", nameof(where));
        }
    }

    private static void RequireContentType(JsonElement element, string expected, string where)
    {
        if (!element.TryGetProperty("content_type", out JsonElement type)
            || type.GetString() != expected)
        {
            throw new ArgumentException(
                $"Enemy catalog {where} must declare content_type '{expected}'.", nameof(expected));
        }
    }

    private static string ReadString(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Enemy catalog {where} needs a non-empty string '{name}'.", nameof(name));
        }

        return value.GetString()!;
    }

    private static int ReadInt(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Enemy catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetInt32();
    }

    private static double ReadDouble(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Enemy catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetDouble();
    }
}
