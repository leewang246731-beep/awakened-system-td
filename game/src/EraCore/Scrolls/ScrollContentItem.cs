using EraCore.Contracts.Content;

namespace EraCore.Scrolls;

/// <summary>
/// K-2 registry entry of one guardian scroll (T-071): wraps a
/// <see cref="ScrollDefinition"/> as an <see cref="IContentItem"/> of kind
/// <see cref="ContentType.Scroll"/> so first-party and modded scrolls load
/// through the SAME content pipeline as towers/enemies (CS-2.5). The
/// definition itself is immutable data the <see cref="ScrollEngine"/>
/// consumes; this wrapper only adds the registry identity (id + aliases).
/// </summary>
/// <param name="Definition">The parsed scroll definition (loader-validated).</param>
public sealed class ScrollContentItem(ScrollDefinition definition) : IContentItem
{
    /// <summary>The wrapped scroll data.</summary>
    public ScrollDefinition Definition { get; } = definition ?? throw new ArgumentNullException(nameof(definition));

    /// <inheritdoc />
    public string Id => Definition.Id;

    /// <inheritdoc />
    public ContentType ContentType => ContentType.Scroll;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases => Definition.Aliases;
}
