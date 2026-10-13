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
/// Flow-following unit movement (C-10 E1 thin, T-039): per 30 Hz tick each
/// unit descends the <see cref="FlowField"/> gradient — current cell →
/// <see cref="FlowField.NextStep"/> → move <c>speed</c> px toward the next
/// cell's centre (pure IEEE 754 float math, no engine float intrinsics, so
/// trajectories are replay-stable). Reaching the field target cell marks
/// <see cref="Unit.ArrivedAtTarget"/> (base breach) and stops the unit.
/// <para>
/// Trample (SP-3a L107, radius from content data; period/damage from the
/// spawn-time trample profile, [NEW-原型校准]): every whole period after the
/// unit's spawn, one pulse damages every <see cref="ITrampleTarget"/> whose
/// centre lies within the unit's trample radius (inclusive).
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
        float dx = targetX - unit.X;
        float dy = targetY - unit.Y;
        float speed = unit.SpeedPxPerTick;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= speed)
        {
            // Within one tick's stride: land exactly on the centre — keeps
            // straight-line trajectories from oscillating around centres.
            unit.MoveTo(targetX, targetY);
            return;
        }

        float scale = (float)(speed / length);
        unit.MoveTo(unit.X + (dx * scale), unit.Y + (dy * scale));
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
}
