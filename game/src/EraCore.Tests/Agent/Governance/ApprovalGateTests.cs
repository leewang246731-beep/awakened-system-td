using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-063] Approval gate (T-050): the F.4 permission matrix —
// default tiers of all 19 verbs, level-scoped auto-approve memory
// (本关卡内自动允许, serializable), danger bits that NEVER auto-allow
// (SellTower.confirm_sell, BuildPlan.confirm_overwrite), capability-lock
// denials below the unlock era, plan aggregation, and the deny catalog.

/// <summary>[C-14][F-063] Permission tiers, danger bits, auto-approve, capability locks.</summary>
public class ApprovalGateTests
{
    private static readonly Intent HoldIntent = new(
        IntentKind.HoldRegion, "守住北线", new Dictionary<string, string>());

    private static Plan PlanOf(params PlanStep[] steps) => new(HoldIntent, steps, 0, Array.Empty<string>());

    private static PlanStep Step(SystemActionKind verb, string json = "{}") => new(verb, json, 0);

    // ---- 组 1：默认档位（19 动作 × E2，无勾选）-------------------------------

    [Theory] // legal — recon + management default to Allow at era 2 (§11.4)
    [InlineData(SystemActionKind.LookAround)]
    [InlineData(SystemActionKind.InspectTile)]
    [InlineData(SystemActionKind.QueryWorld)]
    [InlineData(SystemActionKind.AskPlayer)]
    [InlineData(SystemActionKind.ReportToPlayer)]
    [InlineData(SystemActionKind.TodoBoard)]
    [InlineData(SystemActionKind.SpawnScout)]
    [InlineData(SystemActionKind.SetAlarm)]
    [InlineData(SystemActionKind.StopTask)]
    public void Recon_and_management_default_to_allow(SystemActionKind verb)
    {
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(verb, "{}").Kind);
    }

    [Theory] // legal — construction/logistics/combat default to Ask at era 2 (§11.4)
    [InlineData(SystemActionKind.BuildPlan)]
    [InlineData(SystemActionKind.UpgradeTower)]
    [InlineData(SystemActionKind.SetRally)]
    [InlineData(SystemActionKind.RerouteLogistics)]
    [InlineData(SystemActionKind.SetStockpile)]
    [InlineData(SystemActionKind.FocusFire)]
    [InlineData(SystemActionKind.SetTowerMode)]
    [InlineData(SystemActionKind.TriggerScroll)]
    [InlineData(SystemActionKind.HeroOrder)]
    public void Construction_logistics_combat_default_to_ask(SystemActionKind verb)
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var decision = gate.DecideStep(verb, "{}");
        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Equal($"tier_ask:{verb}", decision.Reason);
    }

    // ---- 组 2：危险位负向用例 ------------------------------------------------

    [Fact] // legal — SellTower with the explicit confirmation bit asks (never silently runs)
    public void Sell_tower_with_confirmation_asks()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var decision = gate.DecideStep(SystemActionKind.SellTower, """{"target_id":"t1","confirm_sell":true}""");
        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Contains("danger_bit_never_auto", decision.Reason);
    }

    [Theory] // illegal — SellTower without an explicit true confirmation is DENIED outright
    [InlineData("""{"target_id":"t1"}""")]                 // bit absent
    [InlineData("""{"target_id":"t1","confirm_sell":false}""")] // bit explicitly false
    public void Sell_tower_without_confirmation_is_denied(string actionJson)
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var decision = gate.DecideStep(SystemActionKind.SellTower, actionJson);
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.Equal("danger_bit_required:confirm_sell", decision.Reason);
    }

    [Theory] // illegal — an unreadable danger-bit payload fails closed (deny, never guess)
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    public void Unreadable_danger_payload_fails_closed(string actionJson)
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var decision = gate.DecideStep(SystemActionKind.SellTower, actionJson);
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.Contains("danger_bit_unreadable", decision.Reason);
    }

    [Fact] // negative — the star case: BuildPlan auto-approved for this level, yet confirm_overwrite STILL pops the card (F.4 危险位永不自动允许)
    public void Confirm_overwrite_still_asks_even_with_auto_approve_on()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);

        // same verb without the demolition bit runs silently
        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(
            SystemActionKind.BuildPlan, """{"tower_id":"era1:watchtower","cells":[{"x":1,"y":1}]}""").Kind);

        // the demolition bit forces the card even under the whitelist
        var decision = gate.DecideStep(
            SystemActionKind.BuildPlan, """{"tower_id":"era1:watchtower","cells":[{"x":1,"y":1}],"confirm_overwrite":true}""");
        Assert.Equal(ApprovalDecisionKind.Ask, decision.Kind);
        Assert.Contains("danger_bit_never_auto", decision.Reason);
    }

    [Fact] // negative — SellTower never enters any auto-approve whitelist, not even by direct API call
    public void Sell_tower_can_not_be_auto_approved()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentException>(
            () => gate.SetLevelAutoApprove(SystemActionKind.SellTower, enabled: true));
        Assert.False(gate.IsAutoApproved(SystemActionKind.SellTower));
    }

    // ---- 组 3：关卡级自动允许记忆 ---------------------------------------------

    [Fact] // legal — whitelisting an Ask-tier verb turns its asks into allows
    public void Level_auto_approve_turns_ask_into_allow()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);
        gate.SetLevelAutoApprove(SystemActionKind.FocusFire, enabled: true);

        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(SystemActionKind.BuildPlan, "{}").Kind);
        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(SystemActionKind.FocusFire, "{}").Kind);
    }

    [Fact] // legal — clearing the flag restores Ask; non-flagged Ask verbs are unaffected
    public void Clearing_auto_approve_restores_ask()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: false);
        gate.SetLevelAutoApprove(SystemActionKind.FocusFire, enabled: true);

        Assert.Equal(ApprovalDecisionKind.Ask, gate.DecideStep(SystemActionKind.BuildPlan, "{}").Kind);
        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(SystemActionKind.FocusFire, "{}").Kind);
    }

    [Fact] // illegal — whitelisting an already-Allow verb is refused (the checkbox only exists for Ask verbs)
    public void Auto_approving_an_allow_tier_verb_is_refused()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentException>(
            () => gate.SetLevelAutoApprove(SystemActionKind.LookAround, enabled: true));
    }

    // ---- 组 4：能力锁 ---------------------------------------------------------

    [Theory] // illegal — verbs above the current era are denied as locked (K-5 Deny cause; G.2 unlock column)
    [InlineData(1, SystemActionKind.SpawnScout)]
    [InlineData(1, SystemActionKind.BuildPlan)]
    [InlineData(1, SystemActionKind.TodoBoard)]
    [InlineData(1, SystemActionKind.HeroOrder)]
    [InlineData(1, SystemActionKind.SetStockpile)]
    public void Locked_verbs_are_denied_until_their_era(int era, SystemActionKind verb)
    {
        var gate = new ApprovalGate(eraIndex: era);
        var decision = gate.DecideStep(verb, "{}");
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.Equal($"capability_locked:{verb}(era{era}<2)", decision.Reason);
    }

    [Theory] // legal — E1-available verbs pass an era-1 gate
    [InlineData(SystemActionKind.LookAround)]
    [InlineData(SystemActionKind.InspectTile)]
    [InlineData(SystemActionKind.QueryWorld)]
    [InlineData(SystemActionKind.AskPlayer)]
    [InlineData(SystemActionKind.ReportToPlayer)]
    [InlineData(SystemActionKind.SetAlarm)]
    public void E1_verbs_pass_an_era_one_gate(SystemActionKind verb)
    {
        var gate = new ApprovalGate(eraIndex: 1);
        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(verb, "{}").Kind);
    }

    [Fact] // legal — era transition re-anchors the lock floor and keeps the level whitelist
    public void Era_transition_keeps_the_level_whitelist()
    {
        var gate = new ApprovalGate(eraIndex: 1);
        gate.SetEra(2); // 关卡内时代注入
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);

        Assert.Equal(ApprovalDecisionKind.Allow, gate.DecideStep(SystemActionKind.BuildPlan, "{}").Kind);
    }

    // ---- 组 5：整案聚合（K-5 Decide(Plan)）------------------------------------

    [Fact] // legal — worst step wins: allow+ask → ask
    public void Plan_with_one_ask_step_asks()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var plan = PlanOf(Step(SystemActionKind.LookAround), Step(SystemActionKind.BuildPlan));
        Assert.Equal(ApprovalDecisionKind.Ask, gate.Decide(plan).Kind);
    }

    [Fact] // legal — all-allow plan runs
    public void All_allow_plan_is_allowed()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var plan = PlanOf(Step(SystemActionKind.LookAround), Step(SystemActionKind.SetAlarm));
        Assert.Equal(ApprovalDecisionKind.Allow, gate.Decide(plan).Kind);
    }

    [Fact] // illegal — one denying step refuses the whole plan (first deny wins, even after allow steps)
    public void One_deny_step_refuses_the_whole_plan()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        var plan = PlanOf(
            Step(SystemActionKind.LookAround),
            Step(SystemActionKind.SellTower, """{"target_id":"t1"}"""), // missing confirm_sell
            Step(SystemActionKind.BuildPlan));
        var decision = gate.Decide(plan);
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.Equal("danger_bit_required:confirm_sell", decision.Reason);
    }

    [Fact] // legal — an empty plan decides Allow (nothing to ask about)
    public void Empty_plan_is_allowed()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Equal(ApprovalDecisionKind.Allow, gate.Decide(PlanOf()).Kind);
    }

    [Fact] // illegal — null plan is a contract violation
    public void Null_plan_is_refused()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentNullException>(() => gate.Decide(null!));
    }

    // ---- 组 6：会话序列化（关卡级，可序列化）-----------------------------------

    [Fact] // legal — the level whitelist round-trips through JSON and lands in a fresh gate
    public void Session_round_trips_through_json()
    {
        var gate = new ApprovalGate(eraIndex: 2);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);
        gate.SetLevelAutoApprove(SystemActionKind.FocusFire, enabled: true);

        string json = gate.ExportSession().Save();

        var restored = new ApprovalGate(eraIndex: 2);
        restored.RestoreSession(
            System.Text.Json.JsonSerializer.Deserialize(json, GovernanceJsonContext.Default.ApprovalSessionState)!);
        Assert.True(restored.IsAutoApproved(SystemActionKind.BuildPlan));
        Assert.True(restored.IsAutoApproved(SystemActionKind.FocusFire));
        Assert.Equal(ApprovalDecisionKind.Allow, restored.DecideStep(SystemActionKind.BuildPlan, "{}").Kind);
    }

    [Fact] // illegal — a hostile save smuggling SellTower into the whitelist is rejected on load
    public void Session_restore_rejects_never_auto_verbs()
    {
        var hostile = new ApprovalSessionState(2, new[] { "SellTower" });
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentException>(() => gate.RestoreSession(hostile));
    }

    [Fact] // illegal — a corrupt save with an unknown verb name is rejected on load
    public void Session_restore_rejects_unknown_verbs()
    {
        var corrupt = new ApprovalSessionState(2, new[] { "NukeEverything" });
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentException>(() => gate.RestoreSession(corrupt));
    }

    [Fact] // illegal — an out-of-range era in the save is rejected on load
    public void Session_restore_rejects_out_of_range_era()
    {
        var corrupt = new ApprovalSessionState(9, Array.Empty<string>());
        var gate = new ApprovalGate(eraIndex: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.RestoreSession(corrupt));
    }
}
