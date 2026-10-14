using System.Text.Json;
using System.Text.RegularExpressions;

namespace EraCore.Agent.Grammar;

/// <summary>
/// The tower alias dictionary of the L1 grammar (C-15, T-052), built from
/// the content registry tower file (<c>game/content/era1/towers.json</c>:
/// id + name_zh + name_en + aliases — K-2「别名词典与 ContentType aliases[]
/// 同源」, SP-2 配套口径). Every surface is TextNormalizer-normalized at
/// load; lookups resolve a captured surface to the namespaced tower id.
/// Includes the homophone tolerance pass (v3.2 §11.5「同音容错」): a tower
/// surface with one substituted character (弩塔-style near miss, 箭楼 vs 箭塔)
/// is repaired in the normalized input before pattern matching.
/// </summary>
public sealed class AliasDictionary
{
    /// <summary>Normalized surface → namespaced tower id.</summary>
    private readonly Dictionary<string, string> _surfaceToId = new(StringComparer.Ordinal);

    /// <summary>Surfaces ordered longest-first (ordinal tiebreak) — match priority order.</summary>
    private readonly List<string> _surfaceOrder = new();

    private AliasDictionary()
    {
    }

    /// <summary>Normalized surfaces, longest-first (deterministic).</summary>
    public IReadOnlyList<string> Surfaces => _surfaceOrder;

    /// <summary>
    /// Builds the dictionary from a towers.json document. Items are processed
    /// in id order (deterministic, mirroring the K-2 registry discipline); a
    /// surface claimed by two towers throws <see cref="InvalidOperationException"/>
    /// — conflicts surface, never silently overwrite. Items without id, or
    /// with no usable name surface at all, throw <see cref="JsonException"/>
    /// (strict content stance, same as TowerSpec).
    /// </summary>
    public static AliasDictionary FromTowerJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Tower alias document has no items array.");
        }

        var rows = new List<(string Id, List<string> Surfaces)>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Tower alias item without a string id.");
            }

            var id = idElement.GetString()!;
            var surfaces = new List<string>();
            CollectSurface(item, "name_zh", surfaces);
            CollectSurface(item, "name_en", surfaces);
            if (item.TryGetProperty("aliases", out var aliasesElement)
                && aliasesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliasesElement.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String)
                    {
                        AddNormalized(alias.GetString(), surfaces);
                    }
                }
            }

            rows.Add((id, surfaces));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        return FromRows(rows.Select(r => (r.Id, (IEnumerable<string>)r.Surfaces)));
    }

    /// <summary>
    /// Builds the dictionary from raw (id, surfaces) rows: normalized at load,
    /// processed in id order (deterministic, mirroring the K-2 registry
    /// discipline); a surface claimed by two ids throws
    /// <see cref="InvalidOperationException"/> — conflicts surface, never
    /// silently overwrite. Rows with an empty surface list are legal (they
    /// simply contribute nothing).
    /// </summary>
    public static AliasDictionary FromRows(IEnumerable<(string Id, IEnumerable<string> Surfaces)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var ordered = rows
            .Select(r => (r.Id, Surfaces: r.Surfaces.ToList()))
            .OrderBy(r => r.Id, StringComparer.Ordinal);
        var dictionary = new AliasDictionary();
        foreach (var (id, surfaces) in ordered)
        {
            foreach (var raw in surfaces)
            {
                var surface = TextNormalizer.Normalize(raw);
                if (surface.Length == 0)
                {
                    continue;
                }

                if (dictionary._surfaceToId.TryGetValue(surface, out var owner) && owner != id)
                {
                    throw new InvalidOperationException(
                        $"Alias conflict: '{surface}' claimed by both '{owner}' and '{id}' (K-2: conflicts surface).");
                }

                dictionary._surfaceToId[surface] = id;
            }
        }

        dictionary._surfaceOrder.AddRange(dictionary._surfaceToId.Keys);
        dictionary._surfaceOrder.Sort((a, b) => b.Length != a.Length ? b.Length - a.Length : string.CompareOrdinal(a, b));
        return dictionary;
    }

    /// <summary>Normalized surface → id pairs, in surface order (deterministic).</summary>
    public IEnumerable<(string Surface, string Id)> Entries
        => _surfaceOrder.Select(surface => (surface, _surfaceToId[surface]));

    /// <summary>
    /// Resolves a captured (already normalized, possibly plural) tower
    /// surface to its id: exact hit first, then English plural stripping
    /// ("arrowtowers" → "arrowtower", "…es" before "…s"). False when unknown.
    /// </summary>
    public bool TryResolve(string captured, out string id)
    {
        ArgumentNullException.ThrowIfNull(captured);

        if (_surfaceToId.TryGetValue(captured, out id!))
        {
            return true;
        }

        if (captured.EndsWith("es", StringComparison.Ordinal) && _surfaceToId.TryGetValue(captured[..^2], out id!))
        {
            return true;
        }

        if (captured.EndsWith('s') && _surfaceToId.TryGetValue(captured[..^1], out id!))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Regex alternation (source text) matching any tower surface with an
    /// optional English plural suffix, longest surface first. Embedded by the
    /// parser into the <c>{tower}</c> slot group.
    /// </summary>
    public string TowerAlternation()
        => string.Join("|", _surfaceOrder.Select(s => Regex.Escape(s) + "(?:es|s)?"));

    /// <summary>
    /// Homophone tolerance pass (v3.2 §11.5「同音容错」), T-069 hardened:
    /// scans the normalized text for a window within edit distance 1 of a
    /// known tower surface and rewrites that window to the canonical surface.
    /// Two window families: equal length (one substituted char, 箭楼→箭塔)
    /// for every surface, and ±1 length (one inserted/deleted char,
    /// camfire→campfire, T-061 miss #205) only for surfaces of ≥6 chars where
    /// the indel risk is low.
    ///
    /// A candidate is REJECTED when (T-061 §5 defect fix — the guard used to
    /// check only the window itself): the window itself contains an exact
    /// tower surface, OR the window OVERLAPS an occurrence of any protected
    /// surface (tower surfaces plus the caller-supplied lexicon words —
    /// regions/resources/scopes) in the original text. The overlap guard is
    /// what stops「在南面建两座火堆」from rewriting the 座火 window into 篝火 —
    /// that window shares its 火 with the adjacent exact tower surface 火堆 —
    /// and「查一下石头」from rewriting 下石 into 落石 over the resource word
    /// 石头. Candidate order: surface (longest first), then start position,
    /// then equal-length before indel windows — first accepted candidate wins
    /// (deterministic). Returns the repaired text, or null when no repair
    /// applies (including when the text already matches exactly).
    /// </summary>
    public string? FuzzyRepair(string normalizedText, IEnumerable<string>? protectedSurfaces = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);

        var intervals = CollectProtectedIntervals(normalizedText, protectedSurfaces);
        foreach (var surface in _surfaceOrder)
        {
            if (normalizedText.Length < surface.Length - 1)
            {
                continue;
            }

            // Equal-length windows (single substitution) — all surfaces.
            if (surface.Length >= 1)
            {
                for (var start = 0; start + surface.Length <= normalizedText.Length; start++)
                {
                    var window = normalizedText.Substring(start, surface.Length);
                    if (ContainsExactSurface(window) || CrossesPhraseBoundary(window)
                        || Overlaps(intervals, start, window.Length))
                    {
                        continue;
                    }

                    if (WithinEditDistance1(window, surface))
                    {
                        return Splice(normalizedText, start, window.Length, surface);
                    }
                }
            }

            // Indel windows (one inserted or deleted char) — long surfaces only.
            if (surface.Length < IndelMinSurfaceLength)
            {
                continue;
            }

            for (var start = 0; start + surface.Length - 1 <= normalizedText.Length; start++)
            {
                var window = normalizedText.Substring(start, surface.Length - 1);
                if (window.Length > 0 && !ContainsExactSurface(window) && !CrossesPhraseBoundary(window)
                    && !Overlaps(intervals, start, window.Length)
                    && IsOneDeletion(surface, window))
                {
                    return Splice(normalizedText, start, window.Length, surface);
                }
            }

            for (var start = 0; start + surface.Length + 1 <= normalizedText.Length; start++)
            {
                var window = normalizedText.Substring(start, surface.Length + 1);
                // Insertion windows: only DOUBLED-char typos (rockfalll,
                // rrockfall) are repaired — deleting the doubled char restores
                // the surface. A one-char-anywhere insertion would happily eat
                // the last letter of a preceding word ("use the rockfalll" →
                // window erockfall), which is a window mis-cut, not a typo.
                if (!CrossesPhraseBoundary(window)
                    && !OverlapsForeignInterval(intervals, start, window.Length, surface)
                    && IsDoubledCharInsertion(window, surface))
                {
                    return Splice(normalizedText, start, window.Length, surface);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// CJK function characters that never appear inside a tower surface but DO
    /// appear in ordinal designations (3号塔) and possessive/attribute phrases
    /// (东门的塔). A repair window containing one crosses a phrase boundary —
    /// rewriting it can only corrupt an ordinal or a region's-tower phrase
    /// (「出售3号塔」的 号塔→箭塔、「升级东门的塔」的 的塔→箭塔 both class
    /// T-069). Such windows are rejected outright.
    /// </summary>
    private static bool CrossesPhraseBoundary(string window)
        => window.Contains('的') || window.Contains('号');

    /// <summary>Surfaces shorter than this never get indel repair (false-positive guard, T-061 §6.2-4).</summary>
    private const int IndelMinSurfaceLength = 6;

    /// <summary>Replaces [start, start+length) with the canonical surface (length may differ by one).</summary>
    private static string Splice(string text, int start, int length, string surface)
        => string.Concat(text.AsSpan(0, start), surface, text.AsSpan(start + length));

    /// <summary>
    /// All [start, length] spans of protected-surface occurrences in the text,
    /// carrying the surface itself (exact tower surfaces are always protected;
    /// the caller adds lexicon words). A repair window overlapping any of them
    /// would corrupt an already-correct word, so it is rejected — except an
    /// occurrence of the candidate surface inside an insertion window (that is
    /// the doubled-char typo signal).
    /// </summary>
    private List<(int Start, int Length, string Surface)> CollectProtectedIntervals(
        string text, IEnumerable<string>? protectedSurfaces)
    {
        var intervals = new List<(int Start, int Length, string Surface)>();
        foreach (var surface in ProtectedSurfaceStream(protectedSurfaces))
        {
            for (var start = 0; start + surface.Length <= text.Length; start++)
            {
                if (text.IndexOf(surface, start, StringComparison.Ordinal) == start)
                {
                    intervals.Add((start, surface.Length, surface));
                }
            }
        }

        return intervals;
    }

    private IEnumerable<string> ProtectedSurfaceStream(IEnumerable<string>? protectedSurfaces)
    {
        foreach (var surface in _surfaceOrder)
        {
            yield return surface;
        }

        if (protectedSurfaces is null)
        {
            yield break;
        }

        foreach (var raw in protectedSurfaces)
        {
            if (raw.Length >= 2)
            {
                yield return raw;
            }
        }
    }

    private static bool Overlaps(List<(int Start, int Length, string Surface)> intervals, int start, int length)
    {
        foreach (var (intervalStart, intervalLength, _) in intervals)
        {
            if (start < intervalStart + intervalLength && intervalStart < start + length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Overlap check that ignores occurrences of the candidate surface itself (insertion-window carve-out).</summary>
    private static bool OverlapsForeignInterval(
        List<(int Start, int Length, string Surface)> intervals, int start, int length, string surface)
    {
        foreach (var (intervalStart, intervalLength, intervalSurface) in intervals)
        {
            if (intervalSurface == surface)
            {
                continue;
            }

            if (start < intervalStart + intervalLength && intervalStart < start + length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when deleting exactly one char of <paramref name="longer"/> yields <paramref name="shorter"/>.</summary>
    private static bool IsOneDeletion(string longer, string shorter)
    {
        if (longer.Length != shorter.Length + 1)
        {
            return false;
        }

        var skipped = false;
        var longerIndex = 0;
        for (var shorterIndex = 0; shorterIndex < shorter.Length; shorterIndex++, longerIndex++)
        {
            if (shorter[shorterIndex] == longer[longerIndex])
            {
                continue;
            }

            if (skipped)
            {
                return false;
            }

            skipped = true;
            longerIndex++; // step over the deleted char in longer
            if (shorter[shorterIndex] != longer[longerIndex])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="window"/> is <paramref name="surface"/> with one
    /// DOUBLED char inserted (the char sits next to its twin: rockfalll,
    /// rrockfall). Free-standing insertions (erockfall) are window mis-cuts,
    /// not typos, and are rejected.
    /// </summary>
    private static bool IsDoubledCharInsertion(string window, string surface)
    {
        if (window.Length != surface.Length + 1)
        {
            return false;
        }

        for (var doubled = 0; doubled < window.Length; doubled++)
        {
            var doubledTwice = doubled > 0 && window[doubled] == window[doubled - 1]
                || doubled < window.Length - 1 && window[doubled] == window[doubled + 1];
            if (!doubledTwice)
            {
                continue;
            }

            var rest = string.Concat(window.AsSpan(0, doubled), window.AsSpan(doubled + 1));
            if (rest == surface)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="window"/> or any of its substrings is
    /// already an exact tower surface — the player named a real tower in
    /// this span, so a near-miss rewrite here could only corrupt text.
    /// </summary>
    private bool ContainsExactSurface(string window)
    {
        for (var start = 0; start < window.Length; start++)
        {
            for (var length = window.Length - start; length >= 1; length--)
            {
                if (_surfaceToId.ContainsKey(window.Substring(start, length)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when two equal-length strings differ in at most one position.</summary>
    public static bool WithinEditDistance1(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            return false;
        }

        var differences = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && ++differences > 1)
            {
                return false;
            }
        }

        return true;
    }

    private static void CollectSurface(JsonElement item, string property, List<string> surfaces)
    {
        if (item.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            AddNormalized(element.GetString(), surfaces);
        }
    }

    private static void AddNormalized(string? surface, List<string> surfaces)
    {
        if (string.IsNullOrWhiteSpace(surface))
        {
            return;
        }

        var normalized = TextNormalizer.Normalize(surface);
        if (normalized.Length > 0)
        {
            surfaces.Add(normalized);
        }
    }
}
