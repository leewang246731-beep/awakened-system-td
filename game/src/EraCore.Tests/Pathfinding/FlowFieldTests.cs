using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Unit + logic tests for <see cref="FlowField"/> (C-11, F-043/F-044):
/// straight-line optimality, U-obstacle detour, blood-cost wall decisions
/// (thin torn vs thick detoured), diagonal corner-cut prevention, wall-tear
/// mode on sealed maps, exact ×5 blood cost arithmetic, and determinism of
/// the field hash. All flows are real Dijkstra floods over a MapGrid (RL2).
/// </summary>
// [C-11] 流场核心行为（直线/绕行/血量代价/卡角/拆墙/确定性）
public class FlowFieldTests
{
    // ------------------------------------------------------------ geometry

    [Fact]
    public void StraightLineMapHorizontalPathIsStraight()
    {
        // [F-043] 直线图路径=直线（水平）
        var field = FlowField.BuildComplete(new MapGrid(11, 1), new CellCoord(10, 0));

        var path = field.BuildPath(new CellCoord(0, 0));

        var expected = new List<CellCoord>();
        for (int x = 0; x <= 10; x++)
        {
            expected.Add(new CellCoord(x, 0));
        }

        Assert.Equal(expected, path);
        Assert.Equal(100L, field.Distance(new CellCoord(0, 0)));
    }

    [Fact]
    public void StraightLineMapVerticalPathIsStraight()
    {
        // [F-043] 直线图路径=直线（垂直）
        var field = FlowField.BuildComplete(new MapGrid(1, 11), new CellCoord(0, 10));

        var path = field.BuildPath(new CellCoord(0, 0));

        Assert.Equal(11, path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            Assert.Equal(0, path[i].X);
            Assert.Equal(i, path[i].Y);
        }
    }

    [Fact]
    public void StraightLineMapDiagonalPathIsPureDiagonal()
    {
        // [F-043] 直线图路径=直线（对角：14 < 2×10，纯对角最优）
        var field = FlowField.BuildComplete(new MapGrid(9, 9), new CellCoord(8, 8));

        var path = field.BuildPath(new CellCoord(0, 0));

        Assert.Equal(9, path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            Assert.Equal(i, path[i].X);
            Assert.Equal(i, path[i].Y);
        }

        Assert.Equal(8L * 14, field.Distance(new CellCoord(0, 0)));
    }

    [Fact]
    public void UShapedObstacleDetoursThroughTheOpening()
    {
        // [F-043] U 形障碍绕行：墙列 x=4 从 y=0..6 封死，唯一开口在 y=7..8
        var grid = new MapGrid(9, 9).BlockColumn(4, 0, 6);
        var field = FlowField.BuildComplete(grid, new CellCoord(8, 4));

        var path = field.BuildPath(new CellCoord(0, 4));

        AssertPathValid(path, new CellCoord(0, 4), new CellCoord(8, 4), field, grid);
        bool crossedAtOpening = path.Contains(new CellCoord(4, 7)) || path.Contains(new CellCoord(4, 8));
        Assert.True(crossedAtOpening, "path must cross the wall column through the opening rows");
        foreach (var cell in path)
        {
            Assert.False(grid.IsBlocked(cell.X, cell.Y), $"path must not enter blocked cell {cell}");
        }

        // Optimal route: 6 diagonal steps (84) + 2 straight steps (20) = 104.
        Assert.Equal(104L, field.Distance(new CellCoord(0, 4)));
    }

    [Fact]
    public void DiagonalCornerCutIsForbidden()
    {
        // [F-043] 对角卡角检测：夹角双阻塞时不得穿墙角（(3,2)/(2,3) 夹住 (2,2)→(3,3)）
        var grid = new MapGrid(7, 7)
            .Block(3, 2)
            .Block(2, 3);
        var field = FlowField.BuildComplete(grid, new CellCoord(6, 6));

        var path = field.BuildPath(new CellCoord(2, 2));

        AssertPathValid(path, new CellCoord(2, 2), new CellCoord(6, 6), field, grid);
        Assert.False(
            AreConsecutive(path, new CellCoord(2, 2), new CellCoord(3, 3)),
            "path must not cut the corner between the two blocked cells");
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i - 1].X != path[i].X && path[i - 1].Y != path[i].Y)
            {
                bool orthoA = !grid.IsBlocked(path[i - 1].X, path[i].Y);
                bool orthoB = !grid.IsBlocked(path[i].X, path[i - 1].Y);
                Assert.True(orthoA || orthoB,
                    $"diagonal step {path[i - 1]}→{path[i]} cuts a blocked corner");
            }
        }
    }

    // ------------------------------------------------------------ blood cost

    [Fact]
    public void ThinBloodWallIsTornThroughDespiteDetourExisting()
    {
        // [F-043] 血量墙 case 1（拆）：薄墙（hp=2，可穿行按 1+2×5 计价）比长绕行便宜
        var grid = new MapGrid(15, 41).BloodColumn(7, 1, 39, 2);
        var field = FlowField.BuildComplete(grid, new CellCoord(14, 20));

        Assert.Equal(FlowFieldMode.Normal, field.Mode);
        var path = field.BuildPath(new CellCoord(0, 20));

        // Straight horizontal is optimal: 6×10 + 10×(1+10) + 7×10 = 240.
        Assert.Equal(15, path.Count);
        Assert.Contains(new CellCoord(7, 20), path);
        Assert.Equal(240L, field.Distance(new CellCoord(0, 20)));
        Assert.True(grid.BloodAt(7, 20) > 0);
    }

    [Fact]
    public void ThickBloodWallIsDetouredAround()
    {
        // [F-043] 血量墙 case 2（绕）：厚墙（hp=500）使穿行远贵于绕行，路径完全避开血格
        var grid = new MapGrid(15, 41).BloodColumn(7, 1, 39, 500);
        var field = FlowField.BuildComplete(grid, new CellCoord(14, 20));

        Assert.Equal(FlowFieldMode.Normal, field.Mode);
        var path = field.BuildPath(new CellCoord(0, 20));

        foreach (var cell in path)
        {
            Assert.True(grid.BloodAt(cell.X, cell.Y) == 0,
                $"detour must avoid blood cells, but path enters {cell}");
        }

        // Crossing happens at the free gap rows y=0 or y=40.
        bool crossedAtGap = path.Any(c => c.X == 7 && (c.Y == 0 || c.Y == 40));
        Assert.True(crossedAtGap, "detour must cross the wall column at a gap row");
        Assert.True(path.Count > 15);

        // 228 (up-right to the gap) + 228 (down-right to target), 40 cell entries.
        Assert.Equal(456L, field.Distance(new CellCoord(0, 20)));
    }

    [Theory] // legal parameter variation of the v3.2 §6 multiplier
    [InlineData(5, 170L)]
    [InlineData(1, 50L)]
    public void BloodCostCountsHpTimesMultiplierExactly(int multiplier, long expected)
    {
        // [F-043] 代价函数=地形+血量×乘数：3×1 图，(1,0) hp=3，目标 (2,0)
        var grid = new MapGrid(3, 1).Blood(1, 0, 3);
        var field = FlowField.BuildComplete(grid, new CellCoord(2, 0), multiplier);

        // enter (1,0): 10×(1+3×mult); enter (2,0): 10×1.
        Assert.Equal(expected, field.Distance(new CellCoord(0, 0)));
    }

    [Fact] // boundary
    public void BloodMultiplierZeroIgnoresBuildingsInCost()
    {
        // [F-043] boundary: 乘数 0 → 建筑不产生额外代价
        var grid = new MapGrid(3, 1).Blood(1, 0, 3);
        var field = FlowField.BuildComplete(grid, new CellCoord(2, 0), 0);

        Assert.Equal(20L, field.Distance(new CellCoord(0, 0)));
    }

    // ------------------------------------------------------------ wall tear

    [Fact]
    public void SealedMapEntersWallTearModeAndPicksCheapestChain()
    {
        // [F-044] 全封死→拆墙：x=7 全列封死（不可步行），上半 hp=500/下半 hp=5
        var grid = new MapGrid(15, 15)
            .BlockColumn(7, 0, 14)
            .BloodColumn(7, 0, 6, 500)
            .BloodColumn(7, 7, 14, 5);
        var field = FlowField.BuildComplete(grid, new CellCoord(14, 7));

        Assert.Equal(FlowFieldMode.WallTear, field.Mode);
        Assert.True(field.IsReachable(new CellCoord(0, 7)));

        var path = field.BuildPath(new CellCoord(0, 7));

        AssertTearPathValid(path, new CellCoord(0, 7), new CellCoord(14, 7), field, grid);
        foreach (var cell in path.Where(c => c.X == 7))
        {
            Assert.Equal(5, grid.BloodAt(cell.X, cell.Y));
        }

        Assert.Contains(new CellCoord(7, 7), path);
        Assert.True(field.IsTearCell(new CellCoord(7, 7)));
        Assert.False(field.IsTearCell(new CellCoord(0, 7)));

        // Straight route through the thin section: 6×10 + 10×(1+25) + 7×10 = 390.
        Assert.Equal(390L, field.Distance(new CellCoord(0, 7)));
    }

    [Fact]
    public void HardTerrainSealStaysUnreachableEvenInWallTearMode()
    {
        // [F-044] boundary: 不可拆地形全封死——仍进拆墙模式但无从可拆，梯度保持原地
        var grid = new MapGrid(15, 15).BlockColumn(7, 0, 14);
        var field = FlowField.BuildComplete(grid, new CellCoord(14, 7));

        Assert.Equal(FlowFieldMode.WallTear, field.Mode);
        Assert.False(field.IsReachable(new CellCoord(0, 7)));
        Assert.Equal(new CellCoord(0, 7), field.NextStep(new CellCoord(0, 7)));
        var path = field.BuildPath(new CellCoord(0, 7));
        Assert.Single(path);

        // The far side of the hard seal still reaches the target normally.
        Assert.True(field.IsReachable(new CellCoord(14, 7)));
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void SameMapBuiltTwiceYieldsEqualHashAndGradient()
    {
        // [F-043] 确定性：同 seed 图两次建场哈希相等（Stopwatch 切片不影响结果）
        var fieldA = FlowField.BuildComplete(TestMazes.SparseObstacles(40, 77, 0.25), new CellCoord(39, 39));
        var fieldB = FlowField.BuildComplete(TestMazes.SparseObstacles(40, 77, 0.25), new CellCoord(39, 39));

        Assert.Equal(fieldA.Hash, fieldB.Hash);
        Assert.Equal(fieldA.Distance(new CellCoord(31, 7)), fieldB.Distance(new CellCoord(31, 7)));
        Assert.Equal(fieldA.NextStep(new CellCoord(5, 5)), fieldB.NextStep(new CellCoord(5, 5)));
        Assert.Equal(fieldA.Mode, fieldB.Mode);
    }

    [Fact]
    public void DifferentMapsYieldDifferentHashes()
    {
        // [F-043] 哈希敏于输入（负向：不同图不得碰撞为同值）
        var fieldA = FlowField.BuildComplete(TestMazes.SparseObstacles(24, 1, 0.3), new CellCoord(23, 23));
        var fieldB = FlowField.BuildComplete(TestMazes.SparseObstacles(24, 2, 0.3), new CellCoord(23, 23));

        Assert.NotEqual(fieldA.Hash, fieldB.Hash);
    }

    // ------------------------------------------------------------ rejections

    [Fact] // illegal: gradient APIs refuse an unbuilt field
    public void NotReadyFieldRefusesGradientReads()
    {
        var field = FlowField.StartBuild(new MapGrid(16, 16), new CellCoord(15, 15));

        Assert.False(field.IsReady);
        Assert.Equal(FlowFieldMode.NotReady, field.Mode);
        Assert.Throws<InvalidOperationException>(() => field.Hash);
        Assert.Throws<InvalidOperationException>(() => field.NextStep(new CellCoord(0, 0)));
        Assert.Throws<InvalidOperationException>(() => field.Distance(new CellCoord(0, 0)));
        Assert.Throws<InvalidOperationException>(() => field.IsTearCell(new CellCoord(0, 0)));
        Assert.Throws<InvalidOperationException>(() => field.BuildPath(new CellCoord(0, 0)));
    }

    [Theory] // illegal: out-of-grid gradient queries
    [InlineData(-1, 0)]
    [InlineData(16, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 16)]
    public void ReadyFieldRejectsOutsideQueries(int x, int y)
    {
        var field = FlowField.BuildComplete(new MapGrid(16, 16), new CellCoord(15, 15));

        Assert.Throws<ArgumentOutOfRangeException>(() => field.NextStep(new CellCoord(x, y)));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.Distance(new CellCoord(x, y)));
    }

    [Fact] // illegal
    public void ReadyFieldRejectsNullCell()
    {
        var field = FlowField.BuildComplete(new MapGrid(4, 4), new CellCoord(3, 3));

        Assert.Throws<ArgumentNullException>(() => field.NextStep(null!));
        Assert.Throws<ArgumentNullException>(() => field.Distance(null!));
    }

    [Theory] // illegal: negative advance budget
    [InlineData(-1)]
    [InlineData(-1000)]
    public void AdvanceRejectsNegativeBudget(int milliseconds)
    {
        var field = FlowField.StartBuild(new MapGrid(4, 4), new CellCoord(3, 3));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => field.Advance(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact] // boundary: zero budget performs no work but stays legal
    public void AdvanceWithZeroBudgetMakesNoProgress()
    {
        var field = FlowField.StartBuild(TestMazes.SparseObstacles(64, 5, 0.2), new CellCoord(63, 63));

        Assert.False(field.Advance(TimeSpan.Zero));
        Assert.False(field.Advance(TimeSpan.Zero));
        Assert.False(field.IsReady);
    }

    // ------------------------------------------------------------ deterministic quota entry (T-067 DEBT 清账)

    [Theory] // illegal: negative cell quota
    [InlineData(-1)]
    [InlineData(-2048)]
    public void AdvanceDeterministicRejectsNegativeCellBudget(int cellBudget)
    {
        var field = FlowField.StartBuild(new MapGrid(4, 4), new CellCoord(3, 3));

        Assert.Throws<ArgumentOutOfRangeException>(() => field.AdvanceDeterministic(cellBudget));
    }

    [Fact] // boundary: zero quota performs no charged work but stays legal
    public void AdvanceDeterministicWithZeroBudgetMakesNoProgress()
    {
        var field = FlowField.StartBuild(TestMazes.SparseObstacles(64, 5, 0.2), new CellCoord(63, 63));

        Assert.False(field.AdvanceDeterministic(0));
        Assert.False(field.AdvanceDeterministic(0));
        Assert.False(field.IsReady);
        Assert.Equal(0, field.ProcessedCells);
    }

    [Fact]
    public void QuotaSlicedBuildEqualsOneShotAcrossAllPhases()
    {
        // [F-043] 配额切片等价（T-067 DEBT 清账口径）：拆墙图三阶段（快照/步
        // 行泛洪/拆墙重泛洪）用极小配额 3 格/次分片推完，与一次算完哈希相等
        // ——切片边界（无论按墙钟还是按格）不进入结果。
        var grid = new MapGrid(32, 32)
            .BlockColumn(16, 0, 31)
            .BloodColumn(16, 0, 15, 500)
            .BloodColumn(16, 16, 31, 5);
        var burst = FlowField.BuildComplete(grid, new CellCoord(31, 15));

        var sliced = FlowField.StartBuild(grid, new CellCoord(31, 15));
        int calls = 0;
        while (!sliced.AdvanceDeterministic(3))
        {
            calls++;
            Assert.True(calls < 100_000, "quota slicing must terminate");
        }

        calls++;
        Assert.True(calls >= 2, $"quota slicing must span multiple calls, got {calls}");
        Assert.Equal(burst.Hash, sliced.Hash);
        Assert.Equal(burst.Mode, sliced.Mode);
        Assert.Equal(FlowFieldMode.WallTear, sliced.Mode); // 全封死图确实穿三阶段
        Assert.Equal(burst.ProcessedCells, sliced.ProcessedCells); // 总工作量也与切片无关
        Assert.Equal(burst.Distance(new CellCoord(0, 15)), sliced.Distance(new CellCoord(0, 15)));
        Assert.Equal(burst.NextStep(new CellCoord(0, 15)), sliced.NextStep(new CellCoord(0, 15)));
    }

    [Fact]
    public void QuotaSpendingIsExactEvenBelowOneRow()
    {
        // [F-043] 配额口径（记账单位）：任何正配额都有进展（逐格快照计费，
        // 配额可小于行宽——不整行起步也就不会在宽图上停滞），且非收尾调用
        // 恰好花满配额；花费量=ProcessedCells 增量——确定性配额的记账单位
        // 就是它。
        var field = FlowField.StartBuild(TestMazes.SparseObstacles(32, 5, 0.2), new CellCoord(31, 31));

        long before = field.ProcessedCells;
        Assert.False(field.AdvanceDeterministic(10)); // 10 < 行宽 32 仍推进 10 格
        Assert.Equal(10, field.ProcessedCells - before);

        before = field.ProcessedCells;
        Assert.False(field.AdvanceDeterministic(320));
        Assert.Equal(320, field.ProcessedCells - before);
    }

    // ------------------------------------------------------------ helpers

    private static void AssertPathValid(
        IReadOnlyList<CellCoord> path,
        CellCoord start,
        CellCoord goal,
        FlowField field,
        MapGrid grid)
    {
        Assert.NotEmpty(path);
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
        for (int i = 1; i < path.Count; i++)
        {
            int dx = Math.Abs(path[i].X - path[i - 1].X);
            int dy = Math.Abs(path[i].Y - path[i - 1].Y);
            Assert.True(dx <= 1 && dy <= 1 && (dx + dy) > 0,
                $"step {path[i - 1]}→{path[i]} is not an 8-neighbour move");
        }

        for (int i = 1; i < path.Count; i++)
        {
            Assert.True(field.Distance(path[i]) < field.Distance(path[i - 1]),
                $"integration distance must strictly decrease at {path[i - 1]}→{path[i]}");
            Assert.Equal(path[i], field.NextStep(path[i - 1]));
        }
    }

    /// <summary>
    /// 拆墙路径可通行性论证：相邻 8 邻接、每格或可步行或可拆（血量>0）、终于目标、
    /// 梯度回放一致、积分距离严格下降。
    /// </summary>
    private static void AssertTearPathValid(
        IReadOnlyList<CellCoord> path,
        CellCoord start,
        CellCoord goal,
        FlowField field,
        MapGrid grid)
    {
        Assert.NotEmpty(path);
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
        for (int i = 0; i < path.Count; i++)
        {
            bool walkable = !grid.IsBlocked(path[i].X, path[i].Y);
            bool tearable = grid.BloodAt(path[i].X, path[i].Y) > 0;
            Assert.True(walkable || tearable,
                $"tear path cell {path[i]} is neither walkable nor tearable");
        }

        AssertPathValid(path, start, goal, field, grid);
    }

    private static bool AreConsecutive(IReadOnlyList<CellCoord> path, CellCoord a, CellCoord b) =>
        path.Zip(path.Skip(1), (x, y) => (x, y)).Any(p =>
            (p.x.X == a.X && p.x.Y == a.Y && p.y.X == b.X && p.y.Y == b.Y)
            || (p.x.X == b.X && p.x.Y == b.Y && p.y.X == a.X && p.y.Y == a.Y));
}
