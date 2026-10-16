using EraCore.CampaignLoop;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055] T-116 E3 全关可玩验收（headless E2E，T-084/T-085 e2e 先例）：
// 每条 e3.json 关行以 T-085 命令流（纯玩家建造）贯穿生产 LevelSession 跑到
// Victory——断言面：①逐关通关（胜波=数据行 win_waves §7 双面）；②命令流全量
// 签发且零拒收（经济面全合法——L3-2 富煤矿岩格图的命令流可建性引擎面证明）；
// ③终局塔数=脚本建造数（零出售）；④理论节奏估算表（中位∈[15,40] 分钟带）
// 与 headless 通关时刻并列输出（时长估算表；估算器不含弹链供给节流，实测
// 时刻=节流口径的诚实读数）；⑤同 seed 双跑状态哈希全等（含岩格图的确定性）。
// E3 装配扩展（T-113）：era3/era.json energy.risk → EnergyRiskTuning 挂载
//（过热通道活性；本批四关劳工营满供 → 负载率<100% 不触发——T-113 Counterplay
// 先例的教学位对策面）。
public class Era3LevelPlaythroughTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era3LevelPlaythroughTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(int slot) =>
        HandcraftedLevelData.Load("e3.json").Items.Single(level => level.Slot == slot);

    /// <summary>
    /// E3 装配：T-085 BuildConfig（towers/enemies/仓库等级 3/钢货币全出自 era3
    /// 内容表）+ era.json energy.risk 过热表挂载（T-113 E3+ 关卡口径；纯内容
    /// 驱动 CS-2.3）。E3 无英雄行（era3/levels.json _source：SP-6 英雄行随
    /// 章节设计定案落位，era3 无 heroes.json）。
    /// </summary>
    internal static SimulationConfig BuildE3Config(HandcraftedLevel level) =>
        BuildConfig(level, "era3") with { EnergyRisk = Era3EnergyRisk() };

    internal static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 4) * intervalTicks); // 胜波+4 波窗（漏敌滞留核心位清场余量）
    }

    private sealed record PlaythroughLine(
        int Slot,
        string NameZh,
        int WinWaves,
        double Threat,
        double EstimateMinutes,
        long VictoryTick,
        double VictorySeconds,
        int Towers,
        int Issued,
        int Applied,
        long StateHash);

    private PlaythroughLine RunOnce(HandcraftedLevel level, ISavePort port)
    {
        SimulationConfig config = BuildE3Config(level);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, "era3");
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t116.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            port);

        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < HorizonTicks(level))
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.Equal(GameFlowState.Victory, session.Machine.State);
        return new PlaythroughLine(
            level.Slot,
            level.NameZh,
            session.WinWaves,
            level.Threat,
            LevelPacingEstimator.Estimate(level, Towers("era3"), Enemies("era3")).DurationMinutes,
            session.Sim.CurrentTick,
            session.Sim.CurrentTick / (double)Simulation.TicksPerSecond,
            session.Sim.Towers.Count,
            session.IssuedCommands.Count,
            (int)session.Sim.AppliedCommandCount,
            session.Sim.ComputeStateHash());
    }

    // ---- ①②③ 逐关命令流通关断言 -------------------------------------------

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 4).Select(slot => new object[] { slot });

    [Theory]
    [MemberData(nameof(Slots))]
    public void Every_E3_level_is_playable_to_victory_with_its_command_stream(int slot)
    {
        // legal — [F-055] headless 全通关：胜波=数据行 win_waves（§7 双面）、
        // 命令流全量签发且零拒收（含 L3-2 岩格图全部建造/档位/配方载荷的
        // 引擎面合法——assign-carrier 路由在真岩格图上可建可通）、终局塔数=
        // 脚本 build-tower 数、E3 无英雄。
        HandcraftedLevel level = Level(slot);
        PlaythroughLine line = RunOnce(level, CreatePort());

        Assert.Equal(level.WinWaves, line.WinWaves);
        Assert.Equal(EraCore.Balance.Campaign.WinWaves(level.Threat), line.WinWaves);
        Assert.Equal(level.CommandScript.Count, line.Issued);
        Assert.Equal(line.Issued, line.Applied); // 零拒收（AppliedCommandCount==签发数）
        Assert.Equal(level.CommandScript.Count(step => step.Verb == "build-tower"), line.Towers);
        _output.WriteLine(
            $"L3-{slot} {line.NameZh}: win={line.WinWaves} threat={level.Threat} " +
            $"estimate={line.EstimateMinutes:F2}min victory={line.VictorySeconds:F0}s " +
            $"towers={line.Towers} issued/applied={line.Issued}/{line.Applied}");
    }

    // ---- ④ 时长估算表（估算中位带 + headless 实测时刻并列） -----------------

    [Fact]
    public void Duration_table_median_estimate_is_inside_the_band_and_victories_are_recorded()
    {
        // [F-055] 时长估算表：理论节奏（估算器口径）中位 ∈[15,40] 分钟带且逐关
        // 落带；headless 通关时刻（弹链节流口径）并列输出——两口径同表即
        // _duration_calibration 的 RED-FLAG 诚实披露（实测口径归 T-095）。
        var lines = new List<PlaythroughLine>();
        foreach (int slot in Enumerable.Range(1, 4))
        {
            lines.Add(RunOnce(Level(slot), CreatePort()));
        }

        var sorted = lines.Select(line => line.EstimateMinutes).OrderBy(minutes => minutes).ToList();
        double median = (sorted[1] + sorted[2]) / 2.0;
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(lines, line => Assert.InRange(line.EstimateMinutes, 15.0, 40.0));

        _output.WriteLine("T-116 E3 时长表（estimate=理论节奏估算 / victory=headless 通关秒）:");
        foreach (PlaythroughLine line in lines.OrderBy(line => line.Slot))
        {
            _output.WriteLine(
                $"  L3-{line.Slot} {line.NameZh}: win={line.WinWaves} estimate={line.EstimateMinutes:F2}min " +
                $"victory={line.VictorySeconds:F0}s towers={line.Towers} commands={line.Issued}/{line.Applied}");
        }

        Assert.All(lines, line => Assert.True(line.VictorySeconds > 0));
    }

    // ---- ⑤ 确定性（含富煤矿岩格图） -----------------------------------------

    [Fact]
    public void Same_seed_double_run_of_every_level_hashes_identically()
    {
        // [F-055] 确定性③（引擎面）：同 seed+命令流双跑 → 终局状态哈希与通关
        // 时刻全等——含 L3-2 固定 seed 岩格图（煤位分布确定性=稀缺机制的可复
        // 现性证明，T-115 §四扫描同 seed 同图口径）。
        foreach (int slot in Enumerable.Range(1, 4))
        {
            HandcraftedLevel level = Level(slot);
            PlaythroughLine first = RunOnce(level, CreatePort());
            PlaythroughLine second = RunOnce(level, CreatePort());

            Assert.Equal(first.VictoryTick, second.VictoryTick);
            Assert.Equal(first.StateHash, second.StateHash);
            Assert.Equal(first.Towers, second.Towers);
        }
    }
}
