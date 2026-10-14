using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-068] T-068 13 个 handler 的单元面（AgentActionHandlers.cs 对应）：
// 侦察双查（InspectTile/QueryWorld）、指令组与治理态、管理组文本过滤。
// 注册表级矩阵在 ActionMatrixTests；此处直测 handler 的合法/边界/非法三行。
public class AgentActionHandlersTests
{
    /// <summary>首波敌入场 tick（E1 夹具 grace=1.5×72s×30Hz=3240，取 3300 安全余量）。</summary>
    private const long FirstEnemyTick = 3300;

    private static SimulationActionView View(bool milestone = false, long stepToTick = 0)
    {
        var (simulation, view) = RegistryFixture.Create(runMilestone: milestone, stepToTick: stepToTick);
        return view;
    }

    private static AgentActionState State() => new(() => 0);

    // ------------------------------------------------ InspectTile ------------------

    [Fact] // legal：threat aspect 报半径内敌数（跑到首波敌后）
    public void InspectTile_threat_counts_enemies_within_radius()
    {
        SimulationActionView view = View(stepToTick: FirstEnemyTick);
        Assert.True(view.LivingEnemies.Count > 0);
        var action = new InspectTileAction();

        InspectTileReport report = action.Run(view, new CellCoord(8, 0), "threat", out string? refusal)!;

        Assert.Null(refusal);
        Assert.Contains(report.Lines, line => line.StartsWith($"enemies-within-{InspectTileAction.ThreatScanRadiusCells}=", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.StartsWith("enemies-total=", StringComparison.Ordinal));
    }

    [Fact] // legal：supply aspect 报池内资源行；stats aspect 报可建/占用
    public void InspectTile_supply_and_stats_read_the_pool_and_cell_state()
    {
        SimulationActionView view = View();
        var action = new InspectTileAction();

        InspectTileReport supply = action.Run(view, new CellCoord(2, 2), "supply", out _)!;
        InspectTileReport stats = action.Run(view, new CellCoord(2, 2), "stats", out _)!;

        Assert.Contains(supply.Lines, line => line.StartsWith("era1:res-wood=", StringComparison.Ordinal));
        Assert.Contains(stats.Lines, line => line.StartsWith("buildable=", StringComparison.Ordinal));
        Assert.Contains(stats.Lines, line => line.StartsWith("occupied=", StringComparison.Ordinal));
    }

    [Fact] // illegal：界外格/未知 aspect → 拒绝码
    public void InspectTile_refuses_out_of_grid_and_unknown_aspects()
    {
        var action = new InspectTileAction();
        SimulationActionView view = View();

        Assert.Null(action.Run(view, new CellCoord(-1, 0), "threat", out string? bounds));
        Assert.StartsWith("cell-out-of-bounds", bounds);
        Assert.Null(action.Run(view, new CellCoord(2, 2), "magma", out string? aspect));
        Assert.StartsWith("aspect-unknown", aspect);
    }

    // ------------------------------------------------ QueryWorld ------------------

    [Fact] // legal：wave/threat/paths 三键读数含锚定字段
    public void QueryWorld_reports_wave_threat_and_paths_figures()
    {
        var action = new QueryWorldAction();
        SimulationActionView view = View(stepToTick: FirstEnemyTick);

        QueryWorldReport wave = action.Run(view, "wave", "all", null);
        QueryWorldReport threat = action.Run(view, "threat", "all", 3);
        QueryWorldReport paths = action.Run(view, "paths", "all", null);

        Assert.Contains(wave.Lines, line => line.StartsWith("wave=", StringComparison.Ordinal));
        Assert.Contains(threat.Lines, line => line.StartsWith("enemy:", StringComparison.Ordinal));
        Assert.Contains(paths.Lines, line => line.StartsWith("carriers=", StringComparison.Ordinal));
    }

    [Fact] // boundary：head_limit 钳到 [1,20]
    public void QueryWorld_head_limit_is_clamped_into_the_frozen_band()
    {
        var action = new QueryWorldAction();
        SimulationActionView view = View(stepToTick: FirstEnemyTick);

        Assert.Single(action.Run(view, "threat", "all", 0).Lines); // 下界 1
        Assert.InRange(action.Run(view, "threat", "all", 999).Lines.Count, 2, 20); // 上界 20（G.2），现场至少 2 行锚定数据
    }

    // ------------------------------------------------ 指令组 ------------------

    [Fact] // legal：SetRally/SetStockpile/FocusFire/SetTowerMode 落治理态
    public void Directive_handlers_write_the_directive_state()
    {
        AgentActionState state = State();
        SimulationActionView view = View(milestone: true);

        Assert.Equal(("east_gate", "hold"), (new SetRallyAction().Run(state, "east_gate", "hold").Lane,
            new SetRallyAction().Run(state, "east_gate", "hold").Stance));
        var stock = new SetStockpileAction().Run(state, view, "era1:res-wood", 25.0, out string? stockRefusal);
        Assert.Null(stockRefusal);
        Assert.Equal(25.0, stock!.MinAmount);
        var mode = new SetTowerModeAction().Run(state, view, new long[] { 5, 7 }, "closest", out string? modeRefusal);
        Assert.Null(modeRefusal);
        Assert.Equal(new long[] { 5, 7 }, mode!.TargetIds);
        Assert.Equal(2, state.TowerModes.Count);
    }

    [Fact] // illegal：FocusFire 无现役敌/SetTowerMode 伪塔 id → 显式拒绝
    public void Combat_directive_handlers_refuse_absent_targets()
    {
        AgentActionState state = State();
        SimulationActionView view = View(); // 无塔无敌

        Assert.Null(new FocusFireAction().Run(state, view, 1000001, out string? fireRefusal));
        Assert.StartsWith("target-invalid:", fireRefusal);
        Assert.Null(new SetTowerModeAction().Run(state, view, new long[] { 5 }, "first", out string? modeRefusal));
        Assert.StartsWith("target-invalid:not-a-tower:", modeRefusal);
        Assert.Null(state.FocusTarget);
        Assert.Empty(state.TowerModes);
    }

    [Fact] // legal：RerouteLogistics 校验里程碑端点并产出执行泳道订单
    public void Reroute_handler_validates_endpoints_and_returns_the_order()
    {
        SimulationActionView view = View(milestone: true);
        var action = new RerouteLogisticsAction();

        RerouteLogisticsPlan plan = action.Run(view, 3, 4, "era1:res-wood", 0.75, out string? refusal)!;

        Assert.Null(refusal);
        Assert.Equal(3, plan.From);
        Assert.Equal(4, plan.To);
        Assert.Equal(0.75, plan.Rate);

        Assert.Null(action.Run(view, 999, 4, "era1:res-wood", 0.5, out string? unknown));
        Assert.StartsWith("target-invalid:from-unknown:", unknown);
    }

    // ------------------------------------------------ 管理组 ------------------

    [Fact] // legal：AskPlayer 追加「其他」+链接过滤；TodoBoard 全量重写+过滤
    public void Management_text_channels_filter_links_and_append_the_other_option()
    {
        var ask = new AskPlayerAction();
        AskPlayerCard card = ask.Run("去 www.evil.com 看吗？", new[] { "去", "不去" }, true);

        Assert.Equal(new[] { "去", "不去", "其他" }, card.Options);
        Assert.Equal("其他", card.Options[^1]);
        Assert.DoesNotContain("evil.com", card.Question);
        Assert.Contains(ReportToPlayerAction.FilteredLinkToken, card.Question);

        var board = new TodoBoardAction();
        TodoBoardSnapshot snapshot = board.Run(State(), new[] { "看 https://x.example.net/a", "北门补塔" });
        Assert.DoesNotContain("example.net", snapshot.Todos[0]);
        Assert.Equal("北门补塔", snapshot.Todos[1]);
    }

    [Fact] // legal：SetAlarm/SpawnScout 生成任务句柄；boundary：秒数由 schema 保证入界
    public void Alarm_and_scout_handlers_register_tasks()
    {
        AgentActionState state = State();

        SetAlarmAck alarm = new SetAlarmAction().Run(state, 90.0, "敌袭");
        SpawnScoutAck scout = new SpawnScoutAction().Run(state, "lane_east", 45.0);

        Assert.Equal("alarm-1", alarm.TaskId);
        Assert.Equal(90 * 30, alarm.DueTick);
        Assert.Equal("scout-1", scout.TaskId);
        Assert.Equal(45 * 30, scout.UntilTick);
        Assert.Equal(2, state.Tasks.Count);
    }

    [Fact] // legal/illegal：TriggerScroll 装备/未装备；StopTask 精确/all/未知
    public void Scroll_and_stop_handlers_gate_on_the_armed_set_and_handles()
    {
        AgentActionState armed = new(() => 0, new HashSet<string>(StringComparer.Ordinal) { "era2:scroll-fire-oil" });
        var scrolls = new TriggerScrollAction();

        TriggerScrollAck ack = scrolls.Run(armed, "era2:scroll-fire-oil", out string? fired)!;
        Assert.Equal(AgentActionState.ScrollCooldownTicks, ack.ReadyAgainTick);
        Assert.Null(scrolls.Run(armed, "era2:scroll-frost", out string? unequipped));
        Assert.StartsWith("scroll-not-equipped:", unequipped);

        AgentActionState state = State();
        state.AddAlarm(90, "a");
        state.AddPatrol("lane_east", 60);
        var stop = new StopTaskAction();
        Assert.Equal(new[] { "alarm-1" }, stop.Run(state, "alarm-1", out _)!.StoppedIds);
        Assert.Equal(new[] { "scout-1" }, stop.Run(state, "all", out _)!.StoppedIds);
        Assert.Null(stop.Run(state, "ghost", out string? unknown));
        Assert.StartsWith("target-invalid:task-unknown:", unknown);
    }
}
