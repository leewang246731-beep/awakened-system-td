using EraCore.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-065][F-068] T-068 治理态（AgentActionState）：任务句柄统一空间
// （闹钟/巡逻/停止）、卷轴装备集+3s 冷却、指令与任务板落账。
public class AgentActionStateTests
{
    private static AgentActionState State(out Func<long> tick)
    {
        long current = 0;
        tick = () => current;
        return new AgentActionState(tick);
    }

    [Fact] // legal：闹钟按 K-1 30Hz 域换算到期 tick，句柄单调
    public void Alarms_register_with_tick_domain_due_times()
    {
        var state = State(out Func<long> tick);

        string first = state.AddAlarm(90, "敌袭");
        string second = state.AddAlarm(60, "换防");

        Assert.Equal("alarm-1", first);
        Assert.Equal("alarm-2", second);
        Assert.Equal(2, state.Tasks.Count);
        Assert.Equal(90 * 30, state.Tasks[0].DueTick);
        Assert.Equal(60 * 30, state.Tasks[1].DueTick);
        Assert.All(state.Tasks, task => Assert.Equal(AgentTaskFamily.Alarm, task.Family));
    }

    [Fact] // legal：巡逻登记直至 tick，与闹钟同一句柄空间
    public void Patrols_register_with_until_ticks()
    {
        var state = State(out _);

        string id = state.AddPatrol("lane_west", 60);

        Assert.Equal("scout-1", id);
        AgentTaskRecord record = Assert.Single(state.Tasks);
        Assert.Equal(AgentTaskFamily.Scout, record.Family);
        Assert.Equal("lane_west", record.Note);
        Assert.Equal(60 * 30, record.UntilTick);
    }

    [Fact] // legal：StopTask all 清空全部任务并按名返回
    public void Stop_all_clears_the_roster()
    {
        var state = State(out _);
        state.AddAlarm(90, "a");
        state.AddPatrol("lane_west", 60);

        IReadOnlyList<string> stopped = state.StopTasks("all");

        Assert.Equal(new[] { "alarm-1", "scout-1" }, stopped);
        Assert.Empty(state.Tasks);
    }

    [Fact] // legal：精确句柄只停对应任务
    public void Stop_by_exact_handle_removes_only_that_task()
    {
        var state = State(out _);
        state.AddAlarm(90, "a");
        state.AddPatrol("lane_west", 60);

        IReadOnlyList<string> stopped = state.StopTasks("scout-1");

        Assert.Equal(new[] { "scout-1" }, stopped);
        Assert.Single(state.Tasks);
        Assert.Equal("alarm-1", state.Tasks[0].TaskId);
    }

    [Fact] // illegal：未知句柄什么都不停（返回空，绝不假装停止）
    public void Stop_by_unknown_handle_stops_nothing()
    {
        var state = State(out _);
        state.AddAlarm(90, "a");

        Assert.Empty(state.StopTasks("scout-9"));
        Assert.Single(state.Tasks);
    }

    [Fact] // legal：指令与任务板落账/全量重写
    public void Directives_and_todos_record_and_rewrite()
    {
        var state = State(out _);
        state.SetRally("east_gate", "hold");
        state.SetStockpileFloor("era1:res-wood", 50);
        state.SetFocusTarget(1000001);
        state.SetTowerMode(5, "strong");
        state.WriteTodos(new[] { "北门补塔", "给西侧塔供弹" });
        state.WriteTodos(new[] { "only-one" }); // 全量重写：旧项消失

        Assert.Equal("hold", state.RallyStances["east_gate"]);
        Assert.Equal(50.0, state.StockpileFloors["era1:res-wood"]);
        Assert.Equal(1000001, state.FocusTarget);
        Assert.Equal("strong", state.TowerModes[5]);
        Assert.Equal(new[] { "only-one" }, state.Todos);
    }

    [Fact] // legal：卷轴装备集内首触成功、3s 冷却窗口内再触被拒、窗口过后可再触
    public void Scroll_trigger_respects_the_armed_set_and_cooldown_window()
    {
        long current = 0;
        var state = new AgentActionState(() => current, new HashSet<string>(StringComparer.Ordinal) { "era2:scroll-fire-oil" });

        Assert.Null(state.TryTriggerScroll("era2:scroll-fire-oil", out long ready));
        Assert.Equal(AgentActionState.ScrollCooldownTicks, ready);
        Assert.Single(state.ScrollActivations);

        string? refusal = state.TryTriggerScroll("era2:scroll-fire-oil", out _);
        Assert.NotNull(refusal);
        Assert.StartsWith("scroll-cooldown:", refusal);

        current = ready; // 冷却窗到点
        Assert.Null(state.TryTriggerScroll("era2:scroll-fire-oil", out long ready2));
        Assert.Equal(ready + AgentActionState.ScrollCooldownTicks, ready2);
        Assert.Equal(2, state.ScrollActivations.Count);
    }

    [Fact] // illegal：未装备卷轴永远拒绝，且不消耗冷却窗
    public void Unequipped_scroll_refuses_without_touching_the_cooldown()
    {
        var state = State(out _);

        string? refusal = state.TryTriggerScroll("era2:scroll-frost", out long ready);

        Assert.Equal("scroll-not-equipped:era2:scroll-frost:only equipped scrolls may fire (G.2)", refusal);
        Assert.Equal(0, ready);
        Assert.Empty(state.ScrollActivations);
    }

    [Fact] // illegal：负库存下限与空参防御
    public void Argument_guards_reject_hostile_values()
    {
        var state = State(out _);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetStockpileFloor("era1:res-wood", -1));
        Assert.Throws<ArgumentException>(() => state.SetRally(string.Empty, "hold"));
        Assert.Throws<ArgumentException>(() => state.StopTasks(string.Empty));
    }
}
