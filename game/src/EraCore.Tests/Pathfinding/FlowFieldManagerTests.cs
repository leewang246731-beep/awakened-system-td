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
/// is in flight, budget slicing of a large maze completing over multiple
/// frames bit-identically to a one-shot build (BOTH entries: legacy wall-clock
/// 1 ms slices and — T-067 DEBT 清账 — the deterministic cell quota the
/// simulation actually drives), manager-level determinism, load-independent
/// completion (CPU noise between floods), and the TierA 300×300 full-flood
/// timing + quota-vs-wallclock throughput records.
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

    // --------------------------------------------- W4 门禁 F1: advance-mode stamp

    [Fact] // [C-11] W4 门禁 F1 观察位：两条预算入口各自盖章（防误回接墙钟的守卫地基）
    public void LastAdvanceModeStampsTheBudgetEntryThatRan()
    {
        var manager = new FlowFieldManager(new MapGrid(4, 4));
        manager.Acquire(new FlowFieldKey(0, 0), new CellCoord(0, 0));

        // 新建管理器只 Acquire 不推进——观察位保持 None。
        Assert.Equal(FlowFieldAdvanceMode.None, manager.LastAdvanceMode);

        // 墙钟入口（合法零预算边界）盖 WallClock 章。
        manager.Update(0);
        Assert.Equal(FlowFieldAdvanceMode.WallClock, manager.LastAdvanceMode);

        // 确定性入口（合法零配额边界）盖 Deterministic 章——最后调用者胜。
        manager.UpdateDeterministic(0);
        Assert.Equal(FlowFieldAdvanceMode.Deterministic, manager.LastAdvanceMode);
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

    // ------------------------------------------------------------ deterministic quota entry (T-067 DEBT 清账)

    [Theory] // illegal
    [InlineData(-1)]
    [InlineData(-2048)]
    public void UpdateDeterministicRejectsNegativeQuota(int quota)
    {
        var manager = new FlowFieldManager(new MapGrid(4, 4));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.UpdateDeterministic(quota));
    }

    [Fact] // boundary
    public void UpdateDeterministicWithZeroQuotaReportsPendingWithoutProgress()
    {
        var manager = new FlowFieldManager(new MapGrid(64, 64));
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(63, 63));

        Assert.False(manager.UpdateDeterministic(0));
        Assert.Equal(1, manager.PendingFieldCount);
        Assert.Equal(0, manager.LastTickCellsProcessed);
        Assert.Null(manager.GetField(key));

        while (!manager.UpdateDeterministic(4096))
        {
        }

        Assert.True(manager.UpdateDeterministic(0)); // 零配额仍如实报告"无挂起"
        Assert.Equal(0, manager.LastTickCellsProcessed); // 空转 tick 零记账
    }

    [Fact]
    public void ManagerDeterministicQuotaSlicesRebuildAcrossFrames()
    {
        // [F-043] 管理器级分帧（确定性口径）：256×256 图，512 格/tick 配额
        // UpdateDeterministic 多次后收敛且就绪——分帧续算语义与墙钟版一致。
        var manager = new FlowFieldManager(TestMazes.SparseObstacles(256, 11, 0.25));
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(255, 255));

        int frames = 0;
        while (!manager.UpdateDeterministic(512))
        {
            frames++;
            Assert.True(frames < 100_000, "deterministic sliced rebuild must terminate");
            Assert.True(manager.LastTickCellsProcessed <= 512, "per-tick spend must never exceed the quota");
        }

        frames++;
        Assert.True(frames >= 2, $"manager quota slicing must span multiple frames, got {frames}");
        Assert.True(manager.GetField(key)!.IsReady);
    }

    [Fact]
    public void QuotaSlicedManagerBuildEqualsOneShotOnLargeMaze()
    {
        // [F-043] 配额切片等价（管理器口径，任务口径 128×128）：小配额分多帧
        // 完成，与一次算完哈希相等——既有切片等价断言在 tick 预算口径下的
        // 等价新钉。
        var grid = TestMazes.SparseObstacles(128, 7, 0.3);
        var burst = FlowField.BuildComplete(grid, new CellCoord(127, 127));

        var manager = new FlowFieldManager(grid);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, new CellCoord(127, 127));
        int frames = 0;
        while (!manager.UpdateDeterministic(96))
        {
            frames++;
            Assert.True(frames < 100_000, "quota slicing must terminate");
        }

        frames++;
        Assert.True(frames >= 2, $"quota slicing must span multiple frames, got {frames}");
        var sliced = manager.GetField(key);
        Assert.NotNull(sliced);
        Assert.Equal(burst.Hash, sliced!.Hash);
        Assert.Equal(burst.Mode, sliced.Mode);
        Assert.Equal(burst.ProcessedCells, sliced.ProcessedCells);

        var rng = new Lcg(99);
        for (int i = 0; i < 32; i++)
        {
            var cell = new CellCoord(rng.NextInt(128), rng.NextInt(128));
            Assert.Equal(burst.Distance(cell), sliced.Distance(cell));
            Assert.Equal(burst.NextStep(cell), sliced.NextStep(cell));
        }
    }

    [Fact]
    public void DeterministicQuotaCompletionIsIndependentOfCpuNoise()
    {
        // [T-067 DEBT / T-086 发现的机制钉]：墙钟预算在 CPU 争用下每 tick 完
        // 成的格数缩水 → 流场重建完成的 tick 漂移 → 单位在旧场上多走/少走数
        // tick → 同种子模拟哈希发散（OffSystemRegression 高负载 2 次实证）。
        // 确定性配额下完成 tick 是图快照的纯函数：本测试在泛洪间插入确定性
        // 计数的 CPU 噪声循环（消耗真实墙钟、时长随负载漂移——正是要注入的
        // 争用），完成 tick / 哈希 / 重建计数 / 总工作量必须与安静对照全等。
        var noisy = new FlowFieldManager(TearMaze());
        var quiet = new FlowFieldManager(TearMaze());
        var key = new FlowFieldKey(0, 0);
        noisy.Acquire(key, new CellCoord(63, 63));
        quiet.Acquire(key, new CellCoord(63, 63));

        int noisyTicks = 0, quietTicks = 0;
        bool noisyDone = false, quietDone = false;
        while (!noisyDone || !quietDone)
        {
            if (!noisyDone)
            {
                noisyDone = noisy.UpdateDeterministic(256);
                noisyTicks++;
                BurnCpu(1_000_000); // 泛洪间计算噪声（墙钟时长随负载漂移）
            }

            if (!quietDone)
            {
                quietDone = quiet.UpdateDeterministic(256);
                quietTicks++;
            }
        }

        Assert.True(quietTicks >= 2, "the tear maze must take multiple quota ticks");
        Assert.Equal(quietTicks, noisyTicks); // 完成 tick 与负载无关（核心断言）
        Assert.Equal(noisy.GetField(key)!.Hash, quiet.GetField(key)!.Hash);
        Assert.Equal(noisy.GetField(key)!.ProcessedCells, quiet.GetField(key)!.ProcessedCells);
        Assert.Equal(noisy.GetRebuildCount(key), quiet.GetRebuildCount(key));
        Assert.Equal(FlowFieldMode.WallTear, noisy.GetField(key)!.Mode); // 确实穿全三阶段
    }

    [Fact]
    public void SoftGuardObservesWithoutChangingBehavior()
    {
        // [T-067 DEBT 清账] 软护栏语义：超线只观察（计数+可选 sink）不改行
        // 为。阈值测试缝降到 0 强制触发（任何非零耗时都超线），行为面（完成
        // tick/哈希/重建计数）必须与默认阈值（从不触发）完全一致。
        var observed = new List<double>();
        var guarded = new FlowFieldManager(TestMazes.SparseObstacles(64, 21, 0.25))
        {
            SoftGuardThresholdMs = 0.0, // 强制触发观察路径
            SlowTickObserver = observed.Add,
        };
        var plain = new FlowFieldManager(TestMazes.SparseObstacles(64, 21, 0.25));
        var key = new FlowFieldKey(0, 0);
        guarded.Acquire(key, new CellCoord(63, 63));
        plain.Acquire(key, new CellCoord(63, 63));

        int guardedTicks = 0, plainTicks = 0;
        bool guardedDone = false, plainDone = false;
        while (!guardedDone || !plainDone)
        {
            if (!guardedDone)
            {
                guardedDone = guarded.UpdateDeterministic(1024);
                guardedTicks++;
            }

            if (!plainDone)
            {
                plainDone = plain.UpdateDeterministic(1024);
                plainTicks++;
            }
        }

        Assert.True(guarded.SlowTickCount > 0, "forced threshold must trip the observation");
        Assert.Equal(guarded.SlowTickCount, observed.Count);
        Assert.All(observed, ms => Assert.True(ms > 0.0));
        Assert.Equal(plainTicks, guardedTicks); // 护栏不改变分帧行为
        Assert.Equal(plain.GetField(key)!.Hash, guarded.GetField(key)!.Hash);
        Assert.Equal(plain.GetRebuildCount(key), guarded.GetRebuildCount(key));
        Assert.Equal(0, plain.SlowTickCount); // 默认 16ms 线在 1024 格/tick 下不触发
    }

    /// <summary>
    /// 64×64 拆墙图：x=32 全列硬封 + 上下两段血墙（500/5），步行泛洪必留
    /// 不可达格 → 必然穿三阶段（快照/步行/拆墙重泛洪）的确定性迷宫。
    /// </summary>
    private static MapGrid TearMaze() => new MapGrid(64, 64)
        .BlockColumn(32, 0, 63)
        .BloodColumn(32, 0, 31, 500)
        .BloodColumn(32, 32, 63, 5);

    /// <summary>
    /// 计算噪声（T-086 机制）：固定迭代次数的忙循环——消耗真实 CPU 墙钟
    /// （时长随机器与负载漂移，正是要注入的争用形态），结果丢弃。
    /// </summary>
    private static void BurnCpu(long iterations)
    {
        long acc = 0;
        for (long i = 0; i < iterations; i++)
        {
            acc += i % 7;
        }

        GC.KeepAlive(acc);
    }

    // ------------------------------------------------------------ performance (TierA)

    // 首败重跑容差（REV-20261014-wave3-round2 #4 同发现类的第三处落点）：本机
    // 为三泳道 worktree 并行共享机，同代码跨 worktree 对照实测门禁首败由
    // 邻居泳道分钟级负载尖峰主导（本批 gate run 实录：同套件空载 1m41s/
    // 三泳道满载 41m，本测试均帧 7→11ms 越过 10ms 线）——处置与
    // Spike1PerformanceTests/SimGroupingPerfGuardrailTests 同口径：首败不直接
    // 红，再补两次完整测量、三次尝试各指标取中位数后重判，仍超才红。
    private const int FirstFailRetryAttempts = 3;

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
        // 首败负载容差（REV-20261014-wave3-round2 #4）：三次运行中位首败→
        // 再补两次完整测量、三次尝试取中位重判（FirstFailRetryAttempts 注释）。
        (double MedianTotal, double MedianAvg, double MedianMax) attempt1 = MeasureFloodEnvelopeOnce();
        var attempts = new List<(double MedianTotal, double MedianAvg, double MedianMax)>(FirstFailRetryAttempts)
        {
            attempt1,
        };

        List<string> failures = EvaluateEnvelope(attempt1);
        if (failures.Count > 0)
        {
            _output.WriteLine("首败（包络超线）→ 三泳道负载容差重跑：再补 " + (FirstFailRetryAttempts - 1) + " 次完整测量（REV-20261014-wave3-round2 #4）");
            for (int attempt = 1; attempt < FirstFailRetryAttempts; attempt++)
            {
                attempts.Add(MeasureFloodEnvelopeOnce());
            }

            failures = EvaluateEnvelope((
                MedianOf(attempts[0].MedianTotal, attempts[1].MedianTotal, attempts[2].MedianTotal),
                MedianOf(attempts[0].MedianAvg, attempts[1].MedianAvg, attempts[2].MedianAvg),
                MedianOf(attempts[0].MedianMax, attempts[1].MedianMax, attempts[2].MedianMax)));
        }

        Assert.True(failures.Count == 0,
            "300x300 full-flood envelope exceeded after first-fail median retry (not explainable as three-lane neighbour noise):\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void DeterministicQuotaThroughputMatchesWallclockOnFullFlood()
    {
        // [T-067 DEBT 清账] 吞吐对比门禁（配额校准依据，同机同轮自比）：
        // 300×300 全图泛洪（与上用例同图）在确定性配额
        // FlowFieldManager.DefaultCellsPerTick 下的总完成 tick 数、均帧格数、
        // 吞吐（格/ms）留档；与墙钟版（8ms 预算切片）同轮对照——**比率断言**
        // （≥90%，机器无关）：配额机制去掉的是 Stopwatch 决策位，不该损失
        // 一成以上吞吐，否则按任务口径调大配额。数字经 _output 留档进
        // REV-20261015-flowfield-determinism.md。
        var attempt1 = MeasureQuotaThroughputOnce();
        var attempts = new List<(double Ratio, int QuotaTicks, double QuotaThroughput, double WallThroughput, long WorkUnits)>
        {
            attempt1,
        };

        if (attempt1.Ratio < 0.9)
        {
            _output.WriteLine("首败（吞吐比 <0.9）→ 三泳道负载容差重跑：再补 " + (FirstFailRetryAttempts - 1) + " 次完整测量（REV-20261014-wave3-round2 #4 同口径）");
            for (int attempt = 1; attempt < FirstFailRetryAttempts; attempt++)
            {
                attempts.Add(MeasureQuotaThroughputOnce());
            }

            double ratio = MedianOf(attempts[0].Ratio, attempts[1].Ratio, attempts[2].Ratio);
            Assert.True(ratio >= 0.9,
                $"deterministic quota throughput fell under 90% of the wall-clock entry (median ratio {ratio:F3} "
                + "— quota needs enlarging per the task card, not a CI excuse)");
        }

        Assert.True(attempt1.Ratio > 0.0);
    }

    /// <summary>
    /// One throughput comparison attempt (same grid, same run): wall-clock
    /// 8 ms slicing vs the deterministic default quota. Returns the ratio
    /// (quota throughput / wall-clock throughput) plus the archive numbers.
    /// Hash equality (burst = wall-clock-sliced = quota-sliced) and the
    /// quota-side tick determinism are hard asserts — they never enter the
    /// retry path.
    /// </summary>
    private (double Ratio, int QuotaTicks, double QuotaThroughput, double WallThroughput, long WorkUnits) MeasureQuotaThroughputOnce()
    {
        var grid = TestMazes.SparseObstacles(300, 42, 0.08);
        var target = new CellCoord(299, 299);
        var burst = FlowField.BuildComplete(grid, target);

        // ---- wall-clock reference: 8 ms slices, exactly the production
        // legacy entry's cadence (same loop the envelope test above runs).
        var wallSliced = FlowField.StartBuild(grid, target);
        var wallWatch = Stopwatch.StartNew();
        int wallFrames = 0;
        while (!wallSliced.Advance(TimeSpan.FromMilliseconds(8)))
        {
            wallFrames++;
        }

        wallFrames++;
        wallWatch.Stop();

        // ---- deterministic entry: default quota via the MANAGER (the sim's
        // real call shape), so per-tick bookkeeping is the production path.
        var manager = new FlowFieldManager(grid);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, target);
        var quotaWatch = Stopwatch.StartNew();
        int quotaTicks = 0;
        long quotaCells = 0;
        while (!manager.UpdateDeterministic())
        {
            quotaTicks++;
            quotaCells += manager.LastTickCellsProcessed;
        }

        quotaTicks++;
        quotaCells += manager.LastTickCellsProcessed;
        quotaWatch.Stop();

        var quotaField = manager.GetField(key)!;
        Assert.True(quotaField.IsReady);
        Assert.Equal(burst.Hash, wallSliced.Hash); // 切片等价（墙钟口径既有钉）
        Assert.Equal(burst.Hash, quotaField.Hash); // 切片等价（配额口径新钉）
        Assert.Equal(burst.ProcessedCells, wallSliced.ProcessedCells); // 总工作量与切片方式无关
        Assert.Equal(burst.ProcessedCells, quotaField.ProcessedCells);
        Assert.Equal(burst.ProcessedCells, quotaCells); // 管理器记账=字段累计

        double wallMs = wallWatch.Elapsed.TotalMilliseconds;
        double quotaMs = quotaWatch.Elapsed.TotalMilliseconds;
        long work = burst.ProcessedCells;
        double wallThroughput = work / wallMs;
        double quotaThroughput = work / quotaMs;

        _output.WriteLine(
            "[T-067 DEBT 清账] 300x300 d=0.08 seed=42: workUnits=" + work
            + " | wallclock(8ms): frames=" + wallFrames + " total=" + (int)wallMs + "ms throughput=" + wallThroughput.ToString("F1") + " cells/ms"
            + " | quota(" + FlowFieldManager.DefaultCellsPerTick + "/tick): ticks=" + quotaTicks
            + " avgCellsPerTick=" + (quotaCells / (double)quotaTicks).ToString("F1")
            + " total=" + (int)quotaMs + "ms throughput=" + quotaThroughput.ToString("F1") + " cells/ms"
            + " | ratio=" + (quotaThroughput / wallThroughput).ToString("F3")
            + " mode=" + burst.Mode);

        return (quotaThroughput / wallThroughput, quotaTicks, quotaThroughput, wallThroughput, work);
    }

    /// <summary>
    /// One measurement attempt: one-shot burst build + three independent sliced
    /// builds; returns the per-metric medians of the three runs. Every sliced
    /// build is hash-asserted against the burst (deterministic, fails hard —
    /// never enters the retry path).
    /// </summary>
    private (double MedianTotal, double MedianAvg, double MedianMax) MeasureFloodEnvelopeOnce()
    {        var grid = TestMazes.SparseObstacles(300, 42, 0.08);
        var target = new CellCoord(299, 299);

        var burstWatch = Stopwatch.StartNew();
        var burst = FlowField.BuildComplete(grid, target);
        burstWatch.Stop();
        double burstMs = burstWatch.Elapsed.TotalMilliseconds;
        Assert.True(burst.IsReady);

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

        return (
            MedianOf(runs[0].Total, runs[1].Total, runs[2].Total),
            MedianOf(runs[0].Avg, runs[1].Avg, runs[2].Avg),
            MedianOf(runs[0].Max, runs[1].Max, runs[2].Max));
    }

    /// <summary>
    /// Envelope gates (threshold rationale kept verbatim from the original
    /// assertion comments); returns over-line messages instead of asserting —
    /// the first-fail retry mechanism re-evaluates the same gates on the
    /// across-attempt medians.
    /// </summary>
    private static List<string> EvaluateEnvelope((double MedianTotal, double MedianAvg, double MedianMax) m)
    {
        var failures = new List<string>();

        // Envelope = worst measured under loaded CI (388ms/9ms/22ms) + documented
        // machine-variance margin; regression catcher, not wall-clock certification
        // (unloaded numbers recorded above via _output).
        if (m.MedianTotal >= 2000)
        {
            failures.Add("median total flood time " + (int)m.MedianTotal + "ms exceeded the 2000ms envelope");
        }

        if (m.MedianAvg > 10.0)
        {
            failures.Add(
                "median average frame " + (int)m.MedianAvg + "ms exceeded the strict unloaded envelope"
                + " 10ms (=8ms budget x1.25 CI-jitter margin; unloaded archived avg is 7ms)");
        }

        if (m.MedianMax > 30.0)
        {
            failures.Add(
                "median worst frame " + (int)m.MedianMax + "ms exceeded the 30ms envelope"
                + " (one 30Hz tick = 33.3ms; 3.3ms boundary margin)");
        }

        return failures;
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
