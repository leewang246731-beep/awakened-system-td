using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.Units;

/// <summary>
/// Receiver of a boss trample pulse (踏群者践踏): the C-09/C-10 seam that
/// lets buildings take AoE damage without the unit system owning building
/// internals. Implementations settle the damage themselves.
/// </summary>
public interface ITrampleTarget
{
    /// <summary>World position X in px.</summary>
    float X { get; }

    /// <summary>World position Y in px.</summary>
    float Y { get; }

    /// <summary>Whether the target is still standing.</summary>
    bool Alive { get; }

    /// <summary>Applies one trample pulse of <paramref name="damage"/> points.</summary>
    void TakeTrample(int damage);
}

/// <summary>
/// The movement seam both unit families implement (T-066): enemies follow
/// the flow field, heroes follow ordered destinations — the stride core in
/// <see cref="UnitMovement"/> is shared through this projection (heroes
/// reuse <c>UnitMovement</c> per the T-066 task card).
/// </summary>
public interface IMovableUnit
{
    /// <summary>World position X in px.</summary>
    float X { get; }

    /// <summary>World position Y in px.</summary>
    float Y { get; }

    /// <summary>Movement speed after all speed-effect channels, px per tick.</summary>
    float EffectiveSpeedPxPerTick { get; }

    /// <summary>Whether the unit is still on the field.</summary>
    bool Alive { get; }

    /// <summary>Whether the unit already reached its movement target.</summary>
    bool ArrivedAtTarget { get; }

    /// <summary>Marks arrival at the movement target.</summary>
    void MarkArrived();

    /// <summary>Moves the unit to a world position.</summary>
    void MoveTo(float x, float y);
}

/// <summary>
/// Flow-following unit movement (C-10 E1 thin, T-039): per 30 Hz tick each
/// unit descends the <see cref="FlowField"/> gradient — current cell →
/// <see cref="FlowField.NextStep"/> → move <c>speed</c> px toward the next
/// cell's centre (pure IEEE 754 float math, no engine float intrinsics, so
/// trajectories are replay-stable). Reaching the field target cell marks
/// <see cref="Unit.ArrivedAtTarget"/> (base breach) and stops the unit.
/// T-066: the stride consumed is <see cref="Unit.EffectiveSpeedPxPerTick"/>
/// (war-drum aura / hero slow / charge burst channels, SP-3b L120~L121);
/// heroes reach the same core through <see cref="IMovableUnit"/>.
/// <para>
/// Trample (SP-3a L107, radius from content data; period/damage from the
/// spawn-time trample profile, [NEW-原型校准]): every whole period after the
/// unit's spawn, one pulse damages every <see cref="ITrampleTarget"/> whose
/// centre lies within the unit's trample radius (inclusive).
/// </para>
/// <para>
/// Charge (冲撞, SP-3b L121 — every 20 s): every whole interval after the
/// unit's spawn the boss rams every <see cref="ITrampleTarget"/> within its
/// charge reach and enters a timed speed burst.
/// </para>
/// <para>
/// The tick domain is driven by the caller's IClock loop — this class holds
/// no mutable state, so determinism reduces entirely to unit + field state.
/// </para>
/// </summary>
public sealed class UnitMovement
{
    private readonly Func<FlowField> _field;
    private readonly int _cellSizePx;

    /// <summary>
    /// Creates the mover over one field supplier. A supplier (rather than a
    /// captured field) lets each tick read the live field of a
    /// <see cref="FlowFieldManager"/> bucket after rebuild swaps.
    /// </summary>
    /// <exception cref="ArgumentNullException">field is null.</exception>
    /// <exception cref="ArgumentException">cellSizePx is not positive.</exception>
    public UnitMovement(Func<FlowField> field, int cellSizePx = 32)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"CellSizePx must be positive (got {cellSizePx}).", nameof(cellSizePx));
        }

        _field = field;
        _cellSizePx = cellSizePx;
    }

    /// <summary>
    /// Moves one unit one tick along the gradient. Dead or arrived units
    /// stay put; a unit already on the target cell is marked arrived; a
    /// unit whose NextStep is itself (unreachable pocket) waits.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The supplier returned null, or the field is not built yet
    /// (<see cref="FlowField.NextStep"/> contract).
    /// </exception>
    public void Step(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.Alive || unit.ArrivedAtTarget)
        {
            return;
        }

        FlowField field = _field()
            ?? throw new InvalidOperationException("Field supplier returned no flow field.");
        var cell = new CellCoord(
            (int)Math.Floor(unit.X / _cellSizePx),
            (int)Math.Floor(unit.Y / _cellSizePx));
        if (cell.X == field.Target.X && cell.Y == field.Target.Y)
        {
            unit.MarkArrived();
            return;
        }

        CellCoord next = field.NextStep(cell);
        if (next.X == cell.X && next.Y == cell.Y)
        {
            return;
        }

        float targetX = next.X * _cellSizePx + (_cellSizePx / 2f);
        float targetY = next.Y * _cellSizePx + (_cellSizePx / 2f);
        MoveToward(unit, targetX, targetY);
    }

    /// <summary>Steps every unit once, in list order.</summary>
    public void StepAll(IReadOnlyList<Unit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        foreach (Unit unit in units)
        {
            Step(unit);
        }
    }

    /// <summary>
    /// Shared stride core (T-066): moves <paramref name="unit"/> one tick's
    /// worth of <see cref="IMovableUnit.EffectiveSpeedPxPerTick"/> px toward
    /// the point, landing exactly on it when the remaining distance fits
    /// inside one stride (keeps trajectories from oscillating around
    /// centres). This is the single place stride math lives — enemies reach
    /// it through <see cref="Step"/>, heroes through ordered movement.
    /// </summary>
    public static void MoveToward(IMovableUnit unit, float targetX, float targetY)
    {
        ArgumentNullException.ThrowIfNull(unit);
        float dx = targetX - unit.X;
        float dy = targetY - unit.Y;
        float speed = unit.EffectiveSpeedPxPerTick;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= speed)
        {
            unit.MoveTo(targetX, targetY);
            return;
        }

        float scale = (float)(speed / length);
        unit.MoveTo(unit.X + (dx * scale), unit.Y + (dy * scale));
    }

    /// <summary>
    /// One ordered-movement tick for hero-style movers (T-066): dead or
    /// arrived movers stay put; otherwise stride toward the ordered point
    /// and mark arrival when the remaining distance fits one stride.
    /// </summary>
    public void StepOrdered(IMovableUnit unit, float targetX, float targetY)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.Alive || unit.ArrivedAtTarget)
        {
            return;
        }

        float dx = targetX - unit.X;
        float dy = targetY - unit.Y;
        if ((dx * dx) + (dy * dy) <= unit.EffectiveSpeedPxPerTick * unit.EffectiveSpeedPxPerTick)
        {
            unit.MoveTo(targetX, targetY);
            unit.MarkArrived();
            return;
        }

        MoveToward(unit, targetX, targetY);
    }

    /// <summary>
    /// Applies one trample pulse if <paramref name="tick"/> is on the unit's
    /// period grid (tick − spawnTick divisible by the period, phase > 0).
    /// Returns the targets hit, in list order; empty when nothing pulses.
    /// </summary>
    public IReadOnlyList<ITrampleTarget> PulseTrample(
        Unit unit,
        IReadOnlyList<ITrampleTarget> targets,
        long tick)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(targets);
        if (!unit.Alive || unit.TramplePeriodTicks <= 0 || targets.Count == 0)
        {
            return Array.Empty<ITrampleTarget>();
        }

        long phase = tick - unit.SpawnTick;
        if (phase <= 0 || phase % unit.TramplePeriodTicks != 0)
        {
            return Array.Empty<ITrampleTarget>();
        }

        float radiusSquared = unit.TrampleRadiusPx * unit.TrampleRadiusPx;
        var hit = new List<ITrampleTarget>();
        foreach (ITrampleTarget target in targets)
        {
            if (!target.Alive)
            {
                continue;
            }

            float dx = target.X - unit.X;
            float dy = target.Y - unit.Y;
            if ((dx * dx) + (dy * dy) <= radiusSquared)
            {
                target.TakeTrample(unit.TrampleDamage);
                hit.Add(target);
            }
        }

        return hit;
    }

    /// <summary>
    /// Applies one charge ram if <paramref name="tick"/> is on the unit's
    /// charge grid (tick − spawnTick divisible by the interval, phase > 0):
    /// every standing <see cref="ITrampleTarget"/> within the charge reach
    /// takes <see cref="Unit.ChargeDamage"/> (冲锋撞塔, SP-3b L121), and the
    /// unit enters its timed charge speed burst. Returns the targets rammed,
    /// in list order; empty when nothing fires.
    /// </summary>
    public IReadOnlyList<ITrampleTarget> PulseCharge(
        Unit unit,
        IReadOnlyList<ITrampleTarget> targets,
        long tick)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(targets);
        if (!unit.Alive || unit.ChargeIntervalTicks <= 0)
        {
            return Array.Empty<ITrampleTarget>();
        }

        long phase = tick - unit.SpawnTick;
        if (phase <= 0 || phase % unit.ChargeIntervalTicks != 0)
        {
            return Array.Empty<ITrampleTarget>();
        }

        unit.BeginChargeBurst(tick + unit.ChargeDurationTicks, unit.ChargeSpeedBonusPercent);

        if (targets.Count == 0)
        {
            return Array.Empty<ITrampleTarget>();
        }

        float reachSquared = unit.ChargeReachPx * unit.ChargeReachPx;
        var hit = new List<ITrampleTarget>();
        foreach (ITrampleTarget target in targets)
        {
            if (!target.Alive)
            {
                continue;
            }

            float dx = target.X - unit.X;
            float dy = target.Y - unit.Y;
            if ((dx * dx) + (dy * dy) <= reachSquared)
            {
                target.TakeTrample(unit.ChargeDamage);
                hit.Add(target);
            }
        }

        return hit;
    }

    /// <summary>
    /// Applies one war-cry (战吼) pulse if <paramref name="tick"/> is on the
    /// crier's cooldown grid (tick − spawnTick divisible by the cooldown,
    /// phase &gt; 0; T-114, era3 boss trait): every OTHER living ally whose
    /// centre lies within the cry radius enters a timed +speed buff window
    /// (<see cref="Unit.BeginWarCryBurst"/> — re-application keeps the
    /// stronger active buff and the later expiry). The crier itself is
    /// excluded (the cry rallies allies, mirroring the war-drum self-exclusion).
    /// Returns the allies buffed by this pulse, in list order; empty when
    /// nothing fires.
    /// </summary>
    public IReadOnlyList<Unit> PulseWarCry(Unit crier, IReadOnlyList<Unit> allies, long tick)
    {
        ArgumentNullException.ThrowIfNull(crier);
        ArgumentNullException.ThrowIfNull(allies);
        if (!crier.Alive || crier.WarCryCooldownTicks <= 0)
        {
            return Array.Empty<Unit>();
        }

        long phase = tick - crier.SpawnTick;
        if (phase <= 0 || phase % crier.WarCryCooldownTicks != 0)
        {
            return Array.Empty<Unit>();
        }

        float radiusSquared = crier.WarCryRadiusPx * crier.WarCryRadiusPx;
        var buffed = new List<Unit>();
        foreach (Unit ally in allies)
        {
            if (!ally.Alive || ReferenceEquals(ally, crier))
            {
                continue;
            }

            float dx = ally.X - crier.X;
            float dy = ally.Y - crier.Y;
            if ((dx * dx) + (dy * dy) <= radiusSquared)
            {
                ally.BeginWarCryBurst(tick + crier.WarCryDurationTicks, crier.WarCrySpeedBonusPercent);
                buffed.Add(ally);
            }
        }

        return buffed;
    }
}
