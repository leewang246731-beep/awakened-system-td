using EraCore.Agent.Autopilot;
using EraCore.Balance;
using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Persistence;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][F-123][T-080+T-118] 无尽模式运行时：永续波次生成、死亡终局（唯一
// 终态）、T-118 整局锁档路由（开局档定榜——占比式 SP-7c 矩阵 0%/1~89%/≥90%
// 保留为纯映射与审计列）、SP-7c+词缀加权记分、T-081 sink 挂接、K-7 campaign
// 域落账、同 seed 100 波确定性哈希、混编跨时代与 Boss 节奏。锁档语义与 3×3
// 混排矩阵的专项断言在 EndlessParticipationTests。
public class EndlessRunTests
{
    // ------------------------------------------------ 永续生成 ---------

    [Fact] // legal：无尽可进+波次持续生成——100 波逐波有穷计划、无胜利波、单调递进
    public void Waves_generate_perpetually()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));

        for (int wave = 1; wave <= 100; wave++)
        {
            EndlessWavePlan plan = run.BeginWave();
            Assert.Equal(wave, plan.Wave);
            Assert.Equal(wave, run.CurrentWave);
            Assert.True(plan.TotalCount > 0, $"wave {wave} must spawn enemies");
            Assert.True(plan.IntervalSeconds > 0);
            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        Assert.Equal(100, run.WavesSurvived);
        Assert.False(run.IsOver);          // 100 波不是终点——没有胜利波
        EndlessWavePlan wave101 = run.BeginWave();
        Assert.Equal(101, wave101.Wave);   // 永续：第 101 波照常生成
    }

    [Fact] // legal：波次内容随外推/词缀推进——40 波常规段、41 波外推 63（t=0.9）、41 波起迅捷
    public void Wave_content_follows_extrapolation_and_affixes()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 39); // 先推完 1..39，下一波恰为 40

        EndlessWavePlan plan40 = run.BeginWave();
        run.CompleteWave();
        EndlessWavePlan plan41 = run.BeginWave();
        run.CompleteWave();

        Assert.Equal(40, plan40.Wave);
        Assert.Equal(41, plan41.Wave);
        Assert.Equal(28, plan40.RegularCount);  // 常规段 (6+⌊39/3⌋)/1×1.5 floor 28
        Assert.Equal(63, plan41.RegularCount);  // SP-7a 外推 (40+2×1)×1.5
        Assert.Empty(plan40.Modifiers.ActiveKinds);
        Assert.Equal(new[] { EndlessAffixKind.Swift }, plan41.Modifiers.ActiveKinds);
    }

    [Fact] // legal：Boss 节奏 [NEW]——每 10 波 1 只、非节奏波 0 只；精英线自波 8 起
    public void Boss_cadence_and_elite_line()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        int bosses = 0, elites = 0;

        for (int wave = 1; wave <= 30; wave++)
        {
            EndlessWavePlan plan = run.BeginWave();
            Assert.Equal(wave % 10 == 0 ? 1 : 0, plan.BossCount);
            Assert.Equal(plan.EliteCount, wave < 8 ? 0 : 1 + ((wave - 8) / 4));
            bosses += plan.BossCount;
            elites += plan.EliteCount;
            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        Assert.Equal(3, bosses);
        Assert.Equal(78, elites); // Σ EliteCount(8..30)=4×1+4×2+4×3+4×4+4×5+3×6
        Assert.Equal(78, run.EliteKills);
        Assert.Equal(3, run.BossKills);
    }

    // ------------------------------------------------ 死亡终局 ---------

    [Fact] // legal：终局=死亡唯一终态——Defeat 结算、二次 Defeat/BeginWave 拒绝
    public void Death_is_the_only_terminal()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 12);

        EndlessSettlement settlement = run.Defeat();

        Assert.True(run.IsOver);
        Assert.Equal(12, settlement.WavesSurvived);
        Assert.Throws<InvalidOperationException>(() => run.Defeat());
        Assert.Throws<InvalidOperationException>(() => run.BeginWave());
        Assert.Throws<InvalidOperationException>(() => run.CompleteWave());
        Assert.Throws<InvalidOperationException>(() => run.RecordAction(byAutopilot: true));
    }

    [Fact] // legal：波中死亡——进行中的波不计存活（终局取已完波数）
    public void Defeat_mid_wave_does_not_count_the_active_wave()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 10);
        _ = run.BeginWave(); // 第 11 波进行中死亡

        Assert.True(run.WaveActive);
        EndlessSettlement settlement = run.Defeat();

        Assert.Equal(10, settlement.WavesSurvived);
        Assert.False(run.WaveActive);
    }

    [Fact] // legal：击杀入账钳到计划线数（战绩不可能超过出生数）；负数拒绝
    public void Kill_tallies_clamp_to_the_plan()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessWavePlan plan = run.BeginWave(); // 波 1：elite 0、boss 0
        Assert.Equal(0, plan.EliteCount);

        run.CompleteWave(eliteKills: 999, bossKills: 999);
        Assert.Equal(0, run.EliteKills);

        plan = run.BeginWave(); // 波 2：elite 0
        Assert.Throws<ArgumentOutOfRangeException>(() => run.CompleteWave(eliteKills: -1, bossKills: 0));
        run.CompleteWave(eliteKills: 0, bossKills: 0);
        Assert.Equal(2, run.WavesSurvived);
    }

    // ------------------------------------------------ 三榜路由矩阵 -----

    public static TheoryData<long, long, EndlessBoardKind> RoutingRows => new()
    {
        // (autopilotActions, totalActions) → SP-7c 榜别（0%→手动；1~89%→辅助；≥90%→全托管）
        { 0, 0, EndlessBoardKind.Manual },      // 无动作=纯手动
        { 0, 100, EndlessBoardKind.Manual },    // 0% 精确
        { 1, 100, EndlessBoardKind.Assisted },  // 1% 下界
        { 45, 100, EndlessBoardKind.Assisted },
        { 89, 100, EndlessBoardKind.Assisted }, // 89% 上界
        { 90, 100, EndlessBoardKind.Autopilot },// 90% 下界（含等号）
        { 99, 100, EndlessBoardKind.Autopilot },
        { 100, 100, EndlessBoardKind.Autopilot },
        { 9, 10, EndlessBoardKind.Autopilot },  // 整数交叉乘不落 double 陷阱
    };

    [Theory] // legal：SP-7c 榜别判定矩阵（纯路由函数——整数交叉乘，90% 边界精确）
    [MemberData(nameof(RoutingRows))]
    public void Routing_matrix(long autopilot, long total, EndlessBoardKind expected)
    {
        Assert.Equal(expected, EndlessRun.RouteBoard(autopilot, total));
    }

    [Fact] // boundary：路由守卫——负计数/托管数超过总数拒绝
    public void Routing_guards_reject_bad_tallies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EndlessRun.RouteBoard(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => EndlessRun.RouteBoard(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EndlessRun.RouteBoard(11, 10));
    }

    [Fact] // legal：T-118 整局锁档路由——Defeat 落榜=开局档（SP-7c 占比降为审计列，动作流不换榜）
    public void Defeat_routes_by_the_locked_opening_mode()
    {
        (EndlessParticipationMode locked, int autopilot, int manual, EndlessBoardKind expected)[] cases =
        {
            // 纯手动开局：任何动作流都入手动榜（含中途全托管化——换档不换榜）
            (EndlessParticipationMode.Manual, 0, 20, EndlessBoardKind.Manual),
            (EndlessParticipationMode.Manual, 20, 0, EndlessBoardKind.Manual),
            // 军师辅助开局：轻/重辅助动作流均落系统辅助榜
            (EndlessParticipationMode.Assisted, 1, 99, EndlessBoardKind.Assisted),
            (EndlessParticipationMode.Assisted, 99, 1, EndlessBoardKind.Assisted),
            // 全托管开局：含玩家整局接管（罚则当波、记档整局——T-081 语义对齐）
            (EndlessParticipationMode.FullAutopilot, 20, 0, EndlessBoardKind.Autopilot),
            (EndlessParticipationMode.FullAutopilot, 0, 20, EndlessBoardKind.Autopilot),
        };

        foreach ((EndlessParticipationMode locked, int autopilot, int manual, EndlessBoardKind expected) in cases)
        {
            var run = new EndlessRun(EndlessFixture.Config(participation: locked), new LcgRng(EndlessFixture.Seed));
            EndlessFixture.PlayWaves(run, 5);
            for (int i = 0; i < manual; i++)
            {
                run.RecordAction(byAutopilot: false);
            }

            for (int i = 0; i < autopilot; i++)
            {
                run.RecordAction(byAutopilot: true, pointsSpentByAutopilot: 2);
            }

            EndlessSettlement settlement = run.Defeat();

            Assert.Equal(expected, settlement.Board);
            Assert.Equal(locked, settlement.LockedParticipation);
            Assert.Equal(EndlessBoardChannels.Of(expected), settlement.ChannelId);
            Assert.Equal(run.AutopilotShare, settlement.AutopilotShare, 12);
            // 审计列：占比式判定（SP-7c 矩阵）如实留痕——锁档与占比冲突时二者可见
            Assert.Equal(EndlessRun.RouteBoard(autopilot, autopilot + manual), settlement.ShareImpliedBoard);
            Assert.Equal(autopilot > 0, settlement.AdvisorParticipated);
        }
    }

    // ------------------------------------------------ T-081 sink 挂接 --

    [Fact] // legal：全托管【开局档】局→sink 收到 endless.autopilot 通道条目；手动/辅助开局局 sink 不收
    public void Autopilot_board_feeds_the_t081_sink()
    {
        var sink = new RecordingScoreSink();

        var auto = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed), scoreSink: sink);
        EndlessFixture.PlayWaves(auto, 15);
        for (int i = 0; i < 20; i++)
        {
            auto.RecordAction(byAutopilot: true, pointsSpentByAutopilot: 3);
        }

        EndlessSettlement settled = auto.Defeat();

        Assert.Equal(EndlessBoardKind.Autopilot, settled.Board);
        Assert.Single(sink.Entries);
        Assert.Equal(AutopilotPenalties.EndlessAutopilotChannelId, sink.Entries[0].ChannelId);
        Assert.Equal(15, sink.Entries[0].Wave);
        Assert.Equal(20, sink.Entries[0].ActionsIssued);
        Assert.Equal(60, sink.Entries[0].PointsSpentByAutopilot);

        var manualSink = new RecordingScoreSink();
        var manual = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed), scoreSink: manualSink);
        EndlessFixture.PlayWaves(manual, 15);
        for (int i = 0; i < 10; i++)
        {
            manual.RecordAction(byAutopilot: false);
        }

        Assert.Equal(EndlessBoardKind.Manual, manual.Defeat().Board);
        Assert.Empty(manualSink.Entries); // 手动局不进托管通道

        var assistedSink = new RecordingScoreSink();
        var assisted = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.Assisted),
            new LcgRng(EndlessFixture.Seed), scoreSink: assistedSink);
        EndlessFixture.PlayWaves(assisted, 15);
        for (int i = 0; i < 50; i++)
        {
            assisted.RecordAction(byAutopilot: i % 2 == 0); // 50% 辅助动作流（开局档=辅助）
        }

        Assert.Equal(EndlessBoardKind.Assisted, assisted.Defeat().Board);
        Assert.Empty(assistedSink.Entries);
    }

    [Fact] // legal：与 T-081 二值路由互证——托管参与局的无尽通道=全托管榜通道；手动局分记
    public void Routing_agrees_with_the_t081_binary_hook()
    {
        var participated = new AutopilotParticipation(true, true, AutopilotMode.FullAutopilot);
        var manual = new AutopilotParticipation(false, false, AutopilotMode.Off);

        // 托管参与+无尽局 → 唯一合法去处=隔离通道（=本任务全托管榜通道）
        Assert.Equal(
            AutopilotPenalties.EndlessAutopilotChannelId,
            AutopilotPenalties.RouteScoreChannel(participated, isEndlessRun: true));
        Assert.Equal(EndlessBoardChannels.Of(EndlessBoardKind.Autopilot), AutopilotPenalties.RouteScoreChannel(participated, true));

        // 手动局走 standard 通道——无尽手动榜与之【分记】（T-081 注释口径），不混同
        Assert.Equal(AutopilotPenalties.StandardChannelId, AutopilotPenalties.RouteScoreChannel(manual, true));
        Assert.NotEqual(AutopilotPenalties.RouteScoreChannel(manual, true), EndlessBoardChannels.Of(EndlessBoardKind.Manual));
    }

    // ------------------------------------------------ 记分（公式单测见 EndlessRulesTests）-------

    [Fact] // legal：结算分与榜行同分——Defeat 的 Score=Record.Score（分账一致）
    public void Settlement_score_matches_the_board_record()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 55); // 55 波全杀

        EndlessSettlement settlement = run.Defeat();

        Assert.Equal(run.EliteKills, settlement.EliteKills);
        Assert.Equal(run.BossKills, settlement.BossKills);
        Assert.Equal(EndlessRules.Score(55, run.EliteKills, run.BossKills, EndlessScoreWeights.Default, 0), settlement.Score);
        Assert.Equal(settlement.Score, settlement.Record.Score);
        Assert.Equal(55, settlement.Record.WavesSurvived);
    }

    // ------------------------------------------------ 落账（K-7）------

    [Fact] // legal：Defeat 落账——campaign 域写入恰一次、榜可重载、他槽透传、到达序续号
    public void Defeat_persists_through_the_campaign_domain()
    {
        var port = new InMemorySavePort();
        byte[] legacyPayload = { 7, 7, 7 };
        var meta = new CampaignMetaDto(
            WorldSeed: 2026,
            EraIndex: 1,
            new Dictionary<string, long> { ["era1:res-wood"] = 5 },
            new List<string>(),
            new List<string>(),
            new List<LevelLayoutSnapshotDto> { new("legacy:campaign-ledger", legacyPayload) });

        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed), port, meta);
        EndlessFixture.PlayWaves(run, 20);
        EndlessSettlement settlement = run.Defeat();

        Assert.True(settlement.Persisted);
        Assert.Equal(1, port.CampaignWrites);
        Assert.Equal(1, settlement.Record.Arrival);

        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(2026L, stored.WorldSeed);                                     // 壳域保真
        Assert.Equal(5L, stored.ResourcePool["era1:res-wood"]);
        Assert.Equal(legacyPayload, stored.LevelLayoutSnapshots
            .Single(slot => slot.LevelId == "legacy:campaign-ledger").LayoutPayload); // 他槽不动
        EndlessBoards boards = EndlessBoardStore.FromMeta(stored);
        Assert.Single(boards.Of(EndlessBoardKind.Manual));                          // 手动局入手动榜

        var second = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed + 1), port, stored);
        EndlessFixture.PlayWaves(second, 30);
        for (int i = 0; i < 95; i++)
        {
            second.RecordAction(byAutopilot: true); // 全托管开局档 → 全托管榜
        }

        EndlessSettlement secondSettled = second.Defeat();
        Assert.Equal(2, secondSettled.Record.Arrival);                              // 到达序全局续号
        EndlessBoards reloaded = EndlessBoardStore.Load(port);
        Assert.Single(reloaded.Of(EndlessBoardKind.Manual));
        Assert.Single(reloaded.Of(EndlessBoardKind.Autopilot));
        Assert.Equal(2, reloaded.Arrival);
    }

    [Fact] // legal：无 port=纯结算不落账（Persisted=false，榜行到达序 0=未入账）
    public void Defeat_without_port_is_pure()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 3);

        EndlessSettlement settlement = run.Defeat();

        Assert.False(settlement.Persisted);
        Assert.Equal(0, settlement.Record.Arrival);
    }

    [Fact] // legal：有 port 无注入 meta——以 port 现有战役文档为合并基（不覆盖池/科技/他槽）
    public void Defeat_with_port_but_no_meta_preserves_the_stored_document()
    {
        var port = new InMemorySavePort();
        port.WriteCampaign(new CampaignMetaDto(
            WorldSeed: 4242,
            EraIndex: 2,
            new Dictionary<string, long> { ["era2:res-iron"] = 30 },
            new List<string> { "tech:e2-root" },
            new List<string> { "relic:keep" },
            new List<LevelLayoutSnapshotDto> { new("legacy:campaign-ledger", new byte[] { 5, 5 }) }),
            SaveVersion.Current);

        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed), port); // 无 meta 注入
        EndlessFixture.PlayWaves(run, 8);
        Assert.True(run.Defeat().Persisted);

        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(4242L, stored.WorldSeed);                              // 现有文档做合并基
        Assert.Equal(2, stored.EraIndex);
        Assert.Equal(30L, stored.ResourcePool["era2:res-iron"]);
        Assert.Equal(new List<string> { "tech:e2-root" }, stored.UnlockedTechs.ToList());
        Assert.Equal(new List<string> { "relic:keep" }, stored.RelicRegistry.ToList());
        Assert.Equal(2, stored.LevelLayoutSnapshots.Count);                  // legacy 槽 + endless 槽
        EndlessBoards boards = EndlessBoardStore.FromMeta(stored);
        Assert.Single(boards.Of(EndlessBoardKind.Manual));
    }

    // ------------------------------------------------ 确定性 100 波 ----

    [Fact] // legal：同 seed 100 波哈希一致（T-080 验收锚）——两实例逐点等计划、等哈希
    public void Same_seed_100_waves_hash_identically()
    {
        var left = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        var right = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));

        for (int wave = 1; wave <= 100; wave++)
        {
            EndlessWavePlan leftPlan = left.BeginWave();
            EndlessWavePlan rightPlan = right.BeginWave();
            AssertEqualPlans(leftPlan, rightPlan);                     // 计划逐字段相等（含名册）
            left.CompleteWave(leftPlan.EliteCount, leftPlan.BossCount);
            right.CompleteWave(rightPlan.EliteCount, rightPlan.BossCount);
            Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash()); // 波间逐点一致
        }

        Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
    }

    /// <summary>Field-wise plan equality (records carrying IReadOnlyList members compare those by reference).</summary>
    private static void AssertEqualPlans(EndlessWavePlan expected, EndlessWavePlan actual)
    {
        Assert.Equal(expected.Wave, actual.Wave);
        Assert.Equal(expected.RegularCount, actual.RegularCount);
        Assert.Equal(expected.EliteCount, actual.EliteCount);
        Assert.Equal(expected.BossCount, actual.BossCount);
        Assert.Equal(expected.TotalCount, actual.TotalCount);
        Assert.Equal(expected.IntervalSeconds, actual.IntervalSeconds);
        Assert.Equal(expected.HpMultiplier, actual.HpMultiplier);
        Assert.Equal(expected.Roster, actual.Roster);               // EndlessSpawn 是纯值记录，序列相等
        Assert.Equal(expected.Modifiers.ActiveKinds, actual.Modifiers.ActiveKinds);
        Assert.Equal(expected.Modifiers.Stacks, actual.Modifiers.Stacks);
        Assert.Equal(expected.Modifiers.SpeedMultiplier, actual.Modifiers.SpeedMultiplier);
        Assert.Equal(expected.Modifiers.ArmorMultiplier, actual.Modifiers.ArmorMultiplier);
        Assert.Equal(expected.Modifiers.HpMultiplier, actual.Modifiers.HpMultiplier);
        Assert.Equal(expected.Modifiers.PhasePeriodTicks, actual.Modifiers.PhasePeriodTicks);
        Assert.Equal(expected.Modifiers.PhaseInvulnerableTicks, actual.Modifiers.PhaseInvulnerableTicks);
        Assert.Equal(expected.Modifiers.SuicideRadiusMultiplier, actual.Modifiers.SuicideRadiusMultiplier);
        Assert.Equal(expected.Modifiers.SuicideDamageMultiplier, actual.Modifiers.SuicideDamageMultiplier);
        Assert.Equal(expected.Modifiers.SuicideBaseRadiusCells, actual.Modifiers.SuicideBaseRadiusCells);
        Assert.Equal(expected.Modifiers.SuicideBaseDamage, actual.Modifiers.SuicideBaseDamage);
    }

    [Fact] // legal：种子改变→哈希改变；动作流改变→哈希改变（种子+指令流=重放身份）
    public void Different_seed_or_action_stream_hashes_differ()
    {
        var baseline = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(baseline, 40);

        var otherSeed = new EndlessRun(EndlessFixture.Config(seed: EndlessFixture.Seed + 1), new LcgRng(EndlessFixture.Seed + 1));
        EndlessFixture.PlayWaves(otherSeed, 40);
        Assert.NotEqual(baseline.ComputeStateHash(), otherSeed.ComputeStateHash());

        var otherActions = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(otherActions, 40);
        otherActions.RecordAction(byAutopilot: true);
        Assert.NotEqual(baseline.ComputeStateHash(), otherActions.ComputeStateHash());
    }

    // ------------------------------------------------ 混编跨时代 -------

    [Fact] // legal：混编跨时代——100 波名册跨 ≥2 时代；常规线时代份额=权重份额（2:1 池）
    public void Roster_mixes_eras_across_one_hundred_waves()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        var regularEras = new Dictionary<int, long>();
        long regularSpawns = 0;
        var eliteEras = new HashSet<int>();
        var bossEras = new HashSet<int>();

        for (int wave = 1; wave <= 100; wave++)
        {
            EndlessWavePlan plan = run.BeginWave();
            foreach (EndlessSpawn spawn in plan.Roster)
            {
                switch (spawn.Role)
                {
                    case EndlessEnemyRole.Regular:
                        regularEras[spawn.EraIndex] = regularEras.GetValueOrDefault(spawn.EraIndex) + 1;
                        break;
                    case EndlessEnemyRole.Elite:
                        eliteEras.Add(spawn.EraIndex);
                        break;
                    case EndlessEnemyRole.Boss:
                        bossEras.Add(spawn.EraIndex);
                        break;
                }
            }

            regularSpawns += plan.RegularCount;
            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        Assert.True(regularEras.Keys.Count >= 2, "常规线名册须混编跨时代");
        Assert.Contains(1, regularEras.Keys); // E1
        Assert.Contains(2, regularEras.Keys); // E2
        Assert.Contains(1, eliteEras);        // 精英线也跨时代
        Assert.Contains(2, eliteEras);
        Assert.Contains(3, bossEras);         // Boss 子池带入 E3（三时代混编证明）

        // 常规线 E1:E2 = 2:1 权重比（大数定律下 ±15% 容差，固定种子确定性）
        double share = (double)regularEras[1] / regularSpawns;
        Assert.InRange(share, (2.0 / 3.0) - 0.15, (2.0 / 3.0) + 0.15);
    }

    // ------------------------------------------------ 入口规则（校验矩阵见 EndlessRulesTests）-------

    [Fact] // legal：奇观带入——配置冻结 id 序列（v2.1 入口规则行），参与身份哈希
    public void Carried_wonders_freeze_into_the_config_and_hash()
    {
        EndlessRunConfig withWonders = EndlessFixture.Config(wonders: new[] { "wonder:pyramid", "wonder:internet" });
        var runA = new EndlessRun(withWonders, new LcgRng(EndlessFixture.Seed));
        var runB = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(runA, 5);
        EndlessFixture.PlayWaves(runB, 5);

        Assert.Equal(new[] { "wonder:pyramid", "wonder:internet" }, withWonders.WonderIds);
        Assert.NotEqual(runA.ComputeStateHash(), runB.ComputeStateHash()); // 奇观带入改变运行身份
    }

    [Fact] // legal：波间隔=120s×间隔乘子 不放宽（SP-7a）——t=0.9 → 120×clamp(2−1.4×0.6/0.9)
    public void Interval_follows_the_campaign_formula()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        EndlessWavePlan plan = run.BeginWave();

        Assert.Equal(Campaign.WaveIntervalSeconds(EndlessFixture.Threat), plan.IntervalSeconds, 9);
        Assert.Equal(120.0 * Threat.Multipliers(EndlessFixture.Threat).Interval, plan.IntervalSeconds, 9);
    }
}
