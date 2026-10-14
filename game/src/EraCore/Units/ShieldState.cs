namespace EraCore.Units;

/// <summary>
/// Runtime shield framework (C-10 full, T-066; F-032): one unit's shield
/// points and their regeneration clock. Design v3.2 §6 L84 freezes the
/// timing — the shield absorbs BEFORE hp (its consumption precedes the
/// armored settlement in <see cref="Unit.ApplyDamage"/>); once broken it
/// regenerates 20 points/s after a 5 s break; a piercing hit interrupts
/// regeneration for 3 s ([NEW-原型校准] interrupt window fixed by the T-066
/// task card — SP-1a L28 names the energy line's 穿甲 flag as the
/// interrupt source).
/// <para>
/// Rulings the source text leaves open (documented so tests can pin them):
/// (1) the shield eats RAW damage points — armor only applies to the
/// remainder that spills into hp, because v3.2 §6 binds 甲 to the hp damage
/// formula alone; (2) a shield damaged below max but never broken keeps
/// regenerating 20/s (the pierce interrupt is what makes that channel
/// contestable); (3) regeneration accumulates in exact integer math —
/// 20 points/s over the 30 Hz tick domain is credited as +20 numerator
/// per tick with one point per whole 30 — so any 30-tick window restores
/// exactly 20 points and replays are bit-stable.
/// </para>
/// Initial points come from content/balance data (the wave formula
/// <see cref="EraCore.Balance.Shield.At"/> for E3+ waves, or an individual
/// spec value); the engine never invents them (RL7/CS-2.3).
/// </summary>
public sealed class ShieldState
{
    /// <summary>Regeneration rate in points per second (v3.2 §6: 破盾 5 s 后 20/s 再生).</summary>
    public const int RegenPointsPerSecond = 20; // ci-allow: R7 T-066 v3.2 §6 冻结域常量：破盾后 20/s 再生（非内容表数值，公式结构常量）

    /// <summary>Break pause in ticks at the K-1 30 Hz domain (v3.2 §6: 5 s).</summary>
    public const int BreakDelayTicks = 150; // ci-allow: R7 T-066 v3.2 §6 冻结域常量：破盾 5 s（5×30 Hz）后再生启动

    /// <summary>Pierce interrupt window in ticks at 30 Hz ([NEW-原型校准] T-066 任务卡：穿甲命中打断再生 3 s).</summary>
    public const int PierceInterruptTicks = 90; // ci-allow: R7 T-066 T-066 任务卡 [NEW]：穿甲弹命中打断再生 3 s（3×30 Hz）

    private readonly int _tickRateHz;
    private long _brokenAtTick = long.MinValue;
    private long _pierceBlockedUntilTick = long.MinValue;
    private int _numerator;

    /// <summary>
    /// Creates a fully charged shield. Structural validation only; balance
    /// points arrive from the caller's data (CS-2.3).
    /// </summary>
    /// <exception cref="ArgumentException">maxPoints or tickRateHz not positive.</exception>
    public ShieldState(int maxPoints, int tickRateHz = 30)
    {
        if (maxPoints <= 0)
        {
            throw new ArgumentException($"Shield max points must be positive (got {maxPoints}).", nameof(maxPoints));
        }

        if (tickRateHz <= 0)
        {
            throw new ArgumentException($"Tick rate must be positive (got {tickRateHz}).", nameof(tickRateHz));
        }

        MaxPoints = maxPoints;
        CurrentPoints = maxPoints;
        _tickRateHz = tickRateHz;
    }

    /// <summary>Maximum shield points (initial charge equals this).</summary>
    public int MaxPoints { get; }

    /// <summary>Current shield points; 0 while broken and awaiting regeneration.</summary>
    public int CurrentPoints { get; private set; }

    /// <summary>Whether the shield was fully depleted and has not recharged a point yet.</summary>
    public bool IsBroken => CurrentPoints == 0;

    /// <summary>
    /// Absorbs up to <paramref name="incoming"/> raw points. Absorption
    /// precedes hp settlement and ignores armor (ruling 1). A hit that
    /// reaches zero raises the break stamp; a piercing hit additionally
    /// blocks regeneration for <see cref="PierceInterruptTicks"/> — even
    /// when it lands on the broken husk (the anti-shield mechanic keeps
    /// the enemy's recovery door shut).
    /// </summary>
    /// <returns>Points actually absorbed (0 when nothing was left to absorb).</returns>
    /// <exception cref="ArgumentOutOfRangeException">incoming is not positive.</exception>
    public int Absorb(int incoming, bool pierce, long tick)
    {
        if (incoming <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incoming), incoming, "Incoming damage must be positive.");
        }

        if (pierce)
        {
            long blockedUntil = tick + PierceInterruptTicks;
            if (blockedUntil > _pierceBlockedUntilTick)
            {
                _pierceBlockedUntilTick = blockedUntil;
            }
        }

        int absorbed = Math.Min(CurrentPoints, incoming);
        CurrentPoints -= absorbed;
        if (CurrentPoints == 0 && absorbed > 0)
        {
            _brokenAtTick = tick;
            _numerator = 0;
        }

        return absorbed;
    }

    /// <summary>
    /// One regeneration step at <paramref name="tick"/>. Gates: the pierce
    /// interrupt must have lapsed, and a broken shield must have waited out
    /// <see cref="BreakDelayTicks"/> since its break stamp. A full shield
    /// resets the accumulator so a later hit cannot bank hidden points.
    /// </summary>
    public void Tick(long tick)
    {
        if (CurrentPoints >= MaxPoints)
        {
            _numerator = 0;
            return;
        }

        if (tick < _pierceBlockedUntilTick)
        {
            return;
        }

        if (CurrentPoints == 0 && _brokenAtTick != long.MinValue
            && tick < _brokenAtTick + BreakDelayTicks)
        {
            return;
        }

        _numerator += RegenPointsPerSecond;
        int whole = _numerator / _tickRateHz;
        if (whole <= 0)
        {
            return;
        }

        _numerator -= whole * _tickRateHz;
        CurrentPoints = Math.Min(MaxPoints, CurrentPoints + whole);
    }
}
