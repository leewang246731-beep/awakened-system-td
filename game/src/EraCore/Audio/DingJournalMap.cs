using EraCore.Agent.Pipeline;
using EraCore.Contracts.Journal;

namespace EraCore.Audio;

/// <summary>
/// K-8 journal → ding trigger binding (C-25 thin wiring, T-060, F-112):
/// the final system REPORT the player receives is an info "叮"
/// (系统汇报, v3.2 §10.3). Clarify cards, order inputs, plan cards and
/// validation traces stay silent — dinging on pipeline internals would be
/// noise, not signal. The payload discriminator is the frozen pipeline
/// stage type of T-053 (<see cref="PipelineJournalTypes"/>), so the map
/// tracks the journal contract instead of re-declaring its strings.
/// </summary>
public static class DingJournalMap
{
    /// <summary>
    /// Maps one journal event to the ding trigger it requests, or null when
    /// the event class never dings.
    /// </summary>
    /// <param name="kind">K-8 record kind (Input/Plan/Validation/Report).</param>
    /// <param name="payloadType">
    /// Stage discriminator inside the payload JSON (<c>"type"</c> field);
    /// null when unknown — treated as not-a-report.
    /// </param>
    public static DingTrigger? Map(JournalEventKind kind, string? payloadType) =>
        kind == JournalEventKind.Report && payloadType == PipelineJournalTypes.Report
            ? DingTrigger.SystemReport
            : null;
}
