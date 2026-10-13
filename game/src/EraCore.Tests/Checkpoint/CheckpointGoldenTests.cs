using System.Text.Json;
using EraCore.Checkpoint;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-001] 检查点金样小回归（T-057）：恢复重放哈希时间线由真实引擎产出
// 落盘 game/content/golden/checkpoint-e1-golden.json；漂移即 exit 1，行内容禁手
// 改（CS-6.1）。首版随 T-057 引擎同 commit（CS-6.2 首版条款，REV+T8 豁免）。
// [C-04][F-001] 重负载串行集合（见 CheckpointTestSupport）
[Collection(CheckpointHeavyCollection.Name)]
public class CheckpointGoldenTests
{
    private const int CheckpointWave = 16;  // t=1.2 ⇒ 胜波 22 ⇒ ⌊16.5⌋
    private const int HorizonWave = 17;     // 检查点后 1 波的比较地平线
    private const int SampleEvery = 2_160;  // 采样步长=波间隔 72 s @ 30 Hz

    private const string Schema = "urn:eratd:golden:checkpoint:1";
    private const string RegenEnvironmentVariable = "ERA_CKPT_GOLDEN_UPDATE";

    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "golden");
            if (Directory.Exists(probe))
            {
                return Path.Combine(probe, "checkpoint-e1-golden.json");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/golden（测试须在仓库内运行）");
    }

    /// <summary>金样场景头（载荷由引擎产出，CS-6.1）。</summary>
    private static SimGoldenFile BuildGolden() => new(
        Schema: Schema,
        Task: "T-057",
        Seed: SimE1Fixture.Seed,
        Ticks: CheckpointFixture.BoundaryTickOf(HorizonWave),
        SampleEvery: SampleEvery,
        RegenCommand: "ERA_CKPT_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Checkpoint",
        CheckCommand: "bash tools/ci/run_all.sh",
        Notes: "检查点回放金样：E1 夹具（t=1.2，检查点波 16）跑至波 17 边界，从 #ckpt"
            + " 槽确定性重放（seed+命令流→检查点 tick）后续跑 1 波的逐采样 StateHash；"
            + "采样值与'从未中断连续跑'逐 tick 相等由 CheckpointGoldenTests 每次运行复证；"
            + "重生成前须核对 08-task-plan T-057 验收（CS-6.1/CS-6.3）",
        Commands: SimE1Fixture.MilestoneCommands()
            .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson)).ToArray(),
        Samples: Array.Empty<SimHashSample>());

    /// <summary>
    /// 引擎侧：检查点恢复重放世界的采样时间线。存档走真实 SQLite 端口（系统
    /// 临时目录 GUID 沙箱，finally 清理——CS-3.8；库内测试夹具不落仓库）。
    /// 同时复证"恢复=从未中断连续跑"的逐采样等价（金样与等价双保险）。
    /// </summary>
    private static IReadOnlyList<SimHashSample> RunCheckpointRestoreEngine(SimGoldenFile golden)
    {
        string root = Path.Combine(Path.GetTempPath(), "era-ckpt-golden", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new EraCore.IO.DevFileStore(root);
            var port = new EraCore.Persistence.SqliteSavePort(files);
            var store = new WaveCheckpointStore(port, "level.e1.m01", CheckpointFixture.Threat);

            // 连续跑世界 A：到波 17 边界（跨过检查点波 16）并采样
            var worldA = new Simulation(SimE1Fixture.Config(), new SimRng(golden.Seed), golden.Seed);
            worldA.EnqueueRange(SimulationReplay.ToCommands(golden.Commands));
            var reference = SampleRun(worldA, golden.Ticks, golden.SampleEvery);

            // 中断世界 B：同一运行，波界经 store 落档
            var worldB = new Simulation(SimE1Fixture.Config(), new SimRng(golden.Seed), golden.Seed);
            IReadOnlyList<Contracts.Sim.SimCommand> commands = SimulationReplay.ToCommands(golden.Commands);
            worldB.EnqueueRange(commands);
            while (worldB.CurrentTick < golden.Ticks)
            {
                worldB.Step();
                _ = store.ObserveBoundary(worldB, commands);
            }

            // 恢复世界 C：从检查点记录重建（重放前缀+续跑同一命令流，从 tick 0
            // 采样——前缀重放本身也在金样覆盖内）
            Assert.True(store.TryRestoreCheckpoint(out RestoredWaveSnapshot checkpoint));
            Assert.Equal(CheckpointWave, checkpoint.Wave);
            Assert.Equal(CheckpointFixture.BoundaryTickOf(CheckpointWave), checkpoint.Tick);
            var worldC = new Simulation(SimE1Fixture.Config(), new SimRng(golden.Seed), checkpoint.Seed);
            worldC.EnqueueRange(SimulationReplay.ToCommands(checkpoint.Commands));
            foreach (Contracts.Sim.SimCommand command in commands)
            {
                if (command.IssuedAtTick > checkpoint.Tick)
                {
                    worldC.Enqueue(command);
                }
            }

            IReadOnlyList<SimHashSample> restored = SampleRun(worldC, golden.Ticks, golden.SampleEvery);

            // 等价复证：恢复世界与从未中断世界逐采样相等（每次运行都成立）
            Assert.Equal(reference.Count, restored.Count);
            for (int i = 0; i < reference.Count; i++)
            {
                Assert.True(reference[i].Tick == restored[i].Tick && reference[i].StateHash == restored[i].StateHash,
                    $"恢复等价破坏：采样 #{i}（tick {reference[i].Tick}）连续跑 {reference[i].StateHash} ≠ 恢复 {restored[i].StateHash}");
            }

            return restored;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>对已构造的运行逐采样（tick 0 起步，步长 stride，至 ticks）。</summary>
    private static IReadOnlyList<SimHashSample> SampleRun(Simulation simulation, long ticks, int stride)
    {
        var samples = new List<SimHashSample> { new(0, simulation.ComputeStateHash()) };
        for (long tick = 1; tick <= ticks; tick++)
        {
            simulation.Step();
            if (tick % stride == 0)
            {
                samples.Add(new SimHashSample(tick, simulation.ComputeStateHash()));
            }
        }

        return samples;
    }

    [Fact]
    public void Checkpoint_restore_hash_regression()
    {
        string path = GoldenPath();
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable(RegenEnvironmentVariable) == "1")
            {
                WriteGolden(path);
                return;
            }

            Assert.Fail($"金样缺失：{path}（首次生成：ERA_CKPT_GOLDEN_UPDATE=1 dotnet test --filter CheckpointGoldenTests）");
        }

        SimGoldenFile golden = DeserializeGolden(path);
        if (Environment.GetEnvironmentVariable(RegenEnvironmentVariable) == "1")
        {
            WriteGolden(path);
            return;
        }

        // 头部不变量：schema/task/采样节奏（被篡改的头=金样纪律违规，直接红）
        Assert.Equal(Schema, golden.Schema);
        Assert.Equal("T-057", golden.Task);
        Assert.Equal(CheckpointFixture.BoundaryTickOf(HorizonWave), golden.Ticks);
        Assert.Equal(SampleEvery, golden.SampleEvery);
        Assert.Equal(10, golden.Commands.Count);

        // 引擎重算 vs 落盘采样：任一采样漂移 → 首分叉报告（exit 1）
        IReadOnlyList<SimHashSample> recomputed = RunCheckpointRestoreEngine(golden);
        Assert.Equal(golden.Samples.Count, recomputed.Count);
        for (int i = 0; i < golden.Samples.Count; i++)
        {
            Assert.True(
                golden.Samples[i].Tick == recomputed[i].Tick && golden.Samples[i].StateHash == recomputed[i].StateHash,
                $"金样漂移：采样 #{i}（tick {golden.Samples[i].Tick}）期望 {golden.Samples[i].StateHash}，"
                + $"引擎重算 {recomputed[i].StateHash}——先排查内容/引擎变更依据，再走 CS-6 再生成流程");
        }

        Assert.Equal((golden.Ticks / SampleEvery) + 1, golden.Samples.Count); // 0, 2160, …, 39960
    }

    private static void WriteGolden(string path)
    {
        SimGoldenFile golden = BuildGolden() with { Samples = RunCheckpointRestoreEngine(BuildGolden()) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(golden, SimReplayJsonContext.Default.SimGoldenFile) + Environment.NewLine);

        // 回读自检：落盘文件必须能被引擎逐采样复现（生成即验证）
        SimGoldenFile reread = DeserializeGolden(path);
        IReadOnlyList<SimHashSample> recomputed = RunCheckpointRestoreEngine(reread);
        Assert.Equal(reread.Samples.Count, recomputed.Count);
        for (int i = 0; i < reread.Samples.Count; i++)
        {
            Assert.Equal(reread.Samples[i].StateHash, recomputed[i].StateHash);
        }
    }

    private static SimGoldenFile DeserializeGolden(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SimReplayJsonContext.Default.SimGoldenFile)
            ?? throw new JsonException("Golden file deserialized to null.");
}
