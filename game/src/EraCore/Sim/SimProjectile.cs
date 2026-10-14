using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Sim;

/// <summary>
/// One in-flight projectile twin of the ballistic shadow layer (C-12 T-067,
/// F-155; K-6 <see cref="IProjectile"/> 载体 — the G.1 条 2 Projectiles
/// group's element type). The E1/E2 combat settlement is hitscan —
/// <c>WeaponFire.TryFire</c> settles the damage inside the firing tick — so
/// this entity carries ZERO combat authority: it is the deterministic flight
/// twin a presentation layer (C-21/K-6) rides on, spawned at the tower's
/// real fire event and flying the straight line from the tower centre toward
/// the target's position AT THE FIRING TICK (damage is never re-settled —
/// the twin advances on a frozen heading, exactly the flight-cost shape
/// spike① measured at the 1000-live-projectile load).
/// <para>
/// Determinism: heading normalization is pure IEEE 754 float math, lifetime
/// is a whole-tick counter and membership lives in the
/// <see cref="EntityGroups.EntityGroup{T}"/> the state hash folds — same
/// seed + commands ⇒ same spawn stream, same trajectories, same hash.
/// </para>
/// </summary>
public sealed class SimProjectile : IProjectile
{
    private readonly float _speedPxPerTick;
    private readonly float _headingX;
    private readonly float _headingY;

    /// <summary>
    /// Arms one projectile at the firing building's cell centre, aimed at the
    /// target point captured at the firing tick.
    /// </summary>
    /// <param name="id">Stable identity within the current level run.</param>
    /// <param name="sourceBuildingId">The tower that fired.</param>
    /// <param name="originX">Muzzle X in px (tower cell centre).</param>
    /// <param name="originY">Muzzle Y in px.</param>
    /// <param name="targetX">Target point X at the firing tick.</param>
    /// <param name="targetY">Target point Y at the firing tick.</param>
    /// <param name="damage">Raw per-shot damage d of the settled shot (v3.2 §6 symbol binding).</param>
    /// <param name="speedPxPerTick">Flight speed in px per tick.</param>
    /// <param name="lifetimeTicks">Flight lifetime in ticks; the twin despawns after it.</param>
    /// <exception cref="ArgumentNullException">id or sourceBuildingId is null.</exception>
    /// <exception cref="ArgumentException">speed or lifetime not positive, damage negative.</exception>
    public SimProjectile(
        EntityId id,
        EntityId sourceBuildingId,
        float originX,
        float originY,
        float targetX,
        float targetY,
        int damage,
        float speedPxPerTick,
        int lifetimeTicks)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceBuildingId);
        if (speedPxPerTick <= 0)
        {
            throw new ArgumentException($"Projectile speed must be positive (got {speedPxPerTick}).", nameof(speedPxPerTick));
        }

        if (lifetimeTicks <= 0)
        {
            throw new ArgumentException($"Projectile lifetime must be positive (got {lifetimeTicks}).", nameof(lifetimeTicks));
        }

        if (damage < 0)
        {
            throw new ArgumentException($"Projectile damage must be non-negative (got {damage}).", nameof(damage));
        }

        Id = id;
        SourceBuildingId = sourceBuildingId;
        X = originX;
        Y = originY;
        Damage = damage;
        _speedPxPerTick = speedPxPerTick;
        LifetimeTicks = lifetimeTicks;

        float dx = targetX - originX;
        float dy = targetY - originY;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length > 1e-6)
        {
            _headingX = (float)(dx / length);
            _headingY = (float)(dy / length);
        }
        else
        {
            // Muzzle on the target point: fly "north" — a degenerate but
            // fixed direction keeps the twin deterministic.
            _headingX = 0f;
            _headingY = -1f;
        }
    }

    /// <inheritdoc />
    public EntityId Id { get; }

    /// <inheritdoc />
    public EntityId SourceBuildingId { get; }

    /// <inheritdoc />
    public float X { get; private set; }

    /// <inheritdoc />
    public float Y { get; private set; }

    /// <inheritdoc />
    public EntityId? TargetUnitId => null; // hitscan twin: the shot already settled; the twin aims at a point.

    /// <inheritdoc />
    public int Damage { get; }

    /// <inheritdoc />
    public long AgeTicks { get; private set; }

    /// <summary>Flight lifetime in ticks (despawn boundary).</summary>
    public int LifetimeTicks { get; }

    /// <summary>Whether the twin has flown out its lifetime and leaves the group.</summary>
    public bool IsExpired => AgeTicks >= LifetimeTicks;

    /// <summary>Advances exactly one tick along the frozen heading.</summary>
    public void Advance()
    {
        X += _headingX * _speedPxPerTick;
        Y += _headingY * _speedPxPerTick;
        AgeTicks++;
    }
}
