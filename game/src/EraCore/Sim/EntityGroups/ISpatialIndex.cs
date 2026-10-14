namespace EraCore.Sim.EntityGroups;

/// <summary>World position accessor pair for a spatially indexable entity.</summary>
/// <param name="X">World position X in px.</param>
/// <param name="Y">World position Y in px.</param>
public delegate void SpatialPositionAccessor<in T>(T entity, out float x, out float y);

/// <summary>
/// Quadtree spatial index seam over one <see cref="EntityGroup{T}"/> (C-12
/// T-067, F-155; G.1 条 2 四叉树). This is the OPTIONAL attachment point of
/// the grouping system: per the 03-decomposition ruling the milestone ships
/// the interface only — spike① measured the G8 stress composite (300 enemies
/// + 100 towers, maze map) at a 1.336 ms mean logic frame with plain
/// dense-array scans (docs/engineering/spike1-report.md), so no spatial query
/// requirement has been demonstrated. The quadtree implementation activates
/// when a spatial query consumer appears (tower-targeting cost, AOE
/// candidate queries at higher unit counts): an implementation rebuilds from
/// the group's dense array per tick (or incrementally on Add/RemoveAtSwap)
/// and answers the queries below deterministically — same build order, same
/// answers, so the replay hash is unaffected either way.
/// </summary>
/// <typeparam name="T">Entity type the index serves.</typeparam>
public interface ISpatialIndex<T>
    where T : class
{
    /// <summary>
    /// Rebuilds the index from the group's current live members. Must be
    /// callable every tick before queries (whole-rebuild quadtree) or once
    /// after membership changes (incremental variant).
    /// </summary>
    /// <param name="entities">Dense entity array view (registration order).</param>
    /// <param name="position">Position accessor of one entity.</param>
    void Rebuild(IReadOnlyList<T> entities, SpatialPositionAccessor<T> position);

    /// <summary>
    /// Candidates within <paramref name="radiusPx"/> of the centre, in group
    /// registration order (deterministic answer set).
    /// </summary>
    IReadOnlyList<T> QueryRadius(float centreX, float centreY, float radiusPx);

    /// <summary>
    /// The strictly nearest candidate satisfying <paramref name="filter"/>
    /// inside <paramref name="maxRadiusPx"/>; ties keep the earlier
    /// registration (the orchestrator's targeting contract).
    /// </summary>
    T? FindNearest(float centreX, float centreY, float maxRadiusPx, Predicate<T> filter);
}
