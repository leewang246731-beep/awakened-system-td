using System.Diagnostics;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Spikes;

/// <summary>
/// T-043 spike① (F-166, G8 Tier A 逻辑预算口径): the 300-enemy +
/// 1000-projectile maze-composite benchmark over the REAL
/// <see cref="Simulation"/> orchestrator (T-040) — every subsystem in the
/// tick pipeline is production code (waves → flow field → unit movement →
/// weapon firing → state hash → snapshot publish), driven 3780 ticks with a
/// 1800-tick (60 s @ 30 Hz) steady-state measurement window. The
/// projectile-in-flight load rides the <see cref="Spike1Projectile"/> shadow
/// layer fed by the towers' real fire events (the E1 combat path is
/// hitscan — no in-flight entities exist to budget, see the scenario doc).
/// <para>
/// 测量与断言口径（08 任务卡 T-043 + 13-quality-gates G8）：
/// <list type="bullet">
/// <item>整逻辑帧 = sim <c>Step()</c> Stopwatch 外包裹 + 弹影层步进；均/p95/p99/单帧最大，
/// 三次独立运行取中位数（T-037 稳态化先例：单次极值由一次性调度尖峰主导）；</item>
/// <item>流场单列 = attribution twin（<see cref="Spike1WorldFlowView"/> 上的第二个
/// <see cref="FlowFieldManager"/>，同一 8 ms 预算、同一脏区事件）——Simulation 无分段钩子，
/// 按 T-043 卡允许的粗粒度外包裹口径，局限注明于报告 §4；</item>
/// <item>门禁线（Tier A 逻辑层）：均逻辑帧 ≤ 16.6 ms × 60% = 10 ms（渲染余量 40%）、
/// sim tick p99 ≤ 12 ms（G8 Tier A 原文）、流场帧切片均值 ≤ 8 ms（设计 v3.2 §6）——
/// 阈值裕量推导注释随各断言（首跑留档数字见 docs/engineering/spike1-report.md）；</item>
/// <item>首败负载容差（REV-20261014-wave3-round2 #4）：门禁首败不直接红——再补
/// 两批完整测量、三批各指标取中位数后重判，仍超才红；依据=本机三泳道
/// worktree 并行共享，同代码跨 worktree 对照显示首败由邻居泳道分钟级负载
/// 尖峰主导（详见测试方法头注释）。场景完整性断言是确定性的，不参与重跑。</item>
/// </list>
/// 性能采样全部真实（RL2/CS-4.5：真 Stopwatch、真计数器，无跳过无 mock）。
/// </para>
/// </summary>
// [F-166] spike① 性能门禁基准（T-043；300 敌+1000 弹迷宫复合逻辑预算达标并留档）
// [Collection(PerfSerialCollection.Name)]（F1）：计时敏感测试独占串行集合——
// 三次运行取中位数的墙钟断言不容并行邻居偷 CPU（见 PerfSerialCollection.cs）。
[Collection(PerfSerialCollection.Name)]
public sealed class Spike1PerformanceTests
{
    private readonly ITestOutputHelper _output;

    public Spike1PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact] // [F-166] [C-12] 迷宫复合 300 敌 + 1000 弹：整逻辑帧/流场切片预算（三次运行中位数；首败→三批取中位重判）
    public void MazeComposite300Enemies1000ProjectilesLogicBudget()
    {
        // 首败重跑容差（REV-20261014-wave3-round2 #4）：本机为三泳道 worktree
        // 并行共享机（三条泳道各持一份 worktree 背靠背跑 CI），同代码跨三份
        // worktree 对照实测：门禁首败由邻居泳道分钟级负载尖峰主导（同一次
        // 提交一份红两份绿的形态反复出现），真劣化在多批中位下稳定复现。
        // 处置=首败不直接红：再补两批完整测量（每批=3780 tick×3 次运行取中位），
        // 三批的各指标取中位数后重判——一次性尖峰被中位剔除，真劣化（三中
        // 二超线）仍稳定转红；非首败路径零额外开销。
        const int attemptsOnFirstFail = 3;

        (Spike1GateMetrics Gates, List<Spike1RunMetrics> Runs) attempt1 = RunBatch(0);
        AssertScenarioIntegrity(attempt1.Runs); // 场景完整性是确定性断言，与负载无关，不参与重跑

        var attempts = new List<Spike1GateMetrics>(attemptsOnFirstFail) { attempt1.Gates };
        List<string> failures = EvaluateGates(attempt1.Gates);
        if (failures.Count > 0)
        {
            _output.WriteLine($"首败（{failures.Count} 条门禁超线）→ 三泳道负载容差重跑：再补 {attemptsOnFirstFail - 1} 批（REV-20261014-wave3-round2 #4，依据见方法头注释）");
            for (int attempt = 1; attempt < attemptsOnFirstFail; attempt++)
            {
                (Spike1GateMetrics Gates, List<Spike1RunMetrics> Runs) retry = RunBatch(attempt);
                AssertScenarioIntegrity(retry.Runs);
                attempts.Add(retry.Gates);
            }

            failures = EvaluateGates(MedianAcrossBatches(attempts));
        }

        Assert.True(failures.Count == 0,
            "spike① 逻辑预算门禁超线（三批中位重判后仍超，非三泳道邻居噪声可解释）：\n"
            + string.Join("\n", failures));
        _output.WriteLine("判定: PASS —— 300 敌+1000 弹迷宫复合逻辑预算达标（Tier A 口径；真机 60fps 判定属 Tier B，CS-4.8）");
    }

    /// <summary>One measurement attempt: three full 3780-tick runs, per-gate metric = median of the three.</summary>
    private (Spike1GateMetrics Gates, List<Spike1RunMetrics> Runs) RunBatch(int batchIndex)
    {
        const int runs = 3;
        var metrics = new Spike1RunMetrics[runs];
        for (int run = 0; run < runs; run++)
        {
            metrics[run] = RunOnce(run);
        }

        _output.WriteLine($"==== spike① 三次运行汇总（T-043 / F-166；批 {batchIndex}，每行一次完整 3780 tick 运行） ====");
        foreach (Spike1RunMetrics m in metrics)
        {
            _output.WriteLine(
                $"run{m.RunIndex}: 敌(最少/均)={m.LiveEnemiesMin}/{m.LiveEnemiesMean:F0} 在动(最少)={m.MoversMin} 到达堆(窗末)={m.ArrivedAtWindowEnd}" +
                $" | 弹(最少/均/最多)={m.LiveProjMin}/{m.LiveProjMean:F0}/{m.LiveProjMax} 窗内射击={m.ShotsLandedInWindow} 击杀={m.KillsTotal}" +
                $" | sim ms(均/p95/p99/max)={m.SimMeanMs:F3}/{m.SimP95Ms:F3}/{m.SimP99Ms:F3}/{m.SimMaxMs:F2}" +
                $" | +弹影 ms(均/max)={m.ProjMeanMs:F3}/{m.ProjMaxMs:F2}" +
                $" | 逻辑帧 ms(均/p95/p99/max)={m.FrameMeanMs:F3}/{m.FrameP95Ms:F3}/{m.FrameP99Ms:F3}/{m.FrameMaxMs:F2}");
            _output.WriteLine(
                $"        流场: 初建={m.FlowInitialBuildMs:F2}ms 重建次数={m.FlowRebuildCount} 重建均/最大={m.FlowRebuildMeanMs:F2}/{m.FlowRebuildMaxMs:F2}ms" +
                $" 全程单切片最大={m.FlowSliceMaxMs:F2}ms | 窗内切片(均/最大)={m.FlowWindowMeanMs:F4}/{m.FlowWindowMaxMs:F3}ms" +
                $" | 剖面微采样(均): hash={m.HashMeanMs:F3} move={m.MoveTwinMeanMs:F3} target-scan={m.ScanTwinMeanMs:F3}ms" +
                $" | 塔={m.TowersPlaced} 命令={m.CommandsApplied} 迷宫比={m.MazePathRatio:F2}({m.MazePathCells}格)");
        }

        var gates = new Spike1GateMetrics(
            FrameMeanMs: MedianOf(metrics[0].FrameMeanMs, metrics[1].FrameMeanMs, metrics[2].FrameMeanMs),
            SimP99Ms: MedianOf(metrics[0].SimP99Ms, metrics[1].SimP99Ms, metrics[2].SimP99Ms),
            FlowWindowMeanMs: MedianOf(metrics[0].FlowWindowMeanMs, metrics[1].FlowWindowMeanMs, metrics[2].FlowWindowMeanMs),
            FlowWindowMaxMs: MedianOf(metrics[0].FlowWindowMaxMs, metrics[1].FlowWindowMaxMs, metrics[2].FlowWindowMaxMs),
            FlowSliceMaxMs: MedianOf(metrics[0].FlowSliceMaxMs, metrics[1].FlowSliceMaxMs, metrics[2].FlowSliceMaxMs));
        _output.WriteLine(
            $"批{batchIndex} 中位数: 逻辑帧均={gates.FrameMeanMs:F3}ms sim p99={gates.SimP99Ms:F3}ms 流场均={gates.FlowWindowMeanMs:F4}ms" +
            $" 流场窗内最大={gates.FlowWindowMaxMs:F3}ms 流场单切片最大(全程)={gates.FlowSliceMaxMs:F2}ms");
        return (gates, metrics.ToList());
    }

    /// <summary>
    /// Scenario integrity (the load is real before the budget is read) —
    /// deterministic, immune to neighbour load, so it always fails hard
    /// and never enters the first-fail retry path.
    /// </summary>
    private static void AssertScenarioIntegrity(IReadOnlyList<Spike1RunMetrics> metrics)
    {
        foreach (Spike1RunMetrics m in metrics)
        {
            Assert.True(m.TowersPlaced == Spike1PerfScenario.TowerCount,
                $"run{m.RunIndex}: 塔放置 {m.TowersPlaced} < {Spike1PerfScenario.TowerCount}（命令流未全落位）");
            Assert.True(m.CommandsApplied == Spike1PerfScenario.TowerCount,
                $"run{m.RunIndex}: 应用命令 {m.CommandsApplied} != {Spike1PerfScenario.TowerCount}");
            Assert.True(m.LiveEnemiesMin >= 300,
                $"run{m.RunIndex}: 窗内在场活敌最少 {m.LiveEnemiesMin} < 300（压测基数失守）");
            Assert.True(m.LiveProjMean is >= 850 and <= 1250,
                $"run{m.RunIndex}: 窗内活弹均值 {m.LiveProjMean:F0} 不在 [850,1250]（目标 1000±25%，射速×寿命推导见报告 §2）");
            Assert.True(m.LiveProjMin >= 700,
                $"run{m.RunIndex}: 窗内活弹最少 {m.LiveProjMin} < 700（弹层供给塌陷：塔火力断续）");
            Assert.True(m.ShotsLandedInWindow >= 40L * Spike1PerfScenario.WindowTicks,
                $"run{m.RunIndex}: 窗内真实射击 {m.ShotsLandedInWindow} < {40L * Spike1PerfScenario.WindowTicks}（≥80% 塔持续供弹才成立）");
            Assert.True(m.MazePathRatio >= 3.0,
                $"run{m.RunIndex}: 迷宫化路径比 {m.MazePathRatio:F2} < 3.0（场景不是迷宫）");
        }
    }

    /// <summary>
    /// Tier A 逻辑预算门禁（阈值推导见各条注释原文；返回超线明细而非直接
    /// 断言——首败重跑机制需要在三批中位样本上重判同一组门禁）。
    /// </summary>
    private static List<string> EvaluateGates(Spike1GateMetrics g)
    {
        var failures = new List<string>();

        // 门禁① 均逻辑帧 ≤ 10 ms = 16.6 ms 帧预算 × 60%（60 fps 渲染档留 40% 给表现层，
        // 08 任务卡 T-043 口径）。首跑留档中位 1.336 ms ⇒ 裕量 7.5×。
        // 回归线 3.0 ms ≈ 中位 × 2.25（T-037 先例：CI 并发下流场均帧劣化 ~×2.3，
        // 取整覆盖并发抖动、同时仍在设计线 3.3× 以内——回归抓手而非墙钟认证）。
        if (g.FrameMeanMs > 10.0)
        {
            failures.Add($"中位均逻辑帧 {g.FrameMeanMs:F3} ms > 10 ms（16.6×60% 设计线，渲染余量口径）");
        }

        if (g.FrameMeanMs > 3.0)
        {
            failures.Add($"中位均逻辑帧 {g.FrameMeanMs:F3} ms > 3.0 ms（首跑留档 1.336×2.25 回归线，T-037 并发劣化先例）");
        }

        // 门禁② sim tick p99 ≤ 12 ms（G8 Tier A 原文：CI headless 逻辑预算）。
        // 首跑留档中位 1.677 ms ⇒ 裕量 7.2×；回归线 4.0 ms ≈ 中位 × 2.4（同上推导取整）。
        if (g.SimP99Ms > 12.0)
        {
            failures.Add($"中位 sim p99 {g.SimP99Ms:F3} ms > 12 ms（G8 Tier A）");
        }

        if (g.SimP99Ms > 4.0)
        {
            failures.Add($"中位 sim p99 {g.SimP99Ms:F3} ms > 4.0 ms（首跑留档 1.677×2.4 回归线）");
        }

        // 门禁③ 流场帧切片 ≤ 8 ms（设计 v3.2 §6）：窗内无建筑事件，切片应近零
        // （首跑留档中位均 0.0003 ms / 最大 0.018 ms）；均值线 8 ms 为设计预算，
        // 窗内最大 ≤ 8 ms 锁住"稳态窗内不得有重建"，1.0 ms 回归线 = 中位均的
        // 3000× 量级裕量（空载 vs CI 并发仍在 1 ms 内，spike② 同口径实测）。
        if (g.FlowWindowMeanMs > 8.0)
        {
            failures.Add($"中位流场均切片 {g.FlowWindowMeanMs:F4} ms > 8 ms（v3.2 §6 流场预算）");
        }

        if (g.FlowWindowMeanMs > 1.0)
        {
            failures.Add($"中位流场均切片 {g.FlowWindowMeanMs:F4} ms > 1.0 ms（稳态窗回归线，留档 0.0003 ms）");
        }

        if (g.FlowWindowMaxMs > 8.0)
        {
            failures.Add($"中位流场窗内最大切片 {g.FlowWindowMaxMs:F3} ms > 8 ms（稳态窗内不应有重建过冲）");
        }

        // 门禁④ 流场预算机制本身：全程（含 100 次塔放置触发的全图重建）单次
        // Update 切片 ≤ 10 ms = 8 ms 预算 + 批过冲余量（spike② 同口径断言先例；
        // 预算机制按 Stopwatch 截断，过冲仅来自 frontier 批粒度与计时器粒度）。
        if (g.FlowSliceMaxMs > 10.0)
        {
            failures.Add($"中位流场单切片最大 {g.FlowSliceMaxMs:F2} ms > 10 ms（8 ms 预算+过冲余量，预算机制失效）");
        }

        return failures;
    }

    /// <summary>Per-gate-metric median across the (odd-count) retry batches — the first-fail verdict sample.</summary>
    private static Spike1GateMetrics MedianAcrossBatches(IReadOnlyList<Spike1GateMetrics> batches) => new(
        FrameMeanMs: MedianOf(batches[0].FrameMeanMs, batches[1].FrameMeanMs, batches[2].FrameMeanMs),
        SimP99Ms: MedianOf(batches[0].SimP99Ms, batches[1].SimP99Ms, batches[2].SimP99Ms),
        FlowWindowMeanMs: MedianOf(batches[0].FlowWindowMeanMs, batches[1].FlowWindowMeanMs, batches[2].FlowWindowMeanMs),
        FlowWindowMaxMs: MedianOf(batches[0].FlowWindowMaxMs, batches[1].FlowWindowMaxMs, batches[2].FlowWindowMaxMs),
        FlowSliceMaxMs: MedianOf(batches[0].FlowSliceMaxMs, batches[1].FlowSliceMaxMs, batches[2].FlowSliceMaxMs));

    /// <summary>The five median metrics the Tier A gates read (one measurement attempt = one sample).</summary>
    private sealed record Spike1GateMetrics(
        double FrameMeanMs,
        double SimP99Ms,
        double FlowWindowMeanMs,
        double FlowWindowMaxMs,
        double FlowSliceMaxMs);

    // ------------------------------------------------------------ 单次完整运行

    private Spike1RunMetrics RunOnce(int runIndex)
    {
        var rng = new SimRng(Spike1PerfScenario.Seed);
        var sim = new Simulation(Spike1PerfScenario.BuildConfig(), rng, Spike1PerfScenario.Seed);
        Spike1PerfScenario.CarveMaze(sim.World);

        // ---- 命令流：100 座压测塔（真 build-tower 路径：扣费/占用/流场脏区） ----
        List<CellCoord> towerCells = Spike1PerfScenario.TowerCells();
        Assert.True(towerCells.Count >= Spike1PerfScenario.TowerCount,
            $"环带可放置格 {towerCells.Count} < {Spike1PerfScenario.TowerCount}：场景几何与预期不符");
        var placementDue = new Dictionary<long, int>();
        for (int i = 0; i < Spike1PerfScenario.TowerCount; i++)
        {
            long tick = Spike1PerfScenario.TowerPlacementStartTick + (i * Spike1PerfScenario.TowerPlacementSpacingTicks);
            placementDue[tick] = i;
            sim.Enqueue(SimCommands.BuildTower(tick, Spike1PerfScenario.TowerId, towerCells[i]));
        }

        // ---- 流场 attribution twin（同世界视图、同预算、同脏区事件） ---------
        var twinView = new Spike1WorldFlowView(sim.World);
        var twin = new FlowFieldManager(twinView, FlowFieldManager.DefaultMaxActiveFields, FlowFieldManager.DefaultFrameBudgetMs);
        var twinKey = new FlowFieldKey(0, 0);
        twin.Acquire(twinKey, Spike1PerfScenario.Base);
        var movementTwin = new UnitMovement(
            () => twin.GetField(twinKey) ?? throw new InvalidOperationException("twin field missing"),
            Spike1PerfScenario.CellSize);

        // ---- 状态与采样容器 -------------------------------------------------
        var prevFired = new int[Spike1PerfScenario.TowerCount];
        var projectiles = new List<Spike1Projectile>(1400);
        long nextProjectileId = 9_000_000;
        long projectileHits = 0;

        var simMs = new double[Spike1PerfScenario.WindowTicks];
        var projMs = new double[Spike1PerfScenario.WindowTicks];
        var flowMs = new double[Spike1PerfScenario.WindowTicks];

        double flowInitialBuildMs = 0;
        double flowPendingMs = 0;
        double flowRebuildTotalMs = 0, flowRebuildMaxMs = 0, flowSliceMaxMs = 0;
        int flowRebuildCount = 0, lastRebuildCount = 0;

        int liveEnemiesMin = int.MaxValue, moversMin = int.MaxValue, arrivedAtWindowEnd = 0;
        double liveEnemiesSum = 0;
        int liveSamples = 0;
        int liveProjMin = int.MaxValue, liveProjMax = 0;
        double liveProjSum = 0;
        double hashMsSum = 0, moveMsSum = 0, scanMsSum = 0;
        int attrSamples = 0;
        long shotsAtWindowStart = -1, shotsInWindow = 0;
        double mazePathRatio = 0;
        int mazePathCells = 0;

        var swSim = new Stopwatch();
        var swFlow = new Stopwatch();
        var swProj = new Stopwatch();
        var swAttr = new Stopwatch();

        // ---- 主循环 ---------------------------------------------------------
        for (long t = 0; t < Spike1PerfScenario.TotalTicks; t++)
        {
            if (placementDue.TryGetValue(t, out int towerIndex))
            {
                CellCoord cell = towerCells[towerIndex];
                twin.MarkDirty(new CellRegion(cell.X - 1, cell.Y - 1, 3, 3)); // mirrors ApplyBuildTower
            }

            swSim.Restart();
            sim.Step();
            swSim.Stop();
            double simTickMs = swSim.Elapsed.TotalMilliseconds;

            swFlow.Restart();
            twin.Update(FlowFieldManager.DefaultFrameBudgetMs);
            swFlow.Stop();
            double flowTickMs = swFlow.Elapsed.TotalMilliseconds;
            flowSliceMaxMs = Math.Max(flowSliceMaxMs, flowTickMs);

            int rebuildCountNow = twin.GetRebuildCount(twinKey);
            bool completing = rebuildCountNow > lastRebuildCount && twin.PendingFieldCount == 0;
            if (twin.PendingFieldCount > 0 || completing)
            {
                flowPendingMs += flowTickMs;
                if (completing)
                {
                    if (lastRebuildCount == 0)
                    {
                        flowInitialBuildMs = flowPendingMs; // first completion = initial full build
                    }
                    else
                    {
                        flowRebuildCount++;
                        flowRebuildTotalMs += flowPendingMs;
                        flowRebuildMaxMs = Math.Max(flowRebuildMaxMs, flowPendingMs);
                    }

                    flowPendingMs = 0;
                    lastRebuildCount = rebuildCountNow;
                }
            }

            swProj.Restart();
            StepProjectileLayer(sim, prevFired, projectiles, ref nextProjectileId, ref projectileHits);
            swProj.Stop();
            double projTickMs = swProj.Elapsed.TotalMilliseconds;

            // ---- 窗内采样 ---------------------------------------------------
            if (t >= Spike1PerfScenario.WindowStartTick)
            {
                int w = (int)(t - Spike1PerfScenario.WindowStartTick);
                simMs[w] = simTickMs;
                projMs[w] = projTickMs;
                flowMs[w] = flowTickMs;

                if (shotsAtWindowStart < 0)
                {
                    shotsAtWindowStart = sim.ShotsLanded;
                }

                shotsInWindow = sim.ShotsLanded - shotsAtWindowStart;

                int live = 0, movers = 0, arrived = 0;
                foreach (Unit unit in sim.Units)
                {
                    if (!unit.Alive)
                    {
                        continue;
                    }

                    live++;
                    if (unit.ArrivedAtTarget)
                    {
                        arrived++;
                    }
                    else
                    {
                        movers++;
                    }
                }

                liveEnemiesMin = Math.Min(liveEnemiesMin, live);
                moversMin = Math.Min(moversMin, movers);
                arrivedAtWindowEnd = arrived;
                liveEnemiesSum += live;
                liveSamples++;
                liveProjMin = Math.Min(liveProjMin, projectiles.Count);
                liveProjMax = Math.Max(liveProjMax, projectiles.Count);
                liveProjSum += projectiles.Count;

                // 分段剖面粗采样（每 60 tick 一次；不进入门禁计时）：
                // hash=真 ComputeStateHash；move=twin UnitMovement 推进同位克隆；
                // target-scan=按 Simulation.FindTarget 同算法的百塔扫描复算。
                if (w % 60 == 0)
                {
                    swAttr.Restart();
                    long hash = sim.ComputeStateHash();
                    swAttr.Stop();
                    hashMsSum += swAttr.Elapsed.TotalMilliseconds;
                    _ = hash;

                    var clones = new List<Unit>(live);
                    foreach (Unit unit in sim.Units)
                    {
                        if (unit.Alive && !unit.ArrivedAtTarget)
                        {
                            clones.Add(new Unit(
                                new EntityId(8_000_000 + clones.Count), unit.ContentId,
                                unit.X, unit.Y, unit.MaxHp, unit.Armor, unit.SpeedPxPerTick));
                        }
                    }

                    swAttr.Restart();
                    movementTwin.StepAll(clones);
                    swAttr.Stop();
                    moveMsSum += swAttr.Elapsed.TotalMilliseconds;

                    swAttr.Restart();
                    long fed = 0;
                    foreach (SimTower tower in sim.Towers)
                    {
                        if (NearestInWeaponRange(sim.Units, tower) is not null)
                        {
                            fed++;
                        }
                    }

                    swAttr.Stop();
                    scanMsSum += swAttr.Elapsed.TotalMilliseconds;
                    _ = fed;

                    attrSamples++;
                }

                // 迷宫路径比（窗首一次；场已就绪、塔已全放）
                if (t == Spike1PerfScenario.WindowStartTick
                    && twin.GetField(twinKey) is { IsReady: true } field)
                {
                    IReadOnlyList<CellCoord> path = field.BuildPath(Spike1PerfScenario.PathProbe);
                    mazePathCells = path.Count;
                    int dx = Math.Abs(Spike1PerfScenario.Base.X - Spike1PerfScenario.PathProbe.X);
                    int dy = Math.Abs(Spike1PerfScenario.Base.Y - Spike1PerfScenario.PathProbe.Y);
                    mazePathRatio = path.Count / Math.Sqrt((dx * dx) + (dy * dy));
                }
            }

            if (t % 300 == 0)
            {
                _output.WriteLine(
                    $"run{runIndex} t={t,4} | 塔={sim.Towers.Count,3} 活敌={CountAlive(sim),3} 弹={projectiles.Count,4}" +
                    $" | sim={simTickMs:F3}ms flow={flowTickMs:F3}ms proj={projTickMs:F3}ms");
            }
        }

        // ---- 收束指标 -------------------------------------------------------
        var frameMs = new double[Spike1PerfScenario.WindowTicks];
        for (int i = 0; i < frameMs.Length; i++)
        {
            frameMs[i] = simMs[i] + projMs[i];
        }

        int towersPlaced = sim.Towers.Count;
        long commandsApplied = sim.AppliedCommandCount;

        _output.WriteLine(
            $"run{runIndex} 完成: 命中弹={projectileHits} 总射击={sim.ShotsLanded} 击杀={sim.KillCount}" +
            $" | 敌(最少/均)={liveEnemiesMin}/{(liveSamples > 0 ? liveEnemiesSum / liveSamples : 0):F0}" +
            $" | 弹(最少/均/最多)={liveProjMin}/{(liveSamples > 0 ? liveProjSum / liveSamples : 0):F0}/{liveProjMax}");

        return new Spike1RunMetrics(
            RunIndex: runIndex,
            SimMeanMs: Mean(simMs),
            SimP95Ms: Percentile(simMs, 0.95),
            SimP99Ms: Percentile(simMs, 0.99),
            SimMaxMs: Max(simMs),
            ProjMeanMs: Mean(projMs),
            ProjMaxMs: Max(projMs),
            FrameMeanMs: Mean(frameMs),
            FrameP95Ms: Percentile(frameMs, 0.95),
            FrameP99Ms: Percentile(frameMs, 0.99),
            FrameMaxMs: Max(frameMs),
            FlowWindowMeanMs: Mean(flowMs),
            FlowWindowMaxMs: Max(flowMs),
            FlowSliceMaxMs: flowSliceMaxMs,
            FlowInitialBuildMs: flowInitialBuildMs,
            FlowRebuildCount: flowRebuildCount,
            FlowRebuildMeanMs: flowRebuildCount > 0 ? flowRebuildTotalMs / flowRebuildCount : 0,
            FlowRebuildMaxMs: flowRebuildMaxMs,
            LiveEnemiesMin: liveEnemiesMin,
            LiveEnemiesMean: liveSamples > 0 ? liveEnemiesSum / liveSamples : 0,
            MoversMin: moversMin,
            ArrivedAtWindowEnd: arrivedAtWindowEnd,
            LiveProjMin: liveProjMin == int.MaxValue ? 0 : liveProjMin,
            LiveProjMean: liveSamples > 0 ? liveProjSum / liveSamples : 0,
            LiveProjMax: liveProjMax,
            ShotsLandedInWindow: shotsInWindow,
            ShotsLandedTotal: sim.ShotsLanded,
            KillsTotal: sim.KillCount,
            TowersPlaced: towersPlaced,
            CommandsApplied: commandsApplied,
            MazePathRatio: mazePathRatio,
            MazePathCells: mazePathCells,
            HashMeanMs: attrSamples > 0 ? hashMsSum / attrSamples : 0,
            MoveTwinMeanMs: attrSamples > 0 ? moveMsSum / attrSamples : 0,
            ScanTwinMeanMs: attrSamples > 0 ? scanMsSum / attrSamples : 0);
    }

    // ------------------------------------------------------------ 弹影层

    /// <summary>
    /// Advances the ballistic shadow layer one tick: spawns one projectile
    /// per REAL tower firing observed this tick (FiredCount delta), then
    /// moves every live projectile (homing + proximity + lifetime culling,
    /// swap-remove).
    /// </summary>
    private static void StepProjectileLayer(
        Simulation sim,
        int[] prevFired,
        List<Spike1Projectile> projectiles,
        ref long nextProjectileId,
        ref long projectileHits)
    {
        IReadOnlyList<SimTower> towers = sim.Towers;
        for (int i = 0; i < towers.Count; i++)
        {
            int fired = towers[i].Weapon.FiredCount;
            int delta = fired - prevFired[i];
            prevFired[i] = fired;
            for (int s = 0; s < delta; s++)
            {
                Unit? target = NearestAliveUnit(sim.Units, towers[i].Cell);
                if (target is not null)
                {
                    projectiles.Add(new Spike1Projectile(
                        new EntityId(nextProjectileId++), towers[i].Id, towers[i].Cell, target,
                        sim.CurrentTick, Spike1PerfScenario.ProjectileSpeedPxPerTick,
                        Spike1PerfScenario.ProjectileHitRadiusPx));
                }
            }
        }

        float mapPx = Spike1PerfScenario.Size * Spike1PerfScenario.CellSize;
        for (int p = projectiles.Count - 1; p >= 0; p--)
        {
            Spike1Projectile projectile = projectiles[p];
            projectile.Advance();
            bool cull = projectile.Hit
                || projectile.AgeTicks > Spike1PerfScenario.ProjectileLifetimeTicks
                || projectile.X < 0f || projectile.Y < 0f || projectile.X > mapPx || projectile.Y > mapPx;
            if (!cull)
            {
                continue;
            }

            if (projectile.Hit)
            {
                projectileHits++;
            }

            int last = projectiles.Count - 1;
            projectiles[p] = projectiles[last];
            projectiles.RemoveAt(last);
        }
    }

    /// <summary>Nearest living unit to a cell centre, unbounded range (spawn-time targeting of the shadow layer).</summary>
    private static Unit? NearestAliveUnit(IReadOnlyList<Unit> units, CellCoord cell)
    {
        float centreX = (cell.X * Spike1PerfScenario.CellSize) + (Spike1PerfScenario.CellSize / 2f);
        float centreY = (cell.Y * Spike1PerfScenario.CellSize) + (Spike1PerfScenario.CellSize / 2f);
        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            float dx = unit.X - centreX;
            float dy = unit.Y - centreY;
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = unit;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Replication of <c>Simulation.FindTarget</c> for the combat attribution sample (read-only).</summary>
    private static Unit? NearestInWeaponRange(IReadOnlyList<Unit> units, SimTower tower)
    {
        double rangePx = tower.Spec.RangeCells * Simulation.CellSizePx;
        float rangeSquared = (float)(rangePx * rangePx);
        float centreX = (tower.Cell.X * Simulation.CellSizePx) + (Simulation.CellSizePx / 2f);
        float centreY = (tower.Cell.Y * Simulation.CellSizePx) + (Simulation.CellSizePx / 2f);
        Unit? best = null;
        float bestDistance = rangeSquared;
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            float dx = unit.X - centreX;
            float dy = unit.Y - centreY;
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = unit;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int CountAlive(Simulation sim)
    {
        int alive = 0;
        foreach (Unit unit in sim.Units)
        {
            if (unit.Alive)
            {
                alive++;
            }
        }

        return alive;
    }

    // ------------------------------------------------------------ 统计辅助

    private static double Mean(double[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (double sample in samples)
        {
            sum += sample;
        }

        return sum / samples.Length;
    }

    private static double Max(double[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double max = samples[0];
        foreach (double sample in samples)
        {
            if (sample > max)
            {
                max = sample;
            }
        }

        return max;
    }

    /// <summary>Nearest-rank percentile (sorted ascending, 1-based rank ⌈q·n⌉).</summary>
    private static double Percentile(double[] samples, double q)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        int rank = Math.Min(sorted.Length, Math.Max(1, (int)Math.Ceiling(q * sorted.Length)));
        return sorted[rank - 1];
    }

    /// <summary>Middle value of three samples (T-037 稳态化先例).</summary>
    private static double MedianOf(double a, double b, double c) =>
        Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
