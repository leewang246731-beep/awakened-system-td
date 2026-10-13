// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Actions;
using EraCore.Contracts.Intents;

namespace EraCore.Contracts.Agent;

/// <summary>
/// One executable step of a plan (K-6 draft).
/// </summary>
/// <param name="Action">Which of the 19 closed verbs this step runs.</param>
/// <param name="ActionJson">Step parameters as action JSON — must pass ISchemaValidator before execution.</param>
/// <param name="PointCost">System points this step charges (per C-03 registry metadata).</param>
public sealed record PlanStep(
    SystemActionKind Action,
    string ActionJson,
    int PointCost);

/// <summary>
/// A whole-course plan (K-6 draft): steps, total cost and risk highlights.
/// Feeds the approval gate and the player-facing plan card (K-5, rolling
/// freeze). Atomic pre-check: when budget or resources cannot cover ALL
/// steps, the whole plan is refused — partial execution is forbidden
/// (C-13).
/// </summary>
/// <param name="SourceIntent">The intent this plan fulfils.</param>
/// <param name="Steps">Ordered steps; executed in order with ≤ 2 self-correction retries per step.</param>
/// <param name="EstimatedPointCost">Sum of step costs; feeds the 40%-of-stock-per-wave guard (N4).</param>
/// <param name="Risks">Risk highlights shown red on the plan card (danger bits such as confirm_sell).</param>
public sealed record Plan(
    Intent SourceIntent,
    IReadOnlyList<PlanStep> Steps,
    int EstimatedPointCost,
    IReadOnlyList<string> Risks);
