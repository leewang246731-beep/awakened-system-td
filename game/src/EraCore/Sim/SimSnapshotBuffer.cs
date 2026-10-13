using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Sim;

/// <summary>
/// Immutable render view of one unit (K-6 <see cref="IUnit"/> draft): a
/// value copy taken at frame end, so the presentation layer can never reach
/// the mutable simulation object behind it (03-decomposition §1.2 snapshot
/// principle).
/// </summary>
public sealed record SimUnitView(
    EntityId Id,
    string ContentId,
    float X,
    float Y,
    int Hp,
    int MaxHp,
    int Armor,
    int Shield,
    float SpeedPxPerTick) : IUnit;

/// <summary>
/// Immutable render view of one projectile (K-6 <see cref="IProjectile"/>
/// draft). The E1 thin combat settles hits in the firing tick, so the
/// flight list stays empty until projectile travel lands (C-09 growth).
/// </summary>
public sealed record SimProjectileView(
    EntityId Id,
    EntityId SourceBuildingId,
    float X,
    float Y,
    EntityId? TargetUnitId,
    int Damage,
    long AgeTicks) : IProjectile;

/// <summary>
/// Immutable render view of one building (K-6 <see cref="IBuilding"/> draft).
/// </summary>
public sealed record SimBuildingView(
    EntityId Id,
    string ContentId,
    CellCoord Cell,
    int Hp,
    int MaxHp,
    int Tier) : IBuilding;

/// <summary>
/// One immutable <see cref="SimSnapshot"/> implementation produced by
/// <see cref="Simulation"/> at frame end. The constructor defensive-copies
/// every collection, so the view is deeply read-only even when the caller
/// later mutates the arrays it handed in.
/// </summary>
public sealed record ImmutableSimSnapshot : SimSnapshot
{
    /// <inheritdoc />
    public long Tick { get; }

    /// <inheritdoc />
    public IReadOnlyList<IUnit> Units { get; }

    /// <inheritdoc />
    public IReadOnlyList<IProjectile> Projectiles { get; }

    /// <inheritdoc />
    public IReadOnlyList<IBuilding> Buildings { get; }

    /// <inheritdoc />
    public long StateHash { get; }

    /// <summary>Creates the view, copying every collection.</summary>
    public ImmutableSimSnapshot(
        long tick,
        IReadOnlyList<IUnit> units,
        IReadOnlyList<IProjectile> projectiles,
        IReadOnlyList<IBuilding> buildings,
        long stateHash)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(projectiles);
        ArgumentNullException.ThrowIfNull(buildings);
        Tick = tick;
        Units = units.ToArray();
        Projectiles = projectiles.ToArray();
        Buildings = buildings.ToArray();
        StateHash = stateHash;
    }
}

/// <summary>
/// Double-buffered snapshot producer (C-12, T-040; K-6 draft): the
/// simulation <see cref="Stage"/>s a fully-built immutable snapshot into the
/// back buffer at frame end and then <see cref="SwapBuffers"/>s, so the
/// presentation side always reads either the latest or the previous complete
/// frame — never a half-written one. This is the real producer the C-21 mock
/// (<c>DeterministicMockSnapshotSource</c>, T-042) will be replaced by in
/// the presentation task; only the <c>ISimSnapshotSource</c> port survives.
/// </summary>
public sealed class SimSnapshotBuffer
{
    private SimSnapshot? _latest;
    private SimSnapshot? _previous;
    private SimSnapshot? _staged;

    /// <summary>
    /// Stages a snapshot into the back buffer. The front buffers are
    /// untouched until <see cref="SwapBuffers"/> — two-stage publish keeps
    /// the swap atomic from the reader's point of view.
    /// </summary>
    /// <exception cref="ArgumentNullException">snapshot is null.</exception>
    public void Stage(SimSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _staged = snapshot;
    }

    /// <summary>
    /// Promotes the staged snapshot: latest moves to previous, staged
    /// becomes latest, the back slot is freed. A swap without a prior stage
    /// is a no-op (nothing half-published).
    /// </summary>
    public void SwapBuffers()
    {
        if (_staged is null)
        {
            return;
        }

        _previous = _latest;
        _latest = _staged;
        _staged = null;
    }

    /// <summary>Latest published frame; null before the first swap.</summary>
    public SimSnapshot? Latest => _latest;

    /// <summary>The frame before the latest one; null before the second swap.</summary>
    public SimSnapshot? Previous => _previous;

    /// <summary>Whether a snapshot is staged awaiting a swap.</summary>
    public bool HasStaged => _staged is not null;

    /// <summary>
    /// Drops every buffer (test seam; a reset level reuses the same buffer).
    /// </summary>
    public void Reset()
    {
        _latest = null;
        _previous = null;
        _staged = null;
    }
}
