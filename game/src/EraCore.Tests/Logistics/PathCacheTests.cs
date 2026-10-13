using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Tests.Pathfinding;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests for the transport path cache (C-07 precursor, T-047, F-040):
/// hit/miss counting with a hit-does-not-recompute probe (SearchCount +
/// CountingGrid), negative results never cached, and the acceptance-bar
/// centrepiece — affected-only invalidation: changing one cell drops exactly
/// the routes passing through it and nothing else (no full clears).
/// </summary>
// [F-040] 路径缓存：命中不重算 + 仅受影响失效（改 1 格只失效含该格路径）
public class PathCacheTests
{
    private static PathCache NewCache(MapGrid grid) => new(new AStarPathfinder(grid));

    // ------------------------------------------------------------ hit / miss

    [Fact]
    public void FirstQueryMissesAndRepeatsHitWithoutRecompute()
    {
        // [F-040] 命中不重算：首查=miss 且 SearchCount+1；复查=hit、同一结果对象、
        // SearchCount 与网格读取数都零增长（计数探针）
        var counting = new CountingGrid(new MapGrid(9, 9));
        var pathfinder = new AStarPathfinder(counting);
        var cache = new PathCache(pathfinder);
        var start = new CellCoord(0, 0);
        var goal = new CellCoord(8, 8);

        PathQueryResult first = cache.GetPath(start, goal);
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(1L, pathfinder.SearchCount);
        Assert.Equal(1, cache.Count);
        int readsAfterMiss = counting.PassableCalls;

        PathQueryResult second = cache.GetPath(start, goal);

        Assert.Equal(1L, cache.Misses);
        Assert.Equal(1L, cache.Hits);
        Assert.Equal(1L, pathfinder.SearchCount); // 命中不重算
        Assert.Equal(readsAfterMiss, counting.PassableCalls); // 网格一次都没再读
        Assert.Same(first, second);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void PathNotFoundResultsAreNeverCached()
    {
        // [F-040] 否定结果不缓存：可达性取决于全图（任意处拆墙都可能翻案），
        // 无法受影响失效 → 每次都真查（Misses/SearchCount 同步 +2，Count 恒 0）
        var grid = new MapGrid(5, 5).BlockColumn(2, 0, 4); // 竖墙封死左右
        var pathfinder = new AStarPathfinder(grid);
        var cache = new PathCache(pathfinder);

        PathQueryResult first = cache.GetPath(new CellCoord(0, 2), new CellCoord(4, 2));
        PathQueryResult second = cache.GetPath(new CellCoord(0, 2), new CellCoord(4, 2));

        Assert.Equal(PathQueryStatus.PathNotFound, first.Status);
        Assert.Equal(PathQueryStatus.PathNotFound, second.Status);
        Assert.Equal(2L, cache.Misses);
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(2L, pathfinder.SearchCount);
        Assert.Equal(0, cache.Count);
    }

    // ------------------------------------------------------------ affected-only invalidation

    [Fact]
    public void ChangingOneCellInvalidatesOnlyRoutesThroughIt()
    {
        // [F-040] 仅受影响失效：三条互不交叉的横向路径（行 0/3/6），改 (3,0)
        // 一格 → 只删行 0 路径（返回 1）；行 3/6 仍命中不重算；行 0 复查=miss 重算
        var cache = NewCache(new MapGrid(7, 7));
        var rowA = (new CellCoord(0, 0), new CellCoord(6, 0));
        var rowB = (new CellCoord(0, 3), new CellCoord(6, 3));
        var rowC = (new CellCoord(0, 6), new CellCoord(6, 6));
        cache.GetPath(rowA.Item1, rowA.Item2);
        cache.GetPath(rowB.Item1, rowB.Item2);
        cache.GetPath(rowC.Item1, rowC.Item2);
        Assert.Equal(3, cache.Count);

        int removed = cache.Invalidate(new CellCoord(3, 0));

        Assert.Equal(1, removed);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetCachedPath(rowA.Item1, rowA.Item2, out _)); // 含 (3,0) 的路径没了
        Assert.True(cache.TryGetCachedPath(rowB.Item1, rowB.Item2, out _)); // 其余原样保留
        Assert.True(cache.TryGetCachedPath(rowC.Item1, rowC.Item2, out _));

        long searchesBefore = SearchCountOf(cache);
        PathQueryResult recomputed = cache.GetPath(rowA.Item1, rowA.Item2);
        Assert.Equal(PathQueryStatus.Found, recomputed.Status); // 改格不挡路 → 重算找到
        Assert.Equal(SearchCountOf(cache), searchesBefore + 1); // 只重算这一条
        Assert.True(cache.TryGetCachedPath(rowB.Item1, rowB.Item2, out _)); // B/C 仍未受影响
    }

    [Fact]
    public void InvalidatingAnUntouchedCellIsANoOp()
    {
        // [F-040] 边界：改动格不在任何缓存路径上 → 返回 0，缓存全保留（禁止全清）
        var cache = NewCache(new MapGrid(7, 7));
        var start = new CellCoord(0, 0);
        var goal = new CellCoord(6, 0);
        cache.GetPath(start, goal);

        int removed = cache.Invalidate(new CellCoord(3, 4)); // 空行，无路径经过

        Assert.Equal(0, removed);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetCachedPath(start, goal, out _));
    }

    [Fact]
    public void SharedCellInvalidationRemovesEveryRouteThroughIt()
    {
        // [F-040] 共享格：横向 (0,0)→(6,0) 与纵向 (3,0)→(3,6) 都过 (3,0)
        // （端点格也在路径格子索引内）→ 一次失效删两条
        var cache = NewCache(new MapGrid(7, 7));
        cache.GetPath(new CellCoord(0, 0), new CellCoord(6, 0));
        cache.GetPath(new CellCoord(3, 0), new CellCoord(3, 6));
        Assert.Equal(2, cache.Count);

        int removed = cache.Invalidate(new CellCoord(3, 0));

        Assert.Equal(2, removed);
        Assert.Equal(0, cache.Count);
        int removedAgain = cache.Invalidate(new CellCoord(3, 0)); // 倒排索引已清理 → 幂等 0
        Assert.Equal(0, removedAgain);
    }

    [Fact]
    public void RegionInvalidationCountsEachAffectedRouteOnce()
    {
        // [F-040] 区域失效去重：region {(3,0),(4,0)} 中两格都只落在行 0 路径上
        // → 该路径只删一次（返回 1），行 6 路径不动
        var cache = NewCache(new MapGrid(7, 7));
        var rowTop = (new CellCoord(0, 0), new CellCoord(6, 0));
        var rowBottom = (new CellCoord(0, 6), new CellCoord(6, 6));
        cache.GetPath(rowTop.Item1, rowTop.Item2);
        cache.GetPath(rowBottom.Item1, rowBottom.Item2);

        int removed = cache.Invalidate(new[] { new CellCoord(3, 0), new CellCoord(4, 0) });

        Assert.Equal(1, removed);
        Assert.False(cache.TryGetCachedPath(rowTop.Item1, rowTop.Item2, out _));
        Assert.True(cache.TryGetCachedPath(rowBottom.Item1, rowBottom.Item2, out _));
    }

    [Fact]
    public void RecomputeAfterInvalidationSeesTheChangedTerrain()
    {
        // [F-040] 失效后重算走新地形：原直路 (0,0)→(4,0) 代价 40；堵 (2,0) 并
        // 失效后重算 → 新路径绕开 (2,0)（代价 48：14+10+10+14，对角进出），
        // 哈希不同
        var grid = new MapGrid(5, 5);
        var cache = NewCache(grid);
        var start = new CellCoord(0, 0);
        var goal = new CellCoord(4, 0);
        PathQueryResult before = cache.GetPath(start, goal);
        Assert.Equal(40L, before.Cost);

        grid.Block(2, 0);
        int removed = cache.Invalidate(new CellCoord(2, 0));
        Assert.Equal(1, removed);

        PathQueryResult after = cache.GetPath(start, goal);
        Assert.Equal(PathQueryStatus.Found, after.Status);
        Assert.Equal(48L, after.Cost);
        Assert.DoesNotContain(new CellCoord(2, 0), after.Cells);
        Assert.NotEqual(before.Hash, after.Hash);
    }

    // ------------------------------------------------------------ arguments

    [Theory]
    [InlineData("pathfinder")]  // illegal: null 寻路器
    [InlineData("start")]       // illegal: null 起点
    [InlineData("goal")]        // illegal: null 终点
    [InlineData("region")]      // illegal: null 失效区域
    public void NullArgumentsAreRejected(string site)
    {
        // [F-040] 非法：null 参数逐一抛 ArgumentNullException（确实拒绝）
        if (site == "pathfinder")
        {
            Assert.Throws<ArgumentNullException>(() => new PathCache(null!));
            return;
        }

        var cache = NewCache(new MapGrid(5, 5));
        switch (site)
        {
            case "start":
                Assert.Throws<ArgumentNullException>(() => cache.GetPath(null!, new CellCoord(1, 1)));
                break;
            case "goal":
                Assert.Throws<ArgumentNullException>(() => cache.GetPath(new CellCoord(1, 1), null!));
                break;
            default:
                Assert.Throws<ArgumentNullException>(() => cache.Invalidate((IEnumerable<CellCoord>)null!));
                break;
        }
    }

    private static long SearchCountOf(PathCache cache)
    {
        // The cache's pathfinder is reachable only through its behaviour, so
        // tests read the recompute signal as Misses (every miss = one search).
        return cache.Misses;
    }
}
