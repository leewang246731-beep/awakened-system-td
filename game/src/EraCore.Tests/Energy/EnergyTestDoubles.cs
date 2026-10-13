using EraCore.Contracts.Foundation;

namespace EraCore.Tests.Energy;

// [C-08] Test doubles for the K-1 ports the energy ledger consumes
// (IClock/IRng — CS-3.4 whitelist: faking a PORT is the sanctioned seam,
// the system under test itself is never mocked). Same shape as the
// World-test LcgRng: a real seeded 64-bit LCG, not a scripted bypass.

/// <summary>
/// Deterministic 64-bit LCG implementation of <see cref="IRng"/> — same
/// parameters as <c>EraCore.Tests.World.LcgRng</c> (kept local to this
/// lane's folder so parallel tasks cannot break these tests by editing it).
/// </summary>
internal sealed class SeededRng : IRng
{
    private ulong _state;

    public SeededRng(long seed) => Reseed(seed);

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue));
        }

        long span = maxValue - (long)minValue;
        return minValue + (int)(NextDouble() * span);
    }

    public double NextDouble()
    {
        _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
        return (_state >> 11) * (1.0 / (1UL << 53));
    }

    public void Reseed(long seed) => _state = unchecked((ulong)seed);
}

/// <summary>
/// Stepped 30 Hz simulation clock: <see cref="Tick"/> only moves when the
/// test calls <see cref="Advance"/>, so settle sequences are explicit.
/// </summary>
internal sealed class SteppedClock : IClock
{
    private long _tick;

    public SteppedClock(long startTick = 0) => _tick = startTick;

    public long Tick => _tick;

    /// <summary>Wall-clock estimate derived from the tick domain; diagnostics only (CS-1.4).</summary>
    public long NowMs => _tick * (1000L / 30L);

    /// <summary>Advances the tick by <paramref name="steps"/> (default 1) and returns the new tick.</summary>
    public long Advance(int steps = 1)
    {
        if (steps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        _tick += steps;
        return _tick;
    }

    /// <summary>Rewinds the tick — used to test the ledger's non-monotonic clock guard.</summary>
    public long Rewind(int steps = 1)
    {
        _tick -= steps;
        return _tick;
    }
}
