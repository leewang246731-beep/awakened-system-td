using System.Diagnostics;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Logic + performance tests for <see cref="FlowFieldManager"/> (C-11):
/// bucket cap ≤6 with LRU eviction, dirty-region rebuilds touching exactly the
/// affected buckets (counter-probe verified), old field served while a rebuild
/// is in flight, 1 ms budget slicing of a large maze completing over multiple
/// frames bit-identically to a one-shot build, manager-level determinism, and
/// the TierA 300×300 full-flood timing record.
/// </summary>
// [Collection(PerfSerialCollection.Name)]（F1）：TierA 300×300 全泛洪的
// total/avg/max 帧包络是墙钟断言——整类进独占串行集合（非计时逻辑用例为
// 小图秒级，串行代价可忽略，取舍见 PerfSerialCollection.cs）。
// [C-11] 流场管理器（分桶≤6/LRU、脏区增量、预算切片等价、性能留档）
[Collection(PerfSerialCollection.Name)]
public class FlowFieldManagerTests
{
    private readonly ITestOutputHelper _output;

    public FlowFieldManagerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------ ctor

    [Theory] // legal
    [InlineData(1, 0.1)]
    [InlineData(6, 8.0)]
    [InlineData(64, 100.0)]
    public void CtorAcceptsPositiveCapAndBudget(int cap, double budgetMs)
    {
        var manager = new FlowFieldManager(new MapGrid(4, 4), cap, budgetMs);

        Assert.Equal(0, manager.ActiveFieldCount);
    }

    [Theory] // boundary
    [InlineData(1, 0.001)]
    [InlineData(6, 8.0)]
    public void CtorAcceptsExactBoundaries(int cap, double budgetMs)
    {
        var manager = new FlowFieldManager(new MapGrid(4, 4), cap, budgetMs);

        Assert.Equal(0, manager.PendingFieldCount);
    }

    [Theory] // illegal
    [InlineData(0, 8.0)]
    [InlineData(-1, 8.0)]
    [InlineData(6, 0.0)]
    [InlineData(6, -0.5)]
    public void CtorRejectsBrokenArguments(int cap, double budgetMs)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new FlowFieldManager(new MapGrid(4, 4), cap, budgetMs));
    }

    [Fact] // illegal
    public void CtorRejectsNullGrid()
    {
        Assert.Throws<ArgumentNullException>(() => new FlowFieldManager(null!));
    }

    [Fact] // illegal
    public void CtorRejectsNonPositiveGrid()
    {
        Assert.Throws<ArgumentException>(() => new FlowFieldManager(new FixedGrid(0, 3)));
    }

    // ------------------------------------------------------------ buckets + LRU

    [Fact]
    public void SeventhKeyEvictsTheLeastRecentlyUsedBucket()
    {
        // [F-043] 分桶 ≤6：第 7 个键把第 1 个（LRU）挤出
        var manager = new FlowFieldManager(new MapGrid(8, 8));
        var keys = Enumerable.Range(0, 7).Select(i => new FlowFieldKey(i, 0)).ToArray();
        foreach (var key in keys)
        {
            manager.Acquire(key, new CellCoord(0, 0));
            manager.Update(1000);
        }

        Assert.True(manager.ActiveFieldCount <= 6, "active buckets must never exceed the cap");
        Assert.Equal(6, manager.ActiveFieldCount);
        Assert.Null(manager.GetField(keys[0]));
        for (int i = 1; i < 7; i++)
        {
            Assert.NotNull(manager.GetField(keys[i]));
            Assert.True(manager.GetField(keys[i])!.IsReady);
        }
    }

    [Fact]
    public void LruRefreshProtectsRecentlyUsedBucket()
    {
        // [F-043] LRU 语义：重取 k1 后，第 7 键挤出的是 k2 而非 k1
        var manager = new FlowFieldManager(new MapGrid(8, 8));
        var keys = Enumerable.Range(0, 7).Select(i => new FlowFieldKey(i, 0)).ToArray();
        for (int i = 0; i < 6; i++)
        {
            manager.Acquire(keys[i], new CellCoord(0, 0));
            manager.Update(1000);
        }

        manager.Acquire(keys[0], new CellCoord(0, 0));
        manager.Acquire(keys[6], new CellCoord(0, 0));
        manager.Update(1000);

        Assert.NotNull(manager.GetField(keys[0]));
        Assert.Null(manager.GetField(keys[1]));
        Assert.Equal(6, manager.ActiveFieldCount);
    }

    [Fact]
    public void AcquireSameKeySameTargetReturnsSameInstance()
    {
        var manager = new FlowFieldManager(new MapGrid(8, 8));
        var key = new FlowFieldKey(1, 0);

        var first = manager.Acquire(key, new CellCoord(7, 7));
        manager.Update(1000);
        var second = manager.Acquire(key, new CellCoord(7, 7));

        Assert.Same(first, second);
    }

    [Fact]
    public void AcquireTargetChangeRebuildsTowardNewTarget()
    {
        var manager = new FlowFieldManager(new MapGrid(16, 16));
        var key = new FlowFieldKey(0, 0);

        manager.Acquire(key, new CellCoord(15, 15));
        manager.Update(1000);
        Assert.Equal(1, manager.GetRebuildCount(key));

        manager.Acquire(key, new CellCoord(0, 15));
        Assert.Equal(1, manager.GetRebuildCount(key));
        while (!manager.Update(1000))
        {
        }

        Assert.Equal(2, manager.GetRebuildCount(key));
        var field = manager.GetField(key);
        Assert.NotNull(field);
        Assert.Equal(new CellCoord(0, 15), field!.Target);
        Assert.Equal(new CellCoord(0, 15), field.BuildPath(new CellCoord(3, 15))[^1]);
    }

    // ------------------------------------------------------------ dirty regions

    [Fact]
    public void DirtyRegionRebuildsOnlyIntersectingBuckets()
    {
        // [F-043] 脏区合批：左半脏区只重算覆盖左半的桶；探针证明恰好一次全图快照
        var probe = new CountingGrid(new MapGrid(16, 16));
        var manager = new FlowFieldManager(probe);
        var left = new FlowFieldKey(0, 0);
        var right = new FlowFieldKey(1, 0);
        manager.Acquire(left, new CellCoord(15, 15), new CellRegion(0, 0, 8, 16));
        manager.Acquire(right, new CellCoord(15, 15), new CellRegion(8, 0, 8, 16));
        while (!manager.Update(1000))
        {
        }

        Assert.Equal(1, manager.GetRebuildCount(left));
        Assert.Equal(1, manager.GetRebuildCount(right));

        probe.Reset();
        manager.MarkDirty(new CellRegion(0, 0, 8, 16));
        while (!manager.Update(1000))
        {
        }

        Assert.Equal(2, manager.GetRebuildCount(left));
        Assert.Equal(1, manager.GetRebuildCount(right));
        Assert.Equal(16 * 16, probe.CostCalls);
        Assert.Equal(16 * 16, probe.PassableCalls);
        Assert.Equal(16 * 16, probe.BloodCalls);
    }

    [Fact]
    public void DirtyRegionOverlappingAllCoveragesRebuildsAll()
    {
        var probe = new CountingGrid(new MapGrid(8, 8));
        var manager = new FlowFieldManager(probe);
        var a = new FlowFieldKey(0, 0);
        var b = new FlowFieldKey(1, 0);
        manager.Acquire(a, new CellCoord(7, 7), new CellRegion(0, 0, 4, 8));
        manager.Acquire(b, new CellCoord(7, 7), new CellRegion(4, 0, 4, 8));
        while (!manager.Update(1000))
        {
        }

        probe.Reset();
        manager.MarkDirty(CellRegion.EntireGrid(probe));
        while (!manager.Update(1000))
        {
        }

        Assert.Equal(2, manager.GetRebuildCount(a));
        Assert.Equal(2, manager.GetRebuildCount(b));
        Assert.Equal(2 * 8 * 8, probe.CostCalls);
    }

    [Fact] // boundary
    public void EmptyDirtyRegionTriggersNoRebuild()
    {
        var manager = new FlowFieldManager(new MapGrid(8, 8));
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(7, 7));
        while (!manager.Update(1000))
        {
        }

        manager.MarkDirty(new CellRegion(3, 3, 0, 5));
        bool settled = manager.Update(1000);

        Assert.True(settled);
        Assert.Equal(1, manager.GetRebuildCount(key));
    }

    [Theory] // illegal
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public void MarkDirtyRejectsNegativeExtent(int width, int height)
    {
        var manager = new FlowFieldManager(new MapGrid(8, 8));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.MarkDirty(new CellRegion(0, 0, width, height)));
    }

    [Fact] // illegal
    public void MarkDirtyRejectsNullRegion()
    {
        var manager = new FlowFieldManager(new MapGrid(8, 8));

        Assert.Throws<ArgumentNullException>(() => manager.MarkDirty(null!));
    }

    // ------------------------------------------------------------ old field during rebuild

    [Fact]
    public void OldFieldKeepsServingWhileRebuildIsPending()
    {
        // [F-043] 结果未就绪时继续用旧场：脏标记挂起、零预算不推进、旧哈希不变
        var grid = new MapGrid(64, 64);
        var manager = new FlowFieldManager(grid);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(63, 63));
        while (!manager.Update(1000))
        {
        }

        var oldField = manager.GetField(key);
        Assert.NotNull(oldField);
        long oldHash = oldField!.Hash;

        // Blood on the straight route from (0,0) must change integrated costs.
        grid.Blood(32, 32, 9);
        manager.MarkDirty(CellRegion.EntireGrid(grid));
        Assert.True(manager.PendingFieldCount == 0, "dirty flag defers the rebuild to the next Update");
        Assert.False(manager.Update(0), "zero budget performs no flood work");
        Assert.True(manager.PendingFieldCount == 1, "rebuild started but not advanced");
        Assert.True(manager.GetField(key)!.IsReady);
        Assert.Equal(oldHash, manager.GetField(key)!.Hash);
        Assert.Same(oldField, manager.GetField(key));

        while (!manager.Update(1000))
        {
        }

        Assert.NotEqual(oldHash, manager.GetField(key)!.Hash);
        Assert.Equal(2, manager.GetRebuildCount(key));
    }

    // ------------------------------------------------------------ budget slicing

    [Fact]
    public void SlicedBuildEqualsOneShotBuildOnLargeMaze()
    {
        // [F-043] 预算切片等价（任务口径 128×128）：1ms 预算分多帧完成，与一次算完哈希相等
        var grid = TestMazes.SparseObstacles(128, 7, 0.3);
        var burst = FlowField.BuildComplete(grid, new CellCoord(127, 127));

        var sliced = FlowField.StartBuild(grid, new CellCoord(127, 127));
        int frames = 0;
        while (!sliced.Advance(TimeSpan.FromMilliseconds(1)))
        {
            frames++;
        }

        frames++;
        Assert.True(frames >= 2, $"slicing must span multiple frames, got {frames}");
        Assert.Equal(burst.Hash, sliced.Hash);
        Assert.Equal(burst.Mode, sliced.Mode);

        var rng = new Lcg(99);
        for (int i = 0; i < 32; i++)
        {
            var cell = new CellCoord(rng.NextInt(128), rng.NextInt(128));
            Assert.Equal(burst.Distance(cell), sliced.Distance(cell));
            Assert.Equal(burst.NextStep(cell), sliced.NextStep(cell));
        }
    }

    [Fact]
    public void ManagerSlicesRebuildAcrossFramesWithinBudget()
    {
        // [F-043] 管理器级分帧：256×256 图，1ms 预算 Update 多次后收敛且就绪
        var manager = new FlowFieldManager(TestMazes.SparseObstacles(256, 11, 0.25));
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(255, 255));

        int frames = 0;
        while (!manager.Update(1))
        {
            frames++;
            Assert.True(frames < 10000, "sliced rebuild must terminate");
        }

        frames++;
        Assert.True(frames >= 2, $"manager slicing must span multiple frames, got {frames}");
        Assert.True(manager.GetField(key)!.IsReady);
    }

    [Theory] // illegal
    [InlineData(-0.1)]
    [InlineData(-1000)]
    public void UpdateRejectsNegativeBudget(double budgetMs)
    {
        var manager = new FlowFieldManager(new MapGrid(4, 4));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.Update(budgetMs));
    }

    [Fact] // boundary
    public void UpdateWithZeroBudgetReportsPendingWithoutProgress()
    {
        var manager = new FlowFieldManager(new MapGrid(64, 64));
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(63, 63));

        Assert.False(manager.Update(0));
        Assert.Equal(1, manager.PendingFieldCount);
        Assert.Null(manager.GetField(key));

        while (!manager.Update(1000))
        {
        }

        Assert.True(manager.Update(0));
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void TwoManagersSameSeedProduceEqualHashes()
    {
        // [F-043] 确定性：同 seed 图两管理器逐桶哈希相等
        var keys = new[] { new FlowFieldKey(0, 0), new FlowFieldKey(1, 1), new FlowFieldKey(2, 0) };
        var targets = new[] { new CellCoord(63, 63), new CellCoord(0, 63), new CellCoord(63, 0) };

        var managerA = new FlowFieldManager(TestMazes.SparseObstacles(64, 21, 0.25));
        var managerB = new FlowFieldManager(TestMazes.SparseObstacles(64, 21, 0.25));
        for (int i = 0; i < keys.Length; i++)
        {
            managerA.Acquire(keys[i], targets[i]);
            managerB.Acquire(keys[i], targets[i]);
        }

        while (!managerA.Update(1000) | !managerB.Update(1000))
        {
        }

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(managerA.GetField(keys[i])!.Hash, managerB.GetField(keys[i])!.Hash);
        }
    }

    // ------------------------------------------------------------ performance (TierA)

    [Fact]
    public void FullFlood300x300SparseStaysWithinBudgetEnvelope()
    {
        // [F-043] TierA 性能留档：300×300 稀疏障碍（密度 0.08，seed 42）全图泛洪，
        // 8ms 预算切片推进。稳态化口径（Wave 1 round 1 review #2）：
        //
        // 语义边界——仿真跑在 K-1 IClock 冻结的 30 Hz 固定 tick 时域上，一个
        // tick=33.3ms；流场每帧预算 8ms（F-043/v3.2 §6）是 tick 内的切片份额，
        // 不是帧率本身。因此单帧包络取 30ms（<33.3ms 一个 tick，留 3.3ms 边界
        // 裕量）：即便某帧被 GC/OS 调度顶穿 8ms 预算，也仍在一个 tick 内完成，
        // 不侵占下一个 tick 的份额。
        //
        // 留档数据（T-037 首测，机器=A5 级开发机）：空载 sliced total=165ms/
        // 21 帧/均 7ms/峰 8ms、burst=176ms；并行 CI（run_all 全量构建+同套件
        // 并发）实测最差 total=388ms/41 帧/均 9ms/峰 22ms（峰值超预算部分=
        // GC/OS 调度抖动，非算法批次粒度——32 pop 批检查空载低于 1ms）。
        //
        // 断言稳态化：①单帧峰值断言改为**三次独立运行取中位数**（单次极值
        // 由一次性调度尖峰主导，中位数抓持续回归不抓毛刺）；②无负载严格
        // 断言：均帧 ≤10ms——空载留档 7~8ms 支持 8ms 线，CI 并发抖动放宽数
        // =8ms×1.25=10ms（25% 调度裕量，注明于此）；③总量 <2000ms（对最差
        // 留档 388ms 约 5× 余量）。真实数字经 ITestOutputHelper 留档。
        var grid = TestMazes.SparseObstacles(300, 42, 0.08);
        var target = new CellCoord(299, 299);

        var burstWatch = Stopwatch.StartNew();
        var burst = FlowField.BuildComplete(grid, target);
        burstWatch.Stop();
        double burstMs = burstWatch.Elapsed.TotalMilliseconds;

        // Three independent sliced builds; per run: (totalMs, avgMs, maxFrameMs).
        var runs = new (double Total, double Avg, double Max)[3];
        for (int run = 0; run < runs.Length; run++)
        {
            var sliced = FlowField.StartBuild(grid, target);
            long maxFrameTicks = 0;
            int frames = 0;
            var totalWatch = Stopwatch.StartNew();
            while (true)
            {
                long frameStart = Stopwatch.GetTimestamp();
                bool done = sliced.Advance(TimeSpan.FromMilliseconds(8));
                long frameEnd = Stopwatch.GetTimestamp();
                maxFrameTicks = Math.Max(maxFrameTicks, frameEnd - frameStart);
                frames++;
                if (done)
                {
                    break;
                }
            }

            totalWatch.Stop();
            double totalMs = totalWatch.Elapsed.TotalMilliseconds;
            runs[run] = (totalMs, totalMs / frames, maxFrameTicks * 1000.0 / Stopwatch.Frequency);
            Assert.Equal(burst.Hash, sliced.Hash); // every run bit-identical to the one-shot build
            _output.WriteLine(
                "[T-037 perf] 300x300 d=0.08 seed=42 run" + run + ": sliced total=" + (int)totalMs
                + "ms frames=" + frames + " avg=" + (int)runs[run].Avg + "ms maxFrame="
                + (int)runs[run].Max + "ms burst=" + (int)burstMs + "ms"
                + " mode=" + burst.Mode + " reachableFromSpawn=" + burst.IsReachable(new CellCoord(0, 0)));
        }

        double medianTotal = MedianOf(runs[0].Total, runs[1].Total, runs[2].Total);
        double medianAvg = MedianOf(runs[0].Avg, runs[1].Avg, runs[2].Avg);
        double medianMax = MedianOf(runs[0].Max, runs[1].Max, runs[2].Max);

        Assert.True(burst.IsReady);
        // Envelope = worst measured under loaded CI (388ms/9ms/22ms) + documented
        // machine-variance margin; regression catcher, not wall-clock certification
        // (unloaded numbers recorded above via _output).
        Assert.True(medianTotal < 2000, "median total flood time " + (int)medianTotal + "ms exceeded the 2000ms envelope");
        Assert.True(medianAvg <= 10.0,
            "median average frame " + (int)medianAvg + "ms exceeded the strict unloaded envelope"
            + " 10ms (=8ms budget x1.25 CI-jitter margin; unloaded archived avg is 7ms)");
        Assert.True(medianMax <= 30.0,
            "median worst frame " + (int)medianMax + "ms exceeded the 30ms envelope"
            + " (one 30Hz tick = 33.3ms; 3.3ms boundary margin)");
    }

    /// <summary>Middle value of three samples (odd count → exact order statistic).</summary>
    private static double MedianOf(double a, double b, double c) =>
        Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>Minimal grid carrying dimensions only (ctor rejection input).</summary>
    private sealed class FixedGrid(int width, int height) : IFlowGrid
    {
        public int Width => width;

        public int Height => height;

        public int Cost(CellCoord c) => 1;

        public bool Passable(CellCoord c) => true;

        public int BloodCost(CellCoord c) => 0;
    }
}
