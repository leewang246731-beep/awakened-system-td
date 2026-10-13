// T2: pure contract — K-9 LLM transport port, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-16 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>One chat message for the translator port (K-9 draft).</summary>
/// <param name="Role">Chat role: system / user / assistant / tool.</param>
/// <param name="Content">Message text (never contains keys — RL5).</param>
public sealed record LlmChatMessage(string Role, string Content);

/// <summary>
/// One translation request to the LLM tier (K-9 draft, C-16 under C-13):
/// prompt turns plus the tool schema. The tools JSON is the L3 projection
/// of the SAME single schema source that powers ISchemaValidator (07 D-14)
/// — the two projections can never drift apart.
/// </summary>
/// <param name="Messages">Prompt turns, context-trimmed by the gateway.</param>
/// <param name="ToolsJson">Function-calling tools JSON projected from the K-3 closed schema.</param>
public sealed record LlmTransportRequest(
    IReadOnlyList<LlmChatMessage> Messages,
    string ToolsJson);
