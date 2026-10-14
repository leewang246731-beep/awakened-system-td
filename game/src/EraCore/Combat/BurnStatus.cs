using EraCore.Contracts.Foundation;

namespace EraCore.Combat;

/// <summary>
/// One active burn on one unit: the per-tick damage rate (converted from
/// the content's points-per-second rate) and the remaining ticks. The
/// minimal DOT field set of the 火油矢燃烧 effect ([NEW] content values);
/// the C-10 unit status-effect framework (状态效果乘数体系) supersedes this
/// shape when it lands — the tracker migrates onto it, content values stay.
/// </summary>
/// <param name="UnitId">Burning unit.</param>
/// <param name="DamagePerTick">Burn damage per tick (rate ÷ 30 Hz).</param>
/// <param name="TicksRemaining">Ticks left before the burn expires.</param>
public sealed record BurnState(EntityId UnitId, double DamagePerTick, int TicksRemaining);

/// <summary>One burn tick's due damage (the caller applies it to the unit).</summary>
/// <param name="UnitId">Burning unit.</param>
/// <param name="Damage">Whole points due this tick (fractional parts bank until they cross 1).</param>

public sealed record BurnTick(EntityId UnitId, int Damage);

/// <summary>
/// Burn damage-over-time tracker (C-09 full, T-065 薄版): owns every active
/// burn, advances them one 30 Hz tick at a time and hands out the due whole-
/// point damages in deterministic ignition order. Fractional per-tick rates
/// (e.g. 12 DPS → 0.4/tick) BANK per unit: each tick adds the rate to an
/// accumulator and emits <c>floor</c> whole points, the remainder carries
/// forward, and expiry flushes the residue rounded AwayFromZero — a 12 DPS
/// × 3 s burn therefore always totals exactly 36 points, never rounds
/// itself away to zero. Re-igniting a burning unit refreshes the duration
/// and keeps the stronger rate ([NEW] convention, content-driven numbers).
/// <para>
/// Applying the returned damages to units is the caller's job (Units own HP
/// mutation — one-way seam, 03-decomposition §3.1); the C-12 combat tick
/// (T-067) wires this poll into the per-tick order after weapon settlement.
/// </para>
/// </summary>
public sealed class BurnTracker
{
    /// <summary>
    /// The fixed tick rate of the simulation domain (K-1 IClock: 30 Hz) —
    /// the conversion base for burn rates declared in points per SECOND on
    /// the tick domain. Same standing as the domain constant on the
    /// simulation side; balance numbers themselves stay in content (RL7).
    /// </summary>
    public const int TicksPerSecond = 30;

    private readonly List<Entry> _burns = new();

    /// <summary>Active burns in first-ignition order (deterministic iteration).</summary>
    public IReadOnlyList<BurnState> Active
    {
        get
        {
            var states = new BurnState[_burns.Count];
            for (int i = 0; i < _burns.Count; i++)
            {
                states[i] = new BurnState(_burns[i].UnitId, _burns[i].DamagePerTick, _burns[i].TicksRemaining);
            }

            return states;
        }
    }

    /// <summary>Number of currently burning units.</summary>
    public int ActiveCount => _burns.Count;

    /// <summary>Whether one unit is currently burning.</summary>
    public bool IsBurning(EntityId unitId)
    {
        ArgumentNullException.ThrowIfNull(unitId);
        foreach (Entry entry in _burns)
        {
            if (entry.UnitId.Equals(unitId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ignites (or refreshes) one unit's burn: the rate converts from
    /// points-per-second to points-per-tick on the fixed 30 Hz domain. A
    /// fresh ignition appends in order; a refresh keeps the entry's position
    /// and takes the stronger rate and the longer remaining duration.
    /// </summary>
    /// <param name="unitId">Unit to ignite.</param>
    /// <param name="burnDps">Burn rate in points per second (content, &gt; 0).</param>
    /// <param name="burnTicks">Burn duration in ticks (&gt; 0).</param>
    /// <exception cref="ArgumentException">burnDps or burnTicks not positive.</exception>
    public void Ignite(EntityId unitId, double burnDps, int burnTicks)
    {
        ArgumentNullException.ThrowIfNull(unitId);
        if (burnDps <= 0)
        {
            throw new ArgumentException($"Burn dps must be positive (got {burnDps}).", nameof(burnDps));
        }

        if (burnTicks <= 0)
        {
            throw new ArgumentException($"Burn ticks must be positive (got {burnTicks}).", nameof(burnTicks));
        }

        double perTick = burnDps / TicksPerSecond;
        foreach (Entry entry in _burns)
        {
            if (entry.UnitId.Equals(unitId))
            {
                entry.DamagePerTick = Math.Max(entry.DamagePerTick, perTick);
                entry.TicksRemaining = Math.Max(entry.TicksRemaining, burnTicks);
                return;
            }
        }

        _burns.Add(new Entry(unitId, perTick, burnTicks));
    }

    /// <summary>
    /// Advances every active burn one tick and returns the due whole-point
    /// damages in ignition order. Expired burns flush their banked residue
    /// (rounded AwayFromZero) on their final tick, then drop out.
    /// </summary>
    public IReadOnlyList<BurnTick> Tick()
    {
        var due = new List<BurnTick>();
        for (int i = _burns.Count - 1; i >= 0; i--)
        {
            Entry entry = _burns[i];
            entry.TicksRemaining--;
            entry.Banked += entry.DamagePerTick;
            int whole = (int)Math.Floor(entry.Banked);
            if (whole > 0)
            {
                entry.Banked -= whole;
            }

            if (entry.TicksRemaining <= 0)
            {
                // Expiry flush: the banked residue leaves with the burn,
                // rounded AwayFromZero — the declared total never leaks.
                double residue = Math.Round(entry.Banked, MidpointRounding.AwayFromZero);
                if (residue >= 1)
                {
                    whole += (int)residue;
                }

                if (whole > 0)
                {
                    due.Add(new BurnTick(entry.UnitId, whole));
                }

                _burns.RemoveAt(i);
                continue;
            }

            if (whole > 0)
            {
                due.Add(new BurnTick(entry.UnitId, whole));
            }
        }

        // due collected in reverse (removals from the tail); emit in
        // ignition order for deterministic downstream application.
        due.Reverse();
        return due;
    }

    private sealed class Entry(EntityId unitId, double damagePerTick, int ticksRemaining)
    {
        public EntityId UnitId { get; } = unitId;

        public double DamagePerTick { get; set; } = damagePerTick;

        public int TicksRemaining { get; set; } = ticksRemaining;

        public double Banked { get; set; }
    }
}
