using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Seam tests for the <see cref="IFlowGrid"/> port (C-11): the flow field must
/// be genuinely driven by the grid port — one snapshot consultation per cell
/// per method — and construction must reject broken inputs. Also pins the
/// documented clamps (terrain cost floor, negative blood to zero).
/// </summary>
// [C-11] 流场网格端口接缝（快照查询真实采样 + 构造校验三行）
public class IFlowGridTests
{
    private static FlowField Build(MapGrid grid, CellCoord target) =>
        FlowField.BuildComplete(grid, target);

    [Fact]
    public void SnapshotConsultsEveryPortMethodOncePerCell()
    {
        // [F-043] 真实实现证明：泛洪由端口驱动，快照每格各查询一次（RL2 禁 mock）
        var probe = new CountingGrid(new MapGrid(8, 6));

        var field = FlowField.BuildComplete(probe, new CellCoord(7, 5));

        Assert.True(field.IsReady);
        Assert.Equal(8 * 6, probe.CostCalls);
        Assert.Equal(8 * 6, probe.PassableCalls);
        Assert.Equal(8 * 6, probe.BloodCalls);
    }

    [Fact]
    public void SingleCellGridServesTargetImmediately()
    {
        // [F-043] boundary: 最小合法网格——唯一格即目标
        var field = Build(new MapGrid(1, 1), new CellCoord(0, 0));

        Assert.True(field.IsReady);
        Assert.Equal(FlowFieldMode.Normal, field.Mode);
        Assert.Equal(0L, field.Distance(new CellCoord(0, 0)));
        Assert.Equal(new CellCoord(0, 0), field.NextStep(new CellCoord(0, 0)));
    }

    [Fact]
    public void PortClampsBelowFloorCostAndNegativeBlood()
    {
        // [F-043] boundary: 低于契约下限的代价被钳制（0 代价边会破坏梯度收敛）
        var grid = new MapGrid(3, 1)
            .SetCost(1, 0, -5)
            .Blood(1, 0, -7);

        var field = Build(grid, new CellCoord(2, 0));

        // (1,0) counts as cost 1, blood 0: enter twice at weight 10 → 20.
        Assert.Equal(20L, field.Distance(new CellCoord(0, 0)));
    }

    [Theory] // legal
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void StartBuildAcceptsNonNegativeBloodMultiplier(int multiplier)
    {
        var field = FlowField.StartBuild(new MapGrid(2, 2), new CellCoord(1, 1), multiplier);

        Assert.NotNull(field);
        Assert.False(field.IsReady);
    }

    [Theory] // illegal
    [InlineData(-1)]
    [InlineData(-100)]
    public void StartBuildRejectsNegativeBloodMultiplier(int multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowField.StartBuild(new MapGrid(2, 2), new CellCoord(1, 1), multiplier));
    }

    [Fact] // illegal
    public void StartBuildRejectsNullGrid()
    {
        Assert.Throws<ArgumentNullException>(
            () => FlowField.StartBuild(null!, new CellCoord(0, 0)));
    }

    [Fact] // illegal
    public void StartBuildRejectsNullTarget()
    {
        Assert.Throws<ArgumentNullException>(
            () => FlowField.StartBuild(new MapGrid(3, 3), null!));
    }

    [Theory] // illegal: target outside the grid
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 3)]
    [InlineData(-100, 100)]
    public void StartBuildRejectsTargetOutsideGrid(int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FlowField.StartBuild(new MapGrid(3, 3), new CellCoord(x, y)));
    }

    [Theory] // illegal: non-positive dimensions
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 4)]
    public void StartBuildRejectsNonPositiveGridDimensions(int width, int height)
    {
        var grid = new FixedSizeGrid(width, height);

        Assert.Throws<ArgumentException>(() => FlowField.StartBuild(grid, new CellCoord(0, 0)));
    }

    /// <summary>Minimal grid whose only job is carrying (possibly invalid) dimensions.</summary>
    private sealed class FixedSizeGrid(int width, int height) : IFlowGrid
    {
        public int Width => width;

        public int Height => height;

        public int Cost(CellCoord c) => 1;

        public bool Passable(CellCoord c) => true;

        public int BloodCost(CellCoord c) => 0;
    }
}
