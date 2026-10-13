// T2: pure contract — K-5 agent pipeline and card ports, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Intents;

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 2 of the five-stage pipeline (K-5 draft, C-13): routes a parsed
/// intent to its handling strategy. Must be pure — no state mutation, no
/// budget consumption, no side channels.
/// </summary>
public interface IIntentRouter
{
    /// <summary>Routes one parsed intent.</summary>
    IntentRoute Route(Intent intent);
}
