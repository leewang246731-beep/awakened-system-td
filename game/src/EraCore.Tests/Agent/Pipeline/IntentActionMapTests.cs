using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Sim;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [F-065][F-066][F-068] T-068 12 意图→19 动作映射表（SP-2 对齐）：
// 行完备性（12 行=SP-2 全量）、意图可达动词覆盖（14/19）、五个宿主驱动动作的
// 归因表、七类新模板的槽位→实参解析（塔/敌/建筑指认，解析不出落 risk 不猜）。
public class IntentActionMapTests
{
    private static readonly CellCoord Anchor = new(12, 12);

    private static PipelinePlanner Planner(IActionWorldView? viewOverride = null) => new(
        viewOverride ?? new SimulationActionView(
            new Simulation(PipelineHarness.Config(), new SimRng(PipelineHarness.Seed), PipelineHarness.Seed),
            PipelineHarness.Config()),
        new Dictionary<string, CellCoord> { ["east_gate"] = Anchor, ["south_gate"] = new(4, 8) },
        fallbackAnchor: new CellCoord(8, 20),
        defaultDefenseTowerId: "era1:tower-wood-arrow",
        eraOrdinal: () => 2);

    private static Intent Intent(IntentKind kind, string text, params (string Key, string Value)[] slots)
        => new(kind, text, slots.ToDictionary(slot => slot.Key, slot => slot.Value));

    [Fact] // legal：12 行=SP-2 全量意图，每行恰一
    public void The_map_covers_the_full_sp2_table_exactly_once()
    {
        Assert.Equal(12, IntentActionMap.Rows.Count);
        Assert.Equal(
            Enum.GetValues<IntentKind>().OrderBy(kind => kind),
            IntentActionMap.Rows.Select(row => row.Intent).OrderBy(kind => kind));
    }

    [Fact] // legal：意图可达动词=14/19，宿主驱动五动作（SetRally/SetStockpile/AskPlayer/TodoBoard/HeroOrder）
    public void Intent_reachable_verbs_are_exactly_the_fourteen_with_orders()
    {
        var expected = new HashSet<SystemActionKind>
        {
            SystemActionKind.LookAround,
            SystemActionKind.BuildPlan,
            SystemActionKind.ReportToPlayer,
            SystemActionKind.UpgradeTower,
            SystemActionKind.SellTower,
            SystemActionKind.FocusFire,
            SystemActionKind.SetTowerMode,
            SystemActionKind.RerouteLogistics,
            SystemActionKind.QueryWorld,
            SystemActionKind.InspectTile,
            SystemActionKind.SetAlarm,
            SystemActionKind.SpawnScout,
            SystemActionKind.TriggerScroll,
            SystemActionKind.StopTask,
        };

        Assert.Equal(expected, IntentActionMap.IntentReachableVerbs);
        Assert.Equal(19, Enum.GetValues<SystemActionKind>().Length); // 14+5=19 全闭包
    }

    [Fact] // legal：归因表覆盖 19 动作全集（宿主驱动入口的展示归因）
    public void Attribution_covers_every_closed_verb()
    {
        foreach (SystemActionKind verb in Enum.GetValues<SystemActionKind>())
        {
            IntentKind attribution = IntentActionMap.AttributionIntent(verb);
            Assert.True(Enum.IsDefined(attribution));
        }
    }

    // ------------------------------------------------ 七类新模板的槽位→实参 ------------------

    [Fact] // legal：升级模板——塔指认解析为实体 id（ord:N 次序）
    public void Upgrade_template_resolves_the_tower_designation()
    {
        var (_, view) = RegistryFixture.Create(runMilestone: true);
        var planner = new PipelinePlanner(
            view,
            new Dictionary<string, CellCoord> { ["east_gate"] = Anchor },
            fallbackAnchor: new CellCoord(8, 20),
            defaultDefenseTowerId: "era1:tower-wood-arrow",
            eraOrdinal: () => 2);

        PlannedOrder planned = planner.Prepare(
            Intent(IntentKind.Upgrade, "把2号塔升到二级", ("target", "ord:2"), ("to_tier", "2")));

        Assert.Equal(
            new[] { "LookAround", "UpgradeTower", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        Assert.Contains("\"target_id\":7", planned.Plan.Steps[1].ActionJson, StringComparison.Ordinal); // 里程碑塔2=id7
        Assert.Contains("\"to_tier\":2", planned.Plan.Steps[1].ActionJson, StringComparison.Ordinal);
        Assert.Equal(2, planned.Plan.EstimatedPointCost);
    }

    [Fact] // legal：出售模板带危险位 true + 红标
    public void Sell_template_carries_the_danger_bit()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.Sell, "卖掉1号塔", ("target", "ord:1")));

        Assert.Contains("\"confirm_sell\":true", planned.Plan.Steps[1].ActionJson, StringComparison.Ordinal);
        Assert.Contains(planned.Plan.Risks, risk => risk.StartsWith("target-unresolved:", StringComparison.Ordinal));
        Assert.Equal(2, planned.Plan.EstimatedPointCost);
    }

    [Fact] // boundary：敌情模板按 scope 换侦察步（next_wave/分区/缺省）
    public void Threat_recon_step_follows_the_scope_slot()
    {
        PlannedOrder nextWave = Planner().Prepare(Intent(IntentKind.QueryThreat, "下一波怕什么", ("scope", "next_wave")));
        PlannedOrder region = Planner().Prepare(Intent(IntentKind.QueryThreat, "东门有什么威胁", ("scope", "east_gate")));
        PlannedOrder plain = Planner().Prepare(Intent(IntentKind.QueryThreat, "当前波", ("scope", "current")));

        Assert.Equal("QueryWorld", VerbAt(nextWave, 0));
        Assert.Contains("\"key\":\"wave\"", nextWave.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Equal("InspectTile", VerbAt(region, 0));
        Assert.Contains("\"aspect\":\"threat\"", region.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Equal("LookAround", VerbAt(plain, 0));
    }

    [Fact] // legal：集火模板——any 指认解析为首个现役敌（首波夹具）
    public void FocusFire_template_resolves_any_to_the_first_living_enemy()
    {
        var (_, view) = RegistryFixture.Create(stepToTick: 3300);
        Assert.True(view.LivingEnemies.Count > 0);
        var planner = new PipelinePlanner(
            view,
            new Dictionary<string, CellCoord> { ["east_gate"] = Anchor },
            fallbackAnchor: new CellCoord(8, 20),
            defaultDefenseTowerId: "era1:tower-wood-arrow",
            eraOrdinal: () => 2);

        PlannedOrder planned = planner.Prepare(Intent(IntentKind.FocusFire, "集火敌人", ("target", "any")));

        Assert.Equal(
            new[] { "LookAround", "FocusFire", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        Assert.Contains($"\"target_id\":{view.LivingEnemies[0].Id}", planned.Plan.Steps[1].ActionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(planned.Plan.Risks, risk => risk.StartsWith("target-unresolved:", StringComparison.Ordinal));
    }

    [Fact] // boundary：改集火模式无塔 → 空名单落 risk（模板仍产出，执行时 schema 诚实拒绝）
    public void ChangeFocusMode_without_towers_marks_the_risk_instead_of_guessing()
    {
        PlannedOrder planned = Planner().Prepare(
            Intent(IntentKind.ChangeFocusMode, "优先打强的", ("mode", "strong"), ("target_towers", "all")));

        Assert.Equal(
            new[] { "SetTowerMode", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        Assert.Contains("\"mode\":\"strong\"", planned.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Contains(planned.Plan.Risks, risk => risk.StartsWith("target-unresolved:", StringComparison.Ordinal));
        Assert.Equal(1, planned.Plan.EstimatedPointCost);
    }

    [Fact] // legal：物流改道模板——分区解析为最近建筑、缺省 from=首个采集器/ to=仓库
    public void Reroute_template_resolves_region_endpoints_to_buildings()
    {
        var (_, view) = RegistryFixture.Create(runMilestone: true);
        var planner = new PipelinePlanner(
            view,
            new Dictionary<string, CellCoord> { ["east_gate"] = Anchor },
            fallbackAnchor: new CellCoord(8, 20),
            defaultDefenseTowerId: "era1:tower-wood-arrow",
            eraOrdinal: () => 2);

        PlannedOrder planned = planner.Prepare(
            Intent(IntentKind.RerouteLogistics, "木材先送南门", ("item", "era1:res-wood"), ("to", "south_gate")));

        Assert.Equal(
            new[] { "RerouteLogistics", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        string json = planned.Plan.Steps[0].ActionJson;
        Assert.Contains("\"from\":3", json, StringComparison.Ordinal); // 缺省 from=首个采集器（id3）
        Assert.Contains("\"item\":\"era1:res-wood\"", json, StringComparison.Ordinal);
        Assert.Equal(1, planned.Plan.EstimatedPointCost);
    }

    [Fact] // legal：巡逻模板 duration 缺省 60s、越界钳到 [15,120]
    public void Patrol_template_defaults_and_clamps_the_duration()
    {
        PlannedOrder plain = Planner().Prepare(Intent(IntentKind.Patrol, "去西线巡逻", ("lane", "lane_west")));
        PlannedOrder over = Planner().Prepare(
            Intent(IntentKind.Patrol, "去西线巡逻", ("lane", "lane_west"), ("duration", "999")));

        Assert.Contains("\"duration\":60", plain.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Contains("\"duration\":120", over.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Contains("duration-clamped:999->120", string.Join(";", over.Plan.Risks), StringComparison.Ordinal);
        Assert.Equal(1, over.Plan.EstimatedPointCost);
    }

    [Fact] // legal：卷轴双路——A 路 scroll 槽产 TriggerScroll、B 路 task 槽产 StopTask
    public void ScrollControl_template_splits_into_the_two_paths()
    {
        PlannedOrder fire = Planner().Prepare(
            Intent(IntentKind.ScrollControl, "开火油卷轴", ("scroll", "era2:scroll-fire-oil")));
        PlannedOrder stop = Planner().Prepare(
            Intent(IntentKind.ScrollControl, "停止", ("task", "all")));

        Assert.Equal("TriggerScroll", VerbAt(fire, 0));
        Assert.Contains("\"scroll_id\":\"era2:scroll-fire-oil\"", fire.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
        Assert.Equal("StopTask", VerbAt(stop, 0));
        Assert.Contains("\"task_id\":\"all\"", stop.Plan.Steps[0].ActionJson, StringComparison.Ordinal);
    }

    [Fact] // legal：宿主驱动单动作入口 PrepareSingle——同五段形态（动作+汇报两步）
    public void PrepareSingle_wraps_one_action_with_the_report_marker()
    {
        string setRally = "{\"action\":\"SetRally\",\"lane\":\"east_gate\",\"stance\":\"hold\"}";
        PlannedOrder planned = Planner().PrepareSingle(SystemActionKind.SetRally, setRally);

        Assert.Equal(
            new[] { "SetRally", "ReportToPlayer" },
            planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray());
        Assert.Equal("HoldRegion", planned.Plan.SourceIntent.Kind.ToString()); // SP-2 #1 归因
        Assert.Equal(2, planned.Plan.EstimatedPointCost);
        Assert.Equal(setRally, planned.Plan.Steps[0].ActionJson);
    }

    private static string VerbAt(PlannedOrder planned, int index) =>
        planned.Plan.Steps[index].Action.ToString();
}
