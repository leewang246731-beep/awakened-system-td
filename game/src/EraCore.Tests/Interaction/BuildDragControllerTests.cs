using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Interaction;

// [C-23][F-036][F-119] T-056 拖放建造状态机（BuildDragController）：idle/
// dragging/invalid 三态、矩形批量（cells≤4+2×E 同限，玩家与系统同规）、
// ESC 取消、松开产出 (towerId, cells) 交执行泳道，以及格子合法性缓存
// （同会话重复查询命中缓存不再重跑 BuildPlan 草案）。
public class BuildDragControllerTests
{
    private const string WoodArrow = "era1:tower-wood-arrow";

    private static (SimulationActionView View, Func<int> Era) MakeView()
    {
        SimulationConfig config = PipelineHarness.Config();
        var simulation = new Simulation(config, new SimRng(PipelineHarness.Seed), PipelineHarness.Seed);
        return (new SimulationActionView(simulation, config), static () => PipelineHarness.AgentEra);
    }

    [Fact] // legal：合法拖放全链——按下→单格绿→拖出矩形→松开产出批量格，状态回 Idle
    public void Drag_from_press_to_commit_produces_the_capped_cell_list() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, new CellCoord(11, 11)); // 执行道测试已证明的绿格
        Assert.Equal(BuildDragState.Dragging, controller.State);
        BuildDragPreview single = controller.Preview();
        BuildDragPreviewCell anchor = Assert.Single(single.Cells);
        Assert.True(anchor.Buildable, "E1 夹具 (11,11) 为可建格");
        Assert.Equal(new CellCoord(11, 11), anchor.Cell);
        Assert.Equal(WoodArrow, single.TowerId);
        Assert.True(single.Affordable);

        controller.UpdateDrag(new CellCoord(12, 12));
        BuildDragPreview rect = controller.Preview();
        int buildable = rect.Cells.Count(cell => cell.Buildable);
        Assert.InRange(buildable, 2, 4); // 至少含两个已证明绿格，全红/单绿都不成立
        Assert.False(rect.CappedByLimit);

        BuildDragCommit? commit = controller.Commit();
        Assert.NotNull(commit);
        Assert.Equal(WoodArrow, commit!.TowerId);
        Assert.Equal(buildable, commit.Cells.Count); // 提交=矩形内绿格集（红格由 UI 门滤除）
        Assert.Equal(BuildDragState.Idle, controller.State);
        Assert.Empty(controller.Preview().Cells);
    }

    [Fact] // boundary：批量同限——矩形超 4+2×E 截断为行-major 前缀；上限读 K-3 冻结表
    public void Rectangle_beyond_the_shared_cap_is_truncated_row_major() // [F-036]
    {
        Assert.Equal(6, PlayerBuildLane.MaxBuildCells(1));  // 4+2×E1（K-3 冻结表）
        Assert.Equal(8, PlayerBuildLane.MaxBuildCells(2));  // 4+2×E2

        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, new CellCoord(12, 10));
        controller.UpdateDrag(new CellCoord(14, 13)); // 3×4=12 格 > 8
        BuildDragPreview preview = controller.Preview();
        Assert.True(preview.CappedByLimit);
        Assert.Equal(PlayerBuildLane.MaxBuildCells(2), preview.Cells.Count);
        Assert.Equal(new CellCoord(12, 10), preview.Cells[0].Cell);
        Assert.Equal(new CellCoord(13, 12), preview.Cells[^1].Cell); // 行-major 前缀末格（两整行+两格）
    }

    [Fact] // illegal：全红矩形（仓库格）→ Invalid 态，松开不产出提交（UI 门；真发送仍由注册表裁决）
    public void All_rejected_cells_flip_the_state_to_invalid_and_commit_nothing() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, SimE1Fixture.WarehouseCell); // (2,14) 仓库占用格
        Assert.Equal(BuildDragState.Invalid, controller.State);
        BuildDragPreview preview = controller.Preview();
        Assert.Single(preview.Cells);
        Assert.False(preview.Cells[0].Buildable);
        Assert.Equal("cell-occupied", preview.Cells[0].Rejection);
        Assert.False(preview.Affordable);

        Assert.Null(controller.Commit());
        Assert.Equal(BuildDragState.Idle, controller.State);
    }

    [Fact] // legal：ESC 取消——任意拖动中 Cancel 清预览回 Idle，之后的指针移动无副作用
    public void Esc_cancels_the_session_and_subsequent_motion_is_inert() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, new CellCoord(12, 12));
        controller.UpdateDrag(new CellCoord(13, 12));
        controller.Cancel();
        Assert.Equal(BuildDragState.Idle, controller.State);
        Assert.Empty(controller.Preview().Cells);

        controller.UpdateDrag(new CellCoord(14, 14)); // 未武装的指针移动
        Assert.Equal(BuildDragState.Idle, controller.State);
        Assert.Empty(controller.Preview().Cells);
    }

    [Fact] // legal：格子合法性缓存——同会话重叠矩形重扫时命中缓存，BuildPlan 草案零增量
    public void Overlapping_rescans_are_served_from_the_session_cache() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, new CellCoord(12, 12));
        Assert.Equal(1, controller.ViewDrafts); // 锚格已过一次草案
        Assert.Equal(0, controller.CacheHits);

        controller.UpdateDrag(new CellCoord(12, 13)); // 扩到 2 格：锚格命中缓存，新格 1 次草案
        Assert.Equal(1, controller.CacheHits);
        Assert.Equal(2, controller.ViewDrafts);

        controller.UpdateDrag(new CellCoord(12, 12)); // 收回 1 格——两格均缓存
        Assert.Equal(2, controller.CacheHits);
        Assert.Equal(2, controller.ViewDrafts);

        controller.UpdateDrag(new CellCoord(13, 13)); // 扩到 4 格——再 2 命中 2 新草案
        Assert.Equal(4, controller.CacheHits);
        Assert.Equal(4, controller.ViewDrafts);

        // 新会话（BeginDrag）清缓存——同一格重新走草案
        controller.BeginDrag(WoodArrow, new CellCoord(12, 12));
        Assert.Equal(0, controller.CacheHits);
        Assert.Equal(1, controller.ViewDrafts);
    }

    [Fact] // illegal：未知塔 ID → Invalid，不产出提交（注册表的 ErrTargetInvalid 同源语义）
    public void Unknown_tower_id_is_invalid_and_commits_nothing() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag("era1:tower-not-in-catalog", new CellCoord(12, 12));
        Assert.Equal(BuildDragState.Invalid, controller.State);
        Assert.All(controller.Preview().Cells, cell => Assert.Equal("tower-unknown", cell.Rejection));
        Assert.Null(controller.Commit());
    }

    [Fact] // boundary：预览造价=单价×可建格数，与 BuildPlan 草案报价同源（wood-arrow 30/座）
    public void Preview_cost_quotes_the_draft_unit_price_per_buildable_cell() // [F-036]
    {
        (SimulationActionView view, Func<int> era) = MakeView();
        var controller = new BuildDragController(view, era);

        controller.BeginDrag(WoodArrow, new CellCoord(11, 11));
        controller.UpdateDrag(new CellCoord(12, 12));
        BuildDragPreview preview = controller.Preview();
        int buildable = preview.Cells.Count(cell => cell.Buildable);
        Assert.True(buildable >= 1, "夹具象限至少一可建格（(11,11)/(12,12) 已由执行道测试证明为绿）");

        Assert.Equal(SimEra1Content.WoodArrowTower().CostTotal, preview.UnitCost);
        Assert.Equal(SimEra1Content.WoodArrowTower().CostTotal * buildable, preview.TotalCost);
        Assert.True(preview.Affordable);
    }
}
