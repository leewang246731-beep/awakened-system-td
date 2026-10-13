using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;

namespace EraCore.Tests.Agent.Governance;

// [C-14] Test doubles for the K-1/K-8 ports the governance lane consumes
// (IClock/IJournalWriter — CS-3.4 whitelist: faking a PORT is the
// sanctioned seam, the system under test is never mocked). Kept local to
// this lane's folder per the per-lane doubles convention (same shape as
// EconomyTestDoubles / EnergyTestDoubles).

/// <summary>
/// Stepped deterministic clock (30 Hz domain): the tick moves only when
/// the test advances it, so journal tick stamps are explicit.
/// </summary>
internal sealed class SteppedClock : IClock
{
    private long _tick;

    public long Tick => _tick;

    public long NowMs => _tick * (1000L / 30L);

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

/// <summary>
/// Capturing K-8 journal fake: records every appended JSONL event in
/// order so tests assert the audit trail (RL10: the JSONL sink is the
/// only lawful war-report channel — the fake captures, never prints).
/// </summary>
internal sealed class CapturingJournalWriter : IJournalWriter
{
    public List<JournalEvent> Events { get; } = new();

    public void Append(JournalEvent evt)
    {
        Events.Add(evt);
    }
}
