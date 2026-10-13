// T2: pure contract — K-6 sim-side draft (T-023 remainder).

using EraCore.Contracts.Foundation;

namespace EraCore.Contracts.Sim;

/// <summary>
/// A projectile in flight, fired by a tower (K-6 draft). Carries the raw
/// per-shot damage <c>d</c>; decay and the armor floor are applied on hit
/// by the damage pipeline — max(d×decay − armor, 10%×d×decay), splash edge
/// floor 40% (v3.2 §6).
/// </summary>
public interface IProjectile
{
    /// <summary>Stable identity within the current level run.</summary>
    EntityId Id { get; }

    /// <summary>The tower/building that fired this projectile.</summary>
    EntityId SourceBuildingId { get; }

    /// <summary>Current world position X in pixels.</summary>
    float X { get; }

    /// <summary>Current world position Y in pixels.</summary>
    float Y { get; }

    /// <summary>Target unit, if any (null for ground/AOE shots).</summary>
    EntityId? TargetUnitId { get; }

    /// <summary>Raw per-shot damage d before decay (v3.2 §6 symbol binding).</summary>
    int Damage { get; }

    /// <summary>Ticks this projectile has been alive (fixed 30 Hz domain, K-1 IClock).</summary>
    long AgeTicks { get; }
}
