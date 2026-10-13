// T2: pure contract — K-6 sim-side draft (T-023 remainder).

namespace EraCore.Contracts.Sim;

/// <summary>
/// Immutable, read-only render view of one simulation tick (K-6 draft,
/// C-12 producer). EraCore publishes a double-buffered snapshot every tick;
/// the presentation layer (Godot/MultiMesh, C-21) only ever READS this view,
/// and every reverse mutation enters the simulation exclusively through
/// <see cref="SimCommand"/> (03-decomposition §1.2 snapshot principle).
/// Field semantics derive from design v3.2 §6.
/// </summary>
public interface SimSnapshot
{
    /// <summary>Tick this snapshot was produced at (fixed 30 Hz tick domain, K-1 IClock).</summary>
    long Tick { get; }

    /// <summary>Units (enemies/elites/bosses/heroes) present this tick; order unspecified.</summary>
    IReadOnlyList<IUnit> Units { get; }

    /// <summary>Projectiles in flight this tick.</summary>
    IReadOnlyList<IProjectile> Projectiles { get; }

    /// <summary>Registered buildings (towers/production/storage/walls) with grid occupancy.</summary>
    IReadOnlyList<IBuilding> Buildings { get; }

    /// <summary>
    /// Deterministic state hash of the whole simulation at this tick.
    /// Replay invariant (03-decomposition §1.2): same seed + same command
    /// stream ⇒ same hash at every tick.
    /// </summary>
    long StateHash { get; }
}
