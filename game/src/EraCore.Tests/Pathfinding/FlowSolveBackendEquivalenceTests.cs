using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// T-101 core regression (C-11, R-E3 / 07 D-31): the three flow-solve
/// backends — <see cref="FlowSolveBackend.FullBurst"/> (一次算完),
/// <see cref="FlowSolveBackend.PerTickCellQuota"/> (每 tick 配额切片) and
/// <see cref="FlowSolveBackend.PerFrameCellBudget"/> (Web 每帧格数预算,
/// F-164) — must produce GRID-EQUAL fields on the same input. The fixture
/// generates N=20 seeded noise mazes (128×128, 墙密度梯度 0.05~0.4, 血量墙
/// 20~500, 水障, 逐格地形代价 1~6): one cost-only anchor (guaranteed
/// <see cref="FlowFieldMode.Normal"/>), nine pure-noise mazes across the
/// density gradient, and ten blood-curtain mazes (full wall column, upper
/// half untearable water / lower half tearable blood — guaranteed
/// <see cref="FlowFieldMode.WallTear"/> with genuine tear chains). Per maze
/// the three backends are asserted equal on: mode, hash, charged work,
/// EVERY cell's Distance / NextStep / IsTearCell, and the BuildPath chains
/// from fixed probe cells. Mathematical basis: see the proof sketch on
/// <see cref="FlowSolveBackendFactory"/> (Dijkstra 确定性 + 配额只切调度).
/// </summary>
// [C-11] [F-159] [F-164] T-101 分帧切片后端三后端逐格等价回归（20 张噪声迷宫）
public class FlowSolveBackendEquivalenceTests
{
    private const int Size = 128;
    private const int NoiseMazeCount = 10;   // 1 cost anchor + 9 gradient
    private const int CurtainMazeCount = 10; // guaranteed WallTear
    private const int TotalMazes = NoiseMazeCount + CurtainMazeCount;

    // 切片配额（刻意取互异的小奇数：远小于总工作量 → 多帧必然；奇数压行内续位路径）
    private const int TickQuota = 97;
    private const int FrameQuota = 313;

    private readonly ITestOutputHelper _output;

    public FlowSolveBackendEquivalenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------ core acceptance

    [Fact]
    public void TwentyNoiseMazesGridEqualAcrossAllThreeBackends()
    {
        // [F-159] [F-164] T-101 验收（08 任务卡）：N 张噪声迷宫 burst==sliced
        // 逐格哈希相等——本测试把 full-burst / per-tick-quota / per-frame-budget
        // 三后端对同一输入的等价性钉成回归资产：逐格距离全等 + NextStep 逐格
        // 全等 + 链一致 + 哈希相等。WallTear 模式同样三后端等效（帘幕组保证覆盖；
        // 纯噪声组在高密度下也自然落入 WallTear——两类一并被等价断言覆盖）。
        int normalCount = 0, tearCount = 0, curtainWallTearCount = 0;

        for (int i = 0; i < TotalMazes; i++)
        {
            MapGrid maze = Maze(i);
            var target = new CellCoord(Size - 1, Size - 1);
            var spawn = new CellCoord(0, 0);

            var burst = FlowSolveBackendFactory.Solve(maze, target, FlowSolveBackend.FullBurst);
            var (tick, tickSlices) = SolveCounted(maze, target, FlowSolveBackend.PerTickCellQuota, TickQuota);
            var (frame, frameSlices) = SolveCounted(maze, target, FlowSolveBackend.PerFrameCellBudget, FrameQuota);

            // 切片真实发生：两个切片后端都必须跨多片完成（单片完成=等价性未被真正考验）。
            Assert.True(tickSlices >= 2, $"maze {i}: per-tick slicing must span multiple slices, got {tickSlices}");
            Assert.True(frameSlices >= 2, $"maze {i}: per-frame slicing must span multiple slices, got {frameSlices}");

            // 模式/哈希/工作量全等（工作量=输入的纯函数——调度方式不得改变它）。
            Assert.True(burst.IsReady && tick.IsReady && frame.IsReady);
            Assert.Equal(burst.Mode, tick.Mode);
            Assert.Equal(burst.Mode, frame.Mode);
            Assert.Equal(burst.Hash, tick.Hash);
            Assert.Equal(burst.Hash, frame.Hash);
            Assert.Equal(burst.ProcessedCells, tick.ProcessedCells);
            Assert.Equal(burst.ProcessedCells, frame.ProcessedCells);

            // 逐格全等：距离（含 Unreached 语义）、梯度下一步、拆墙格归属。
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var cell = new CellCoord(x, y);
                    long expected = burst.Distance(cell);
                    Assert.Equal(expected, tick.Distance(cell));
                    Assert.Equal(expected, frame.Distance(cell));

                    var step = burst.NextStep(cell);
                    Assert.Equal(step, tick.NextStep(cell));
                    Assert.Equal(step, frame.NextStep(cell));

                    bool tear = burst.IsTearCell(cell);
                    Assert.Equal(tear, tick.IsTearCell(cell));
                    Assert.Equal(tear, frame.IsTearCell(cell));
                }
            }

            // 链一致：固定探针点的梯度下降路径三后端逐节点相等。
            foreach (var probe in Probes())
            {
                var chain = burst.BuildPath(probe);
                Assert.Equal(chain, tick.BuildPath(probe));
                Assert.Equal(chain, frame.BuildPath(probe));
            }

            if (i == 0)
            {
                // 成本锚点图（全可走）必然 Normal——Normal 模式覆盖的确定性保证。
                Assert.Equal(FlowFieldMode.Normal, burst.Mode);
            }

            if (i >= NoiseMazeCount)
            {
                // 帘幕组：必然 WallTear，且拆墙链真实穿过血墙（F-044 语义被等价性覆盖）。
                Assert.Equal(FlowFieldMode.WallTear, burst.Mode);
                var chain = burst.BuildPath(spawn);
                Assert.Equal(target, chain[^1]);
                Assert.Contains(chain, c => burst.IsTearCell(c));
                curtainWallTearCount++;
            }

            if (burst.Mode == FlowFieldMode.Normal)
            {
                normalCount++;
            }
            else
            {
                tearCount++;
            }

            _output.WriteLine(
                $"maze {i:D2}: mode={burst.Mode} workUnits={burst.ProcessedCells} hash={burst.Hash} "
                + $"tick({TickQuota}/slice)={tickSlices} slices frame({FrameQuota}/slice)={frameSlices} slices"
                + (i >= NoiseMazeCount ? " [curtain]" : string.Empty));
        }

        // 模式覆盖：20 张里 Normal 与 WallTear 双双被等价断言真实覆盖
        // （帘幕组 10 张全部 WallTear 且拆墙链过血墙；纯噪声组高密度图也自然
        // 落入 WallTear——两类都在逐格等价断言之内）。
        Assert.Equal(TotalMazes, normalCount + tearCount); // fixture 自检：每张迷宫都归类
        Assert.True(normalCount >= 1, "the Normal mode must be genuinely exercised (cost anchor maze)");
        Assert.Equal(CurtainMazeCount, curtainWallTearCount);
        _output.WriteLine(
            $"coverage: {normalCount} Normal-mode + {tearCount} WallTear-mode mazes "
            + $"({curtainWallTearCount} curtain-guaranteed with genuine tear chains) — all grid-equal across three backends");
    }

    // ------------------------------------------------------------ frame-host load independence (F-164 Web story)

    [Fact]
    public void PerFrameBackendSliceCountIsIndependentOfFrameNoise()
    {
        // [F-164] Web 帧率不稳口径：per-frame-budget 的工作量预算使完成片数与
        // 帧间 CPU 噪声无关（墙钟预算在同样噪声下会缩水每片工作量、漂移完成
        // 时刻——这正是 Web 改用工作量预算的动因）。机制级：片间插入确定性
        // 计数噪声循环（消耗真实墙钟、时长随负载漂移），完成片数/哈希/工作量
        // 必须与安静对照全等。
        MapGrid maze = Maze(NoiseMazeCount + 5); // 中密度帘幕图：三相位（快照/步行/拆墙重泛洪）
        var target = new CellCoord(Size - 1, Size - 1);

        var (quiet, quietSlices) = SolveCounted(maze, target, FlowSolveBackend.PerFrameCellBudget, 256);

        var noisy = FlowField.StartBuild(maze, target);
        int noisySlices = 0;
        bool done = false;
        while (!done)
        {
            done = FlowSolveBackendFactory.Advance(noisy, FlowSolveBackend.PerFrameCellBudget, 256);
            noisySlices++;
            BurnCpu(200_000); // 帧间噪声（墙钟时长随机器/负载漂移）
        }

        Assert.True(quietSlices >= 2);
        Assert.Equal(quietSlices, noisySlices); // 完成片数与负载无关（核心断言）
        Assert.Equal(quiet.Hash, noisy.Hash);
        Assert.Equal(quiet.ProcessedCells, noisy.ProcessedCells);
        Assert.Equal(FlowFieldMode.WallTear, noisy.Mode);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Maze i of the N=20 fixture: 0 = cost-only anchor (all walkable,
    /// varied terrain costs — guaranteed Normal); 1..9 = pure noise across
    /// the wall-density gradient 0.05~0.40 (walls split into untearable
    /// water and tearable blood walls 20~500 HP, walkable cells carry
    /// terrain costs 1~6); 10..19 = the same noise (water-only) plus a full
    /// blood curtain — column x=64: upper half untearable water, lower half
    /// tearable blood 80~500 — guaranteeing WallTear with genuine tear
    /// chains. Seeds fixed (1001+i) so the fixture is reproducible.
    /// </summary>
    private static MapGrid Maze(int i)
    {
        uint seed = (uint)(1001 + i);
        if (i == 0)
        {
            return NoiseMaze(seed, density: 0.0, bloodShareOfWalls: 0.0, costOnly: true);
        }

        if (i < NoiseMazeCount)
        {
            double density = 0.05 + 0.35 * (i - 1) / (NoiseMazeCount - 2); // 0.05 .. 0.40
            return NoiseMaze(seed, density, bloodShareOfWalls: 0.45, costOnly: false);
        }

        double curtainDensity = 0.05 + 0.35 * (i - NoiseMazeCount) / (CurtainMazeCount - 1); // 0.05 .. 0.40
        // 噪声含血墙（tear 图=可走∪血墙，免得最大水密度把出生点可拆连通域切碎
        // ——帘幕图必须保证出生点到血帘幕的真实拆墙链）。
        MapGrid maze = NoiseMaze(seed, curtainDensity, bloodShareOfWalls: 0.45, costOnly: false);
        int x = Size / 2;
        for (int y = 0; y < Size; y++)
        {
            if (y < Size / 2)
            {
                maze.Block(x, y); // 上半：水障——不可拆，封死步行通路
            }
            else
            {
                // 下半：血墙（可拆），血量 80~500 沿列梯度。
                maze.Block(x, y).Blood(x, y, 80 + (y * 37) % 421);
            }
        }

        return maze;
    }

    /// <summary>
    /// Seeded noise maze: walls at <paramref name="density"/> (split into
    /// water = untearable and blood = tearable by
    /// <paramref name="bloodShareOfWalls"/>), walkable cells carrying
    /// terrain costs 1~6. A 5×5 walkable clearing around spawn (0,0) and
    /// target (Size-1,Size-1) keeps both endpoints connected to their side
    /// of the map (only seed-fixed counterfactual enclosures beyond 2 cells
    /// could seal them — none exist for the pinned seeds; the curtain tests
    /// rely on this to guarantee a genuine tear chain).
    /// <paramref name="costOnly"/> builds the all-walkable anchor.
    /// </summary>
    private static MapGrid NoiseMaze(uint seed, double density, double bloodShareOfWalls, bool costOnly)
    {
        var grid = new MapGrid(Size, Size);
        var rng = new Lcg(seed);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (costOnly || IsEndpointClearing(x, y) || !rng.Chance(density))
                {
                    grid.SetCost(x, y, 1 + rng.NextInt(6)); // 地形代价 1~6
                    continue;
                }

                if (rng.Chance(bloodShareOfWalls))
                {
                    grid.Block(x, y).Blood(x, y, 20 + rng.NextInt(481)); // 血量墙 20~500
                }
                else
                {
                    grid.Block(x, y); // 水障：不可拆
                }
            }
        }

        return grid;
    }

    /// <summary>Within 2 cells of spawn (0,0) or target (Size-1,Size-1).</summary>
    private static bool IsEndpointClearing(int x, int y) =>
        (x < 5 && y < 5) || (x >= Size - 5 && y >= Size - 5);

    /// <summary>Drives one backend to completion, counting the host slices it took.</summary>
    private static (FlowField Field, int Slices) SolveCounted(
        MapGrid maze, CellCoord target, FlowSolveBackend backend, int quota)
    {
        var field = FlowField.StartBuild(maze, target);
        int slices = 0;
        bool done = false;
        while (!done)
        {
            done = FlowSolveBackendFactory.Advance(field, backend, quota);
            slices++;
            Assert.True(slices < 2_000_000, "sliced solve must terminate");
        }

        return (field, slices);
    }

    private static CellCoord[] Probes() => new[]
    {
        new CellCoord(0, 0),
        new CellCoord(Size - 1, 0),
        new CellCoord(0, Size - 1),
        new CellCoord(Size / 2, Size / 2),
    };

    /// <summary>
    /// 计算噪声（T-086 机制同型）：固定迭代次数的忙循环——消耗真实 CPU 墙钟
    /// （时长随机器与负载漂移，正是要注入的帧率不稳形态），结果丢弃。
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
}
