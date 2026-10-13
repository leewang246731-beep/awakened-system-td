using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Combat;

/// <summary>
/// One firing of a weapon, as a structured event (C-09 E1, T-038; F-013):
/// simulation-side record only — UI/音效 adapters translate these later,
/// nothing presentation-shaped lives here (K-8 journal carries them as JSON).
/// </summary>
/// <param name="Tick">Simulation tick of the firing (30 Hz domain, K-1 IClock).</param>
/// <param name="TowerId">Stable id of the firing building.</param>
/// <param name="TowerContentId">Content id of the tower definition (K-2).</param>
/// <param name="TowerClass">Class of the firing tower (SP-1a).</param>
/// <param name="TargetId">Stable id of the targeted unit.</param>
/// <param name="DamagePerShot">Undecayed single-shot damage d carried by the shot.</param>
/// <param name="AmmoShotsRemaining">Shot reserve after this firing (1 ammo item = 10 shots, v3.2 §5).</param>
/// <param name="CooldownTicksSet">Reload cycle armed by this firing, in ticks.</param>
public sealed record WeaponFireEvent(
    long Tick,
    EntityId TowerId,
    string TowerContentId,
    TowerClass TowerClass,
    EntityId TargetId,
    double DamagePerShot,
    int AmmoShotsRemaining,
    int CooldownTicksSet);

/// <summary>Settled hit of one firing against one unit (damage resolution output).</summary>
/// <param name="TargetId">Stable id of the struck unit.</param>
/// <param name="Decay">Decay factor applied (1.0 direct; splash uses <see cref="Damage.SplashDecay"/>).</param>
/// <param name="DamageDealt">Final damage after decay, armor and floor (v3.2 §6).</param>
public sealed record WeaponHit(EntityId TargetId, double Decay, double DamageDealt);

/// <summary>
/// Tick-driven weapon loop of one placed tower (C-09 E1 thin version, T-038;
/// F-013/F-026). Owns exactly three concerns on the 30 Hz tick domain:
/// reload counting (<c>reload_ticks</c> from the registry), shot accounting
/// (<c>ammo_per_shot</c>; one ammo item = 10 shots, v3.2 §5), and structured
/// fire events. Hit settlement delegates to <see cref="Damage"/> — this class
/// never re-implements the formula.
/// <para>
/// Burst price (F-028): a tower declaring <c>burst_windup_ticks</c> starts
/// from cold — its first shot waits out the wind-up (anchor: 落石台 45 ticks,
/// SP-1b L37); every following cycle is a plain reload. Sustained DPS
/// therefore matches the registry value <c>dps = damage_per_shot × 30 /
/// reload_ticks</c> in the steady state, wind-up or not.
/// </para>
/// <para>
/// Control/aura towers (spike trap, campfire) have no weapon cycle — their
/// <c>reload_ticks</c> is null and the constructor rejects them; their
/// effects land with the unit status system (C-10).
/// </para>
/// </summary>
public sealed class WeaponFire
{
    private readonly TowerSpec _spec;

    /// <summary>
    /// Arms the weapon loop. <paramref name="initialAmmoShots"/> counts
    /// individual shots, not ammo items (a full wood-arrow load is 10 — one
    /// ammo item).
    /// </summary>
    /// <exception cref="ArgumentNullException">towerId or spec is null.</exception>
    /// <exception cref="ArgumentException">
    /// spec has no reload cycle (null/≤0 <c>reload_ticks</c> — control/aura
    /// tower), or negative ammo shots.
    /// </exception>
    public WeaponFire(EntityId towerId, TowerSpec spec, int initialAmmoShots)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.ReloadTicks is null || spec.ReloadTicks <= 0)
        {
            throw new ArgumentException(
                $"{spec.Id} has no weapon cycle (reload_ticks={spec.ReloadTicks}); control/aura towers fire no shots.",
                nameof(spec));
        }

        if (initialAmmoShots < 0)
        {
            throw new ArgumentException(
                $"Initial ammo shots must be non-negative (got {initialAmmoShots}).",
                nameof(initialAmmoShots));
        }

        TowerId = towerId;
        _spec = spec;
        AmmoShotsRemaining = initialAmmoShots;
        CooldownTicksRemaining = Math.Max(0, spec.Traits?.BurstWindupTicks ?? 0);
    }

    /// <summary>Stable id of the owning building.</summary>
    public EntityId TowerId { get; }

    /// <summary>Content id of the tower definition this loop fires for.</summary>
    public string TowerContentId => _spec.Id;

    /// <summary>Reload cycle length in ticks (registry value, read-only here).</summary>
    public int ReloadTicks => _spec.ReloadTicks!.Value;

    /// <summary>Shot reserve left; individual shots, 1 ammo item = 10 shots.</summary>
    public int AmmoShotsRemaining { get; private set; }

    /// <summary>Ticks left before the next firing is allowed; 0 = ready.</summary>
    public int CooldownTicksRemaining { get; private set; }

    /// <summary>Total shots fired since construction.</summary>
    public int FiredCount { get; private set; }

    /// <summary>Whether the loop could fire right now: cooled down AND shots available.</summary>
    public bool ReadyToFire =>
        CooldownTicksRemaining == 0 && (_spec.AmmoPerShot <= 0 || AmmoShotsRemaining >= _spec.AmmoPerShot);

    /// <summary>
    /// Advances one simulation tick: a running cooldown counts down by one.
    /// Call exactly once per 30 Hz tick (K-1 IClock domain).
    /// </summary>
    public void Tick()
    {
        if (CooldownTicksRemaining > 0)
        {
            CooldownTicksRemaining--;
        }
    }

    /// <summary>
    /// Fires at <paramref name="targetId"/> when ready: consumes
    /// <c>ammo_per_shot</c> shots, arms the reload cooldown and returns the
    /// structured fire event. Returns null — touching nothing — while cooling
    /// down or out of shots.
    /// </summary>
    /// <param name="targetId">Target of the shot.</param>
    /// <param name="tick">Current simulation tick (event timestamp).</param>
    public WeaponFireEvent? TryFire(EntityId targetId, long tick)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        if (CooldownTicksRemaining > 0)
        {
            return null;
        }

        if (_spec.AmmoPerShot > 0)
        {
            if (AmmoShotsRemaining < _spec.AmmoPerShot)
            {
                return null;
            }

            AmmoShotsRemaining -= _spec.AmmoPerShot;
        }

        CooldownTicksRemaining = ReloadTicks;
        FiredCount++;
        return new WeaponFireEvent(
            tick, TowerId, _spec.Id, _spec.Class, targetId, _spec.DamagePerShot,
            AmmoShotsRemaining, ReloadTicks);
    }

    /// <summary>
    /// Tops up the shot reserve (logistics hand-off; caller converts ammo
    /// items to shots — ×10 per item, v3.2 §5).
    /// </summary>
    /// <exception cref="ArgumentException">shots negative.</exception>
    public void ReloadShots(int shots)
    {
        if (shots < 0)
        {
            throw new ArgumentException($"Reload shots must be non-negative (got {shots}).", nameof(shots));
        }

        AmmoShotsRemaining += shots;
    }

    /// <summary>
    /// Settles the hit of a fired shot against <paramref name="target"/>:
    /// reads the target's armor through the K-6 <see cref="IUnit"/> seam and
    /// applies the damage engine (decay first, armor after, 10% floor;
    /// <paramref name="armorPenRatio"/> routes through the pierce branch).
    /// Splash towers compute their decay with
    /// <see cref="Damage.SplashDecay"/> first and pass it in. The caller owns
    /// HP mutation — this returns the settled numbers only.
    /// </summary>
    /// <exception cref="ArgumentNullException">target is null.</exception>
    public WeaponHit ResolveHit(IUnit target, double decay, double armorPenRatio = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        double dealt = Damage.Pierce(_spec.DamagePerShot, decay, target.Armor, armorPenRatio);
        return new WeaponHit(target.Id, decay, dealt);
    }
}
