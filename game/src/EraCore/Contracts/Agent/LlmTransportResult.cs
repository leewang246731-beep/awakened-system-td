// T2: pure contract — K-9 LLM transport port, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-16 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>
/// Reply of the LLM translator port (K-9 draft): a candidate action JSON or
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
