using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062][F-071] C-03 集成：BuildPlan 草案接 Simulation 真实状态——
// 命令流建成链后的占用格拒绝/资源口径/拆除标注/不落子（执行属 T-053 管线）。
// 时窗：MilestoneCommands 到 tick 700 已落地（人力/采集/加工/两塔/三民夫）。
public class BuildPlanSimulationIntegrationTests
{
    [Fact]
    public void Draft_over_the_running_level_rejects_occupied_cells()
    {
        // legal 状态读：真实占用格（劳工营 cell）拒绝 + 风险标注；空地步照常可建
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create(runMilestone: true);
        var registry = new ActionRegistry(sink, view, eraOrdinalProvider: () => EraOrdinals.E2);
        long hashBefore = simulation.ComputeStateHash();
        CellCoord freeCell = FindFreeCell(simulation, view);
        string payload = "{\"action\":\"BuildPlan\",\"tower_id\":\"era1:tower-wood-arrow\",\"cells\":["
            + "{\"x\":" + SimE1Fixture.LaborCampCell.X + ",\"y\":" + SimE1Fixture.LaborCampCell.Y + "},"
            + "{\"x\":" + freeCell.X + ",\"y\":" + freeCell.Y + "}]}";

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var draft = Assert.IsType<BuildPlanDraft>(outcome.Payload);

        BuildPlanStep occupied = draft.Steps[0];
        Assert.False(occupied.Buildable);
        Assert.Equal("cell-occupied", occupied.Rejection);
        Assert.Contains(draft.Risks, r => r.Contains("cell-occupied"));

        Assert.True(draft.Steps[1].Buildable);
        Assert.Equal(simulation.Stock.AmountOf(view.CostCurrency), draft.AvailableCurrency);
        Assert.Equal(simulation.ComputeStateHash(), hashBefore); // 仍是草案：哈希不动
    }

    [Fact]
    public void Draft_flags_shortfall_against_the_depleted_pool()
    {
        // boundary→illegal 状态：库存只授 10 木（< 单塔 30）→ Affordable=false + 资源不足标注
        var sink = new CapturingLogSink();
        var (_, view) = RegistryFixture.Create(
            initialStock: new List<KeyValuePair<ResourceKey, double>> { new(SimE1Fixture.Wood, 10.0) });
        var registry = new ActionRegistry(sink, view, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan());
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var draft = Assert.IsType<BuildPlanDraft>(outcome.Payload);
        Assert.False(draft.Affordable);
        Assert.Equal(10.0, draft.AvailableCurrency);
        Assert.Contains(draft.Risks, r => r.Contains("insufficient-resources"));
    }

    [Fact]
    public void Draft_with_confirm_overwrite_converts_occupied_cells_into_demolition_steps()
    {
        // boundary：显式 confirm_overwrite=true → 占用格转为可建+拆除标注（危险动作必须显式）
        var sink = new CapturingLogSink();
        var (_, view) = RegistryFixture.Create(runMilestone: true);
        var registry = new ActionRegistry(sink, view, eraOrdinalProvider: () => EraOrdinals.E2);
        string payload = "{\"action\":\"BuildPlan\",\"tower_id\":\"era1:tower-wood-arrow\",\"confirm_overwrite\":true,\"cells\":["
            + "{\"x\":" + SimE1Fixture.LaborCampCell.X + ",\"y\":" + SimE1Fixture.LaborCampCell.Y + "}]}";

        ActionOutcome outcome = registry.Invoke(payload);
        var draft = Assert.IsType<BuildPlanDraft>(outcome.Payload);
        BuildPlanStep demolition = Assert.Single(draft.Steps);
        Assert.True(demolition.Buildable);
        Assert.True(demolition.DemolishesExisting);
        Assert.Contains(draft.Risks, r => r.Contains("demolition"));
    }

    [Fact]
    public void Registry_denies_plans_that_break_the_era_cell_cap()
    {
        // illegal：E2 上限=8 格，9 格清单 → schema 拒绝并记日志（时代相关上限走同一张表）
        var sink = new CapturingLogSink();
        var (_, view) = RegistryFixture.Create();
        var registry = new ActionRegistry(sink, view, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan(cellCount: 9));
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("/cells", outcome.Result.ErrorMessage);
        Assert.Contains("verdict=denied", sink.Entries.Single().Message);
        Assert.Contains("payload=", sink.Entries.Single().Message);
    }

    /// <summary>扫描一个真实可建格（seed 固定下 90% 地格为可通行、占用格有限）。</summary>
    private static CellCoord FindFreeCell(Simulation simulation, SimulationActionView view)
    {
        for (int y = 0; y < simulation.World.Height; y++)
        {
            for (int x = 0; x < simulation.World.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (view.IsBuildable(cell, out _))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("整图无可建格（fixture 异常）");
    }
}
