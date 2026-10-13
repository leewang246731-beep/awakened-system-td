// T2: pure contract — K-8 war report journal, T-025, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Journal;

/// <summary>Journal record kinds (K-8): the five-stage pipeline audit trail.</summary>
public enum JournalEventKind
{
    /// <summary>Commander input: the raw order text entering the pipeline.</summary>
    Input,

    /// <summary>Plan: the plan-card content (steps/costs/risks) and its approval verdict.</summary>
    Plan,

    /// <summary>Validation: schema/state validation outcomes of executed actions.</summary>
    Validation,

    /// <summary>Report: what the system reported back to the player.</summary>
    Report,
}

/// <summary>
/// One JSONL journal record (K-8). The journal is append-only and ordered
/// by <c>(tick, seq)</c> — the same ordering domain as the event bus
/// (D-12) — and is the single source for war-report replay (F-121), the
/// offline replay tool and audits. Rotation keeps the latest 50 runs or
/// 200 MB, configurable (07 D-11). Keys and secrets never appear in
/// payloads (RL5).
/// </summary>
/// <param name="Kind">Pipeline stage the record belongs to.</param>
/// <param name="Tick">Simulation tick of the record (30 Hz domain; -1 outside a run).</param>
/// <param name="Seq">Bus-assigned sequence number; strictly increasing per run.</param>
/// <param name="PayloadJson">Stage payload serialized as JSON (STJ source generator, D-05).</param>
public sealed record JournalEvent(
    JournalEventKind Kind,
    long Tick,
    long Seq,
    string PayloadJson);
