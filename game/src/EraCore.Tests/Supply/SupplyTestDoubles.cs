using EraCore.Contracts.Foundation;

namespace EraCore.Tests.Supply;

// [C-06][C-07] Test doubles for the K-1 ports the supply lane consumes
// (IClock — CS-3.4 whitelist: faking a PORT is the sanctioned seam, the
// system under test itself is never mocked). Same shape as the
// Economy/Logistics lane copies: a stepped 30 Hz clock kept local to this
// lane's folder so parallel tasks cannot break these tests by editing a
// shared copy.

/// <summary>
/// Stepped 30 Hz simulation clock: <see cref="IClock.Tick"/> only moves when
/// the test calls <see cref="Advance"/>, so tick sequences are explicit.
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
}
