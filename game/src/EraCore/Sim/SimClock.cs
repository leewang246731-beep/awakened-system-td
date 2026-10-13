using EraCore.Contracts.Foundation;

namespace EraCore.Sim;

/// <summary>
/// Manual fixed-step simulation clock (C-12, T-040) — the production
/// <see cref="IClock"/> implementation inside one <see cref="Simulation"/>.
/// The orchestrator advances it exactly once per simulated tick (the frozen
/// 30 Hz domain, K-1/03-decomposition §1.2), so every subsystem reads the
/// same monotonic tick during one <see cref="Simulation.Step"/>.
/// <para>
/// <see cref="IClock.NowMs"/> is a diagnostics field per the port contract;
/// a manual clock cannot know wall time (CS-1.4 bans DateTime inside
/// EraCore), so it derives a tick-domain equivalent — deterministic and
/// clearly not wall time. Gameplay never branches on it.
/// </para>
/// </summary>
public sealed class SimClock : IClock
{
    /// <summary>Fixed tick rate of the simulation domain (K-1: 30 Hz).</summary>
    public const int TicksPerSecond = 30;

    private long _tick;

    /// <summary>
    /// Creates a clock at <paramref name="tick"/> (0 = level start).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">tick is negative.</exception>
    public SimClock(long tick = 0)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "Ticks are non-negative.");
        }

        _tick = tick;
    }

    /// <inheritdoc />
    public long Tick => _tick;

    /// <inheritdoc />
    public long NowMs => (_tick * 1000L) / TicksPerSecond;

    /// <summary>Advances exactly one tick (called once per Simulation.Step).</summary>
    public void Advance() => _tick++;

    /// <summary>
    /// Jumps to an absolute tick (test/diagnostic seam). Monotonic guard:
    /// backwards jumps are rejected because every subsystem assumes a
    /// monotonic clock (e.g. <c>EnergyLedger.Settle</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">tick below the current tick.</exception>
    public void Set(long tick)
    {
        if (tick < _tick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tick), tick, $"Clock is monotonic: cannot go back below {_tick}.");
        }

        _tick = tick;
    }
}
