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

        var dictionary = new AliasDictionary();
        foreach (var (id, surfaces) in rows)
        {
            foreach (var surface in surfaces)
            {
                if (dictionary._surfaceToId.TryGetValue(surface, out var owner) && owner != id)
                {
                    throw new InvalidOperationException(
                        $"Tower alias conflict: '{surface}' claimed by both '{owner}' and '{id}' (K-2: conflicts surface).");
                }

                dictionary._surfaceToId[surface] = id;
            }
        }

        dictionary._surfaceOrder.AddRange(dictionary._surfaceToId.Keys);
        dictionary._surfaceOrder.Sort((a, b) => b.Length != a.Length ? b.Length - a.Length : string.CompareOrdinal(a, b));
        return dictionary;
    }

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
    /// Homophone tolerance pass (v3.2 §11.5「同音容错」): scans the normalized
    /// text for a window that sits within one substitution of a known tower
    /// surface (equal length — the classic same-length homophone/typo case)
    /// and contains no exact tower surface of its own, then rewrites that
    /// window to the canonical surface. The substring guard keeps valid
    /// adjacency intact: 「建十座箭塔」 contains 座+箭塔 (measure word +
    /// exact tower) and must NOT be "repaired" into 座箭塔→木箭塔, which
    /// would eat the measure word. First candidate in surface-then-position
    /// order wins (deterministic). Returns the repaired text, or null when
    /// no repair applies (including when the text already matches exactly).
    /// </summary>
    public string? FuzzyRepair(string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);

        foreach (var surface in _surfaceOrder)
        {
            if (normalizedText.Length < surface.Length)
            {
                continue;
            }

            for (var start = 0; start + surface.Length <= normalizedText.Length; start++)
            {
                var window = normalizedText.Substring(start, surface.Length);
                if (ContainsExactSurface(window))
                {
                    continue;
                }

                if (!WithinEditDistance1(window, surface))
                {
                    continue;
                }

                var chars = normalizedText.ToCharArray();
                surface.CopyTo(chars.AsSpan(start));
                return new string(chars);
            }
        }

        return null;
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
