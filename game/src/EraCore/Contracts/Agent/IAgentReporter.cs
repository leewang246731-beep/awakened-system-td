// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Intents;

namespace EraCore.Contracts.Agent;

/// <summary>
/// Reporting stage port (K-6 draft, C-13): receives the report that closes
/// a handled order. Implementations render the report card and journal the
/// event through K-8 (RL10: JSONL on disk — console output is never a
/// substitute).
/// </summary>
public interface IAgentReporter
{
    /// <summary>Emits one report for the order identified by its source intent.</summary>
    void Report(Intent sourceIntent, AgentReport report);
}
