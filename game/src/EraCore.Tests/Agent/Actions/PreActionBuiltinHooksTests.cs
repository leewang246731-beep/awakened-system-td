using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-075][F-071] T-070 两个内置 PreAction 钩子（真实 E1 关卡夹具，CS-3.4
// 被测系统禁 mock）：①格位占用冲突改写——cell-occupied 且 +1x 可建的格自动
// 偏移 1 格+注记（confirm_overwrite 显式拆除路径永不改写；无可建偏移格/
// 无冲突=Pass）；②纪念馆保护——受保护遗迹格上的建造直接拦截（缺省探针
// 保护零格=E1 无纪念馆时的惰性行为）。

/// <summary>[F-075] Built-in PreAction hooks: placement-conflict rewrite and memorial protection.</summary>
public class PreActionBuiltinHooksTests
{
    private static void OccupyACell(Simulation simulation, CellCoord cell)
    {
        // 经 SimCommand 泳道占格（合法路径）：排一条 PlayerBuild 并步进一 tick。
        string payload = "{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\","
            + "\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}";
        simulation.Enqueue(new SimCommand(simulation.CurrentTick + 1, SimCommandKind.PlayerBuild, payload));
        simulation.Step();
    }

    private static (Simulation Simulation, SimulationActionView View) FreshView(double initialWood = 10_000.0)
    {
        var (simulation, view) = RegistryFixture.Create(
            initialStock: new List<KeyValuePair<ResourceKey, double>> { new(SimE1Fixture.Wood, initialWood) });
        return (simulation, view);
    }

    // ------------------------------------------------ 格位占用冲突改写

    [Fact] // legal — 占用格+右邻可建 → 自动偏移 +1x，注记记录原格→新格
    public void Occupied_cell_with_free_neighbor_is_offset_by_one()
    {
        var (simulation, view) = FreshView();
        CellCoord target = RegistryFixture.FreeCells(view, 2)[0];
        CellCoord neighbor = new(target.X + 1, target.Y);
        Assert.True(view.IsBuildable(neighbor, out _), "夹具前置：右邻应可建（固定种子地形）");
        OccupyACell(simulation, target);
        Assert.False(view.IsBuildable(target, out string? rejection));
        Assert.Equal("cell-occupied", rejection);

        string payload = ActionJsonSamples.BuildPlanCells(
            "[{\"x\":" + target.X + ",\"y\":" + target.Y + "}]");
        var hook = new BuildPlacementConflictHook();
        PreActionHookVerdict verdict = hook.Apply(new PreActionContext(
            SystemActionKind.BuildPlan, payload, simulation.CurrentTick, view));

        Assert.Equal(PreActionVerdictKind.Rewrite, verdict.Kind);
        Assert.Contains($"cell({target.X},{target.Y})→({neighbor.X},{neighbor.Y})", verdict.Note);
        Assert.Contains("{\"x\":" + neighbor.X + ",\"y\":" + neighbor.Y + "}", verdict.RewrittenActionJson);
        Assert.DoesNotContain("{\"x\":" + target.X + ",", verdict.RewrittenActionJson);
    }

    [Fact] // legal — 无冲突（全可建格）→ Pass 原样
    public void Buildable_cells_pass_through_untouched()
    {
        var (_, view) = FreshView();
        CellCoord free = RegistryFixture.FreeCells(view, 1)[0];
        string payload = ActionJsonSamples.BuildPlanCells(
            "[{\"x\":" + free.X + ",\"y\":" + free.Y + "}]");

        PreActionHookVerdict verdict = new BuildPlacementConflictHook().Apply(new PreActionContext(
            SystemActionKind.BuildPlan, payload, 0, view));
        Assert.Equal(PreActionVerdictKind.Pass, verdict.Kind);
    }

    [Fact] // boundary — confirm_overwrite=true（显式拆除路径）永不改写
    public void Explicit_overwrite_path_is_never_rewritten()
    {
        var (simulation, view) = FreshView();
        CellCoord target = RegistryFixture.FreeCells(view, 1)[0];
        OccupyACell(simulation, target);
        string payload = ActionJsonSamples.BuildPlanCells(
            "[{\"x\":" + target.X + ",\"y\":" + target.Y + "}]", confirmOverwrite: true);

        PreActionHookVerdict verdict = new BuildPlacementConflictHook().Apply(new PreActionContext(
            SystemActionKind.BuildPlan, payload, simulation.CurrentTick, view));
        Assert.Equal(PreActionVerdictKind.Pass, verdict.Kind);
    }

    [Fact] // boundary — 无世界视图（未接线）/非 BuildPlan 动词 → Pass（钩子惰性）
    public void Hook_is_inert_without_a_view_or_for_other_verbs()
    {
        var hook = new BuildPlacementConflictHook();
        Assert.Equal(PreActionVerdictKind.Pass, hook.Apply(new PreActionContext(
            SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlan(), 0, WorldView: null)).Kind);
        Assert.Equal(PreActionVerdictKind.Pass, hook.Apply(new PreActionContext(
            SystemActionKind.SetAlarm, ActionJsonSamples.SetAlarm(), 0, WorldView: null)).Kind);
    }

    [Fact] // legal — 注册表集成：接线后改写生效（占格目标被偏移，草案在新格可建）
    public void Wired_registry_builds_on_the_offset_cell()
    {
        var (simulation, view) = FreshView();
        CellCoord target = RegistryFixture.FreeCells(view, 2)[0];
        CellCoord neighbor = new(target.X + 1, target.Y);
        Assert.True(view.IsBuildable(neighbor, out _), "夹具前置：右邻应可建（固定种子地形）");
        OccupyACell(simulation, target);

        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(
            sink, view, eraOrdinalProvider: () => EraOrdinals.E2,
            preActionHooks: new IPreActionHook[] { new BuildPlacementConflictHook() });
        string payload = ActionJsonSamples.BuildPlanCells(
            "[{\"x\":" + target.X + ",\"y\":" + target.Y + "}]");

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var draft = Assert.IsType<BuildPlanDraft>(outcome.Payload);
        BuildPlanStep step = Assert.Single(draft.Steps);
        Assert.True(step.Buildable);
        Assert.Equal(neighbor, step.Cell);
        Assert.Contains(sink.Entries, e => e.Message.Contains("hook-rewrite") && e.Message.Contains("auto-offset"));
    }

    // ------------------------------------------------ 纪念馆保护

    [Fact] // illegal — 受保护格上的建造被拦截（ErrHookBlocked 面在链测试；此处验钩子 Block）
    public void Building_on_a_protected_cell_is_blocked()
    {
        var hook = new MemorialProtectionHook(cell => cell.X == 3 && cell.Y == 4);
        string payload = ActionJsonSamples.BuildPlanCells("[{\"x\":3,\"y\":4},{\"x\":9,\"y\":9}]");

        PreActionHookVerdict verdict = hook.Apply(new PreActionContext(
            SystemActionKind.BuildPlan, payload, 0, WorldView: null));
        Assert.Equal(PreActionVerdictKind.Block, verdict.Kind);
        Assert.Contains("build-on-protected-cell:(3,4)", verdict.BlockReason);
        Assert.Contains("纪念馆", verdict.BlockReason);
    }

    [Fact] // legal — 缺省探针（E1 无纪念馆）保护零格：一律 Pass
    public void Default_probe_protects_nothing()
    {
        var hook = new MemorialProtectionHook();
        PreActionHookVerdict verdict = hook.Apply(new PreActionContext(
            SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlan(), 0, WorldView: null));
        Assert.Equal(PreActionVerdictKind.Pass, verdict.Kind);
    }

    [Fact] // legal — 非受保护格/非 BuildPlan 动词照常通过
    public void Unprotected_cells_and_other_verbs_pass()
    {
        var hook = new MemorialProtectionHook(cell => cell.X == 3 && cell.Y == 4);
        Assert.Equal(PreActionVerdictKind.Pass, hook.Apply(new PreActionContext(
            SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlanCells("[{\"x\":5,\"y\":5}]"), 0, null)).Kind);
        Assert.Equal(PreActionVerdictKind.Pass, hook.Apply(new PreActionContext(
            SystemActionKind.SetAlarm, ActionJsonSamples.SetAlarm(), 0, null)).Kind);
    }

    [Fact] // illegal — 注册表集成：受保护格建造 → Err + ErrHookBlocked:memorial-protection:…
    public void Registry_blocks_builds_on_memorial_cells()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(
            sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2,
            preActionHooks: new IPreActionHook[] { new MemorialProtectionHook(cell => cell.X == 6 && cell.Y == 21) });

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan()); // 样例首格=(6,21)
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(
            "ErrHookBlocked:memorial-protection:build-on-protected-cell:(6,21)",
            outcome.Result.ErrorMessage, StringComparison.Ordinal);
    }
}
