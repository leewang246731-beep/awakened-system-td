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

// [C-19][F-055][F-018][F-033][F-009] T-135 终章全关可玩验收（headless E2E，
// T-084/T-085/T-116/T-124/T-134 e2e 先例）：每条 e6.json 关行以 T-085 命令流
//（纯玩家建造——含 e6-final 双奇点炮线、e6-02 的 drone 20/s 干线、e6-03 的
// 时空塔对策步）贯穿生产 LevelSession 跑到 Victory——断言面：①逐关通关（胜波=
// 数据行 win_waves §7 双面）；②命令流全量签发且零拒收（经济面全合法——反物质
// 货币+暗物质三级炼链的引擎面证明）；③终局塔数=脚本建造数（零出售——终章无
// upgrade-tower 步，两塔 max_tier 1）；④理论节奏估算表（中位∈[15,40] 分钟带）
// 与 headless 通关时刻并列输出（时长估算表；估算器不含弹链供给节流，实测时刻=
// 节流口径的诚实读数）；⑤同 seed 双跑状态哈希全等；⑥终局关 Boss 手算锚（堕落
// 守望者出场 HP=120000×威胁 HP 乘子×Boss 状态 ×1.5=210000、出场刻不早于第 19
// 波开场）；⑦零点能=无风险能源装配面（EnergyRisk=null——终章未列风险项的两处
// 真相源一致（v3.2 §4 能源阶梯行+SP-4 L142），湮灭/过热/熔毁通道全部不挂）；
// ⑧通关之门数据位（e6-final requires_wonder=纪元之门——本验收面=『门后可通』
// 的引擎证明：WonderFinaleGateBoard 开锁后关行可通关）。
public class Era6LevelPlaythroughTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era6LevelPlaythroughTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(int slot) =>
        HandcraftedLevelData.Load("e6.json").Items.Single(level => level.Slot == slot);

    /// <summary>
    /// 终章装配：T-085 BuildConfig（towers/enemies/仓库等级 6/反物质货币全出自
    /// era6 内容表）+ EnergyRisk=null（零点能=无风险能源——终章关卡不挂任何
    /// 负载率类风险通道；E5 装配的对照面=Era5LevelPlaythroughTests.BuildE5Config
    /// 挂 overheat+annihilation 双表）。终章无英雄行（era6/levels.json _source：
    /// SP-6 英雄行随章节设计定案落位，era6 无 heroes.json）。
    /// </summary>
    internal static SimulationConfig BuildE6Config(HandcraftedLevel level)
    {
        SimulationConfig config = BuildConfig(level, "era6");
        Assert.Null(config.EnergyRisk); // 零点能=无风险：装配面即证据（无 overheat/meltdown/annihilation 通道）
        return config;
    }

    internal static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 12) * intervalTicks); // 胜波+12 波窗（终局关 Boss 波+漏敌滞留清场余量，E5 同款加宽版）
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
        SimulationConfig config = BuildE6Config(level);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, "era6");
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t135.{level.Id}", level.Threat, level.Map.Seed),
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
            // 堕落守望者 120000→210000（全六代顶点）；Boss 死亡=场清谓词的最后一环。
            EraCore.Units.Unit boss = Assert.Single(session.Sim.Units, u => u.IsBoss);
            int expectedMaxHp = (int)Math.Ceiling(
                Enemies("era6")[level.Waves.BossEnemyId!].Hp
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
            LevelPacingEstimator.Estimate(level, Towers("era6"), Enemies("era6")).DurationMinutes,
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
        long graceTicks = (long)Math.Round(intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((wave - 1) * intervalTicks);
    }

    // ---- ①②③ 逐关命令流通关断言 -------------------------------------------

    public static IEnumerable<object[]> Slots() => Enumerable.Range(1, 5).Select(slot => new object[] { slot });

    [Theory]
    [MemberData(nameof(Slots))]
    public void Every_finale_level_is_playable_to_victory_with_its_command_stream(int slot)
    {
        // legal — [F-055] headless 全通关：胜波=数据行 win_waves（§7 双面）、
        // 命令流全量签发且零拒收（含 e6-final 双奇点炮线与弹链、e6-02 drone
        // 干线 assign-carrier、e6-03 时空塔+时序单元支链的引擎面合法）、终局
        // 塔数=脚本 build-tower 数、终章无英雄无升级步。
        HandcraftedLevel level = Level(slot);
        PlaythroughLine line = RunOnce(level, CreatePort());

        Assert.Equal(level.WinWaves, line.WinWaves);
        Assert.Equal(EraCore.Balance.Campaign.WinWaves(level.Threat), line.WinWaves);
        Assert.Equal(level.CommandScript.Count, line.Issued);
        Assert.Equal(line.Issued, line.Applied); // 零拒收（AppliedCommandCount==签发数）
        Assert.Equal(level.CommandScript.Count(step => step.Verb == "build-tower"), line.Towers);
        if (slot == 5)
        {
            Assert.Equal(210000, line.BossMaxHp); // 堕落守望者 120000×1.1667×1.5（v3.2 §6 手算锚）
        }

        _output.WriteLine(
            $"L6-{slot} {line.NameZh}: win={line.WinWaves} threat={level.Threat} " +
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

        _output.WriteLine("T-135 终章时长表（estimate=理论节奏估算 / victory=headless 通关秒）:");
        foreach (PlaythroughLine line in lines.OrderBy(line => line.Slot))
        {
            _output.WriteLine(
                $"  L6-{line.Slot} {line.NameZh}: win={line.WinWaves} estimate={line.EstimateMinutes:F2}min " +
                $"victory={line.VictorySeconds:F0}s towers={line.Towers} commands={line.Issued}/{line.Applied}");
        }

        Assert.All(lines, line => Assert.True(line.VictorySeconds > 0));
    }

    // ---- ⑤ 确定性 -----------------------------------------------------------

    [Fact]
    public void Same_seed_double_run_of_every_level_hashes_identically()
    {
        // [F-055] 确定性③（引擎面）：同 seed+命令流双跑 → 终局状态哈希与通关
        // 时刻全等——含 e6-final 的 Boss 出场序（终局关固定 seed+布局参数）与
        // 零点能无风险装配面（EnergyRisk=null 双跑一致——无风险通道=哈希位
        // 恒缺的确定性形态）。
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

    // ---- ⑦ 零点能=无风险能源族（E5 对照面） --------------------------------

    [Fact]
    public void Zero_point_energy_assembly_carries_no_risk_channels_unlike_E5()
    {
        // [F-009][F-010] 终章风险口径的装配面：E6 关卡 EnergyRisk=null（零点能=
        // 无风险能源——终章未列风险项的两处真相源一致：v3.2 §4 能源阶梯行
        //（E1 粮仓/E3 过热/E4 熔毁/E5 湮灭，E2 无，终章未列）+SP-4 L142 适用代
        //=E3 起负载率类风险表无终章行）；E5 对照装配挂过热+湮灭双表（era5
        // energy.risk+annihilation → Era5EnergyRisk）——两代装配面差异即
        // 『无风险能源族』的引擎证据。
        SimulationConfig finale = BuildConfig(Level(1), "era6");
        Assert.Null(finale.EnergyRisk);
        Assert.Null(BuildConfig(Level(5), "era6").EnergyRisk); // 终局关同口径

        SimulationConfig e5 = BuildConfig(
            HandcraftedLevelData.Load("e5.json").Items.Single(level => level.Id == "campaign:level-e5-01"),
            "era5");
        Assert.Null(e5.EnergyRisk); // BuildConfig 基线不挂（E5 挂载面=Era5LevelPlaythroughTests.BuildE5Config）
        var e5WithRisk = Era5LevelPlaythroughTests.BuildE5Config(
            HandcraftedLevelData.Load("e5.json").Items.Single(level => level.Id == "campaign:level-e5-01"));
        Assert.NotNull(e5WithRisk.EnergyRisk);          // E5 装配面：过热+湮灭在挂
        Assert.NotNull(e5WithRisk.EnergyRisk!.Overheat);
        Assert.NotNull(e5WithRisk.EnergyRisk.Annihilation);
    }
}
