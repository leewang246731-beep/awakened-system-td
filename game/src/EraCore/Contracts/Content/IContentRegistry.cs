// T2: pure contract — K-2 content registry, T-020, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Content;

/// <summary>
/// Content registry (K-2): the single lookup authority for all game content
/// (towers, enemies, eras, scrolls, …). Registration order is deterministic
/// — loaders feed items sorted by id so mod overlay and conflict reporting
/// are reproducible (v2.1 §16 item 3). First-party and modded content use
/// the same pipeline.
/// </summary>
public interface IContentRegistry
{
    /// <summary>
    /// Registers <paramref name="item"/> under its kind and namespaced id.
    /// Throws <see cref="InvalidOperationException"/> on a duplicate id
    /// (same kind + same namespace): conflicts must surface, never silently
    /// overwrite. The mod overlay ("later load wins with a conflict report")
    /// is resolved by the C-02 loader before it reaches this call.
    /// </summary>
    void Register(IContentItem item);

    /// <summary>
    /// Exact-id lookup (namespaced or bare). Throws
    /// <see cref="KeyNotFoundException"/> when unknown.
    /// </summary>
    IContentItem Get(ContentType type, string id);

    /// <summary>
    /// Name lookup for the commander grammar and UI. Resolution order:
    /// full namespaced id (<c>modid:contentid</c>), then bare id within the
    /// first-party namespace, then registered aliases. Throws
    /// <see cref="KeyNotFoundException"/> when nothing matches.
    /// </summary>
    IContentItem GetByName(ContentType type, string name);

    /// <summary>Null-returning variant of <see cref="GetByName"/>.</summary>
    IContentItem? FindByName(ContentType type, string name);

    /// <summary>All registered items of one kind, ordered by id (deterministic).</summary>
    IReadOnlyList<IContentItem> List(ContentType type);
}
