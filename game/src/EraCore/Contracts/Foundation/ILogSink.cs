// T2: pure contract — K-1 foundation ports, T-019, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>Severity levels accepted by <see cref="ILogSink"/> (K-1).</summary>
public enum LogLevel
{
    /// <summary>Fine-grained diagnostic detail (development builds only).</summary>
    Trace,

    /// <summary>Diagnostic detail useful while debugging a run.</summary>
    Debug,

    /// <summary>Routine operational information.</summary>
    Info,

    /// <summary>Recoverable anomaly worth surfacing (e.g. degraded supply line).</summary>
    Warn,

    /// <summary>Failure that loses a capability or aborts an operation.</summary>
    Error,
}

/// <summary>
/// Diagnostic log port (K-1). EraCore emits diagnostic logs only through
/// this port; implementations choose the destination (editor console, file,
/// test capture). The war-report JSONL stream is a separate contract (K-8)
/// — this sink is never a substitute for journaling (RL10).
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// Writes one entry. <paramref name="tick"/> (-1 when outside a
    /// simulation run) ties the entry to the deterministic time domain so
    /// logs can be aligned against replays.
    /// </summary>
    void Write(LogLevel level, long tick, string message);
}
