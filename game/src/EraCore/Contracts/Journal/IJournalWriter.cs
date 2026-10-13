// T2: pure contract — K-8 war report journal, T-025, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Journal;

/// <summary>
/// Append-only JSONL journal writer (K-8): one line per
/// <see cref="JournalEvent"/>. Writing is synchronous and ordered — the
/// journal observes the single-threaded tick domain and never reorders
/// records (D-12). Console output is never a substitute for this sink
/// (RL10).
/// </summary>
public interface IJournalWriter
{
    /// <summary>Appends one record as a single JSONL line, flushed before returning.</summary>
    void Append(JournalEvent evt);
}
