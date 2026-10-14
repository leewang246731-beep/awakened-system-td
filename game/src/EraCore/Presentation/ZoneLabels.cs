using System.Text.Json;
using EraCore.Contracts.Foundation;

namespace EraCore.Presentation;

/// <summary>
/// One named map zone (T-093, F-117 分区命名标注层, §10.5/§10.12): the
/// place-name layer whose ids share the order-lane enum source
/// (zones.json — SimHost 锚点表 keys 同源，运行时互证).
/// </summary>
/// <param name="Id">Stable zone id (= order lane id, e.g. <c>east_gate</c>).</param>
/// <param name="NameZh">Chinese place name.</param>
/// <param name="NameEn">English place name.</param>
/// <param name="Cell">Representative cell (label anchor / camera target).</param>
/// <param name="Aliases">Lookup aliases (令簿地名可点击定位的解析面).</param>
public sealed record ZoneLabel(
    string Id,
    string NameZh,
    string NameEn,
    CellCoord Cell,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Zone-label table parsed from zones.json (T-093, F-117). Strict parse:
/// wrong content_type, empty items, duplicate ids, blank names, or cells
/// outside the declared map bounds all throw. Owns the two pure
/// decisions of the layer: which labels surface for a camera rect
/// (镜头靠近浮显) and which zone a place-name query resolves to
/// (令簿地名点击定位镜头).
/// </summary>
public sealed class ZoneLabelTable
{
    private const string ExpectedContentType = "ZoneLabels";

    private readonly List<ZoneLabel> _zones;

    private ZoneLabelTable(List<ZoneLabel> zones) => _zones = zones;

    /// <summary>Zones in file order.</summary>
    public IReadOnlyList<ZoneLabel> Zones => _zones;

    /// <summary>Zone count (smoke probe).</summary>
    public int Count => _zones.Count;

    /// <summary>
    /// Parses one zones.json document with the map bounds the cells must
    /// live inside.
    /// </summary>
    /// <param name="json">Document text.</param>
    /// <param name="sourceName">Label for error messages.</param>
    /// <param name="mapWidthCells">Map width in cells (bounds check).</param>
    /// <param name="mapHeightCells">Map height in cells (bounds check).</param>
    /// <exception cref="ArgumentException">Structure or value violations.</exception>
    public static ZoneLabelTable Parse(
        string json, string sourceName, int mapWidthCells, int mapHeightCells)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (mapWidthCells < 1 || mapHeightCells < 1)
        {
            throw new ArgumentException("map bounds must be positive.");
        }

        ZoneDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, UiContentJsonContext.Default.ZoneDocument);
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

        if (document.Items is not { Length: > 0 })
        {
            throw new ArgumentException($"{sourceName}: items must be a non-empty array.");
        }

        var zones = new List<ZoneLabel>(document.Items.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZoneEntryJson item in document.Items)
        {
            string id = item.Id ?? string.Empty;
            if (id.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[].id must be non-blank.");
            }

            if (!seen.Add(id))
            {
                throw new ArgumentException($"{sourceName}: duplicate zone id '{id}'.");
            }

            string nameZh = item.NameZh ?? string.Empty;
            string nameEn = item.NameEn ?? string.Empty;
            if (nameZh.Trim().Length == 0 || nameEn.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[{id}] name_zh/name_en must be non-blank (双语交付).");
            }

            if (item.Cell is not { } cell)
            {
                throw new ArgumentException($"{sourceName}: items[{id}].cell is required.");
            }

            if (cell.X < 0 || cell.X >= mapWidthCells || cell.Y < 0 || cell.Y >= mapHeightCells)
            {
                throw new ArgumentException(
                    $"{sourceName}: items[{id}].cell ({cell.X},{cell.Y}) outside the {mapWidthCells}x{mapHeightCells} map.");
            }

            zones.Add(new ZoneLabel(id, nameZh, nameEn, new CellCoord(cell.X, cell.Y), item.Aliases ?? Array.Empty<string>()));
        }

        return new ZoneLabelTable(zones);
    }

    /// <summary>
    /// Zones whose representative cell lies inside the camera view rect
    /// (cell domain) — the labels that surface while the camera is near
    /// (§10.12 镜头靠近浮显区名). Inclusive edges.
    /// </summary>
    /// <param name="cellX0">View left edge in cells.</param>
    /// <param name="cellY0">View top edge in cells.</param>
    /// <param name="cellX1">View right edge in cells (inclusive).</param>
    /// <param name="cellY1">View bottom edge in cells (inclusive).</param>
    public IReadOnlyList<ZoneLabel> VisibleIn(int cellX0, int cellY0, int cellX1, int cellY1) =>
        _zones.Where(zone =>
            zone.Cell.X >= cellX0 && zone.Cell.X <= cellX1
            && zone.Cell.Y >= cellY0 && zone.Cell.Y <= cellY1).ToArray();

    /// <summary>
    /// Resolves a place-name query (id / zh name / en name / alias,
    /// case-insensitive contains, first hit in file order) — the lookup
    /// behind「令簿地名可点击定位镜头」. Blank queries resolve nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException">query is null.</exception>
    public ZoneLabel? TryResolve(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string needle = query.Trim();
        if (needle.Length == 0)
        {
            return null;
        }

        return _zones.FirstOrDefault(zone =>
            zone.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || zone.NameZh.Contains(needle, StringComparison.Ordinal)
            || zone.NameEn.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || zone.Aliases.Any(alias => alias.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Root shape of zones.json.</summary>
internal sealed record ZoneDocument(string? ContentType, ZoneEntryJson[]? Items);

/// <summary>Raw zone row shape of zones.json.</summary>
internal sealed record ZoneEntryJson(string? Id, string? NameZh, string? NameEn, ZoneCellJson? Cell, string[]? Aliases);

/// <summary>Raw cell shape of zones.json.</summary>
internal sealed record ZoneCellJson(int X, int Y);
