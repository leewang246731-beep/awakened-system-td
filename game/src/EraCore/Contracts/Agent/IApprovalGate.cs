// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 4 of the five-stage pipeline (K-6 draft, C-14): decides
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
