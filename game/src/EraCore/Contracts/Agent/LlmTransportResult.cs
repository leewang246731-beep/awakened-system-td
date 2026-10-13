// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>
/// Reply of the LLM translator port (K-6 draft): a candidate action JSON or
/// a classified failure. Candidates are UNTRUSTED until they pass
/// ISchemaValidator, the approval gate and the budget ledger (injection
/// defence, F.4).
/// </summary>
/// <param name="ActionJson">Model-proposed action JSON; null on failure.</param>
/// <param name="ErrorKey">
/// Gateway error-classification key (auth / timeout / rate-limit / …) for
/// localized copy; null on success.
/// </param>
public sealed record LlmTransportResult(
    string? ActionJson,
    string? ErrorKey);
