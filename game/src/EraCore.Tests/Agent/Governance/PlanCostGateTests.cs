using EraCore.Agent.Governance;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-064] Plan-cost gate (T-050): the 30% half of the two-threshold
// budget chokepoint (v3.2 §11.4 / F.4 ④) — a plan whose aggregated
// resource consumption exceeds 30% of the current stock forces the plan
// card even when every verb is auto-allowed.

/// <summary>[C-14][F-064] Plan-level 30% resource aggregation line.</summary>
public class PlanCostGateTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly ResourceKey Stone = new("era1:res-stone");
    private static readonly ResourceKey Food = new("era1:res-food");

    private static IReadOnlyDictionary<ResourceKey, double> Costs(
        params (ResourceKey Key, double Amount)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Amount);

    private static IReadOnlyDictionary<ResourceKey, double> Stock(
        params (ResourceKey Key, double Amount)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Amount);

    [Theory] // legal — below the 30% line: no forced card
    [InlineData(0.0, 100.0)]      // empty plan
    [InlineData(29.9, 100.0)]     // just under
    [InlineData(3.0, 100.0)]
    public void Below_the_line_does_not_force_ask(double cost, double stock)
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, cost)), Stock((Wood, stock)));
        Assert.False(verdict.ForceAsk);
        Assert.Equal(cost, verdict.TotalPlanCost, 9);
        Assert.Equal(stock, verdict.TotalStock, 9);
    }

    [Fact] // boundary — exactly at 30% is still below (strictly greater forces the card)
    public void Exactly_at_the_line_does_not_force_ask()
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, 30.0)), Stock((Wood, 100.0)));
        Assert.Equal(30.0, verdict.Threshold, 9);
        Assert.False(verdict.ForceAsk);
    }

    [Fact] // legal — one over the line forces the card
    public void One_over_the_line_forces_ask()
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, 30.1)), Stock((Wood, 100.0)));
        Assert.True(verdict.ForceAsk);
    }

    [Fact] // legal — aggregation crosses kinds: 10 wood + 25 stone = 35 > 30% of 100
    public void Aggregation_spans_resource_kinds()
    {
        var verdict = PlanCostGate.Evaluate(
            Costs((Wood, 10.0), (Stone, 25.0)),
            Stock((Wood, 60.0), (Stone, 40.0)));
        Assert.Equal(35.0, verdict.TotalPlanCost, 9);
        Assert.Equal(100.0, verdict.TotalStock, 9);
        Assert.True(verdict.ForceAsk);
    }

    [Fact] // boundary — aggregation exactly at the line stays silent
    public void Aggregation_exactly_at_the_line_stays_silent()
    {
        var verdict = PlanCostGate.Evaluate(
            Costs((Wood, 10.0), (Stone, 20.0)),
            Stock((Wood, 60.0), (Stone, 40.0)));
        Assert.False(verdict.ForceAsk);
    }

    [Fact] // illegal — any cost against an empty stock forces the card (threshold 0)
    public void Any_cost_against_empty_stock_forces_ask()
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, 1.0)), Stock((Food, 0.0)));
        Assert.Equal(0.0, verdict.Threshold, 9);
        Assert.True(verdict.ForceAsk);
    }

    [Theory] // illegal — negative amounts are refused on both sides (no negative billing/stock)
    [InlineData(-1.0, 100.0)]
    [InlineData(10.0, -5.0)]
    public void Negative_amounts_are_refused(double cost, double stock)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanCostGate.Evaluate(Costs((Wood, cost)), Stock((Wood, stock))));
    }

    [Fact] // illegal — null maps are refused
    public void Null_maps_are_refused()
    {
        Assert.Throws<ArgumentNullException>(
            () => PlanCostGate.Evaluate(null!, Stock((Wood, 10.0))));
        Assert.Throws<ArgumentNullException>(
            () => PlanCostGate.Evaluate(Costs((Wood, 1.0)), null!));
    }

    // ---- 与 K-5 verdict 的组合（即使动作类已自动允许）------------------------

    [Fact] // negative — the composition case: an auto-allowed decision is ESCALATED to Ask by the 30% line
    public void Compose_escalates_auto_allow_to_ask()
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, 50.0)), Stock((Wood, 100.0)));
        Assert.True(verdict.ForceAsk);

        var escalated = PlanCostGate.Compose(
            new ApprovalDecision(ApprovalDecisionKind.Allow), verdict);
        Assert.Equal(ApprovalDecisionKind.Ask, escalated.Kind);
        Assert.Equal("plan_cost_over_30pct", escalated.Reason);
    }

    [Fact] // legal — an under-the-line verdict leaves Allow untouched
    public void Compose_leaves_allow_untouched_below_the_line()
    {
        var verdict = PlanCostGate.Evaluate(Costs((Wood, 10.0)), Stock((Wood, 100.0)));
        var composed = PlanCostGate.Compose(
            new ApprovalDecision(ApprovalDecisionKind.Allow), verdict);
        Assert.Equal(ApprovalDecisionKind.Allow, composed.Kind);
    }

    [Fact] // legal — Ask stays Ask; a forced card never softens an existing verdict
    public void Compose_never_softens_ask_or_deny()
    {
        var forceAsk = PlanCostGate.Evaluate(Costs((Wood, 50.0)), Stock((Wood, 100.0)));
        Assert.Equal(
            ApprovalDecisionKind.Ask,
            PlanCostGate.Compose(new ApprovalDecision(ApprovalDecisionKind.Ask, "tier_ask:BuildPlan"), forceAsk).Kind);
        Assert.Equal(
            ApprovalDecisionKind.Deny,
            PlanCostGate.Compose(new ApprovalDecision(ApprovalDecisionKind.Deny, "danger_bit_required:confirm_sell"), forceAsk).Kind);
    }
}
