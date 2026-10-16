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

// [C-19][F-055] T-124 E4 全关可玩验收（headless E2E，T-084/T-085/T-116 e2e
// 先例）：每条 e4.json 关行以 T-085 命令流（纯玩家建造——含 e4-final 的
// upgrade-tower 激光塔升级步）贯穿生产 LevelSession 跑到 Victory——断言面：
// ①逐关通关（胜波=数据行 win_waves §7 双面）；②命令流全量签发且零拒收（经济
// 面全合法——混凝土货币+三级炼链+升级差价的引擎面证明）；③终局塔数=脚本建造
// 数（零出售）；④理论节奏估算表（中位∈[15,40] 分钟带）与 headless 通关时刻
// 并列输出（时长估算表；估算器不含弹链供给节流，实测时刻=节流口径的诚实读数）；
// ⑤同 seed 双跑状态哈希全等；⑥终局关 Boss 手算锚（夜枭出场
// HP=20000×威胁 HP 乘子×Boss 状态 ×1.5=35000、出场刻不早于第 19 波开场）。
// E4 装配扩展（T-124）：era4/era.json energy.risk+meltdown → EnergyRiskTuning
// 全挂载+辐射区 tick 衰减（T-123 EnergyRiskWorldConsumer 参数位；本批五关
// 劳工营满供 → 负载率<100% 不触发——T-113 Counterplay 先例的教学位对策面）。
public class Era4LevelPlaythroughTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era4LevelPlaythroughTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(int slot) =>
        HandcraftedLevelData.Load("e4.json").Items.Single(level => level.Slot == slot);

    /// <summary>
    /// E4 装配：T-085 BuildConfig（towers/enemies/仓库等级 4/混凝土货币全出自
    /// era4 内容表）+ era.json energy.risk+meltdown 双表挂载（T-124 E4+ 关卡
    /// 口径；纯内容驱动 CS-2.3）+ 辐射区 tick 衰减（T-123 [NEW] 行）。E4 无
    /// 英雄行（era4/levels.json _source：SP-6 英雄行随章节设计定案落位，era4
    /// 无 heroes.json）。
    /// </summary>
    internal static SimulationConfig BuildE4Config(HandcraftedLevel level) =>
        BuildConfig(level, "era4") with
        {
            EnergyRisk = Era4EnergyRisk(),
            RadiationZoneHpDecayPerSecond = Era4RadiationZoneHpDecayPerSecond(),
        };

    internal static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 12) * intervalTicks); // 胜波+12 波窗（终局关 Boss 波+漏敌滞留清场余量，FinalLevelTransitionReachability 加宽版）
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
        int? BossMaxHp,
        long StateHash);

    private PlaythroughLine RunOnce(HandcraftedLevel level, ISavePort port)
    {
        SimulationConfig config = BuildE4Config(level);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, "era4");
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t124.{level.Id}", level.Threat, level.Map.Seed),
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

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"未通关（state={session.Machine.State}）: id={level.Id} wave={session.Sim.CurrentWave} " +
            $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
            $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
            $"alive={session.Sim.Units.Count(u => u.Alive)} aliveHp={session.Sim.Units.Where(u => u.Alive).Sum(u => u.Hp)} " +
            $"bossAlive={session.Sim.Units.Any(u => u.IsBoss && u.Alive)}");

        int? bossMaxHp = null;
        if (level.Waves.BossEnemyId is not null)
        {
            // Boss 出场胜败手算锚（v3.2 §6 Boss 状态 ×1.5 × 威胁 HP 乘子 F.2）：
            // 夜枭 20000→35000；Boss 死亡=场清谓词的最后一环。
            EraCore.Units.Unit boss = Assert.Single(session.Sim.Units, u => u.IsBoss);
            int expectedMaxHp = (int)Math.Ceiling(
                Enemies("era4")[level.Waves.BossEnemyId!].Hp
                * Threat2HpMultiplier(level.Threat)
                * 1.5);
            Assert.Equal(expectedMaxHp, boss.MaxHp);
            Assert.False(boss.Alive); // Boss 必死（Victory=全灭场清）
            long waveOpen = WaveStartTick(level, level.Waves.BossWave!.Value);
            Assert.True(boss.SpawnTick >= waveOpen,
                $"Boss 出场刻 {boss.SpawnTick} 须不早于第 {level.Waves.BossWave} 波开场 {waveOpen}");
            bossMaxHp = boss.MaxHp;
        }

        return new PlaythroughLine(
            level.Slot,
            level.NameZh,
            session.WinWaves,
            level.Threat,
            LevelPacingEstimator.Estimate(level, Towers("era4"), Enemies("era4")).DurationMinutes,
            session.Sim.CurrentTick,
            session.Sim.CurrentTick / (double)Simulation.TicksPerSecond,
            session.Sim.Towers.Count,
            session.IssuedCommands.Count,
            (int)session.Sim.AppliedCommandCount,
            bossMaxHp,
            session.Sim.ComputeStateHash());
    }

    private static double Threat2HpMultiplier(double threat) =>
        EraCore.Balance.Threat.Multipliers(threat).Hp;

    private static long WaveStartTick(HandcraftedLevel level, int wave)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((wave - 1) * intervalTicks);
    }

    // ---- ①②③ 逐关命令流通关断言 -------------------------------------------

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 5).Select(slot => new object[] { slot });

    [Theory]
    [MemberData(nameof(Slots))]
    public void Every_E4_level_is_playable_to_victory_with_its_command_stream(int slot)
    {
        // legal — [F-055] headless 全通关：胜波=数据行 win_waves（§7 双面）、
        // 命令流全量签发且零拒收（含 e4-final upgrade-tower 升级差价与电容转换
        // 器供弹的引擎面合法）、终局塔数=脚本 build-tower 数、E4 无英雄。
        HandcraftedLevel level = Level(slot);
        PlaythroughLine line = RunOnce(level, CreatePort());

        Assert.Equal(level.WinWaves, line.WinWaves);
        Assert.Equal(EraCore.Balance.Campaign.WinWaves(level.Threat), line.WinWaves);
        Assert.Equal(level.CommandScript.Count, line.Issued);
        Assert.Equal(line.Issued, line.Applied); // 零拒收（AppliedCommandCount==签发数）
        Assert.Equal(level.CommandScript.Count(step => step.Verb == "build-tower"), line.Towers);
        if (slot == 5)
        {
            Assert.Equal(35000, line.BossMaxHp); // 夜枭 20000×1.1667×1.5（v3.2 §6 手算锚）
        }

        _output.WriteLine(
            $"L4-{slot} {line.NameZh}: win={line.WinWaves} threat={level.Threat} " +
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
        foreach (int slot in Enumerable.Range(1, 5))
        {
            lines.Add(RunOnce(Level(slot), CreatePort()));
        }

        var sorted = lines.Select(line => line.EstimateMinutes).OrderBy(minutes => minutes).ToList();
        double median = sorted[2]; // 五关中位=第三序统计量
        Assert.InRange(median, 15.0, 40.0);
        Assert.All(lines, line => Assert.InRange(line.EstimateMinutes, 15.0, 40.0));

        _output.WriteLine("T-124 E4 时长表（estimate=理论节奏估算 / victory=headless 通关秒）:");
        foreach (PlaythroughLine line in lines.OrderBy(line => line.Slot))
        {
            _output.WriteLine(
                $"  L4-{line.Slot} {line.NameZh}: win={line.WinWaves} estimate={line.EstimateMinutes:F2}min " +
                $"victory={line.VictorySeconds:F0}s towers={line.Towers} commands={line.Issued}/{line.Applied}");
        }

        Assert.All(lines, line => Assert.True(line.VictorySeconds > 0));
    }

    // ---- ⑤ 确定性 -----------------------------------------------------------

    [Fact]
    public void Same_seed_double_run_of_every_level_hashes_identically()
    {
        // [F-055] 确定性③（引擎面）：同 seed+命令流双跑 → 终局状态哈希与通关
        // 时刻全等——含 e4-final 的 Boss 出场序（终局关固定 seed+布局参数）。
        foreach (int slot in Enumerable.Range(1, 5))
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
