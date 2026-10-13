// T2: pure contract — K-5 agent pipeline and card ports, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 4 of the five-stage pipeline (K-5 draft, C-14): decides
/// allow/ask/deny per the F.4 permission matrix. Rules above any
/// implementation: dangerous verbs (SellTower's <c>confirm_sell</c>) are
/// never auto-allowed; level-level auto-approve whitelists never cover
/// danger bits; trust tiers change wording only, never capability (F.4).
/// </summary>
public interface IApprovalGate
{
    /// <summary>Decides whether a plan may run, must ask, or is refused.</summary>
    ApprovalDecision Decide(Plan plan);
}
