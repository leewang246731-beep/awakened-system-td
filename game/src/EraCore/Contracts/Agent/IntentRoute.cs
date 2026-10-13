// T2: pure contract — K-5 agent pipeline and card ports, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>How the pipeline proceeds after routing an intent (K-5 draft).</summary>
public enum RouteKind
{
    /// <summary>Read-only intents (stockpile/enemy queries) are answered directly from state.</summary>
    AnswerDirectly,

    /// <summary>Mutating intents go through plan → approval → execute → report.</summary>
    PlanAndApprove,

    /// <summary>Slots missing or ambiguous — answer with a clarification card plus the quick wheel.</summary>
    Clarify,

    /// <summary>Intent is out of scope or unsafe to act on; refuse with a reason.</summary>
    Reject,
}

/// <summary>Routing decision for one parsed intent (K-5 draft).</summary>
/// <param name="Kind">Decision class.</param>
/// <param name="Note">Journal-facing remark (machine-readable, never user copy).</param>
public sealed record IntentRoute(RouteKind Kind, string? Note = null);
