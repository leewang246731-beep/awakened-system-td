using System.Diagnostics;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// T-101 performance anchor (C-11; R-E3): throughput comparison of the three
/// flow-solve backends on the TierA 300×300 sparse full flood (same grid as
/// the T-037/T-067 anchors), archived via <see cref="ITestOutputHelper"/>.
/// Machine-independent gate: each sliced backend must keep ≥90% of the
/// full-burst throughput — slicing removes Stopwatch decision points from
/// the per-unit path, so it must not COST a tenth of it. First-fail retry
/// follows the three-lane load-tolerance convention
/// (REV-20261014-wave3-round2 #4). Also prints the Web frame-budget
/// recommendation (<see cref="FlowSolveBackendFactory.DefaultWebFrameCellBudget"/>
/// 口径).
/// </summary>
// [C-11] [F-159] [F-164] T-101 三后端吞吐对比留档（300×300 性能锚）
[Collection(PerfSerialCollection.Name)]
public class FlowSolveBackendThroughputTests
{
    // 首败重跑容差（REV-20261014-wave3-round2 #4 同口径，见 FlowFieldManagerTests 注释）
    private const int FirstFailRetryAttempts = 3;

    private readonly ITestOutputHelper _output;

    public FlowSolveBackendThroughputTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ThreeBackendThroughputAt300x300Anchored()
    {
        // [F-159] [F-164] 三后端 300×300 吞吐对比留档 + ≥90% 机器无关比率门禁
        var attempt1 = MeasureOnce();
        var attempts = new List<(double TickRatio, double FrameRatio, double BurstMs, double TickMs, double FrameMs, long Work)>
        {
            attempt1,
        };

        if (attempt1.TickRatio < 0.9 || attempt1.FrameRatio < 0.9)
        {
            _output.WriteLine("首败（切片后端吞吐比 <0.9）→ 三泳道负载容差重跑：再补 "
                + (FirstFailRetryAttempts - 1) + " 次完整测量（REV-20261014-wave3-round2 #4 同口径）");
            for (int attempt = 1; attempt < FirstFailRetryAttempts; attempt++)
            {
                attempts.Add(MeasureOnce());
            }

            double tickRatio = MedianOf(attempts[0].TickRatio, attempts[1].TickRatio, attempts[2].TickRatio);
            double frameRatio = MedianOf(attempts[0].FrameRatio, attempts[1].FrameRatio, attempts[2].FrameRatio);
            Assert.True(tickRatio >= 0.9,
                $"per-tick-quota throughput fell under 90% of full-burst (median ratio {tickRatio:F3})");
            Assert.True(frameRatio >= 0.9,
                $"per-frame-budget throughput fell under 90% of full-burst (median ratio {frameRatio:F3})");
        }
    }

    /// <summary>
    /// One measurement attempt (same grid, same run): every backend solves
    /// the 300×300 sparse flood three times round-robin (burst → tick →
    /// frame, ×3) and the per-backend MEDIAN wall time enters the ratio —
    /// the two sliced backends execute identical statements, so a ratio
    /// below 1 can only be scheduling noise; the median kills transient
    /// neighbour-lane spikes that a single ordered pass can land entirely
    /// on one backend. The three-way grid equality (hash + charged work) is
    /// hard-asserted and never enters the retry path.
    /// </summary>
    private (double TickRatio, double FrameRatio, double BurstMs, double TickMs, double FrameMs, long Work) MeasureOnce()
    {
        var grid = TestMazes.SparseObstacles(300, 42, 0.08);
        var target = new CellCoord(299, 299);
        var backends = new[]
        {
            FlowSolveBackend.FullBurst,
            FlowSolveBackend.PerTickCellQuota,
            FlowSolveBackend.PerFrameCellBudget,
        };

        // 等价性硬断言（本任务核心验收的锚点复验）：三后端逐格哈希/工作量全等。
        long work = 0;
        long hash = 0;
        foreach (FlowSolveBackend backend in backends)
        {
            var field = FlowSolveBackendFactory.Solve(grid, target, backend);
            Assert.True(field.IsReady);
            if (work == 0)
            {
                work = field.ProcessedCells;
                hash = field.Hash;
            }
            else
            {
                Assert.Equal(work, field.ProcessedCells);
                Assert.Equal(hash, field.Hash);
            }
        }

        var timings = new double[backends.Length][];
        for (int backend = 0; backend < backends.Length; backend++)
        {
            timings[backend] = new double[3];
        }

        // 轮转 ×3：三后端交替测，同一后端取中位——单次邻居负载尖峰不再整段
        // 落在某个后端的独占计时窗内。
        for (int rep = 0; rep < 3; rep++)
        {
            for (int backend = 0; backend < backends.Length; backend++)
            {
                var watch = Stopwatch.StartNew();
                FlowSolveBackendFactory.Solve(grid, target, backends[backend]);
                watch.Stop();
                timings[backend][rep] = watch.Elapsed.TotalMilliseconds;
            }
        }

        double burstMs = MedianOf(timings[0][0], timings[0][1], timings[0][2]);
        double tickMs = MedianOf(timings[1][0], timings[1][1], timings[1][2]);
        double frameMs = MedianOf(timings[2][0], timings[2][1], timings[2][2]);
        double burstThroughput = work / burstMs;
        double tickThroughput = work / tickMs;
        double frameThroughput = work / frameMs;

        _output.WriteLine(
            "[T-101 perf] 300x300 d=0.08 seed=42: workUnits=" + work
            + " | fullBurst=" + (int)burstMs + "ms (" + burstThroughput.ToString("F1") + " cells/ms)"
            + " | perTickQuota(" + FlowSolveBackendFactory.DefaultTickQuota + ")=" + (int)tickMs
            + "ms (" + tickThroughput.ToString("F1") + " cells/ms, ratio " + (tickThroughput / burstThroughput).ToString("F3") + ")"
            + " | perFrameBudget(" + FlowSolveBackendFactory.DefaultWebFrameCellBudget + ")=" + (int)frameMs
            + "ms (" + frameThroughput.ToString("F1") + " cells/ms, ratio " + (frameThroughput / burstThroughput).ToString("F3") + ")");

        // Web 帧预算口径建议值（留档）：单位成本=burst 总时/工作量；本机与 4× 悲观
        // WebAssembly 折算下的每帧墙钟，对 8ms 设计切片（v3.2 §6）的裕量。
        double nativePerFrameMs = FlowSolveBackendFactory.DefaultWebFrameCellBudget * burstMs / work;
        double webPessimisticPerFrameMs = nativePerFrameMs * 4.0;
        _output.WriteLine(
            "[T-101 Web] 帧预算建议=" + FlowSolveBackendFactory.DefaultWebFrameCellBudget + " 格/帧"
            + "（本机 " + nativePerFrameMs.ToString("F2") + "ms/帧；WebAssembly 4× 悲观 "
            + webPessimisticPerFrameMs.ToString("F2") + "ms/帧 —— 8ms 设计切片内裕量 "
            + (8.0 / webPessimisticPerFrameMs).ToString("F1") + "×；证据链见 DefaultWebFrameCellBudget 注释）");

        return (tickThroughput / burstThroughput, frameThroughput / burstThroughput, burstMs, tickMs, frameMs, work);
    }

    /// <summary>Middle value of three samples (odd count → exact order statistic).</summary>
    private static double MedianOf(double a, double b, double c) =>
        Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
