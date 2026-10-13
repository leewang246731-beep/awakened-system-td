using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001] 金样回归（T-040）：固定 seed + 固定命令流跑 1800 tick，每
// 300 tick 的 StateHash 由真实引擎产出后落盘 game/content/golden/
// sim-e1-golden.json（CS-4.4：金样必须由数据 JSON 驱动引擎产出）；漂移即
// exit 1。行内容禁手改（CS-6.1），唯一再生成入口=环境开关（regen_command
// 已留档在金样文件头）。[C-12][F-001]
public class SimGoldenTests
{
    private const long Ticks = 1_800; // 60 s @ 30 Hz
    private const int SampleEvery = 300; // 7 个采样：0,300,…,1800

    private static SimGoldenFile BuildGolden() => new(
        Schema: SimGolden.Schema,
        Task: "T-040",
        Seed: SimE1Fixture.Seed,
        Ticks: Ticks,
        SampleEvery: SampleEvery,
        RegenCommand: "ERA_SIM_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Sim",
        CheckCommand: "bash tools/ci/run_all.sh",
        Notes: "E1 里程碑金样：固定 seed+固定命令流（5 动词 10 命令）×1800 tick 的整体 StateHash，"
            + "由 era1 内容 JSON 驱动引擎产出；重生成前须核对 08-task-plan T-040 验收（CS-6.1/CS-6.3）",
        Commands: SimE1Fixture.MilestoneCommands()
            .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson)).ToArray(),
        Samples: Array.Empty<SimHashSample>());

    private static IReadOnlyList<SimHashSample> RunGoldenEngine(SimGoldenFile golden) =>
        SimulationReplay.Capture(
            SimE1Fixture.Config(), new SimRng(golden.Seed), golden.Seed,
            SimulationReplay.ToCommands(golden.Commands), golden.Ticks, golden.SampleEvery).Samples;

    [Fact]
    public void Golden_state_hash_regression()
    {
        string path = SimGolden.GoldenPath();
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable(SimGolden.RegenEnvironmentVariable) == "1")
            {
                WriteGolden(path);
                return;
            }

            Assert.Fail($"金样缺失：{path}（首次生成：ERA_SIM_GOLDEN_UPDATE=1 dotnet test --filter SimGoldenTests）");
        }

        SimGoldenFile golden = SimGolden.Deserialize(File.ReadAllText(path));

        if (Environment.GetEnvironmentVariable(SimGolden.RegenEnvironmentVariable) == "1")
        {
            WriteGolden(path);
            return;
        }

        // 头部不变量：schema/task/采样节奏（被篡改的头=金样纪律违规，直接红）
        Assert.Equal(SimGolden.Schema, golden.Schema);
        Assert.Equal("T-040", golden.Task);
        Assert.Equal(Ticks, golden.Ticks);
        Assert.Equal(SampleEvery, golden.SampleEvery);
        Assert.Equal(10, golden.Commands.Count);

        // 引擎重算 vs 落盘采样：任一采样漂移 → 首分叉 tick 报告（exit 1）
        IReadOnlyList<SimHashSample> recomputed = RunGoldenEngine(golden);
        Assert.Equal(golden.Samples.Count, recomputed.Count);
        for (int i = 0; i < golden.Samples.Count; i++)
        {
            Assert.True(
                golden.Samples[i].Tick == recomputed[i].Tick && golden.Samples[i].StateHash == recomputed[i].StateHash,
                $"金样漂移：采样 #{i}（tick {golden.Samples[i].Tick}）期望 {golden.Samples[i].StateHash}，"
                + $"引擎重算 {recomputed[i].StateHash}——先排查内容/引擎变更依据，再走 CS-6 再生成流程");
        }

        Assert.Equal(7, golden.Samples.Count); // 0,300,600,900,1200,1500,1800
        Assert.Equal(0, golden.Samples[0].Tick);
        Assert.Equal(1_800, golden.Samples[^1].Tick);
    }

    private static void WriteGolden(string path)
    {
        SimGoldenFile golden = BuildGolden() with { Samples = RunGoldenEngine(BuildGolden()) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SimGolden.Serialize(golden) + Environment.NewLine);

        // 回读自检：落盘文件必须能被引擎逐采样复现（生成即验证）
        SimGoldenFile reread = SimGolden.Deserialize(File.ReadAllText(path));
        IReadOnlyList<SimHashSample> recomputed = RunGoldenEngine(reread);
        Assert.Equal(reread.Samples.Count, recomputed.Count);
        for (int i = 0; i < reread.Samples.Count; i++)
        {
            Assert.Equal(reread.Samples[i].StateHash, recomputed[i].StateHash);
        }
    }
}
