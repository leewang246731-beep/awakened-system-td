// T2: pure contract — K-2 content registry, T-020, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Content;

/// <summary>
/// Base contract of every registry entry (K-2). Content ships as JSON
/// ("files are content"), loads through the C-02 pipeline with strict schema
/// validation (units and guards enforced), and registers under its
/// namespaced id with optional aliases.
/// </summary>
public interface IContentItem
{
    /// <summary>
    /// Registry id. Namespaced form <c>modid:contentid</c> for modded
    /// content; first-party content may use the bare <c>contentid</c>.
    /// </summary>
    string Id { get; }

    /// <summary>Content kind this item registers under.</summary>
    ContentType ContentType { get; }

    /// <summary>
    /// Alternative names for the commander alias dictionary (design §17,
    /// same source as the L1 grammar): matched by
    /// <see cref="IContentRegistry.GetByName"/> so military orders can
    /// address content by colloquial names. Mod-added towers extend their
    /// own aliases through the same mechanism.
    /// </summary>
    IReadOnlyList<string> Aliases { get; }
}
