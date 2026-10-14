using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [F-065][F-066][F-067][F-068][F-069][F-096] T-068 五段管线全量 E2E：
// 19 动作全部经 意图→计划→审批→执行→汇报 走通（12 意图 + 5 宿主驱动入口 +
// 2 既有建造/汇报路径）；统一自纠 ≤2；澄清卡统一出口；注入防护 ②③④ 复核。
public class FullPipelineActionTests
{
    private const string ArmedScroll = "era2:scroll-fire-oil";

    private static IReadOnlySet<string> Armed() =>
        new HashSet<string>(StringComparer.Ordinal) { ArmedScroll };

    // ------------------------------------------------ ① 12 意图军令全走通 ------------------

    [Fact] // legal：设闹军令走满五段（SetAlarm 真 handler + 治理态任务）
    public void SetAlarm_order_runs_all_five_stages_and_registers_the_task()
    {
        using var harness = new PipelineHarness();

        OrderOutcome done = harness.Pipeline.Handle("三分钟后叫我");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var alarmStep = done.Trace.Steps.First(step => step.Verb == "SetAlarm");
        Assert.Equal("Ok", alarmStep.Status);
        Assert.Single(alarmStep.Attempts);
        AgentTaskRecord task = Assert.Single(harness.Pipeline.State.Tasks);
        Assert.Equal(AgentTaskFamily.Alarm, task.Family);
        Assert.StartsWith("已照办", done.Report!.Summary, StringComparison.Ordinal);
        Assert.Equal(5, harness.Pipeline.Ledger.Points); // 0 点动词
    }

    [Fact] // legal：巡逻军令（E2）——SpawnScout 落任务（allow 档直跑不挂起）
    public void Patrol_order_registers_the_scout_at_era2()
    {
        using var harness = new PipelineHarness();

        OrderOutcome done = harness.Pipeline.Handle("去西线巡逻");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var scoutStep = done.Trace.Steps.First(step => step.Verb == "SpawnScout");
        Assert.Equal("Ok", scoutStep.Status);
        AgentTaskRecord task = Assert.Single(harness.Pipeline.State.Tasks);
        Assert.Equal(AgentTaskFamily.Scout, task.Family);
        Assert.Equal("lane_west", task.Note);
        Assert.Equal(4, harness.Pipeline.Ledger.Points); // 巡逻 1 点
    }

    [Fact] // boundary：E1 巡逻军令 → 整案拒绝（capability_locked 预检）+ 零扣点
    public void Patrol_order_at_era1_is_denied_whole_by_the_capability_precheck()
    {
        using var harness = new PipelineHarness(eraIndex: 1);

        OrderOutcome denied = harness.Pipeline.Handle("去西线巡逻");

        Assert.Equal(PipelineOutcomeKind.Denied, denied.Kind);
        Assert.Empty(harness.Pipeline.State.Tasks);
        Assert.Equal(0, harness.Pipeline.Ledger.Points); // E1 不开启点数
        // E1 计价动词先撞原子预检（预算不可覆盖）——整案拒绝零扣费；时代锁话术由注册表矩阵覆盖
        Assert.Contains("办不了", denied.Report!.Summary, StringComparison.Ordinal);
    }

    [Fact] // legal：储备查询军令带 QueryWorld 步（SP-2 #8 动作化收口）
    public void Stockpile_order_queries_the_world_and_reports()
    {
        using var harness = new PipelineHarness();

        OrderOutcome done = harness.Pipeline.Handle("我们还剩多少木材");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("Ok", done.Trace.Steps.First(step => step.Verb == "QueryWorld").Status);
        Assert.Contains(done.Report!.Findings, finding => finding.Text.Contains("QueryWorld", StringComparison.Ordinal));
    }

    [Fact] // legal：集火军令——先跑到首波敌入场，any→首个现役，E2E 全通
    public void FocusFire_order_targets_the_first_living_enemy_end_to_end()
    {
        using var harness = new PipelineHarness();
        harness.Simulation.Run(3300); // E1 夹具 grace=1.5×72s×30Hz=3240
        var living = harness.Simulation.Units.Where(unit => unit.Alive).ToList();
        Assert.True(living.Count > 0, "首波敌应在 tick≈3240 入场（E1 夹具口径）");

        OrderOutcome awaiting = harness.Pipeline.Handle("集火敌人");
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var fireStep = done.Trace.Steps.First(step => step.Verb == "FocusFire");
        Assert.Equal("Ok", fireStep.Status);
        Assert.Equal(living[0].Id.Value, harness.Pipeline.State.FocusTarget);
        Assert.Equal(4, harness.Pipeline.Ledger.Points); // 战斗 1 点
    }

    [Fact] // legal：改集火模式军令（先建 2 塔；换波解除 40% 预算闸）
    public void ChangeFocusMode_order_sets_modes_on_placed_towers()
    {
        using var harness = new PipelineHarness();
        OrderOutcome build = harness.Pipeline.Handle("在东门建两座木箭塔");
        harness.Pipeline.Confirm(build.PendingId);
        Assert.Equal(2, harness.Simulation.Towers.Count);
        harness.Pipeline.Ledger.BeginWave(2, isBossWave: false); // 波 1 已花 2/2，换波重置

        OrderOutcome awaiting = harness.Pipeline.Handle("改模式优先打强的");
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var modeStep = done.Trace.Steps.First(step => step.Verb == "SetTowerMode");
        Assert.Equal("Ok", modeStep.Status);
        Assert.Equal(2, harness.Pipeline.State.TowerModes.Count);
        Assert.All(harness.Pipeline.State.TowerModes.Values, mode => Assert.Equal("strong", mode));
        Assert.Equal(4, harness.Pipeline.Ledger.Points); // 5-1
    }

    // ------------------------------------------------ ② 升级/出售/英雄/物流：SimCommand 泳道 ------------------

    [Fact] // legal：E2 升级军令——建箭楼→升级到二级（upgrade-tower SimCommand 全量记账）
    public void Upgrade_order_runs_the_full_sim_lane_at_era2()
    {
        using var harness = new PipelineHarness(
            config: PipelineHarness.Era2Config(),
            parser: PipelineHarness.Era2Parser());

        OrderOutcome build = harness.Pipeline.Handle("在东门建一座箭楼");
        OrderOutcome built = harness.Pipeline.Confirm(build.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, built.Kind);
        SimTower tower = Assert.Single(harness.Simulation.Towers);
        Assert.Equal("era2:tower-arrow", tower.Spec.Id);
        Assert.Equal(3, harness.Pipeline.Ledger.Points); // 5-2
        harness.Pipeline.Ledger.BeginWave(2, isBossWave: false); // 40% 波闸=2 点/波：建造后换波再升级

        OrderOutcome upgrade = harness.Pipeline.Handle("把1号塔升到二级");
        OrderOutcome done = harness.Pipeline.Confirm(upgrade.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var upgradeStep = done.Trace.Steps.First(step => step.Verb == "UpgradeTower");
        Assert.Equal("Ok", upgradeStep.Status);
        Assert.Equal("era2:tower-crossbow", Assert.Single(harness.Simulation.Towers).Spec.Id); // 同格替换+分组重建
        Assert.Equal(3, harness.Pipeline.Ledger.Points); // 波 2 回满 5 − 升级 2
    }

    [Fact] // legal：E2 出售军令——confirm_sell 危险位 → sell-tower SimCommand（残骸+退款）
    public void Sell_order_runs_the_full_sim_lane_at_era2()
    {
        using var harness = new PipelineHarness(
            config: PipelineHarness.Era2Config(),
            parser: PipelineHarness.Era2Parser());
        OrderOutcome build = harness.Pipeline.Handle("在东门建一座箭楼");
        harness.Pipeline.Confirm(build.PendingId);
        harness.Pipeline.Ledger.BeginWave(2, isBossWave: false); // 40% 波闸=2 点/波：建造后换波再出售
        double timberAfterBuild = harness.Simulation.Stock.AmountOf(new ResourceKey("era2:res-timber"));

        OrderOutcome awaiting = harness.Pipeline.Handle("卖掉1号塔");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        Assert.Contains("danger:confirm_sell", string.Join(";", awaiting.PendingCard!.Risks), StringComparison.Ordinal);
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("Ok", done.Trace.Steps.First(step => step.Verb == "SellTower").Status);
        Assert.Empty(harness.Simulation.Towers); // 已拆除
        Assert.True(harness.Simulation.Estate.Ruins.Count > 0); // 残骸账
        Assert.True(harness.Simulation.Stock.AmountOf(new ResourceKey("era2:res-timber")) > timberAfterBuild); // 30% 退款
    }

    [Fact] // legal：英雄军令（宿主驱动入口）——hero-order SimCommand 走通（E2 英雄在场）
    public void HeroOrder_host_action_moves_the_hero_through_the_sim_lane()
    {
        using var harness = new PipelineHarness(
            config: PipelineHarness.Era2Config(),
            parser: PipelineHarness.Era2Parser());
        Assert.NotNull(harness.Simulation.Hero);
        float yBefore = harness.Simulation.Hero!.Y;

        OrderOutcome awaiting = harness.Pipeline.HandleSystemAction(
            SystemActionKind.HeroOrder, "{\"action\":\"HeroOrder\",\"order\":\"move\"}");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("Ok", done.Trace.Steps.First(step => step.Verb == "HeroOrder").Status);
        Assert.Equal(4, harness.Pipeline.Ledger.Points); // 战斗 1 点
        Assert.NotEqual(yBefore, harness.Simulation.Hero!.Y); // 真动了（向入口推进）
    }

    [Fact] // legal：物流改道军令——assign-carrier 真落子（先铺采集链）
    public void Reroute_order_moves_a_real_carrier_through_the_assign_lane()
    {
        using var harness = new PipelineHarness();
        harness.Simulation.Enqueue(new SimCommand(1, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-harvester\",\"resource\":\"era1:res-wood\",\"seats\":1,\"cell\":{\"x\":6,\"y\":18}}"));
        harness.Simulation.Enqueue(new SimCommand(1, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-converter\",\"cell\":{\"x\":9,\"y\":18}}"));
        harness.Simulation.Run(1);
        int carriersBefore = harness.Simulation.Carriers.Count;

        OrderOutcome rerouteAwait = harness.Pipeline.Handle("木材先送南门");
        OrderOutcome done = harness.Pipeline.Confirm(rerouteAwait.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var rerouteStep = done.Trace.Steps.First(step => step.Verb == "RerouteLogistics");
        Assert.Equal("Ok", rerouteStep.Status);
        Assert.Equal(carriersBefore + 1, harness.Simulation.Carriers.Count); // assign-carrier 真落子
        Assert.Equal(4, harness.Pipeline.Ledger.Points); // 物流 1 点
    }

    // ------------------------------------------------ ③ 五个宿主驱动动作全走五段 ------------------

    public static TheoryData<string, string> HostRows => new()
    {
        // legal：五个无军令意图的动作经 HandleSystemAction 走满五段（ask 挂起 / allow 直跑）
        { "{\"action\":\"SetRally\",\"lane\":\"east_gate\",\"stance\":\"hold\"}", "AwaitingApproval" },
        { "{\"action\":\"SetStockpile\",\"item_id\":\"era1:res-wood\",\"min_amount\":30.0}", "AwaitingApproval" },
        { "{\"action\":\"AskPlayer\",\"question\":\"先补箭塔还是先修墙？\",\"options\":[{\"label\":\"补塔\"},{\"label\":\"修墙\"}],\"multi_select\":false}", "Completed" },
        { "{\"action\":\"TodoBoard\",\"todos\":[\"北门补一座箭塔\",\"给西侧塔供弹\"]}", "Completed" },
    };

    [Theory] // legal：宿主驱动动作五段全走（意图→计划→审批→执行→汇报）
    [MemberData(nameof(HostRows))]
    public void Host_driven_actions_run_the_full_five_stages(string actionJson, string expectedFirstOutcome)
    {
        using var harness = new PipelineHarness();
        _ = ActionCatalog.TryResolveName(VerbOf(actionJson), out var kind);
        OrderOutcome first = harness.Pipeline.HandleSystemAction(kind, actionJson);
        Assert.Equal(expectedFirstOutcome, first.Kind.ToString());

        OrderOutcome done = first.Kind == PipelineOutcomeKind.AwaitingApproval
            ? harness.Pipeline.Confirm(first.PendingId)
            : first;

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("Ok", done.Trace.Steps[0].Status);
        var stages = AgentPipelineClosedLoopTests.StageSequence(
            JsonlJournalWriter.ReadAll(harness.JournalPath), done.Order);
        Assert.Equal("input", stages[0]);
        Assert.Equal("report", stages[^1]); // 五段首尾完整（中间 plan/approval/execution 由其余断言覆盖）
    }

    // ------------------------------------------------ ④ 卷轴双路 + StopTask ------------------

    [Fact] // legal：开卷军令（装备集内）→ 触发+冷却记账；窗口内再开被拒如实汇报
    public void Scroll_order_fires_once_then_the_cooldown_refuses_honestly()
    {
        using var harness = new PipelineHarness(armedScrolls: Armed());

        OrderOutcome firstAwait = harness.Pipeline.Handle("开火油卷轴");
        OrderOutcome first = harness.Pipeline.Confirm(firstAwait.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, first.Kind);
        Assert.Equal("Ok", first.Trace.Steps.First(step => step.Verb == "TriggerScroll").Status);
        Assert.Single(harness.Pipeline.State.ScrollActivations);

        OrderOutcome secondAwait = harness.Pipeline.Handle("开火油卷轴");
        OrderOutcome second = harness.Pipeline.Confirm(secondAwait.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, second.Kind);
        var scrollStep = second.Trace.Steps.First(step => step.Verb == "TriggerScroll");
        Assert.Equal("Err", scrollStep.Status);
        Assert.Contains("scroll-cooldown", scrollStep.Attempts[0].Error, StringComparison.Ordinal);
        Assert.Single(scrollStep.Attempts); // 冷却不可自纠——单次尝试
        Assert.StartsWith("尽力了但没全办成", second.Report!.Summary, StringComparison.Ordinal);
        Assert.Single(harness.Pipeline.State.ScrollActivations);
    }

    [Fact] // legal：停止军令（B 路）——统一句柄停掉闹钟+巡逻
    public void Stop_order_cancels_registered_tasks_through_the_unified_handles()
    {
        using var harness = new PipelineHarness();
        harness.Pipeline.Handle("三分钟后叫我");
        harness.Pipeline.Handle("去西线巡逻");
        Assert.Equal(2, harness.Pipeline.State.Tasks.Count);

        OrderOutcome done = harness.Pipeline.Handle("停掉所有卷轴");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("Ok", done.Trace.Steps.First(step => step.Verb == "StopTask").Status);
        Assert.Empty(harness.Pipeline.State.Tasks);
    }

    // ------------------------------------------------ ⑤ 统一自纠（非 BuildPlan 动词） ------------------

    [Fact] // boundary：集火目标失效 → 统一自纠改打首个现役（1 次自纠内成，≤2）
    public void FocusFire_self_corrects_to_the_first_living_enemy()
    {
        using var harness = new PipelineHarness();
        harness.Simulation.Run(3300);
        var living = harness.Simulation.Units.Where(unit => unit.Alive).ToList();
        Assert.True(living.Count > 0);

        // 宿主驱动入口带一个必失效目标（999999 无此敌）→ 执行失败 → 调整器改打首个现役
        OrderOutcome awaiting = harness.Pipeline.HandleSystemAction(
            SystemActionKind.FocusFire, "{\"action\":\"FocusFire\",\"target_id\":999999}");
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var fireStep = done.Trace.Steps.First(step => step.Verb == "FocusFire");
        Assert.Equal("Ok", fireStep.Status);
        Assert.Equal(2, fireStep.Attempts.Count); // 1 次失败 + 1 次自纠成（≤2）
        Assert.Contains("target-invalid", fireStep.Attempts[0].Error, StringComparison.Ordinal);
        Assert.StartsWith("retarget-first-living:", fireStep.Attempts[1].RetryAdjustment, StringComparison.Ordinal);
        Assert.True(fireStep.Attempts[1].SelfCorrected);
        Assert.Equal(living[0].Id.Value, harness.Pipeline.State.FocusTarget);
    }

    // ------------------------------------------------ ⑥ 澄清卡统一出口 ------------------

    [Fact] // illegal：意图层失败统一出口=澄清卡+快捷轮盘（12 意图全量轮盘）
    public void Parse_failure_exits_through_the_clarification_card()
    {
        using var harness = new PipelineHarness();

        OrderOutcome clarified = harness.Pipeline.Handle("呃呃呃呃");

        Assert.Equal(PipelineOutcomeKind.Clarification, clarified.Kind);
        Assert.Equal(EraCore.Agent.Grammar.L1IntentParser.DefaultClarifyingKey, clarified.ClarifyingKey);
        Assert.Equal(12, clarified.QuickWheel.Count);
        Assert.Null(clarified.Report);
        Assert.Equal(5, harness.Pipeline.Ledger.Points);
    }

    // ------------------------------------------------ ⑦ 注入防护复核 ------------------

    [Fact] // legal：注入防护②——方案卡只渲染结构化字段（步=JSON、风险=机器码，无自由文本通道）
    public void Plan_card_renders_structured_fields_only()
    {
        using var harness = new PipelineHarness();

        OrderOutcome awaiting = harness.Pipeline.Handle("在东门建两座木箭塔");
        PlanCard card = awaiting.PendingCard!;

        foreach (PlanCardStep step in card.Steps)
        {
            using var document = System.Text.Json.JsonDocument.Parse(step.ActionJson); // 步载荷=可解析 JSON
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
        }

        Assert.All(card.Risks, risk => Assert.DoesNotContain("http", risk, StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // boundary：注入防护④——单计划资源聚合 >30% 强制弹卡标记（即使动作本就 ask）
    public void Plan_cost_over_30pct_forces_the_card_flag()
    {
        using var harness = new PipelineHarness(initialWood: 90.0); // 2 塔 60 > 90×30%=27
        harness.Pipeline.Ledger.BeginWave(2, isBossWave: false);

        OrderOutcome outcome = harness.Pipeline.Handle("在东门建两座木箭塔");

        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, outcome.Kind);
        var approval = AgentPipelineClosedLoopTests.ApprovalPayloadOf(
            JsonlJournalWriter.ReadAll(harness.JournalPath), outcome.Order);
        Assert.NotNull(approval);
        Assert.True(approval!.PlanCostForcedAsk);
    }

    [Fact] // boundary：注入防护④ 对照——线上（≤30%）不强制弹卡标记
    public void Plan_cost_under_30pct_does_not_force_the_flag()
    {
        using var harness = new PipelineHarness(initialWood: 200.0); // 60 ≤ 200×30%=60（等于线=线下）
        harness.Pipeline.Ledger.BeginWave(2, isBossWave: false);

        OrderOutcome outcome = harness.Pipeline.Handle("在东门建两座木箭塔");

        var approval = AgentPipelineClosedLoopTests.ApprovalPayloadOf(
            JsonlJournalWriter.ReadAll(harness.JournalPath), outcome.Order);
        Assert.NotNull(approval);
        Assert.False(approval!.PlanCostForcedAsk);
    }

    // ------------------------------------------------ 辅助 ------------------

    private static string VerbOf(string actionJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(actionJson);
        return document.RootElement.GetProperty("action").GetString()!;
    }
}
