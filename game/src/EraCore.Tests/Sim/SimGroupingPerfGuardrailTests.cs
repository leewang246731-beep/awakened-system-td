using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Spikes;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Sim;

// [C-12][F-155] 分组重构性能护栏（T-067 验收④：spike① 场景复跑，重构不许
// 显著劣化）。
//
// 结果留档（T-067 重构当日，本机=多泳道共享机）：
//   · 归档墙钟基线（spike① 报告 §3，2026-10-13）：sim Step 均 0.959 ms，
//     逻辑帧（sim+弹影）均 1.336 ms（三次运行中位）。
//   · 配对 A/B（同窗口背靠背，worktree 隔离的重构前 HEAD vs 分组迁移后）：
//     重构前 sim Step CPU 均 2.179 ms（同跑配对校准 829.45 ms）→ 重构后
//     取优 1.840 ms（配对校准 750.19 ms）——归一比值 ×0.94 ≤ ×1.2 任务线，
//     热路径索引化+分组迁移未劣化（验收④的 ×1.2 判据由此配对测量证实）。
//   · 本机共享争用实测（PerfSerialCollection 先例 REV-20261014-wave2-round1：
//     多邻居叠加劣化 ~×2.3；本任务实测同代码 best-of-3 跨分钟漂移 ×1.3）——
//     紧公差墙钟门禁在本机不可复现，故套内断言取进程 CPU 时间口径
//     （对邻居偷墙钟免疫）+ 4 次取优 + 并发容差 ×2.25（仓库既有先例口径），
//     抓"显著劣化"而不追分钟级邻居噪声；真值判据以配对 A/B 留档为准。
// 门禁线 = 0.959（归档基线）× 1.2（任务容差）× 2.25（共享机并发容差，先例
// 口径）= 2.587 CPU-ms。完整逻辑帧墙钟口径由 Spike1PerformanceTests 的
// 3.0 ms 回归线持续把守；弹影层是测试侧载荷，不随引擎重构变化，不进本护栏。
// 首败负载容差（REV-20261014-wave3-round2 #4）：门禁首败不直接红——再补
// 两次完整测量、三次尝试取中位数后重判，仍超才红（依据=三泳道 worktree
// 并行共享机对照实测，见 FirstFailRetryAttempts 注释）。
[Collection(PerfSerialCollection.Name)]
public sealed class SimGroupingPerfGuardrailTests
{
    private readonly ITestOutputHelper _output;

    public SimGroupingPerfGuardrailTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>归档墙钟基线（spike① 报告 §3 sim Step 均中位，ms）。</summary>
    private const double ArchivedSimStepMeanMs = 0.959;

    /// <summary>护栏容差（T-067 任务卡：≤ 基线 ×1.2）。</summary>
    private const double GuardrailMultiplier = 1.2;

    /// <summary>共享机并发容差（PerfSerialCollection/REV-20261014-wave2-round1 先例口径 ×2.25）。</summary>
    private const double SharedBoxContentionSlack = 2.25;

    // 首败重跑容差（REV-20261014-wave3-round2 #4）：本机为三泳道 worktree 并行
    // 共享机（A/B/C 三条泳道各持一份 worktree 背靠背跑 CI），同代码在三份
    // worktree 上的对照实测显示：门禁首败由邻居泳道的分钟级负载尖峰主导
    // （跨 worktree 对照同一次提交，一份红两份绿的形态反复出现），而真实
    // 劣化在三次重跑中位下稳定复现。故处置=首败不直接红：再补两次完整
    // 测量，取三次尝试的中位数判线——一次性负载尖峰被中位剔除，真劣化
    // （三中二超线）仍稳定转红；非首败路径零额外开销。
    private const int FirstFailRetryAttempts = 3;

    [Fact] // [F-155] 四次完整 3780 tick 运行取优（首跑含 JIT 预热，取优吸收）；首败→三次尝试取中位（负载容差）
    public void Sim_step_mean_stays_within_the_guardrail_after_the_grouping_refactor()
    {
        double limit = ArchivedSimStepMeanMs * GuardrailMultiplier * SharedBoxContentionSlack;

        var attempts = new List<double>(FirstFailRetryAttempts) { MeasureBestOfFourRuns() };
        _output.WriteLine($"分组重构护栏（CPU 时间口径）: 尝试1 sim Step 均 ms = {attempts[0]:F3}（4 次取优）");
        if (attempts[0] > limit)
        {
            // 首败重跑（三泳道 worktree 对照依据见 FirstFailRetryAttempts 注释）
            for (int attempt = 2; attempt <= FirstFailRetryAttempts; attempt++)
            {
                attempts.Add(MeasureBestOfFourRuns());
                _output.WriteLine($"首败重跑: 尝试{attempt} sim Step 均 ms = {attempts[^1]:F3}（4 次取优）");
            }
        }

        double verdict = attempts.Count == 1 ? attempts[0] : MedianOf(attempts);
        _output.WriteLine($"门禁线 = {ArchivedSimStepMeanMs}（归档基线）×{GuardrailMultiplier}（任务容差）×{SharedBoxContentionSlack}（共享机容差）= {limit:F3} ms");
        _output.WriteLine(
            attempts.Count == 1
                ? "判定样本: 首次尝试即过线，未触发重跑"
                : $"判定样本: 三次尝试 [{string.Join("/", attempts.Select(v => v.ToString("F3")))}] 取中位 {verdict:F3}（三泳道负载容差，REV-20261014-wave3-round2 #4）");
        _output.WriteLine("真值判据：配对 A/B 留档（类注释）——重构后/前归一 ×0.94 ≤ ×1.2");
        Assert.True(verdict <= limit,
            $"sim Step CPU 均 {verdict:F3} ms（{attempts.Count} 次尝试{(attempts.Count == 1 ? "" : "取中位")}）> 门禁线 {limit:F3} ms（归档 {ArchivedSimStepMeanMs}×{GuardrailMultiplier}×{SharedBoxContentionSlack}）——"
            + "三次尝试中位仍超线=非邻居噪声，分组迁移显著劣化热路径（对照 docs/engineering/spike1-report.md §3 与类注释的配对 A/B 口径复核分段）");
    }

    /// <summary>One attempt: four full spike① runs, best of the four (first run absorbs JIT warm-up).</summary>
    private double MeasureBestOfFourRuns()
    {
        const int runs = 4;
        var perRun = new double[runs];
        for (int run = 0; run < runs; run++)
        {
            perRun[run] = RunSimOnlyMeanMs(run);
        }

        return perRun.Min();
    }

    /// <summary>Middle value of an odd-length sample list (first-fail median verdict).</summary>
    private static double MedianOf(IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// One full spike① run measuring ONLY the <see cref="Simulation.Step"/>
    /// cost — in PROCESS CPU TIME (<c>TotalProcessorTime</c> deltas), not
    /// wall clock: CPU time is immune to neighbours stealing wall slices on
    /// this shared machine (residual SMT/cache contention is what the
    /// slack factor covers). The ballistic shadow layer is test-side load
    /// and stays out of this guardrail's scope.
    /// </summary>
    private double RunSimOnlyMeanMs(int runIndex)
    {
        var rng = new SimRng(Spike1PerfScenario.Seed);
        var sim = new Simulation(Spike1PerfScenario.BuildConfig(), rng, Spike1PerfScenario.Seed);
        Spike1PerfScenario.CarveMaze(sim.World);

        // Same command stream as the spike: 100 stress towers through the
        // real build-tower path, one every 6 ticks from t=200.
        List<CellCoord> towerCells = Spike1PerfScenario.TowerCells();
        for (int i = 0; i < Spike1PerfScenario.TowerCount; i++)
        {
            long at = Spike1PerfScenario.TowerPlacementStartTick + (i * Spike1PerfScenario.TowerPlacementSpacingTicks);
            CellCoord cell = towerCells[i];
            sim.Enqueue(new SimCommand(
                at, SimCommandKind.PlayerBuild,
                "{\"verb\":\"build-tower\",\"tower\":\"" + Spike1PerfScenario.TowerId
                + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}"));
        }

        // Measurement window: the spike's steady window [1980, 3780). One
        // CPU-time reading at the window start and end (process times lump
        // at clock-interrupt granularity, so per-tick deltas are quantized —
        // the WINDOW TOTAL is exact and the mean is total ÷ ticks).
        var process = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan windowStartCpu = default;
        for (long tick = 1; tick <= Spike1PerfScenario.TotalTicks; tick++)
        {
            if (tick == Spike1PerfScenario.WindowStartTick)
            {
                windowStartCpu = process.TotalProcessorTime;
            }

            sim.Step();
        }

        double windowCpuMs = (process.TotalProcessorTime - windowStartCpu).TotalMilliseconds;

        // Scenario integrity (the mean is only meaningful on the real load)
        Assert.Equal(Spike1PerfScenario.TowerCount, sim.Towers.Count);
        Assert.True(sim.Units.Count(u => u.Alive) >= 300, $"run{runIndex}: 活敌 {sim.Units.Count(u => u.Alive)} < 300（压测基数失守）");

        return windowCpuMs / Spike1PerfScenario.WindowTicks;
    }
}
