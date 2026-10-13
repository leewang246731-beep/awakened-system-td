// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Intents;

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 2 of the five-stage pipeline (K-6 draft, C-13): routes a parsed
/// intent to its handling strategy. Must be pure — no state mutation, no
/// budget consumption, no side channels.
/// </summary>
public interface IIntentRouter
{
    /// <summary>Routes one parsed intent.</summary>
    IntentRoute Route(Intent intent);
}
