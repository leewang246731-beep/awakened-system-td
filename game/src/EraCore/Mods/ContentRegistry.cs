using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EraCore.Contracts.Content;

namespace EraCore.Mods;

/// <summary>
/// One registry entry produced by the C-02 content pipeline (T-103, F-156):
/// first-party files and mod packages compose into these items, and the
/// production <see cref="ContentRegistry"/> stores them. The item carries
/// the raw JSON of its source definition so downstream consumers keep their
/// own deserializers (<c>TowerSpec.LoadCollection</c> shape, scroll engine,
/// …) — the registry is the lookup authority, not a typed DTO bank
/// (RL7: numbers live in content files, the engine reads tables).
/// </summary>
/// <param name="Id">Namespaced registry id (<c>modid:contentid</c> for modded content).</param>
/// <param name="ContentType">Content kind (K-2 closed taxonomy).</param>
/// <param name="Aliases">Alias dictionary entries (commander grammar source, F-157).</param>
/// <param name="RawJson">Raw JSON text of the item's source definition (pass-through payload).</param>
/// <param name="SourceFile">File the item was loaded from (relative name for reports).</param>
/// <param name="SourceLine">1-based line of the item's object token in <paramref name="SourceFile"/>.</param>
/// <param name="SourceModId">Mod id when modded; null for first-party content.</param>
/// <param name="Patched">True when a mod patch replaced a first-party definition (overlay winner).</param>
public sealed record JsonContentItem(
    string Id,
    ContentType ContentType,
    IReadOnlyList<string> Aliases,
    string RawJson,
    string SourceFile,
    int SourceLine,
    string? SourceModId,
    bool Patched) : IContentItem;

/// <summary>
/// Production K-2 registry (T-103, F-156): deterministic sorted listing,
/// exact-id lookup, and the documented name-resolution order of the frozen
/// <see cref="IContentRegistry"/> contract — full namespaced id, then bare
/// id, then registered aliases. Duplicate exact ids throw: the C-02 mod
/// loader resolves every overlay BEFORE anything reaches
/// <see cref="Register"/> (contract wording), so a throw here is a pipeline
/// bug, never a content conflict channel.
/// </summary>
public sealed class ContentRegistry : IContentRegistry
{
    // Concurrent for future Godot-adapter reads off the main thread; the
    // load pipeline itself is single-threaded (CS-1.5 synchronous bus).
    private readonly ConcurrentDictionary<(ContentType Type, string Id), JsonContentItem> _items = new();

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Duplicate id (same kind + namespace).</exception>
    public void Register(IContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is not JsonContentItem jsonItem)
        {
            throw new ArgumentException(
                $"{nameof(ContentRegistry)} stores {nameof(JsonContentItem)}s (pipeline payload); got {item.GetType().Name}.",
                nameof(item));
        }

        if (!_items.TryAdd((item.ContentType, item.Id), jsonItem))
        {
            throw new InvalidOperationException(
                $"Duplicate content id '{item.Id}' under {item.ContentType} (same kind + namespace conflicts must surface).");
        }
    }

    /// <inheritdoc />
    public IContentItem Get(ContentType type, string id) =>
        _items.TryGetValue((type, id), out JsonContentItem? item)
            ? item
            : throw new KeyNotFoundException($"No {type} '{id}'.");

    /// <inheritdoc />
    public IContentItem GetByName(ContentType type, string name) =>
        FindByName(type, name) ?? throw new KeyNotFoundException($"No {type} named '{name}'.");

    /// <inheritdoc />
    public IContentItem? FindByName(ContentType type, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Resolution order (frozen contract): exact namespaced id, then bare
        // id, then aliases. Bare-id matching prefers first-party content —
        // the loader guarantees first-party ids are unique per kind even
        // after namespaced mods pile on.
        IContentItem? bare = null;
        foreach (KeyValuePair<(ContentType Type, string Id), JsonContentItem> entry in _items)
        {
            if (entry.Key.Type != type)
            {
                continue;
            }

            if (entry.Value.Id == name)
            {
                return entry.Value; // exact hit, done
            }

            int colon = entry.Value.Id.IndexOf(':', StringComparison.Ordinal);
            bool isBareMatch = colon >= 0
                && string.CompareOrdinal(entry.Value.Id, colon + 1, name, 0, name.Length) == 0
                && entry.Value.Id.Length == colon + 1 + name.Length;
            if (isBareMatch && (bare is null || entry.Value.SourceModId is null))
            {
                bare = entry.Value; // first-party bare hit wins over modded
            }
        }

        if (bare is not null)
        {
            return bare;
        }

        foreach (KeyValuePair<(ContentType Type, string Id), JsonContentItem> entry in _items)
        {
            if (entry.Key.Type == type && entry.Value.Aliases.Contains(name))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IContentItem> List(ContentType type) =>
        _items.Values
            .Where(i => i.ContentType == type)
            .OrderBy(i => i.Id, StringComparer.Ordinal)
            .Select(static i => (IContentItem)i)
            .ToList();

    /// <summary>Number of registered items across all kinds.</summary>
    public int Count => _items.Count;
}

/// <summary>Raw JSON re-parsing helpers for <see cref="JsonContentItem"/> pass-through payloads.</summary>
public static class JsonContentItemJson
{
    /// <summary>
    /// Wraps one item's raw JSON into a collection document
    /// (<c>{"content_type":…,"items":[item]}</c>) so collection-shaped
    /// deserializers (<c>TowerSpec.LoadCollection</c>) can consume a single
    /// registry entry — the "mod content flows into the same engine reader"
    /// seam of the overlay pipeline.
    /// </summary>
    public static string AsCollectionDocument(JsonContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using JsonDocument document = JsonDocument.Parse(item.RawJson);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content_type", item.ContentType.ToString());
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            document.RootElement.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
