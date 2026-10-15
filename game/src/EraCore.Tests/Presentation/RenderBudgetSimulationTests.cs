using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraCore.Tests.Spikes;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Presentation;

// [C-21] [F-159] T-102 验收后半（集显稳定帧率）的 headless 替代口径：
// CI 无 GPU（CS-4.8/D-19 同源限制），帧率差用【帧预算仿真】证明——把
// spike① 场景（G8 门禁同款：300 敌+100 塔+迷宫，128×128 图）的真实负载数
// （每 tick 活敌位置→脏 chunk、真实塔射击→特效 spawn 请求）喂给
// RendererDecisionMap 的同一估算式，断言 L 档决策数/绘制调用估算显著低于
// M 档。真·集显帧率实测归 T-106 Web 实测口径（任务卡注记），此处如实声明
// headless 局限：估算模型≠GPU 墙钟。
//
// 串行集合：真实 Simulation 跑 2220 tick 属 CPU 重负载，与 spike 计时门禁
// 互斥运行（F1 纪律），本测试自身断言全为确定性计数、不读墙钟。
[Collection(PerfSerialCollection.Name)]
public sealed class RenderBudgetSimulationTests
{
    private const int WindowTicks = 240; // 8 s @ 30 Hz 稳态窗（spike① 稳态窗 1980 起的前 240 tick）

    private readonly ITestOutputHelper _output;

    public RenderBudgetSimulationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact] // [F-159] [C-21] spike1 场景三档帧预算仿真：L 决策数/绘制调用估算显著低于 M；H 同式打分且脏粒度更细省实例写入
    public void Spike1_scene_low_tier_frame_budget_is_significantly_below_medium()
    {
        // ---- 场景：与 G8 门禁同款的真实 spike① 编排（配置/迷宫/塔流一致）----
        var rng = new SimRng(Spike1PerfScenario.Seed);
        var sim = new Simulation(Spike1PerfScenario.BuildConfig(), rng, Spike1PerfScenario.Seed);
        Spike1PerfScenario.CarveMaze(sim.World);
        IReadOnlyList<CellCoord> towerCells = Spike1PerfScenario.TowerCells();
        var prevFired = new int[Spike1PerfScenario.TowerCount];
        for (int i = 0; i < Spike1PerfScenario.TowerCount; i++)
        {
            sim.Enqueue(SimCommands.BuildTower(
                Spike1PerfScenario.TowerPlacementStartTick
                    + (i * Spike1PerfScenario.TowerPlacementSpacingTicks),
                Spike1PerfScenario.TowerId, towerCells[i]));
        }

        // 脏 chunk 仿真：两种粒度各一份（桥接层同款规则——每个活敌标脏其所在
        // chunk；单 tick 内重写 = 脏 chunk 内的全部单位实例，MultiMesh 现行为）。
        var dirtyAt32 = new DirtyRegionSet(Spike1PerfScenario.Size, Spike1PerfScenario.Size, 32);
        var dirtyAt16 = new DirtyRegionSet(Spike1PerfScenario.Size, Spike1PerfScenario.Size, 16);

        long shotsTotal = 0;
        int aliveMin = int.MaxValue;
        double aliveSum = 0;
        int aliveSamples = 0;
        var tierTotals = new Dictionary<RenderTier, long[]>();
        foreach (RenderTier tier in RenderTierMatrix.AllTiers)
        {
            tierTotals[tier] = new long[3]; // [decisionCount, drawCalls, instanceWrites]
        }

        for (long t = 0; t < Spike1PerfScenario.WindowStartTick + WindowTicks; t++)
        {
            sim.Step();
            if (t < Spike1PerfScenario.WindowStartTick)
            {
                for (int i = 0; i < sim.Towers.Count && i < prevFired.Length; i++)
                {
                    prevFired[i] = sim.Towers[i].Weapon.FiredCount; // 预热期只推进基线
                }

                continue;
            }

            // ---- 本 tick 真实负载数 ------------------------------------------------
            int shots = 0;
            for (int i = 0; i < sim.Towers.Count && i < prevFired.Length; i++)
            {
                int fired = sim.Towers[i].Weapon.FiredCount;
                shots += fired - prevFired[i];
                prevFired[i] = fired;
            }

            shotsTotal += shots;

            (int Chunks32, int Writes32) = MarkAndDrain(dirtyAt32, sim.Units);
            (int Chunks16, int Writes16) = MarkAndDrain(dirtyAt16, sim.Units);

            int alive = CountAlive(sim.Units);
            aliveMin = Math.Min(aliveMin, alive);
            aliveSum += alive;
            aliveSamples++;

            // ---- 三档同式打分（RendererDecisionMap.EstimateFrameWork）------------
            foreach (RenderTier tier in RenderTierMatrix.AllTiers)
            {
                RenderTierCapabilities caps = RenderTierMatrix.Capabilities(tier);
                RenderFramePlan plan = RendererDecisionMap.PlanFrame(caps, shots);

                // 稳态帧：地面层烘焙完毕无重建（仍提交 1 draw）；单位层按该档粒度
                // 的真实脏 chunk 数/实例重写数。
                var ground = new RenderLayerWork(DirtyChunks: 0, InstanceWrites: 0, AnyVisibleInstances: true);
                var units = caps.DirtyChunkCells == 16
                    ? new RenderLayerWork(Chunks16, Writes16, alive > 0)
                    : new RenderLayerWork(Chunks32, Writes32, alive > 0);

                RenderWorkEstimate estimate = RendererDecisionMap.EstimateFrameWork(plan, ground, units);
                long[] totals = tierTotals[tier];
                totals[0] += estimate.DecisionCount;
                totals[1] += estimate.EstimatedDrawCalls;
                totals[2] += estimate.InstanceWrites;
            }
        }

        // ---- 起始帧（静态烘焙决策差）：M/H 全图烘焙 vs L 跳过只直绘视野 chunk ----
        // 视野=设计分辨率 640×360（C-05 32px/格 ⇒ 20×11.25 格），镜头居中。
        // 计入各档 totals 后，L 的起始帧成本即按需直绘的真实口径。
        const int mapCells = Spike1PerfScenario.Size * Spike1PerfScenario.Size; // 128×128=16384
        foreach (RenderTier tier in RenderTierMatrix.AllTiers)
        {
            RenderTierCapabilities caps = RenderTierMatrix.Capabilities(tier);
            RenderFramePlan plan = RendererDecisionMap.PlanFrame(caps, 0);

            RenderLayerWork ground;
            if (caps.StaticChunkBake)
            {
                int chunkEdge = caps.DirtyChunkCells;
                int chunksPerAxis = (Spike1PerfScenario.Size + chunkEdge - 1) / chunkEdge;
                ground = new RenderLayerWork(chunksPerAxis * chunksPerAxis, mapCells, true);
            }
            else
            {
                // L 按需直绘：只写镜头视野圈到的 chunk（CellsInView 口径 640×360）。
                IReadOnlyList<(int ChunkX, int ChunkY)> view = RendererDecisionMap.ChunksInView(
                    Spike1PerfScenario.Size, Spike1PerfScenario.Size, caps.DirtyChunkCells,
                    viewX0Cell: 54, viewY0Cell: 58, viewX1Cell: 74, viewY1Cell: 70); // 居中 20×12 格视野
                ground = new RenderLayerWork(view.Count, view.Count * caps.DirtyChunkCells * caps.DirtyChunkCells, true);
            }

            var units = new RenderLayerWork(0, 0, false); // 起始帧敌人未入场
            RenderWorkEstimate estimate = RendererDecisionMap.EstimateFrameWork(plan, ground, units);
            long[] totals = tierTotals[tier];
            totals[0] += estimate.DecisionCount;
            totals[1] += estimate.EstimatedDrawCalls;
            totals[2] += estimate.InstanceWrites;
        }

        long[] low = tierTotals[RenderTier.Low];
        long[] medium = tierTotals[RenderTier.Medium];
        long[] high = tierTotals[RenderTier.High];

        _output.WriteLine(
            $"spike1 帧预算仿真 {WindowTicks} 帧：活敌(最少/均)={aliveMin}/{aliveSum / Math.Max(1, aliveSamples):F0} " +
            $"特效请求总={shotsTotal}（均值 {shotsTotal / (double)WindowTicks:F1}/帧）");
        foreach (RenderTier tier in RenderTierMatrix.AllTiers)
        {
            long[] v = tierTotals[tier];
            _output.WriteLine(
                $"{RenderTierMatrix.TierId(tier)} 档：决策数={v[0]} 绘制调用估算={v[1]} 实例写入={v[2]} " +
                $"帧预算目标={RenderTierMatrix.Capabilities(tier).TargetFrameBudgetMs}ms");
        }

        // ---- 场景完整性（确定性，负载数据失真则仿真无意义）--------------------
        Assert.True(aliveMin >= 250,
            $"窗内活敌最少 {aliveMin} < 250（spike① 锚点 ≥300，留容差；压测基数失守）");
        Assert.True(shotsTotal >= 40L * WindowTicks,
            $"窗内真实射击 {shotsTotal} < {40L * WindowTicks}（spike① 锚点 ≥40/tick 持续供弹）");

        // ---- 三档差异（任务卡验收：L 档决策数/绘制调用估算显著低于 M 档）------
        Assert.True(low[1] * 10 <= medium[1] * 6,
            $"L 绘制调用估算 {low[1]} 未显著低于 M {medium[1]}（要求 ≤0.6×）");
        Assert.True(low[0] < medium[0],
            $"L 决策数 {low[0]} 应低于 M {medium[0]}（特效判定减半+两 pass 全免+烘焙跳过）");
        Assert.True(low[2] < medium[2],
            $"L 实例写入 {low[2]} 应低于 M {medium[2]}（起始静态烘焙跳过：视野直绘 vs 全图 16384 格）");

        // H：同式打分（pass/密度与 M 相同 ⇒ 绘制调用估算同 M），差异在更细脏
        // 粒度——脏 chunk 内重写的单位实例数不高于 M（16 格 chunk 邻域更小）。
        Assert.Equal(medium[1], high[1]);
        Assert.True(high[2] <= medium[2],
            $"H 实例写入 {high[2]} 应 ≤ M {medium[2]}（16 格更细脏粒度：同事件更小的重写单元）");
    }

    /// <summary>
    /// Bridge-parity dirty simulation for one tick: every alive unit marks its
    /// owning chunk, the set drains once (chunk-coalesced), and the instance
    /// rewrite count is every unit living inside a drained chunk (the
    /// MultiMesh unit layer rewrites all members of dirty chunks).
    /// </summary>
    private static (int DirtyChunks, int UnitInstanceWrites) MarkAndDrain(
        DirtyRegionSet set, IReadOnlyList<Unit> units)
    {
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            int cellX = Math.Clamp((int)(unit.X / Spike1PerfScenario.CellSize), 0, set.WidthCells - 1);
            int cellY = Math.Clamp((int)(unit.Y / Spike1PerfScenario.CellSize), 0, set.HeightCells - 1);
            set.MarkDirty(cellX, cellY);
        }

        IReadOnlyCollection<(int ChunkX, int ChunkY)> drained = set.TakeDirtyChunks();
        var dirty = new HashSet<(int, int)>(drained);
        int writes = 0;
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            int cellX = Math.Clamp((int)(unit.X / Spike1PerfScenario.CellSize), 0, set.WidthCells - 1);
            int cellY = Math.Clamp((int)(unit.Y / Spike1PerfScenario.CellSize), 0, set.HeightCells - 1);
            if (dirty.Contains((cellX / set.ChunkCells, cellY / set.ChunkCells)))
            {
                writes++;
            }
        }

        return (dirty.Count, writes);
    }

    private static int CountAlive(IReadOnlyList<Unit> units)
    {
        int alive = 0;
        foreach (Unit unit in units)
        {
            if (unit.Alive)
            {
                alive++;
            }
        }

        return alive;
    }
}
