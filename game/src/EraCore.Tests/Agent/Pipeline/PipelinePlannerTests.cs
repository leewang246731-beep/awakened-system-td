using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Sim;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-065] 第 2/3 段单元面（T-053）：意图路由映射 + 目标级模板计划
// （BuildPlan 意图产建造步、HoldRegion 意图产 LookAround+BuildPlan+Report
// 序列）+ 环扫确定性 + 数量/设闹钳制。
public class PipelinePlannerTests
{
    private static readonly CellCoord Anchor = new(12, 12);

    private static PipelinePlanner Planner()
        => new(
            new SimulationActionView(new Simulation(PipelineHarness.Config(), new SimRng(PipelineHarness.Seed), PipelineHarness.Seed), PipelineHarness.Config()),
            new Dictionary<string, CellCoord> { ["east_gate"] = Anchor },
            fallbackAnchor: new CellCoord(8, 20),
            defaultDefenseTowerId: "era1:tower-wood-arrow",
            eraOrdinal: () => 2);

    private static Intent Intent(IntentKind kind, string text, params (string Key, string Value)[] slots)
        => new(kind, text, slots.ToDictionary(slot => slot.Key, slot => slot.Value));

    // ---- 路由（stage 2） ------------------------------------------------------

    public static TheoryData<IntentKind, RouteKind, string> RouteRows => new()
    {
        // legal：只读意图直答
        { IntentKind.QueryThreat, RouteKind.AnswerDirectly, "read-only:threat" },
        { IntentKind.QueryStockpile, RouteKind.AnswerDirectly, "read-only:stockpile" },
        // legal：其余十个 SP-2 意图全部走计划+审批（T-068 全量路由）
        { IntentKind.Build, RouteKind.PlanAndApprove, "template:Build" },
        { IntentKind.HoldRegion, RouteKind.PlanAndApprove, "template:HoldRegion" },
        { IntentKind.SetAlarm, RouteKind.PlanAndApprove, "template:SetAlarm" },
        { IntentKind.Upgrade, RouteKind.PlanAndApprove, "template:Upgrade" },
        { IntentKind.Sell, RouteKind.PlanAndApprove, "template:Sell" },
        { IntentKind.FocusFire, RouteKind.PlanAndApprove, "template:FocusFire" },
        { IntentKind.ChangeFocusMode, RouteKind.PlanAndApprove, "template:ChangeFocusMode" },
        { IntentKind.RerouteLogistics, RouteKind.PlanAndApprove, "template:RerouteLogistics" },
        { IntentKind.Patrol, RouteKind.PlanAndApprove, "template:Patrol" },
        { IntentKind.ScrollControl, RouteKind.PlanAndApprove, "template:ScrollControl" },
    };

    [Theory] // legal + illegal
    [MemberData(nameof(RouteRows))]
    public void Route_maps_the_t053_subset(IntentKind kind, RouteKind expected, string expectedNote)
    {
        var route = new IntentRouter().Route(Intent(kind, "x"));

        Assert.Equal(expected, route.Kind);
        Assert.Equal(expectedNote, route.Note);
    }

    [Fact] // legal：T-068 全量——SP-2 十二意图全部有路由（Reject 不再出现）
    public void Route_covers_the_full_sp2_table()
    {
        var router = new IntentRouter();
        foreach (IntentKind kind in Enum.GetValues<IntentKind>())
        {
            IntentRoute route = router.Route(Intent(kind, "x"));
            Assert.NotEqual(RouteKind.Reject, route.Kind);
            Assert.NotEqual(RouteKind.Clarify, route.Kind);
        }
    }

    [Fact] // illegal：null 意图被拒（契约防御）
    public void Route_rejects_null_intent()
    {
        Assert.Throws<ArgumentNullException>(() => new IntentRouter().Route(null!));
    }

    // ---- 计划模板（stage 3） --------------------------------------------------

    [Fact] // legal：BuildPlan 意图产建造步——侦察→建造→汇报（F-169 闭环形状）
    public void Build_intent_plans_lookaround_buildplan_report()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.Build, "在东门建两座木箭塔", ("tower", "era1:tower-wood-arrow"), ("qty", "2"), ("where", "east_gate")));

        Assert.Equal(
            new[] { SystemActionKind.LookAround.ToString(), "BuildPlan", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        Assert.Equal(2 + 0 + 0, planned.Plan.EstimatedPointCost); // 建造 2 点（§11.4 计价），侦察/汇报免费
        Assert.Equal(60.0, planned.EstimatedResources); // 2 × 30（towers.json cost_total）
        Assert.Equal(2, planned.Plan.Steps[1].PointCost);
    }

    [Fact] // legal：HoldRegion 目标级模板首次落地——LookAround+BuildPlan+Report 序列
    public void HoldRegion_intent_expands_the_goal_template()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.HoldRegion, "守住东门", ("target", "east_gate")));

        Assert.Equal(
            new[] { "LookAround", "BuildPlan", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        // 目标级模板用默认防御塔（无 tower 槽位——守住 X 只给目标）
        Assert.Equal("era1:tower-wood-arrow", TowerIdOf(planned.Plan.Steps[1].ActionJson));
        Assert.Equal(2, CellsOf(planned.Plan.Steps[1].ActionJson).Count); // 模板数量=2
    }

    [Fact] // legal：T-068 全量——储备查询走 QueryWorld{key:resource}（SP-2 #8）
    public void Read_only_intents_plan_recon_query_report()
    {
        PlannedOrder threat = Planner().Prepare(Intent(IntentKind.QueryThreat, "敌情汇报", ("scope", "current")));
        PlannedOrder stock = Planner().Prepare(Intent(IntentKind.QueryStockpile, "我们还剩多少木材", ("item", "era1:res-wood")));

        Assert.Equal(new[] { "LookAround", "ReportToPlayer" }, threat.Plan.Steps.Select(s => s.Action.ToString()).ToArray());
        Assert.Equal(
            new[] { "LookAround", "QueryWorld", "ReportToPlayer" },
            stock.Plan.Steps.Select(s => s.Action.ToString()).ToArray());
        Assert.Equal(0, threat.Plan.EstimatedPointCost);
        Assert.Equal(0, stock.Plan.EstimatedPointCost);
        Assert.Equal(0.0, threat.EstimatedResources);
    }

    [Fact] // boundary：数量被时代上限钳制（E2 cells ≤ 4+2×2=8）并落风险红标
    public void Quantity_is_capped_by_the_era_cell_cap()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.Build, "建十座箭塔", ("tower", "era1:tower-wood-arrow"), ("qty", "10")));

        Assert.Equal(8, CellsOf(planned.Plan.Steps[1].ActionJson).Count);
        Assert.Contains(planned.Plan.Risks, risk => risk.StartsWith("qty-capped:10->8", StringComparison.Ordinal));
    }

    [Fact] // legal：未知 where 槽回落到 fallback 锚（不猜区域）
    public void Unknown_where_falls_back_to_the_configured_anchor()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.Build, "建一座箭塔", ("tower", "era1:tower-wood-arrow"), ("qty", "1"), ("where", "nowhere")));

        (int x, int y) first = CellsOf(planned.Plan.Steps[1].ActionJson)[0];
        Assert.Equal((8, 20), first);
    }

    [Fact] // legal：环扫确定性——同输入两次构造的 cells 完全一致（行列主序）
    public void Ring_scan_is_deterministic()
    {
        var left = Planner().Prepare(Intent(IntentKind.Build, "建三座箭塔", ("tower", "era1:tower-wood-arrow"), ("qty", "3"), ("where", "east_gate")));
        var right = Planner().Prepare(Intent(IntentKind.Build, "建三座箭塔", ("tower", "era1:tower-wood-arrow"), ("qty", "3"), ("where", "east_gate")));

        Assert.Equal(CellsOf(left.Plan.Steps[1].ActionJson), CellsOf(right.Plan.Steps[1].ActionJson));
        Assert.Equal(
            new List<(int, int)> { (12, 12), (11, 11), (12, 11) },
            CellsOf(left.Plan.Steps[1].ActionJson));
    }

    [Fact] // legal：SetAlarm 槽位进动作 JSON（秒数被 K-3 边界钳制）
    public void SetAlarm_intent_carries_clamped_seconds()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.SetAlarm, "十秒后叫我", ("after", "10"), ("reason", "敌袭")));

        string alarmJson = planned.Plan.Steps[0].ActionJson;
        Assert.Contains("\"after_seconds\":30", alarmJson, StringComparison.Ordinal); // 钳到下界 30（K-3）
        Assert.Contains("\"action\":\"SetAlarm\"", alarmJson, StringComparison.Ordinal);
        Assert.Contains("after-clamped:10->30", string.Join(";", planned.Plan.Risks), StringComparison.Ordinal);
    }

    [Fact] // legal：T-068 全量——升级/出售/集火等十个新模板可用（对照 IntentActionMap）
    public void Every_sp2_intent_prepares_a_plan_matching_the_map()
    {
        var planner = Planner();
        foreach (IntentActionRow row in IntentActionMap.Rows)
        {
            Intent intent = row.Intent switch
            {
                IntentKind.Upgrade => Intent(row.Intent, "升级", ("target", "era1:tower-wood-arrow"), ("to_tier", "2")),
                IntentKind.Sell => Intent(row.Intent, "出售", ("target", "era1:tower-wood-arrow")),
                IntentKind.FocusFire => Intent(row.Intent, "集火", ("target", "any")),
                IntentKind.ChangeFocusMode => Intent(row.Intent, "改模式", ("mode", "strong"), ("target_towers", "all")),
                IntentKind.RerouteLogistics => Intent(row.Intent, "改道", ("item", "era1:res-wood"), ("to", "east_gate")),
                IntentKind.Patrol => Intent(row.Intent, "巡逻", ("lane", "lane_west")),
                IntentKind.ScrollControl => Intent(row.Intent, "开卷轴", ("scroll", "era2:scroll-fire-oil")),
                IntentKind.SetAlarm => Intent(row.Intent, "设闹", ("after", "90"), ("reason", "敌袭")),
                _ => Intent(row.Intent, "默认"),
            };
            PlannedOrder planned = planner.Prepare(intent);
            string[] verbs = planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray();
            Assert.Equal(row.Actions[0].ToString(), verbs[0]);
            Assert.Equal("ReportToPlayer", verbs[^1]);
        }
    }

    [Fact] // illegal：构造参数防御（空默认塔 id）
    public void Constructor_rejects_an_empty_default_tower_id()
    {
        Assert.Throws<ArgumentException>(() => new PipelinePlanner(
            new SimulationActionView(
                new Simulation(PipelineHarness.Config(), new SimRng(PipelineHarness.Seed), PipelineHarness.Seed),
                PipelineHarness.Config()),
            new Dictionary<string, CellCoord>(),
            new CellCoord(8, 20),
            defaultDefenseTowerId: " ",
            eraOrdinal: () => 2));
    }

    private static string TowerIdOf(string buildPlanJson) =>
        System.Text.Json.JsonSerializer.Deserialize<BuildPlanActionJson>(
            buildPlanJson, PipelinePlannerJsonContext.Default.BuildPlanActionJson)!.TowerId;

    private static List<(int X, int Y)> CellsOf(string buildPlanJson) =>
        System.Text.Json.JsonSerializer.Deserialize<BuildPlanActionJson>(
            buildPlanJson, PipelinePlannerJsonContext.Default.BuildPlanActionJson)!.Cells
            .Select(cell => (cell.X, cell.Y))
            .ToList();
}
