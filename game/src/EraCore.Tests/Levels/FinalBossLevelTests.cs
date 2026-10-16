using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-003][F-055] 终局关 headless 全通关验收（跃迁前置补全；T-084/T-116
// e2e 先例）：e1.json/e2.json 各追加一条 Boss 终局关（id=era_transition.json
// final_levels 引用值），每关以 offsystem 纯玩家命令流贯穿生产 LevelSession
// 跑到 Victory——断言面：①两终局关可玩至胜（胜波 19=数据行 win_waves=§7 公式
// 双面；19 波=Boss 波，终局一搏）；②命令流全量签发且零拒收（经济面全合法）；
//③Boss 出场胜败手算锚：踏群者/铁蹄 MaxHp=敌表 HP×威胁 HP 乘子×Boss 状态
// ×1.5（2500→4375 / 4000→7000）、践踏/冲撞/战鼓技能参数=BossBattle/
// IronhoofBattle 先例锚、Boss 必死（场清谓词）；④时长估算表（估算中位带
// [15,40] 分钟+headless 通关时刻并列输出）；⑤同 seed 双跑状态哈希全等。
public class FinalBossLevelTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public FinalBossLevelTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 两终局关 + 各自的践踏调参（[NEW-原型校准] 测试侧配置，与
    /// BossBattleIntegrationTests（踏群者 60/45）和 IronhoofBattleTests
    /// （铁蹄 100/45）同锚——引擎不携带设计数值，CS-2.3/RL7）。
    /// </summary>
    private static readonly (string Id, string EraDir, int TrampleDamage)[] Finals =
    {
        ("campaign:level-e1-final", "era1", 60),
        ("campaign:level-e2-final", "era2", 100),
    };

    private static HandcraftedLevel Level(string id, string eraDir) =>
        HandcraftedLevelData.Load(eraDir == "era1" ? "e1.json" : "e2.json").TryGet(id)
        ?? throw new InvalidOperationException($"缺终局关行 {id}");

    /// <summary>
    /// 终局关装配：T-085 BuildConfig（含 boss_wave/boss_enemy_id 透传）+
    /// 践踏调参挂载（Boss 技能组经 spawner boss 批次自动接线——冲撞/战鼓走
    /// spec traits，践踏周期/伤害=TrampleProfile 注入）。
    /// </summary>
    internal static SimulationConfig BuildBossConfig(HandcraftedLevel level, string eraDir, int trampleDamage) =>
        BuildConfig(level, eraDir) with
        {
            Waves = BuildConfig(level, eraDir).Waves with
            {
                Trample = new TrampleProfile(Damage: trampleDamage, PeriodTicks: 45),
            },
        };

    /// <summary>胜波+8 波窗 horizon（Boss/漏敌滞留核心位的清场余量，E3 +4 先例加宽）。</summary>
    internal static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 8) * intervalTicks);
    }

    private static long WaveStartTick(HandcraftedLevel level, int wave)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((wave - 1) * intervalTicks);
    }

    private sealed record BossPlaythroughLine(
        string Id,
        string NameZh,
        int WinWaves,
        int BossWave,
        int BossMaxHp,
        int ExpectedBossMaxHp,
        long BossSpawnTick,
        long Wave19StartTick,
        double EstimateMinutes,
        long VictoryTick,
        double VictorySeconds,
        int Towers,
        int Issued,
        int Applied,
        int BossTrampleDamage,
        int BossChargeDamage,
        int BossChargeIntervalTicks,
        float BossWarDrumsRadiusPx,
        long StateHash);

    private BossPlaythroughLine RunOnce(HandcraftedLevel level, string eraDir, int trampleDamage, ISavePort port)
    {
        SimulationConfig config = BuildBossConfig(level, eraDir, trampleDamage);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, eraDir);
        var session = new LevelSession(
            new LevelSessionConfig(config, $"final.{level.Id}", level.Threat, level.Map.Seed),
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
            $"未通关（state={session.Machine.State}）: wave={session.Sim.CurrentWave} tick={session.Sim.CurrentTick} " +
            $"issued={session.IssuedCommands.Count} applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
            $"alive={session.Sim.Units.Count(u => u.Alive)} aliveHp={session.Sim.Units.Where(u => u.Alive).Sum(u => u.Hp)} " +
            $"bossAlive={session.Sim.Units.Any(u => u.IsBoss && u.Alive)}");

        // Boss 出场胜败手算锚（v3.2 §6 Boss 状态 ×1.5 × 威胁 HP 乘子 F.2）：
        // 踏群者 2500→4375 / 铁蹄 4000→7000；Boss 死亡=场清谓词的最后一环。
        Unit boss = Assert.Single(session.Sim.Units, u => u.IsBoss);
        int expectedMaxHp = (int)Math.Ceiling(
            Enemies(eraDir)[level.Waves.BossEnemyId!].Hp
            * Threat.Multipliers(level.Threat).Hp
            * 1.5);
        Assert.Equal(expectedMaxHp, boss.MaxHp);
        Assert.False(boss.Alive); // Boss 必死（Victory=全灭场清）
        Assert.True(boss.SpawnTick >= WaveStartTick(level, level.Waves.BossWave!.Value),
            $"Boss 出场刻 {boss.SpawnTick} 须不早于第 {level.Waves.BossWave} 波开场");

        return new BossPlaythroughLine(
            level.Id,
            level.NameZh,
            session.WinWaves,
            level.Waves.BossWave!.Value,
            boss.MaxHp,
            expectedMaxHp,
            boss.SpawnTick,
            WaveStartTick(level, level.Waves.BossWave!.Value),
            LevelPacingEstimator.Estimate(level, Towers(eraDir), Enemies(eraDir)).DurationMinutes,
            session.Sim.CurrentTick,
            session.Sim.CurrentTick / (double)Simulation.TicksPerSecond,
            session.Sim.Towers.Count,
            session.IssuedCommands.Count,
            (int)session.Sim.AppliedCommandCount,
            boss.TrampleDamage,
            boss.ChargeDamage,
            boss.ChargeIntervalTicks,
            boss.WarDrumsRadiusPx,
            session.Sim.ComputeStateHash());
    }

    // ---- ①②③ 逐关命令流通关 + Boss 手算锚 -----------------------------------

    public static IEnumerable<object[]> FinalRows() => Finals.Select(f => new object[] { f.Id, f.EraDir });

    [Theory]
    [MemberData(nameof(FinalRows))]
    public void Every_final_boss_level_is_playable_to_victory_with_its_command_stream(string id, string eraDir)
    {
        // legal — [F-003][F-055] headless 全通关：胜波=19=数据行 win_waves（§7
        // 双面）、命令流全量签发且零拒收、终局塔数=脚本 build-tower 数、Boss
        // 出场/死亡/技能参数全锚（BossBattle/IronhoofBattle 先例值）。
        (string _, string _, int trampleDamage) = Finals.Single(f => f.Id == id);
        HandcraftedLevel level = Level(id, eraDir);
        BossPlaythroughLine line = RunOnce(level, eraDir, trampleDamage, CreatePort());

        Assert.Equal(19, line.WinWaves);
        Assert.Equal(EraCore.Balance.Campaign.WinWaves(level.Threat), line.WinWaves);
        Assert.Equal(level.WinWaves, line.BossWave); // Boss 波=胜波（终局一搏）
        Assert.Equal(level.CommandScript.Count, line.Issued);
        Assert.Equal(line.Issued, line.Applied); // 零拒收
        Assert.Equal(level.CommandScript.Count(step => step.Verb == "build-tower"), line.Towers);

        // 技能锚：践踏=调参×Boss 状态 ×1.3（60→78 / 100→130，先例同式）；
        // E2 铁蹄冲撞 150→195/600 ticks+战鼓 3 格=96 px（IronhoofBattle 断言值）。
        int expectedTrample = (int)Math.Ceiling(trampleDamage * 1.3);
        Assert.Equal(expectedTrample, line.BossTrampleDamage);
        if (eraDir == "era2")
        {
            Assert.Equal(600, line.BossChargeIntervalTicks); // 每 20 s（SP-3b L121）
            Assert.Equal(195, line.BossChargeDamage); // ceil(150×1.3)（IronhoofBattle 锚）
            Assert.Equal(96f, line.BossWarDrumsRadiusPx, 3); // 3 格×32 px
        }
        else
        {
            Assert.Equal(0, line.BossChargeIntervalTicks); // E1 踏群者无冲撞/战鼓
            Assert.Equal(0, line.BossChargeDamage);
            Assert.Equal(0f, line.BossWarDrumsRadiusPx, 3);
        }

        _output.WriteLine(
            $"{line.Id} {line.NameZh}: win={line.WinWaves} bossHp={line.BossMaxHp} " +
            $"bossWave={line.BossWave} (spawn {line.BossSpawnTick} ≥ open {line.Wave19StartTick}) " +
            $"estimate={line.EstimateMinutes:F2}min victory={line.VictorySeconds:F0}s " +
            $"towers={line.Towers} issued/applied={line.Issued}/{line.Applied}");
    }

    // ---- ④ 时长估算表（估算带 + headless 实测时刻并列） ----------------------

    [Fact]
    public void Duration_table_both_finals_land_in_the_band_and_victories_are_recorded()
    {
        // [F-055] 时长估算落带注记：理论节奏（估算器口径，含 Boss 波行）逐关
        // ∈[15,40] 分钟带；headless 通关时刻（弹链供给节流口径）并列输出——
        // 两口径同表即 _duration_calibration 的 RED-FLAG 诚实披露（实测口径归
        // T-095；终局关 Boss 波=单 Boss 批次，估算不因 19 波胜波失控）。
        var lines = new List<BossPlaythroughLine>();
        foreach ((string id, string eraDir, int trampleDamage) in Finals)
        {
            lines.Add(RunOnce(Level(id, eraDir), eraDir, trampleDamage, CreatePort()));
        }

        Assert.All(lines, line => Assert.InRange(line.EstimateMinutes, 15.0, 40.0));
        Assert.All(lines, line => Assert.InRange(line.VictorySeconds, 0, 3600 * 2));

        _output.WriteLine("终局关时长表（estimate=理论节奏估算 / victory=headless 通关秒）:");
        foreach (BossPlaythroughLine line in lines)
        {
            _output.WriteLine(
                $"  {line.Id} {line.NameZh}: win={line.WinWaves} bossHp={line.BossMaxHp} " +
                $"estimate={line.EstimateMinutes:F2}min victory={line.VictorySeconds:F0}s " +
                $"towers={line.Towers} commands={line.Issued}/{line.Applied}");
        }
    }

    // ---- ⑤ 确定性（同 seed 双跑） ---------------------------------------------

    [Fact]
    public void Same_seed_double_run_of_both_finals_hashes_identically()
    {
        // [F-055] 确定性③（引擎面）：同 seed+命令流双跑 → 通关时刻与终局状态
        // 哈希全等（终局关固定 seed+布局参数，Boss 出场序确定）。
        foreach ((string id, string eraDir, int trampleDamage) in Finals)
        {
            HandcraftedLevel level = Level(id, eraDir);
            BossPlaythroughLine first = RunOnce(level, eraDir, trampleDamage, CreatePort());
            BossPlaythroughLine second = RunOnce(level, eraDir, trampleDamage, CreatePort());

            Assert.Equal(first.VictoryTick, second.VictoryTick);
            Assert.Equal(first.BossSpawnTick, second.BossSpawnTick);
            Assert.Equal(first.StateHash, second.StateHash);
            Assert.Equal(first.Towers, second.Towers);
        }
    }
}
