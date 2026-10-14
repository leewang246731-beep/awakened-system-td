using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Units;

/// <summary>Hero command stance after an order (SP-2/G.2: move / skill / retreat).</summary>
public enum HeroStance
{
    /// <summary>No movement order; the hero holds its current position.</summary>
    Hold,

    /// <summary>Ordered to advance toward the engagement cell.</summary>
    Advance,

    /// <summary>Ordered to fall back to the home (base) position.</summary>
    Retreat,
}

/// <summary>Outcome of one active-skill use (SP-6: exactly one active per hero).</summary>
public enum HeroSkillStatus
{
    /// <summary>The skill fired and its effect settled.</summary>
    Fired,

    /// <summary>The cooldown has not elapsed; nothing was consumed.</summary>
    OnCooldown,

    /// <summary>No living enemy stood inside the skill's reach; nothing was consumed.</summary>
    NoTargets,

    /// <summary>The hero is down and awaiting respawn.</summary>
    HeroDown,
}

/// <summary>
/// The optional player-avatar hero (C-10 full, T-066; F-039; SP-6):
/// a K-6 <see cref="IUnit"/> that carries exactly one active skill and one
/// passive, moves through the shared <see cref="UnitMovement"/> stride core
/// (<see cref="IMovableUnit"/>), and respawns at its home position
/// <paramref name="spec"/>-defined seconds after falling (SP-6 L176: 60 s).
/// Optional by construction — nothing in the unit system requires a hero
/// instance to exist; a level without one simply never receives one.
/// <para>
/// Orders arrive as the three G.2 verbs (move / skill / retreat) —
/// <see cref="OrderMove"/> aims the shared stride at a cell,
/// <see cref="OrderRetreat"/> aims it back home, and
/// <see cref="TryUseSkill"/> settles the spec's active (E1 warcry slows
/// every enemy inside the radius; E2 armor thrust picks the nearest enemy
/// inside its reach, reduces its armor, then lands the damage). All
/// targeting is deterministic: warcry sweeps in list order, the thrust
/// picks the strictly nearest enemy with list order breaking ties.
/// </para>
/// <para>
/// The veteran passive recomputes from waves held
/// (<see cref="OnWaveHeld"/>): attack bonus steps up per
/// <c>WavesPerStep</c> waves and clamps at <c>CapPercent</c> — the value
/// is exposed for the combat layer to consume as the hero's attack scale.
/// </para>
/// </summary>
public sealed class Hero : IUnit, IMovableUnit
{
    private readonly HeroSpec _spec;

    /// <summary>
    /// Creates a deployed hero at its home position, full hp, skill ready.
    /// Structural validation only — every balance number arrives from
    /// <paramref name="spec"/> (content data, CS-2.3).
    /// </summary>
    /// <exception cref="ArgumentNullException">spec or id is null.</exception>
    public Hero(HeroSpec spec, EntityId id, float homeX, float homeY)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(id);
        _spec = spec;
        Id = id;
        ContentId = spec.Id;
        HomeX = homeX;
        HomeY = homeY;
        X = homeX;
        Y = homeY;
        MaxHp = spec.Hp;
        Hp = spec.Hp;
        SpeedPxPerTick = (float)spec.SpeedPxPerTick;
        Alive = true;
        Stance = HeroStance.Hold;
    }

    /// <summary>Raised exactly once when hp reaches zero, inline in the damaging call.</summary>
    public event Action<Hero>? Died;

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
    public int Armor => _spec.Armor;

    /// <inheritdoc />
    public int Shield => 0; // SP-6 defines no hero shield; the E3 shield era may revisit.

    /// <inheritdoc />
    public float SpeedPxPerTick { get; }

    /// <summary>Whether the hero is on the field (false while awaiting respawn).</summary>
    public bool Alive { get; private set; }

    /// <summary>Home (base) position the hero respawns and retreats to, X in px.</summary>
    public float HomeX { get; }

    /// <summary>Home (base) position, Y in px.</summary>
    public float HomeY { get; }

    /// <summary>Tick the hero fell on; -1 while never dead.</summary>
    public long DeathTick { get; private set; } = -1;

    /// <summary>Tick the hero revives on; -1 while alive.</summary>
    public long RespawnTick { get; private set; } = -1;

    /// <summary>Current command stance (move/skill/retreat verbs map onto it).</summary>
    public HeroStance Stance { get; private set; }

    /// <summary>Ordered destination X in px; null while holding.</summary>
    public float? OrderedX { get; private set; }

    /// <summary>Ordered destination Y in px; null while holding.</summary>
    public float? OrderedY { get; private set; }

    /// <summary>Whether a movement order is active.</summary>
    public bool HasOrder => OrderedX is not null;

    /// <summary>Whether the hero reached its ordered destination.</summary>
    public bool ArrivedAtTarget { get; private set; }

    /// <summary>Earliest tick the active skill may fire again.</summary>
    public long NextSkillReadyTick { get; private set; } = long.MinValue;

    /// <summary>Waves held counter feeding the veteran passive.</summary>
    public int WavesHeld { get; private set; }

    /// <summary>Veteran passive attack bonus in percent (0 until stepped, capped).</summary>
    public int AttackBonusPercent { get; private set; }

    /// <summary>Heroes carry no speed-effect channels — the effective speed is the base speed.</summary>
    public float EffectiveSpeedPxPerTick => SpeedPxPerTick;

    /// <summary>
    /// Hero damage settlement: hp-only branch through the authoritative
    /// integer twin <see cref="Damage.DirectInt"/> (SP-6 heroes carry no
    /// armor and no shield, so effective = d). Death stamps the respawn
    /// tick and raises <see cref="Died"/> exactly once; the pending order
    /// is dropped.
    /// </summary>
    /// <returns>Effective damage applied; 0 while down.</returns>
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
            RespawnTick = tick + _spec.RespawnTicks;
            OrderedX = null;
            OrderedY = null;
            ArrivedAtTarget = false;
            Stance = HeroStance.Hold;
            Died?.Invoke(this);
        }

        return effective;
    }

    /// <summary>
    /// One hero upkeep tick: revives at home with full hp once the respawn
    /// delay has elapsed (SP-6 L176/L186). Call after damage settlement,
    /// before movement.
    /// </summary>
    public void Tick(long tick)
    {
        if (Alive || RespawnTick < 0 || tick < RespawnTick)
        {
            return;
        }

        Alive = true;
        Hp = MaxHp;
        X = HomeX;
        Y = HomeY;
        Stance = HeroStance.Hold;
        ArrivedAtTarget = false;
    }

    /// <summary>
    /// The move verb: aims the shared stride at the centre of
    /// <paramref name="cell"/> and clears any arrival flag.
    /// </summary>
    public void OrderMove(CellCoord cell, int cellSizePx)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"CellSizePx must be positive (got {cellSizePx}).", nameof(cellSizePx));
        }

        Stance = HeroStance.Advance;
        OrderedX = (cell.X * cellSizePx) + (cellSizePx / 2f);
        OrderedY = (cell.Y * cellSizePx) + (cellSizePx / 2f);
        ArrivedAtTarget = false;
    }

    /// <summary>The retreat verb: aims the shared stride back at the home position.</summary>
    public void OrderRetreat()
    {
        Stance = HeroStance.Retreat;
        OrderedX = HomeX;
        OrderedY = HomeY;
        ArrivedAtTarget = false;
    }

    /// <summary>
    /// The skill verb: settles the spec's active skill. E1 warcry slows
    /// every living enemy inside the radius (inclusive, list order); E2
    /// armor thrust reduces the nearest living enemy's armor then lands
    /// the damage through <see cref="Unit.ApplyDamage"/>. The cooldown is
    /// consumed only on <see cref="HeroSkillStatus.Fired"/>.
    /// </summary>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="enemies">Living enemy roster (targets resolved deterministically).</param>
    /// <param name="cellSizePx">Cell size in px (1 cell = 32 px).</param>
    public HeroSkillStatus TryUseSkill(long tick, IReadOnlyList<Unit> enemies, int cellSizePx)
    {
        ArgumentNullException.ThrowIfNull(enemies);
        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"CellSizePx must be positive (got {cellSizePx}).", nameof(cellSizePx));
        }

        if (!Alive)
        {
            return HeroSkillStatus.HeroDown;
        }

        if (tick < NextSkillReadyTick)
        {
            return HeroSkillStatus.OnCooldown;
        }

        HeroActiveSkill skill = _spec.Active;
        bool fired;
        if (skill.Kind == "warcry")
        {
            float radiusPx = (float)(skill.RadiusCells * cellSizePx);
            float radiusSquared = radiusPx * radiusPx;
            int affected = 0;
            foreach (Unit enemy in enemies)
            {
                if (!enemy.Alive)
                {
                    continue;
                }

                float dx = enemy.X - X;
                float dy = enemy.Y - Y;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    enemy.ApplySlow(skill.SlowPercent, tick + skill.DurationTicks);
                    affected++;
                }
            }

            fired = affected > 0;
        }
        else if (skill.Kind == "armor_thrust")
        {
            float rangePx = (float)(skill.RangeCells * cellSizePx);
            float rangeSquared = rangePx * rangePx;
            Unit? target = null;
            float best = rangeSquared;
            foreach (Unit enemy in enemies)
            {
                if (!enemy.Alive)
                {
                    continue;
                }

                float dx = enemy.X - X;
                float dy = enemy.Y - Y;
                float distance = (dx * dx) + (dy * dy);
                if (distance <= best)
                {
                    // nearest wins; list order breaks exact ties
                    target = enemy;
                    best = distance;
                }
            }

            if (target is null)
            {
                return HeroSkillStatus.NoTargets;
            }

            target.ApplyArmorReduction(skill.ArmorReduction, tick + skill.DurationTicks);
            target.ApplyDamage(skill.Damage, tick);
            fired = true;
        }
        else
        {
            throw new InvalidOperationException(
                $"Hero '{ContentId}' carries unknown active skill kind '{skill.Kind}' (content/table drift).");
        }

        if (!fired)
        {
            return HeroSkillStatus.NoTargets;
        }

        NextSkillReadyTick = tick + skill.CooldownTicks;
        return HeroSkillStatus.Fired;
    }

    /// <summary>
    /// Feeds the veteran passive: recomputes the attack bonus from waves
    /// held, stepping per <c>WavesPerStep</c> and clamping at
    /// <c>CapPercent</c> (SP-6 L182: every 5 waves +10%, cap +30%).
    /// </summary>
    public void OnWaveHeld()
    {
        WavesHeld++;
        HeroPassive passive = _spec.Passive;
        if (passive.Kind != "veteran" || passive.WavesPerStep <= 0)
        {
            return;
        }

        int steps = WavesHeld / passive.WavesPerStep;
        AttackBonusPercent = Math.Min(passive.CapPercent, steps * passive.AttackBonusPercentPerStep);
    }

    /// <summary>Movement seam shared with <see cref="UnitMovement"/>.</summary>
    public void MoveTo(float x, float y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Movement seam: marks the ordered destination reached.</summary>
    public void MarkArrived() => ArrivedAtTarget = true;
}
