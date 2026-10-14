using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Units;

/// <summary>
/// One simulated enemy unit (C-10, T-039 + T-066 full) implementing the K-6
/// <see cref="IUnit"/> draft seam so C-09 (towers/weapons) and C-12
/// (orchestration) can target units without owning their internals
/// (03-decomposition §3.1). State beyond the contract: spawn/death ticks,
/// arrival flag, the T-066 additions — the shield framework instance, the
/// speed-effect channels (war-drum aura / hero slow / boss charge burst),
/// the armor-reduction debuff, and the boss skill tuning (trample, war
/// drums, charge).
/// <para>
/// Damage settlement delegates to the single authoritative integer entry
/// <see cref="Damage.DirectInt"/> (damage-formula unification, batch2 review
/// F1): effective = max(d − armor, ceil(10%·d)) — armor can never fully
/// negate a hit. The shield absorbs raw points BEFORE that settlement
/// (v3.2 §6 shield-first ruling, <see cref="ShieldState"/>); a piercing hit
/// (armorPen flag of the energy line, SP-1a L28) additionally interrupts
/// shield regeneration.
/// </para>
/// <para>
/// Death raises <see cref="Died"/> exactly once, inline and synchronously
/// (CS-1.5 semantics); C-12 republishes it onto the journaling bus.
/// </para>
/// </summary>
public sealed class Unit : IUnit, IMovableUnit
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
    public int Shield => _shield?.CurrentPoints ?? 0;

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

    /// <summary>War-drum aura radius in px; 0 = the unit drums no aura (SP-3b L120).</summary>
    public float WarDrumsRadiusPx { get; private set; }

    /// <summary>War-drum aura speed bonus in percent (SP-3b L120: +15).</summary>
    public int WarDrumsSpeedBonusPercent { get; private set; }

    /// <summary>Charge (冲撞) ram damage in points; 0 = no charge (SP-3b L121).</summary>
    public int ChargeDamage { get; private set; }

    /// <summary>Charge ram reach in px.</summary>
    public float ChargeReachPx { get; private set; }

    /// <summary>Charge period in ticks (SP-3b L121: every 20 s = 600 ticks).</summary>
    public int ChargeIntervalTicks { get; private set; }

    /// <summary>Charge burst length in ticks.</summary>
    public int ChargeDurationTicks { get; private set; }

    /// <summary>Charge burst speed bonus in percent.</summary>
    public int ChargeSpeedBonusPercent { get; private set; }

    /// <summary>
    /// Time-boxed charge burst stamp: the speed bonus applies while
    /// <c>tick &lt; ChargeUntilTick</c> (long.MinValue = no burst running).
    /// </summary>
    public long ChargeUntilTick { get; private set; } = long.MinValue;

    /// <summary>Time-boxed hero slow in percent (SP-6 战吼); expires at <see cref="SlowUntilTick"/>.</summary>
    public int SlowPercent { get; private set; }

    /// <summary>Slow expiry stamp (long.MinValue = no slow applied).</summary>
    public long SlowUntilTick { get; private set; } = long.MinValue;

    /// <summary>
    /// War-drum aura bonus for the current tick, recomputed by the
    /// aura pass (<see cref="UnitAuras.ApplyWarDrums"/>); 0 outside any drum.
    /// </summary>
    public int AuraSpeedBonusPercent { get; private set; }

    /// <summary>
    /// Speed bonus folded from all channels by the last
    /// <see cref="ResolveSpeedEffects"/> call (percent; negative = slowed).
    /// </summary>
    public int ResolvedSpeedBonusPercent { get; private set; }

    /// <summary>Time-boxed armor reduction in points (SP-6 破甲突刺 −4 甲 10 s).</summary>
    public int ArmorReduction { get; private set; }

    /// <summary>Armor-reduction expiry stamp (long.MinValue = none applied).</summary>
    public long ArmorReductionUntilTick { get; private set; } = long.MinValue;

    private ShieldState? _shield;

    /// <summary>
    /// Armor the damage formula sees at <paramref name="tick"/>: base armor
    /// minus an active (unexpired) reduction, clamped at ≥0 (v3.2 §6 armor
    /// clamp). Expiry folds here so callers never see a stale debuff.
    /// </summary>
    public int EffectiveArmorAt(long tick)
    {
        int reduction = tick < ArmorReductionUntilTick ? ArmorReduction : 0;
        return Math.Max(0, Armor - reduction);
    }

    /// <summary>
    /// Movement speed the movement layer consumes: base speed scaled by the
    /// resolved bonus percent, with the total slow capped at −50% — the
    /// final factor never drops below 0.5 (SP-1a L29 多源减速总封顶 −50%).
    /// </summary>
    public float EffectiveSpeedPxPerTick
    {
        get
        {
            float factor = 1f + (ResolvedSpeedBonusPercent / 100f);
            return SpeedPxPerTick * Math.Max(0.5f, factor);
        }
    }

    /// <summary>
    /// Damage settlement (T-039 + T-066 shield-first): the shield absorbs
    /// raw points before the armored hp settlement; the hp remainder runs
    /// through <see cref="Damage.DirectInt"/> on <see cref="EffectiveArmorAt"/>
    /// (integer math only, replay-stable). A piercing hit interrupts shield
    /// regeneration for <see cref="ShieldState.PierceInterruptTicks"/>.
    /// </summary>
    /// <param name="rawDamage">Incoming raw damage, must be positive.</param>
    /// <param name="tick">Current simulation tick (stamps <see cref="DeathTick"/>).</param>
    /// <param name="pierce">Whether the shot carries the armorPen flag (energy line, SP-1a L28).</param>
    /// <returns>Total points settled (absorbed by the shield + effective hp damage); 0 when already dead.</returns>
    /// <exception cref="ArgumentOutOfRangeException">rawDamage is not positive.</exception>
    public int ApplyDamage(int rawDamage, long tick, bool pierce = false)
    {
        if (rawDamage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawDamage), rawDamage, "Damage must be positive.");
        }

        if (!Alive)
        {
            return 0;
        }

        int incoming = rawDamage;
        int absorbed = 0;
        if (_shield is not null)
        {
            absorbed = _shield.Absorb(incoming, pierce, tick);
            incoming -= absorbed;
            if (incoming == 0)
            {
                return absorbed;
            }
        }

        int effective = Damage.DirectInt(incoming, EffectiveArmorAt(tick));
        Hp = Math.Max(0, Hp - effective);
        if (Hp == 0)
        {
            Alive = false;
            DeathTick = tick;
            Died?.Invoke(this);
        }

        return absorbed + effective;
    }

    /// <summary>
    /// Installs the shield framework instance (spawn seam). Points come
    /// from the wave shield formula or an individual spec value — never
    /// invented here (RL7). <paramref name="maxPoints"/> 0 removes the
    /// shield (E1/E2 rows carry none).
    /// </summary>
    /// <exception cref="ArgumentException">maxPoints negative.</exception>
    public void SetShield(int maxPoints)
    {
        if (maxPoints < 0)
        {
            throw new ArgumentException($"Shield points must be non-negative (got {maxPoints}).", nameof(maxPoints));
        }

        _shield = maxPoints > 0 ? new ShieldState(maxPoints) : null;
    }

    /// <summary>One shield regeneration step; no-op without a shield (spawn seam, caller per tick).</summary>
    public void TickShield(long tick) => _shield?.Tick(tick);

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

    /// <summary>
    /// Sets the war-drum aura tuning from enemy content data at spawn time
    /// (SP-3b L120: radius 3 cells, +15% allies). Zero radius disables.
    /// </summary>
    /// <exception cref="ArgumentException">Non-positive radius with a bonus, or negative bonus.</exception>
    public void SetWarDrums(float radiusPx, int speedBonusPercent)
    {
        if (radiusPx < 0 || speedBonusPercent < 0 || (radiusPx == 0 && speedBonusPercent != 0))
        {
            throw new ArgumentException(
                $"War drums need a non-negative pair with radius>0 when drumming (got {radiusPx}/{speedBonusPercent}).");
        }

        WarDrumsRadiusPx = radiusPx;
        WarDrumsSpeedBonusPercent = speedBonusPercent;
    }

    /// <summary>
    /// Sets the charge (冲撞) tuning from enemy content data at spawn time
    /// (SP-3b L121: every 20 s; damage/reach/duration/bonus are
    /// [NEW-原型校准] columns of the same row). Zero interval disables.
    /// </summary>
    /// <exception cref="ArgumentException">Non-positive interval with non-zero damage, or negative members.</exception>
    public void SetCharge(int damage, float reachPx, int intervalTicks, int durationTicks, int speedBonusPercent)
    {
        if (intervalTicks < 0 || damage < 0 || reachPx < 0 || durationTicks <= 0 || speedBonusPercent < 0
            || (intervalTicks == 0 && (damage != 0 || reachPx != 0)))
        {
            throw new ArgumentException(
                $"Charge needs positive interval/duration/bonus and non-negative damage/reach (got {damage}/{reachPx}/{intervalTicks}/{durationTicks}/{speedBonusPercent}).");
        }

        ChargeDamage = damage;
        ChargeReachPx = reachPx;
        ChargeIntervalTicks = intervalTicks;
        ChargeDurationTicks = durationTicks;
        ChargeSpeedBonusPercent = speedBonusPercent;
    }

    /// <summary>
    /// Starts one charge burst (engine seam, called by
    /// <see cref="UnitMovement.PulseCharge"/> on the charge grid).
    /// </summary>
    public void BeginChargeBurst(long untilTick, int speedBonusPercent)
    {
        if (untilTick > ChargeUntilTick)
        {
            ChargeUntilTick = untilTick;
            ChargeSpeedBonusPercent = speedBonusPercent;
        }
    }

    /// <summary>
    /// Applies a time-boxed slow (SP-6 战吼). Re-application keeps the
    /// stronger active slow and the later expiry — deterministic fold.
    /// </summary>
    /// <exception cref="ArgumentException">slowPercent outside (0,100].</exception>
    public void ApplySlow(int slowPercent, long untilTick)
    {
        if (slowPercent <= 0 || slowPercent > MaxSlowPercent)
        {
            throw new ArgumentException($"Slow percent must lie in (0,{MaxSlowPercent}] (got {slowPercent}).", nameof(slowPercent));
        }

        bool active = SlowUntilTick > long.MinValue;
        if (!active || slowPercent > SlowPercent)
        {
            SlowPercent = slowPercent;
        }

        if (untilTick > SlowUntilTick)
        {
            SlowUntilTick = untilTick;
        }
    }

    /// <summary>
    /// Applies a time-boxed armor reduction (SP-6 破甲突刺: −4 甲 10 s).
    /// Stronger reduction wins while both windows overlap; the later
    /// expiry is kept.
    /// </summary>
    /// <exception cref="ArgumentException">reduction not positive.</exception>
    public void ApplyArmorReduction(int reductionPoints, long untilTick)
    {
        if (reductionPoints <= 0)
        {
            throw new ArgumentException($"Armor reduction must be positive (got {reductionPoints}).", nameof(reductionPoints));
        }

        if (ArmorReductionUntilTick <= long.MinValue || reductionPoints > ArmorReduction)
        {
            ArmorReduction = reductionPoints;
        }

        if (untilTick > ArmorReductionUntilTick)
        {
            ArmorReductionUntilTick = untilTick;
        }
    }

    /// <summary>
    /// Folds the time-boxed channels (slow, charge) into the resolved bonus
    /// alongside the aura channel. Called once per tick before movement;
    /// pure function of the stored stamps, so replays are stable.
    /// </summary>
    public void ResolveSpeedEffects(long tick)
    {
        int slow = tick < SlowUntilTick ? -SlowPercent : 0;
        int charge = tick < ChargeUntilTick ? ChargeSpeedBonusPercent : 0;
        ResolvedSpeedBonusPercent = slow + charge + AuraSpeedBonusPercent;
    }

    /// <summary>
    /// Sets the aura channel value for this tick (engine seam for
    /// <see cref="UnitAuras.ApplyWarDrums"/> — reset-then-apply, max wins).
    /// </summary>
    public void SetAuraSpeedBonus(int percent) => AuraSpeedBonusPercent = percent;

    /// <summary>Upper bound of one slow application, in percent (a slow is a ratio by definition).</summary>
    private const int MaxSlowPercent = 100; // ci-allow: R7 T-066 百分比量纲上界（量纲常量，非平衡数值：slow=(0,100%]）

    /// <summary>Moves the unit (movement seam, called by <see cref="UnitMovement"/>).</summary>
    public void MoveTo(float x, float y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Marks base arrival (movement seam, called by <see cref="UnitMovement"/>).</summary>
    public void MarkArrived() => ArrivedAtTarget = true;
}
