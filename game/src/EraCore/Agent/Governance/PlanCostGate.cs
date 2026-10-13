using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Governance;

/// <summary>
/// Verdict of the plan-level resource aggregation check (T-050, C-14):
/// whether a plan's total resource consumption crosses 30% of the
/// current stock and therefore must pop the plan card even when every
 /// verb in the plan is auto-allowed (v3.2 §11.4 / F.4 注入防护④).
/// </summary>
/// <param name="TotalPlanCost">Plan resource consumption aggregated over all resource kinds (Σ cost).</param>
/// <param name="TotalStock">Current stock aggregated over the same kinds (Σ stock).</param>
/// <param name="Threshold">30% of <paramref name="TotalStock"/> (= stock × 3 / 10).</param>
/// <param name="ForceAsk">true when TotalPlanCost strictly exceeds the threshold (弹卡).</param>
public sealed record PlanCostVerdict(
    double TotalPlanCost,
    double TotalStock,
    double Threshold,
    bool ForceAsk);

/// <summary>
/// The plan-level resource aggregation gate (T-050, C-14; the 30% half
/// of the two-threshold budget chokepoint, v3.2 §11.4 / F.4 ④: 单计划总
/// 资源消耗 &gt; 当前库存 30% 强制弹卡). Independent of the per-wave 40%
/// system-point cap (the other half, <see cref="SystemPointLedger"/>):
/// both thresholds live in the C-14 governance seam and are enforced
/// together by the pipeline (T-053) — this class never bills, it only
/// escalates the approval verdict.
/// </summary>
public static class PlanCostGate
{
    /// <summary>Threshold numerator: 30% = × 3 / 10 (§11.4).</summary>
    public const int ThresholdNumerator = 3;

    /// <summary>Threshold denominator: 30% = × 3 / 10 (§11.4).</summary>
    public const int ThresholdDenominator = 10;

    /// <summary>
    /// Aggregates a plan's per-kind resource costs against the current
    /// per-kind stock. Comparison is strictly greater: a plan at exactly
    /// 30% does not force a card (30% 线上弹卡/线下放行，等于线=线下).
    /// Negative inputs are refused (a cost/stock map never carries
    /// negative amounts).
    /// </summary>
    /// <param name="planResourceCosts">Per-resource-kind cost of one plan (C-13 computes it from steps).</param>
    /// <param name="currentStock">Per-resource-kind current stock (same key domain).</param>
    /// <exception cref="ArgumentOutOfRangeException">Any value is negative.</exception>
    public static PlanCostVerdict Evaluate(
        IReadOnlyDictionary<ResourceKey, double> planResourceCosts,
        IReadOnlyDictionary<ResourceKey, double> currentStock)
    {
        ArgumentNullException.ThrowIfNull(planResourceCosts);
        ArgumentNullException.ThrowIfNull(currentStock);

        double totalCost = 0;
        foreach (var pair in planResourceCosts)
        {
            if (pair.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(planResourceCosts), pair.Value, $"Plan cost for '{pair.Key.Value}' is negative.");
            }

            totalCost += pair.Value;
        }

        double totalStock = 0;
        foreach (var pair in currentStock)
        {
            if (pair.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(currentStock), pair.Value, $"Stock for '{pair.Key.Value}' is negative.");
            }

            totalStock += pair.Value;
        }

        double threshold = totalStock * ThresholdNumerator / ThresholdDenominator;
        return new PlanCostVerdict(totalCost, totalStock, threshold, totalCost > threshold);
    }

    /// <summary>
    /// Composes this gate with a stage-4 verdict (the seam T-053 calls):
    /// a forced card ESCALATES an Allow into an Ask and leaves Ask/Deny
    /// untouched (即使动作类已自动允许，超 30% 仍弹卡; a denial can never
    /// be softened by this gate).
    /// </summary>
    public static ApprovalDecision Compose(ApprovalDecision gateDecision, PlanCostVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(gateDecision);
        ArgumentNullException.ThrowIfNull(verdict);

        if (verdict.ForceAsk && gateDecision.Kind == ApprovalDecisionKind.Allow)
        {
            return new ApprovalDecision(
                ApprovalDecisionKind.Ask, "plan_cost_over_30pct");
        }

        return gateDecision;
    }
}
