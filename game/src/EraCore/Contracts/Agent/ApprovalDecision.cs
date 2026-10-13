// T2: pure contract — K-5 agent pipeline and card ports, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>Approval gate verdicts (K-5 draft; F.4 permission tiers).</summary>
public enum ApprovalDecisionKind
{
    /// <summary>Execute without asking (recon/free verbs; level-level whitelists).</summary>
    Allow,

    /// <summary>Show the plan card and wait for the player.</summary>
    Ask,

    /// <summary>Refuse: danger bit not whitelisted, locked capability, or injection guard hit.</summary>
    Deny,
}

/// <summary>One gate verdict (K-5 draft).</summary>
/// <param name="Kind">allow / ask / deny.</param>
/// <param name="Reason">Journal-facing reason (machine-readable, never user copy).</param>
public sealed record ApprovalDecision(ApprovalDecisionKind Kind, string? Reason = null);
