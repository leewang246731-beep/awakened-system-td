using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001][F-155] E2 金样回归（T-067）：固定 seed + 固定命令流（10 动词
// 25 命令——七座劳工营/双采集线/E2 弹药链×2/箭楼+火油塔/六档物流全家/升级/
// 英雄令×2/确认出售）×1800 tick 的整体 StateHash，由 era2 内容 JSON 驱动引擎
// 产出后落盘 game/content/golden/sim-e2-golden.json（CS-4.4）；漂移即 exit 1。
// 行内容禁手改（CS-6.1），唯一再生成入口=环境开关（regen_command 已留档在
// 金样文件头）。首版随引擎同 commit 合入（CS-6.2 v1.1 首版条款）。
public class SimE2GoldenTests
{
    private const long Ticks = 1_800; // 60 s @ 30 Hz
    private const int SampleEvery = 300; // 7 个采样：0,300,…,1800
    private const int CommandCount = 28;

    private static SimGoldenFile BuildGolden() => new(
        Schema: SimGolden.Schema,
        Task: "T-067",
        Seed: SimE2Fixture.Seed,
        Ticks: Ticks,
        SampleEvery: SampleEvery,
        RegenCommand: "ERA_SIM_E2_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Sim",
        CheckCommand: "bash tools/ci/run_all.sh",
        Notes: "E2 全子系统金样：固定 seed+固定命令流（10 动词 28 命令：能量骨干/三采集线/E2 弹药"
            + "链×3（箭矢·石矢·火油）/箭楼→弩塔升级+火油溅射燃烧/六档物流（民夫·马车·矿轨·传送带·无人机·"
            + "传送阵能量钩）/执戈者英雄令×2/确认出售残骸）×1800 tick 的整体 StateHash，由 era2 内容 JSON 驱动引擎"
            + "产出；护盾经 ShieldStartWave=1 夹具口径激活（设计归属 E3/T-114）。重生成前须核对"
            + " 08-task-plan T-067 验收（CS-6.1/CS-6.3）",
        Commands: SimE2Fixture.MilestoneCommands()
            .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson)).ToArray(),
        Samples: Array.Empty<SimHashSample>());

    private static IReadOnlyList<SimHashSample> RunGoldenEngine(SimGoldenFile golden) =>
        SimulationReplay.Capture(
            SimE2Fixture.Config(), new SimRng(golden.Seed), golden.Seed,
            SimulationReplay.ToCommands(golden.Commands), golden.Ticks, golden.SampleEvery).Samples;

    [Fact]
    public void Golden_state_hash_regression()
    {
        // [C-12][F-001][F-155] E2 金样回归主断言
        string path = SimE2Golden.GoldenPath();
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable(SimE2Golden.RegenEnvironmentVariable) == "1")
            {
                WriteGolden(path);
                return;
            }

            Assert.Fail($"金样缺失：{path}（首次生成：ERA_SIM_E2_GOLDEN_UPDATE=1 dotnet test --filter SimE2GoldenTests）");
        }

        SimGoldenFile golden = SimE2Golden.Deserialize(File.ReadAllText(path));

        if (Environment.GetEnvironmentVariable(SimE2Golden.RegenEnvironmentVariable) == "1")
        {
            WriteGolden(path);
            return;
        }

        // 头部不变量：schema/task/采样节奏（被篡改的头=金样纪律违规，直接红）
        Assert.Equal(SimGolden.Schema, golden.Schema);
        Assert.Equal("T-067", golden.Task);
        Assert.Equal(Ticks, golden.Ticks);
        Assert.Equal(SampleEvery, golden.SampleEvery);
        Assert.Equal(CommandCount, golden.Commands.Count);

        // 引擎重算 vs 落盘采样：任一采样漂移 → 首分叉 tick 报告（exit 1）
        IReadOnlyList<SimHashSample> recomputed = RunGoldenEngine(golden);
        Assert.Equal(golden.Samples.Count, recomputed.Count);
        for (int i = 0; i < golden.Samples.Count; i++)
        {
            Assert.True(
                golden.Samples[i].Tick == recomputed[i].Tick && golden.Samples[i].StateHash == recomputed[i].StateHash,
                $"E2 金样漂移：采样 #{i}（tick {golden.Samples[i].Tick}）期望 {golden.Samples[i].StateHash}，"
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
        File.WriteAllText(path, SimE2Golden.Serialize(golden) + Environment.NewLine);

        // 回读自检：落盘文件必须能被引擎逐采样复现（生成即验证）
        SimGoldenFile reread = SimE2Golden.Deserialize(File.ReadAllText(path));
        IReadOnlyList<SimHashSample> recomputed = RunGoldenEngine(reread);
        Assert.Equal(reread.Samples.Count, recomputed.Count);
        for (int i = 0; i < reread.Samples.Count; i++)
        {
            Assert.Equal(reread.Samples[i].StateHash, recomputed[i].StateHash);
        }
    }
}
