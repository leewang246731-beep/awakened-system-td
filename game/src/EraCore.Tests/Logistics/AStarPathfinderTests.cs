using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Tests.Pathfinding;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Unit tests for the transport A* (C-07 precursor, T-047, F-040): straight
/// and diagonal optimality, U-obstacle detour, unreachable → explicit
/// PathNotFound, diagonal corner-cut prevention, terrain-cost avoidance, the
/// BloodCost-never-participates semantic split from the enemy flow field,
/// and the deterministic (f, h, coordinate) tie-break. All searches are real
/// A* runs over a MapGrid (RL2: no mocked routes).
/// </summary>
// [F-040] 运输者网格 A*（直线/绕行/不可达/卡角/代价/平局确定性）
public class AStarPathfinderTests
{
    // ------------------------------------------------------------ geometry

    [Fact]
    public void HorizontalCorridorPathIsTheRowItself()
    {
        // [F-040] 直线图：11×1 走廊路径=整行，代价 10 步 ×10
        var pathfinder = new AStarPathfinder(new MapGrid(11, 1));

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(10, 0));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        var expected = new List<CellCoord>();
        for (int x = 0; x <= 10; x++)
        {
            expected.Add(new CellCoord(x, 0));
        }

        Assert.Equal(expected, result.Cells);
        Assert.Equal(100L, result.Cost);
    }

    [Fact]
    public void DiagonalCorridorPathIsPureDiagonal()
    {
        // [F-040] 对角直线：14 < 2×10，纯对角最优（8 步 ×14 = 112）
        var pathfinder = new AStarPathfinder(new MapGrid(9, 9));

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(8, 8));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        Assert.Equal(9, result.Cells.Count);
        for (int i = 0; i < result.Cells.Count; i++)
        {
            Assert.Equal(i, result.Cells[i].X);
            Assert.Equal(i, result.Cells[i].Y);
        }

        Assert.Equal(8L * 14, result.Cost);
    }

    [Fact]
    public void UShapedWallDetoursThroughTheOpening()
    {
        // [F-040] U 形障碍：墙列 x=4 封 y=0..6，唯一开口 y=7..8。最优绕行
        // (0,4)⤵(3,7) 3 对角+... 手算：到 (4,7) = 42+10（(0,4)⤵(3,7) 三对角
        // 42 + 直行 10），沿 y=7 过墙一格 10，再 (5,7)⤗(8,4) 三对角 42 →
        // 合计 104（经 (4,8) 的路线因 (4,6) 堵死卡角要 118）
        var grid = new MapGrid(9, 9).BlockColumn(4, 0, 6);
        var pathfinder = new AStarPathfinder(grid);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 4), new CellCoord(8, 4));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        Assert.Equal(new CellCoord(0, 4), result.Cells[0]);
        Assert.Equal(new CellCoord(8, 4), result.Cells[^1]);
        Assert.Equal(104L, result.Cost);
        AssertRouteWalkable(result, grid);
    }

    [Fact]
    public void SealedGoalReturnsExplicitPathNotFound()
    {
        // [F-040] 不可达：目标 (2,2) 被完整墙环围死 → 明确 PathNotFound（空路径）
        var grid = new MapGrid(5, 5)
            .Block(1, 1).Block(2, 1).Block(3, 1)
            .Block(1, 2).Block(3, 2)
            .Block(1, 3).Block(2, 3).Block(3, 3);
        var pathfinder = new AStarPathfinder(grid);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(2, 2));

        Assert.Equal(PathQueryStatus.PathNotFound, result.Status);
        Assert.Empty(result.Cells);
        Assert.Equal(1L, pathfinder.SearchCount);
    }

    [Theory]
    [InlineData(0, 0, PathQueryStatus.Found)]          // legal: 两端通畅 → 找到
    [InlineData(1, 0, PathQueryStatus.PathNotFound)]   // illegal: 起点被堵 → 不可达
    [InlineData(0, 1, PathQueryStatus.PathNotFound)]   // illegal: 终点被堵 → 不可达
    [InlineData(1, 1, PathQueryStatus.PathNotFound)]   // illegal: 双端被堵 → 不可达
    public void ImpassableEndpointIsPathNotFound(int blockStart, int blockGoal, PathQueryStatus expected)
    {
        // [F-040] 端点不可通行：起点被堵/终点被堵/双堵 → PathNotFound（合法查询的否定答案）
        var grid = new MapGrid(5, 5);
        if (blockStart == 1)
        {
            grid.Block(0, 0);
        }

        if (blockGoal == 1)
        {
            grid.Block(4, 4);
        }

        var pathfinder = new AStarPathfinder(grid);
        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(4, 4));
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void StartEqualsGoalReturnsSingleCellRoute()
    {
        // [F-040] 边界：起点=终点 → 单格路径，代价 0
        var pathfinder = new AStarPathfinder(new MapGrid(5, 5));

        PathQueryResult result = pathfinder.FindPath(new CellCoord(2, 2), new CellCoord(2, 2));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        Assert.Equal(new[] { new CellCoord(2, 2) }, result.Cells);
        Assert.Equal(0L, result.Cost);
    }

    // ------------------------------------------------------------ corner rule

    [Fact]
    public void DiagonalStepRequiresBothOrthogonalFlanksPassable()
    {
        // [F-040] 对角卡角（与流场同规则）： flank
        // (1,0) 堵死 → (0,0)↘(1,1) 禁走，只能绕 (0,1)，代价 20
        var grid = new MapGrid(3, 3).Block(1, 0);
        var pathfinder = new AStarPathfinder(grid);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(1, 1));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        Assert.Equal(
            new[] { new CellCoord(0, 0), new CellCoord(0, 1), new CellCoord(1, 1) },
            result.Cells);
        Assert.Equal(20L, result.Cost);
    }

    // ------------------------------------------------------------ blood cost split

    [Fact]
    public void BloodCostNeverParticipates_TearableWallIsNotARouteForPorters()
    {
        // [F-040] 与敌方流场的语义区别：可拆建筑列（Passable=false + BloodCost>0）
        // 对流场是 WallTear 拆墙路线，对运输寻路只是不可通行 → PathNotFound，
        // 且 BloodCost 端口一次都不被调用（CountingGrid 探针）
        var mapGrid = new MapGrid(7, 3).BlockColumn(3, 0, 2).BloodColumn(3, 0, 2, 100);
        var counting = new CountingGrid(mapGrid);
        var pathfinder = new AStarPathfinder(counting);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 1), new CellCoord(6, 1));

        Assert.Equal(PathQueryStatus.PathNotFound, result.Status);
        Assert.Equal(0, counting.BloodCalls); // 运输寻路不读血量：没有拆墙语义

        // 对照：同一网格上敌方流场进入拆墙模式（F-044）——两个引擎对同一
        // 障碍的答案刻意不同。
        FlowField field = FlowField.BuildComplete(mapGrid, new CellCoord(6, 1));
        Assert.Equal(FlowFieldMode.WallTear, field.Mode);
    }

    // ------------------------------------------------------------ cost model

    [Fact]
    public void ExpensiveForestCellIsRoutedAround()
    {
        // [F-040] 代价参与：中心格代价 5 → 穿心 2 对角 = 14×5+14 = 84；
        // 贴边绕行 (0,0)→(1,0)→(2,1)→(2,2) = 10+14+10 = 34 → 绕开
        var grid = new MapGrid(3, 3).SetCost(1, 1, 5);
        var pathfinder = new AStarPathfinder(grid);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(2, 2));

        Assert.Equal(PathQueryStatus.Found, result.Status);
        Assert.Equal(34L, result.Cost);
        Assert.DoesNotContain(new CellCoord(1, 1), result.Cells);
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void EqualCostTieBreaksByRemainingDistanceThenCoordinateOrder()
    {
        // [F-040] 平局判定（距离+坐标序）：中心 (1,1) 堵死时 (0,0)→(2,2) 有
        // 两条等代价绕行（40）；逐格手推： (f34,h24) 同分 →
        // idx1 (1,0) 先于 idx3 (0,1) 出堆，目标经 (2,1) 首次松弛 → 右路胜出
        var grid = new MapGrid(3, 3).Block(1, 1);
        var pathfinder = new AStarPathfinder(grid);

        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(2, 2));

        Assert.Equal(
            new[]
            {
                new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(2, 0),
                new CellCoord(2, 1), new CellCoord(2, 2),
            },
            result.Cells);
        Assert.Equal(40L, result.Cost);
    }

    [Fact]
    public void RepeatedSearchesOverIdenticalGridsAreBitIdentical()
    {
        // [F-040] 确定性：同一网格两次独立搜索 → 路径/代价/哈希全等
        var gridA = TestMazes.SparseObstacles(24, 31, 0.10);
        var gridB = TestMazes.SparseObstacles(24, 31, 0.10);

        var first = new AStarPathfinder(gridA).FindPath(new CellCoord(0, 0), new CellCoord(23, 23));
        var second = new AStarPathfinder(gridB).FindPath(new CellCoord(0, 0), new CellCoord(23, 23));

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Cells, second.Cells);
        Assert.Equal(first.Cost, second.Cost);
        Assert.Equal(first.Hash, second.Hash);
    }

    // ------------------------------------------------------------ arguments

    [Theory]
    [InlineData(2, 2, 3, 3, true)]   // legal: 内部点
    [InlineData(0, 0, 4, 4, true)]   // legal: 对角角落
    [InlineData(0, 4, 4, 0, true)]   // legal: 反对角角落
    [InlineData(-1, 0, 4, 4, false)] // illegal: 起点出界（左）
    [InlineData(0, -1, 4, 4, false)] // illegal: 起点出界（上）
    [InlineData(5, 0, 4, 4, false)]  // illegal: 起点出界（右，5×5 网格）
    [InlineData(0, 0, 0, 5, false)]  // illegal: 终点出界（下）
    [InlineData(0, 0, -3, 0, false)] // illegal: 终点出界（左，负值）
    public void OutOfBoundsEndpointsAreRejected(int sx, int sy, int gx, int gy, bool inBounds)
    {
        // [F-040] 边界三行：合法/角点合法/出界抛 ArgumentOutOfRangeException
        var pathfinder = new AStarPathfinder(new MapGrid(5, 5));
        var start = new CellCoord(sx, sy);
        var goal = new CellCoord(gx, gy);
        if (inBounds)
        {
            PathQueryResult result = pathfinder.FindPath(start, goal);
            Assert.Equal(PathQueryStatus.Found, result.Status);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => pathfinder.FindPath(start, goal));
        }
    }

    [Fact]
    public void NullEndpointsAndGridAreRejected()
    {
        // [F-040] 非法：null 网格/null 端点抛 ArgumentNullException；非正尺寸抛 ArgumentException
        Assert.Throws<ArgumentNullException>(() => new AStarPathfinder(null!));
        Assert.Throws<ArgumentException>(() => new AStarPathfinder(new MapGrid(0, 5)));
        var pathfinder = new AStarPathfinder(new MapGrid(5, 5));
        Assert.Throws<ArgumentNullException>(() => pathfinder.FindPath(null!, new CellCoord(1, 1)));
        Assert.Throws<ArgumentNullException>(() => pathfinder.FindPath(new CellCoord(1, 1), null!));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Route validity: adjacent 8-neighbour steps, every cell passable.</summary>
    private static void AssertRouteWalkable(PathQueryResult result, MapGrid grid)
    {
        for (int i = 1; i < result.Cells.Count; i++)
        {
            int dx = Math.Abs(result.Cells[i].X - result.Cells[i - 1].X);
            int dy = Math.Abs(result.Cells[i].Y - result.Cells[i - 1].Y);
            Assert.True(dx <= 1 && dy <= 1 && (dx + dy) > 0, $"Step {i - 1}→{i} is not an 8-neighbour move.");
            Assert.True(grid.Passable(result.Cells[i]), $"Step {i} enters blocked cell {result.Cells[i]}.");
        }
    }
}
