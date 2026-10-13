// T2: pure contract — K-1 foundation ports, T-019, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Base shape of every event crossing the event bus (K-1). Events carry
/// their ordering coordinates <c>(tick, seq)</c> so the JSONL journal
/// (K-8), the replay harness and the audio/UI relays all observe one
/// totally ordered stream (07-decided-plan D-12).
/// </summary>
public interface IGameEvent
{
    /// <summary>Simulation tick on which the event was raised (30 Hz domain).</summary>
    long Tick { get; }

    /// <summary>
    /// Sequence number assigned by the bus in raise order; strictly
    /// increasing with publication order, unique within the run.
    /// </summary>
    long Seq { get; }
}
