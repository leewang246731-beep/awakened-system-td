using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-071][F-065] 第 5 段执行通道单元面（T-053）：BuildPlan 步的
// 校验→落子路径（经 SimCommand 泳道）、三类失败码（无格可建/资源不足/
// 部分格被拒）与自纠调整器（带错误重入：按失败码从当前状态重建载荷）。
public class PipelineExecutorTests
{
    private static string BuildPlanJson(params (int X, int Y)[] cells) =>
        System.Text.Json.JsonSerializer.Serialize(
            new BuildPlanActionJson(
                "BuildPlan",
                "era1:tower-wood-arrow",
                cells.Select(cell => new CellJson(cell.X, cell.Y)).ToArray()),
            PipelinePlannerJsonContext.Default.BuildPlanActionJson);

    [Fact] // legal：合法步——注册表校验通过→SimCommand 落子→Ok 且库存已扣
    public void BuildPlan_step_places_through_the_sim_command_lane()
    {
        using var harness = new PipelineHarness();
        var executor = new PipelineExecutor(
            new EraCore.Agent.Actions.ActionRegistry(harness.LogSink,
                new SimulationActionView(harness.Simulation, PipelineHarness.Config()), eraOrdinalProvider: () => 2),
            harness.Simulation,
            new SimulationActionView(harness.Simulation, PipelineHarness.Config()));

        var outcome = executor.InvokeStep(SystemActionKind.BuildPlan, BuildPlanJson((12, 12), (11, 11)));

        Assert.Equal(EraCore.Contracts.Actions.ActionResultStatus.Ok, outcome.Result.Status);
        Assert.IsType<EraCore.Agent.Actions.BuildPlanDraft>(outcome.Payload);
        Assert.Equal(2, harness.Simulation.Towers.Count);
        Assert.Equal(1, harness.Simulation.CurrentTick);
        Assert.Equal(140.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
    }

    [Fact] // illegal：格子全部不可建（占用水格/占用格）→ Err no-buildable-cells，零落子零扣费
    public void BuildPlan_step_fails_when_no_cell_is_buildable()
    {
        using var harness = new PipelineHarness();
        var executor = MakeExecutor(harness);

        var outcome = executor.InvokeStep(SystemActionKind.BuildPlan, BuildPlanJson((2, 14))); // 仓库格

        Assert.Equal(EraCore.Contracts.Actions.ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(PipelineExecutor.NoBuildableCells, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(200.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
    }

    [Fact] // illegal：库存不足 → Err insufficient-resources（不落子）
    public void BuildPlan_step_fails_when_the_stock_cannot_cover_the_draft()
    {
        using var harness = new PipelineHarness(initialWood: 30.0);
        var executor = MakeExecutor(harness);

        var outcome = executor.InvokeStep(SystemActionKind.BuildPlan, BuildPlanJson((12, 12), (11, 11)));

        Assert.Equal(EraCore.Contracts.Actions.ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(PipelineExecutor.InsufficientResourcesPrefix, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(30.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
    }

    [Fact] // boundary：部分格被拒 → Err cells-rejected（混合态=失败，触发上层自纠）
    public void BuildPlan_step_fails_when_only_part_of_the_cells_placeable()
    {
        using var harness = new PipelineHarness();
        var executor = MakeExecutor(harness);

        var outcome = executor.InvokeStep(SystemActionKind.BuildPlan, BuildPlanJson((12, 12), (2, 14)));

        Assert.Equal(EraCore.Contracts.Actions.ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(PipelineExecutor.CellsRejectedPrefix, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("cell-occupied", outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers); // 混合态一个都不建
    }

    [Fact] // legal：自纠调整——cells-rejected 错误重入后仅保留可建格
    public void AdjustBuildPlan_drops_rejected_cells()
    {
        using var harness = new PipelineHarness();
        var executor = MakeExecutor(harness);
        string original = BuildPlanJson((12, 12), (2, 14)); // (2,14)=仓库占用

        string? adjusted = executor.AdjustBuildPlan(original, PipelineExecutor.CellsRejectedPrefix + ":2,14:cell-occupied");

        Assert.NotNull(adjusted);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<BuildPlanActionJson>(
            adjusted!, PipelinePlannerJsonContext.Default.BuildPlanActionJson);
        Assert.Single(parsed!.Cells);
        Assert.Equal(12, parsed.Cells[0].X);
        Assert.Equal(12, parsed.Cells[0].Y);
    }

    [Fact] // legal：自纠调整——资源不足错误重入后取可负担前缀（按计划序）
    public void AdjustBuildPlan_takes_the_affordable_prefix()
    {
        using var harness = new PipelineHarness(initialWood: 35.0); // 1 座 30 可负担、2 座 60 不可
        var executor = MakeExecutor(harness);
        string original = BuildPlanJson((12, 12), (11, 11));

        string? adjusted = executor.AdjustBuildPlan(
            original, PipelineExecutor.InsufficientResourcesPrefix + ":need-60-have-35");

        Assert.NotNull(adjusted);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<BuildPlanActionJson>(
            adjusted!, PipelinePlannerJsonContext.Default.BuildPlanActionJson);
        Assert.Single(parsed!.Cells);
        Assert.Equal((12, 12), (parsed.Cells[0].X, parsed.Cells[0].Y));
    }

    [Fact] // illegal：无可调整（全部格不可建 / 调整后与原载荷相同）→ null（上层如实汇报）
    public void AdjustBuildPlan_returns_null_when_no_useful_retry_exists()
    {
        using var harness = new PipelineHarness();
        var executor = MakeExecutor(harness);

        Assert.Null(executor.AdjustBuildPlan(BuildPlanJson((2, 14)), PipelineExecutor.NoBuildableCells));
        Assert.Null(executor.AdjustBuildPlan(BuildPlanJson((12, 12)), PipelineExecutor.CellsRejectedPrefix + ":none")); // 全可建却报被拒=无意义重试
        Assert.Null(executor.AdjustBuildPlan(BuildPlanJson(), PipelineExecutor.InsufficientResourcesPrefix + ":x")); // 非法载荷
    }

    private static PipelineExecutor MakeExecutor(PipelineHarness harness)
    {
        var view = new SimulationActionView(harness.Simulation, PipelineHarness.Config());
        return new PipelineExecutor(
            new EraCore.Agent.Actions.ActionRegistry(harness.LogSink, view, eraOrdinalProvider: () => 2),
            harness.Simulation,
            view);
    }
}
