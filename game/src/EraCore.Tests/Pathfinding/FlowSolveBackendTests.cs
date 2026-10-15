using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Unit tests for the T-101 backend-strategy factory
/// (<see cref="FlowSolveBackendFactory"/>, C-11): every enum member solves a
/// real maze to the same field (backend-level equivalence smoke), the
/// minimal-slice boundary (quota = 1) still terminates bit-identically, and
/// the illegal inputs (zero/negative quota, quota on the burst backend,
/// unknown enum values, null field) are rejected. The N-maze grid-equal
/// regression itself lives in
/// <see cref="FlowSolveBackendEquivalenceTests"/>; the 300×300 throughput
/// anchor in <see cref="FlowSolveBackendThroughputTests"/>.
/// </summary>
// [C-11] [F-159] [F-164] 流场分帧切片后端策略工厂（T-101）
public class FlowSolveBackendTests
{
    private static MapGrid SampleMaze() => TestMazes.SparseObstacles(48, 7, 0.2);

    // ------------------------------------------------------------ legal

    [Theory] // legal
    [InlineData(FlowSolveBackend.FullBurst)]
    [InlineData(FlowSolveBackend.PerTickCellQuota)]
    [InlineData(FlowSolveBackend.PerFrameCellBudget)]
    public void EveryBackendSolvesToTheSameReadyField(FlowSolveBackend backend)
    {
        // [F-159] 三后端对同一输入产出同一就绪流场（等价性冒烟——逐格回归在 Equivalence 测试）
        var burst = FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), FlowSolveBackend.FullBurst);

        // burst 无切片旋钮（传配额=参数误用被拒，见 BurstBackendRejectsASliceQuota）。
        int? quota = backend == FlowSolveBackend.FullBurst ? null : 37;
        var solved = FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), backend, cellsPerSlice: quota);

        Assert.True(solved.IsReady);
        Assert.Equal(burst.Mode, solved.Mode);
        Assert.Equal(burst.Hash, solved.Hash);
        Assert.Equal(burst.ProcessedCells, solved.ProcessedCells);
    }

    [Fact] // legal
    public void SlicedBackendsUseTheirDocumentedDefaultQuotaWhenUnspecified()
    {
        // [F-164] 默认预算接线：per-tick 默认=FlowFieldManager.DefaultCellsPerTick
        // （2048，T-067 校准档——跨类钉死防漂移）；per-frame 默认=Web 口径
        // 独立标定的 DefaultWebFrameCellBudget。可观察面：默认配额下一次
        // Advance 在小图上真实推进，续推到完成与 burst 全等。
        Assert.Equal(FlowFieldManager.DefaultCellsPerTick, FlowSolveBackendFactory.DefaultTickQuota);
        Assert.Equal(2048, FlowSolveBackendFactory.DefaultWebFrameCellBudget);

        foreach (FlowSolveBackend backend in new[]
                 { FlowSolveBackend.PerTickCellQuota, FlowSolveBackend.PerFrameCellBudget })
        {
            var field = FlowField.StartBuild(SampleMaze(), new CellCoord(47, 47));

            bool complete = FlowSolveBackendFactory.Advance(field, backend);

            // 48×48 快照+泛洪合计约 2×2304 > 2048：默认配额一次调用不保证完成，
            // 但必须做了真实工作（ProcessedCells>0）——切片推进真实发生。
            Assert.True(field.ProcessedCells > 0, "the default quota must do real work on the first slice");
            while (!complete)
            {
                complete = FlowSolveBackendFactory.Advance(field, backend);
            }

            var burst = FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), FlowSolveBackend.FullBurst);
            Assert.True(field.IsReady);
            Assert.Equal(burst.Hash, field.Hash);
        }
    }

    // ------------------------------------------------------------ boundary

    [Theory] // boundary
    [InlineData(FlowSolveBackend.PerTickCellQuota)]
    [InlineData(FlowSolveBackend.PerFrameCellBudget)]
    public void MinimalQuotaOfOneCellPerSliceStillTerminatesBitIdentically(FlowSolveBackend backend)
    {
        // [F-159] 最小切片边界：配额=1（每片恰一个记账工作单元）仍收敛且逐位相等
        var burst = FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), FlowSolveBackend.FullBurst);

        var solved = FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), backend, cellsPerSlice: 1);

        Assert.True(solved.IsReady);
        Assert.Equal(burst.Hash, solved.Hash);
        Assert.True(solved.ProcessedCells > 2304, "per-cell slicing must span many more units than one snapshot pass");
    }

    [Fact] // boundary
    public void FullBurstOneAdvanceSliceCompletesAShipScaleGrid()
    {
        // [F-159] burst 单片边界：一次 Advance 即完成 128×128（任务口径图幅）
        var field = FlowField.StartBuild(TestMazes.SparseObstacles(128, 7, 0.3), new CellCoord(127, 127));

        bool complete = FlowSolveBackendFactory.Advance(field, FlowSolveBackend.FullBurst);

        Assert.True(complete);
        Assert.True(field.IsReady);
    }

    // ------------------------------------------------------------ illegal

    [Theory] // illegal
    [InlineData(FlowSolveBackend.PerTickCellQuota, 0)]
    [InlineData(FlowSolveBackend.PerTickCellQuota, -1)]
    [InlineData(FlowSolveBackend.PerFrameCellBudget, 0)]
    [InlineData(FlowSolveBackend.PerFrameCellBudget, -97)]
    public void SolveRejectsNonPositiveSliceQuota(FlowSolveBackend backend, int quota)
    {
        // [F-159] 非法：零/负配额会让阻塞式 Solve 永不终止——入口必须拒绝
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowSolveBackendFactory.Solve(SampleMaze(), new CellCoord(47, 47), backend, cellsPerSlice: quota));
    }

    [Theory] // illegal
    [InlineData(FlowSolveBackend.PerTickCellQuota, 0)]
    [InlineData(FlowSolveBackend.PerFrameCellBudget, -5)]
    public void AdvanceRejectsNonPositiveSliceQuota(FlowSolveBackend backend, int quota)
    {
        var field = FlowField.StartBuild(SampleMaze(), new CellCoord(47, 47));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowSolveBackendFactory.Advance(field, backend, quota));
    }

    [Fact] // illegal
    public void BurstBackendRejectsASliceQuota()
    {
        // [F-159] 非法：burst 无切片旋钮——传配额=参数误用，静默忽略比拒绝更危险
        Assert.Throws<ArgumentException>(
            () => FlowSolveBackendFactory.Solve(
                SampleMaze(), new CellCoord(47, 47), FlowSolveBackend.FullBurst, cellsPerSlice: 128));

        var field = FlowField.StartBuild(SampleMaze(), new CellCoord(47, 47));
        Assert.Throws<ArgumentException>(
            () => FlowSolveBackendFactory.Advance(field, FlowSolveBackend.FullBurst, 128));
    }

    [Fact] // illegal
    public void UnknownBackendValueIsRejected()
    {
        // [F-159] 非法：未定义枚举值（switch 显式 default 臂，无反射——AOT 安全）
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowSolveBackendFactory.Solve(
                SampleMaze(), new CellCoord(47, 47), (FlowSolveBackend)99));

        var field = FlowField.StartBuild(SampleMaze(), new CellCoord(47, 47));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowSolveBackendFactory.Advance(field, (FlowSolveBackend)(-1)));
    }

    [Fact] // illegal
    public void NullFieldIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => FlowSolveBackendFactory.Advance(null!, FlowSolveBackend.PerTickCellQuota));
    }

    [Fact] // illegal
    public void SolvePropagatesStartBuildContractChecks()
    {
        // [F-159] 非法：网格/目标契约校验由 StartBuild 单点把守（委托不复制）
        Assert.Throws<ArgumentNullException>(
            () => FlowSolveBackendFactory.Solve(null!, new CellCoord(0, 0), FlowSolveBackend.FullBurst));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowSolveBackendFactory.Solve(
                SampleMaze(), new CellCoord(48, 0), FlowSolveBackend.PerTickCellQuota));
    }
}
