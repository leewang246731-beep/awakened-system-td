namespace EraCore.Agent.Grammar;

/// <summary>
/// Multimodal pointing context for the L1 grammar (C-15, T-069; v3.2 §11.2/
/// §11.5 多模态指认补槽): the UI-side facts a pointed order resolves against.
/// Pure data, no I/O — the presentation layer fills it later from the mouse
/// (last click cell, camera center, current selection); until then callers
/// pass <see langword="null"/> and pointed orders route to clarification
/// (SP-2 配套口径: missing slots clarify, never guess).
/// </summary>
/// <param name="SelectedId">
/// Designation of the most recently selected entity (content-registry id
/// such as <c>era1:tower-campfire</c>), or null when nothing is selected.
/// Resolves entity demonstratives —「这个塔/这个」/「this tower」→
/// <c>sel:{id}</c>.
/// </param>
/// <param name="SelectedCellX">Column of the last clicked/selected cell, or null.</param>
/// <param name="SelectedCellY">Row of the last clicked/selected cell, or null.</param>
/// <param name="CameraCellX">Column of the camera-center cell, or null.</param>
/// <param name="CameraCellY">Row of the camera-center cell, or null.</param>
/// <remarks>
/// Place demonstratives —「这里/这个位置」/「here」— resolve to the last
/// selected cell first (最近选中), falling back to the camera center
/// (镜头中心格); either yields <c>cell:{x},{y}</c>. An explicit
/// <c>@x,y</c> order suffix overrides both without needing any context.
/// </remarks>
public sealed record PointingContext(
    string? SelectedId,
    int? SelectedCellX,
    int? SelectedCellY,
    int? CameraCellX,
    int? CameraCellY)
{
    /// <summary>True when an entity demonstrative can resolve (a selection exists).</summary>
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedId);

    /// <summary>True when a place demonstrative can resolve (selected or camera cell exists).</summary>
    public bool HasCell =>
        (SelectedCellX is not null && SelectedCellY is not null)
        || (CameraCellX is not null && CameraCellY is not null);

    /// <summary>
    /// Resolves an entity demonstrative → <c>sel:{SelectedId}</c>; null when
    /// nothing is selected (caller routes to clarification).
    /// </summary>
    public string? ResolveSelected()
        => HasSelection ? "sel:" + SelectedId! : null;

    /// <summary>
    /// Resolves a place demonstrative → <c>cell:{x},{y}</c> (last selected
    /// cell first, camera center as fallback); null when neither exists.
    /// </summary>
    public string? ResolveCell()
    {
        if (SelectedCellX is { } x && SelectedCellY is { } y)
        {
            return $"cell:{x},{y}";
        }

        if (CameraCellX is { } cx && CameraCellY is { } cy)
        {
            return $"cell:{cx},{cy}";
        }

        return null;
    }
}
