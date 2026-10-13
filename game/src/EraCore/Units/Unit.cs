using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Units;

/// <summary>
/// One simulated enemy unit (C-10 E1 thin, T-039) implementing the K-6
/// <see cref="IUnit"/> draft seam so C-09 towers (T-038) can target units
/// without owning their internals (03-decomposition §3.1). State beyond the
/// contract: spawn/death ticks, arrival flag, trample tuning and the death
/// notification.
/// <para>
/// Damage settlement delegates to the single authoritative integer entry
/// <see cref="Damage.DirectInt"/> (damage-formula unification, batch2 review
/// F1): effective = max(d − armor, ceil(10%·d)) — armor can never fully
/// negate a hit. The local formula copy that shipped with T-039 was removed;
/// Units → Combat stays one-way (03-decomposition §3.1) and the divergence
/// grid in DamageTests pins DirectInt against the legacy ceil semantics.
/// </para>
/// <para>
/// Death raises <see cref="Died"/> exactly once, inline and synchronously
/// (CS-1.5 semantics); wiring it onto the (tick, seq) journaling event bus
/// lands with C-12 orchestration (T-040).
/// </para>
/// </summary>
public sealed class Unit : IUnit
{
    /// <summary>
    /// Creates a live unit at full hp. Structural validation only — balance
    /// numbers arrive from the enemy spec and the wave parameters (CS-2.3).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Blank contentId, non-positive maxHp, negative armor or negative speed.
    /// </exception>
    public Unit(
        EntityId id,
        string contentId,
        float x,
        float y,
        int maxHp,
        int armor,
        float speedPxPerTick,
        bool isBoss = false,
        bool isElite = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(contentId))
        {
            throw new ArgumentException("ContentId must not be blank.", nameof(contentId));
        }

        if (maxHp <= 0)
        {
            throw new ArgumentException($"MaxHp must be positive (got {maxHp}).", nameof(maxHp));
        }

        if (armor < 0)
        {
            throw new ArgumentException($"Armor must be non-negative (got {armor}).", nameof(armor));
        }

        if (speedPxPerTick < 0)
        {
            throw new ArgumentException($"Speed must be non-negative (got {speedPxPerTick}).", nameof(speedPxPerTick));
        }

        Id = id;
        ContentId = contentId;
        X = x;
        Y = y;
        MaxHp = maxHp;
        Hp = maxHp;
        Armor = armor;
        SpeedPxPerTick = speedPxPerTick;
        IsBoss = isBoss;
        IsElite = isElite;
        Alive = true;
        SpawnTick = -1;
        DeathTick = -1;
    }

    /// <summary>
    /// Raised exactly once when hp reaches zero, inline in the damaging
    /// call. C-12 (T-040) re-publishes this onto the journaling bus with
    /// (tick, seq) ordering.
    /// </summary>
    public event Action<Unit>? Died;

    /// <inheritdoc />
    public EntityId Id { get; }

    /// <inheritdoc />
    public string ContentId { get; }

    /// <inheritdoc />
    public float X { get; private set; }

    /// <inheritdoc />
    public float Y { get; private set; }

    /// <inheritdoc />
    public int Hp { get; private set; }

    /// <inheritdoc />
    public int MaxHp { get; }

    /// <inheritdoc />
    public int Armor { get; }

    /// <inheritdoc />
    public int Shield => 0; // E1: shields start with E3 (v3.2 §6).

    /// <inheritdoc />
    public float SpeedPxPerTick { get; }

    /// <summary>Whether this unit is a boss (boss-state multipliers applied at spawn).</summary>
    public bool IsBoss { get; }

    /// <summary>Whether this unit spawned from the independent elite line (F-031).</summary>
    public bool IsElite { get; }

    /// <summary>Whether the unit is still on the field.</summary>
    public bool Alive { get; private set; }

    /// <summary>
    /// Set by <see cref="UnitMovement"/> when the unit's cell reaches the
    /// flow-field target (base breach); movement stops there.
    /// </summary>
    public bool ArrivedAtTarget { get; private set; }

    /// <summary>Tick the unit materialized on; -1 until spawned.</summary>
    public long SpawnTick { get; private set; }

    /// <summary>Tick the unit died on; -1 while alive.</summary>
    public long DeathTick { get; private set; }

    /// <summary>Trample pulse damage (points); 0 = no trample.</summary>
    public int TrampleDamage { get; private set; }

    /// <summary>Trample aura radius in px; 0 = no trample.</summary>
    public float TrampleRadiusPx { get; private set; }

    /// <summary>Trample pulse period in ticks; 0 = no trample.</summary>
    public int TramplePeriodTicks { get; private set; }

    /// <summary>
    /// E1 minimal direct-damage settlement (T-039 task card), delegated to
    /// the authoritative integer twin <see cref="Damage.DirectInt"/> (batch2
    /// review F1): effective = max(d − armor, ceil(10%·d)) — armor can never
    /// fully negate a hit. Integer math only, so results are replay-stable.
    /// </summary>
    /// <param name="rawDamage">Incoming raw damage, must be positive.</param>
    /// <param name="tick">Current simulation tick (stamps <see cref="DeathTick"/>).</param>
    /// <returns>Effective damage applied; 0 when the unit is already dead.</returns>
    /// <exception cref="ArgumentOutOfRangeException">rawDamage is not positive.</exception>
    public int ApplyDamage(int rawDamage, long tick)
    {
        if (rawDamage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawDamage), rawDamage, "Damage must be positive.");
        }

        if (!Alive)
        {
            return 0;
        }

        int effective = Damage.DirectInt(rawDamage, Armor);
        Hp = Math.Max(0, Hp - effective);
        if (Hp == 0)
        {
            Alive = false;
            DeathTick = tick;
            Died?.Invoke(this);
        }

        return effective;
    }

    /// <summary>Stamps the spawn tick (spawn seam, called by <see cref="WaveSpawner"/>).</summary>
    public void MarkSpawned(long tick) => SpawnTick = tick;

    /// <summary>
    /// Sets the trample tuning resolved at spawn time (spawn seam; the
    /// engine never invents these numbers — they arrive from the wave
    /// configuration, CS-2.3).
    /// </summary>
    /// <exception cref="ArgumentException">Any of the triple is not positive.</exception>
    public void SetTrample(int damage, float radiusPx, int periodTicks)
    {
        if (damage <= 0 || radiusPx <= 0 || periodTicks <= 0)
        {
            throw new ArgumentException(
                $"Trample needs positive damage/radius/period (got {damage}/{radiusPx}/{periodTicks}).");
        }

        TrampleDamage = damage;
        TrampleRadiusPx = radiusPx;
        TramplePeriodTicks = periodTicks;
    }

    /// <summary>Moves the unit (movement seam, called by <see cref="UnitMovement"/>).</summary>
    public void MoveTo(float x, float y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Marks base arrival (movement seam, called by <see cref="UnitMovement"/>).</summary>
    public void MarkArrived() => ArrivedAtTarget = true;
}
