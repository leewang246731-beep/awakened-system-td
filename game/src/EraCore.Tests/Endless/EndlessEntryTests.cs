using EraCore.Agent.Autopilot;
using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Persistence;
using EraCore.Tests.Levels;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][F-123][T-118][T-154] 无尽入口正式化：从战役界面语境组装任选时代的无尽
// 会话（EndlessEntry.Begin——最小接入点裁定：战役选图/菜单语境经宿主调用，
// 无尽不进 LevelSession/检查点域）；死亡终局→记分→返回的最小会话流
// （Running→Settled→Returned）；E3 时代无尽可用（era3 现四关内容集真数据
// 混编池+词缀照 SP-7 生产默认档）；同 seed 确定性。T-154 缺省定版：词缀节奏
// 两档显式选项（缺省=滚词缀档/ClassicFixed=经典 SP-7b 固定表回切）+策略表
// 缺省链+两档分账/零漂移矩阵。IO 由测试自持（HandcraftedLevelContent 仓库
// 相对读取 game/content，RL9）。
public class EndlessEntryTests
{
    private const long Seed = 20261180;

    /// <summary>Era1 L1 波形（game/content/levels/e1.json 首关 waves 行）.</summary>
    private static (int Base, int Begin, int Spacing, double Scaling) Era1Shape => (14, 3, 3, 1.6);

    /// <summary>Era2 L1 波形（game/content/levels/e2.json 首关 waves 行）.</summary>
    private static (int Base, int Begin, int Spacing, double Scaling) Era2Shape => (16, 3, 4, 1.6);

    /// <summary>Era3 L1 波形（game/content/levels/e3.json 首关 L3-1 waves 行——现四关内容集）.</summary>
    private static (int Base, int Begin, int Spacing, double Scaling) Era3Shape => (16, 3, 4, 1.6);

    private static EndlessEraContent EraContent(int era) => era switch
    {
        1 => new EndlessEraContent(1, Rows(1), Era1Shape.Base, Era1Shape.Begin, Era1Shape.Spacing, Era1Shape.Scaling),
        2 => new EndlessEraContent(2, Rows(2), Era2Shape.Base, Era2Shape.Begin, Era2Shape.Spacing, Era2Shape.Scaling),
        3 => new EndlessEraContent(3, Rows(3), Era3Shape.Base, Era3Shape.Begin, Era3Shape.Spacing, Era3Shape.Scaling),
        _ => throw new ArgumentOutOfRangeException(nameof(era)),
    };

    /// <summary>真内容敌表（game/content/era{N}/enemies.json 经生产解析器 EnemySpecCatalog）.</summary>
    private static IReadOnlyList<EnemySpec> Rows(int era) =>
        HandcraftedLevelContent.Enemies($"era{era}").Values.OrderBy(spec => spec.Id, StringComparer.Ordinal).ToList();

    private static EndlessEntryRequest Request(
        int startEra,
        EndlessParticipationMode mode,
        long seed = Seed,
        IReadOnlyList<string>? wonders = null,
        int[]? eras = null) => new(
        seed,
        startEra,
        mode,
        ThreatLevel: 0.9,
        EraContent: (eras ?? new[] { 1, 2, 3 }).Select(EraContent).ToArray(),
        WonderIds: wonders ?? Array.Empty<string>());

    // ------------------------------------------------ 入口规则 -----

    [Fact] // legal：任选时代——已装载内容集内任一时代可开局（E1/E2/E3 各一），波形取自开局时代
    public void Any_loaded_era_can_start()
    {
        foreach (int era in new[] { 1, 2, 3 })
        {
            EndlessSession session = EndlessEntry.Begin(Request(era, EndlessParticipationMode.Manual), new LcgRng(Seed));
            Assert.Equal(EndlessSessionState.Running, session.State);
            Assert.Equal(era, session.Run.Config.StartEraIndex);
            (int Base, int Begin, int Spacing, double Scaling) shape = era switch
            {
                1 => Era1Shape,
                2 => Era2Shape,
                _ => Era3Shape,
            };
            Assert.Equal(shape.Base, session.Run.Config.WaveBaseCount); // 开局时代的波形生效
            Assert.Equal(shape.Scaling, session.Run.Config.WaveScaling, 12);
        }
    }

    [Fact] // legal：开局档随入口冻结（三档各一），HUD 快照即查
    public void Entry_freezes_the_locked_participation_mode()
    {
        foreach (EndlessParticipationMode mode in new[]
                     { EndlessParticipationMode.Manual, EndlessParticipationMode.Assisted, EndlessParticipationMode.FullAutopilot })
        {
            EndlessSession session = EndlessEntry.Begin(Request(3, mode), new LcgRng(Seed));
            Assert.Equal(mode, session.Run.Config.Participation);
            Assert.Equal(mode, session.Participation.LockedMode); // HUD 位（EndlessSession 查询通道）
            Assert.Equal(mode, session.Run.LockedParticipation);
        }
    }

    [Fact] // legal：混编池——三时代内容合集入池、每线跨时代；奇观带入透传冻结
    public void Entry_builds_the_cross_era_pool_and_carries_wonders()
    {
        string[] wonders = { "wonder:pyramid", "wonder:expo-e2" };
        EndlessSession session = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Assisted, wonders: wonders),
            new LcgRng(Seed));

        Assert.Equal(new[] { 1, 2, 3 }, session.Run.Config.Pool.EraIndexes);
        Assert.Contains("era3:enemy-boss", session.Run.Config.Pool.Entries
            .Where(entry => entry.Role == EndlessEnemyRole.Boss).Select(entry => entry.EnemyId)); // 堡垒号在 Boss 池
        Assert.Equal(wonders, session.Run.Config.WonderIds);
    }

    // illegal：入口守卫——未装载时代拒绝（任选时代限于装载集）、未定义档拒绝、空内容集拒绝
    [Fact]
    public void Entry_guards_reject_unloaded_eras_and_undefined_modes()
    {
        Assert.Throws<ArgumentException>(
            () => EndlessEntry.Begin(Request(4, EndlessParticipationMode.Manual), new LcgRng(Seed)));
        Assert.Throws<ArgumentException>(
            () => EndlessEntry.Begin(
                Request(3, EndlessParticipationMode.Manual, eras: new[] { 1, 2 }),
                new LcgRng(Seed))); // era3 不在装载集
        Assert.Throws<ArgumentException>(() => EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, 3, (EndlessParticipationMode)99, 0.9,
                new[] { EraContent(3) }, Array.Empty<string>()),
            new LcgRng(Seed)));
        Assert.Throws<ArgumentException>(() => EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, 3, EndlessParticipationMode.Manual, 0.9,
                Array.Empty<EndlessEraContent>(), Array.Empty<string>()),
            new LcgRng(Seed)));
    }

    // ------------------------------------------------ 会话流（死亡终局→记分→返回）-----

    [Fact] // legal：E3 全托管开局 20 波死亡——落账恰一次入全托管榜、T-081 sink 收条目、返回战役界面闭环
    public void Era3_full_flow_dies_scores_and_returns()
    {
        var port = new InMemorySavePort();
        var sink = new RecordingScoreSink();
        var meta = new CampaignMetaDto(
            WorldSeed: 777,
            EraIndex: 3,
            new Dictionary<string, long> { ["era3:res-steel"] = 40 },
            new List<string>(),
            new List<string>(),
            new List<LevelLayoutSnapshotDto> { new("legacy:campaign-ledger", new byte[] { 9, 9 }) });

        EndlessSession session = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.FullAutopilot),
            new LcgRng(Seed),
            port,
            meta,
            sink);
        for (int i = 0; i < 20; i++)
        {
            EndlessWavePlan plan = session.Run.BeginWave();
            session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        for (int i = 0; i < 30; i++)
        {
            session.Run.RecordAction(byAutopilot: true, pointsSpentByAutopilot: 1);
        }

        EndlessSettlement settlement = session.Die();

        Assert.Equal(EndlessSessionState.Settled, session.State);
        Assert.Equal(EndlessBoardKind.Autopilot, settlement.Board);
        Assert.True(settlement.Persisted);
        Assert.Equal(1, port.CampaignWrites);
        Assert.Equal(AutopilotPenalties.EndlessAutopilotChannelId, sink.Entries.Single().ChannelId); // T-081 隔离通道

        // 落账保真：campaign 域其他字段与他槽不动（K-7 合并纪律）
        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(777L, stored.WorldSeed);
        Assert.Equal(40L, stored.ResourcePool["era3:res-steel"]);
        Assert.Equal(new byte[] { 9, 9 }, stored.LevelLayoutSnapshots
            .Single(slot => slot.LevelId == "legacy:campaign-ledger").LayoutPayload);

        // 三榜查询：仅全托管榜有本局；另两榜空（互不混排）
        EndlessBoards boards = EndlessBoardStore.Load(port);
        Assert.Single(boards.Of(EndlessBoardKind.Autopilot));
        Assert.Empty(boards.Of(EndlessBoardKind.Manual));
        Assert.Empty(boards.Of(EndlessBoardKind.Assisted));

        session.Return();
        Assert.Equal(EndlessSessionState.Returned, session.State);
    }

    // boundary：会话流守卫——Running 不可 Return、二次 Die 拒绝、Abandoned 不可 Die、二次 Return 拒绝
    [Fact]
    public void Session_flow_guards()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(2, EndlessParticipationMode.Manual), new LcgRng(Seed));
        Assert.Throws<InvalidOperationException>(session.Return); // 未死先返拒绝

        EndlessFixture.PlayWaves(session.Run, 3);
        _ = session.Die();
        Assert.Throws<InvalidOperationException>(session.Die);    // 死亡终局唯一

        EndlessSession abandoned = EndlessEntry.Begin(
            Request(2, EndlessParticipationMode.Manual), new LcgRng(Seed));
        EndlessFixture.PlayWaves(abandoned.Run, 3);
        abandoned.Abandon();
        Assert.Equal(EndlessSessionState.Abandoned, abandoned.State);
        Assert.Throws<InvalidOperationException>(abandoned.Die);  // 弃局不再记分
        abandoned.Return();

        EndlessSession settled = EndlessEntry.Begin(
            Request(2, EndlessParticipationMode.Manual), new LcgRng(Seed));
        _ = settled.Die();
        settled.Return();
        Assert.Throws<InvalidOperationException>(settled.Return); // 返回唯一
    }

    [Fact] // legal：弃局不记分（负向）——Abandoned 会话零榜行零落账
    public void Abandoned_sessions_record_nothing()
    {
        var port = new InMemorySavePort();
        EndlessSession session = EndlessEntry.Begin(
            Request(1, EndlessParticipationMode.Assisted), new LcgRng(Seed), port);
        EndlessFixture.PlayWaves(session.Run, 8);
        session.Abandon();
        session.Return();

        Assert.Equal(0, port.CampaignWrites); // 无落账
        EndlessBoards boards = EndlessBoardStore.Load(port);
        Assert.Empty(boards.Of(EndlessBoardKind.Manual));
        Assert.Empty(boards.Of(EndlessBoardKind.Assisted));
        Assert.Empty(boards.Of(EndlessBoardKind.Autopilot));
    }

    // ------------------------------------------------ E3 无尽可用 + 确定性 -----

    [Fact] // legal：E3 无尽确定性——同 seed 两会话 60 波逐点等哈希（era3 真内容池+SP-7 生产词缀）
    public void Era3_endless_is_deterministic_across_sixty_waves()
    {
        var left = EndlessEntry.Begin(Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));
        var right = EndlessEntry.Begin(Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));

        for (int wave = 1; wave <= 60; wave++)
        {
            EndlessWavePlan leftPlan = left.Run.BeginWave();
            EndlessWavePlan rightPlan = right.Run.BeginWave();
            Assert.Equal(leftPlan.Roster, rightPlan.Roster);
            left.Run.CompleteWave(leftPlan.EliteCount, leftPlan.BossCount);
            right.Run.CompleteWave(rightPlan.EliteCount, rightPlan.BossCount);
            Assert.Equal(left.Run.ComputeStateHash(), right.Run.ComputeStateHash());
        }

        Assert.Equal(60, left.Run.WavesSurvived);
    }

    [Fact] // legal：E3 名册混编——era3 内容集行真的出现在常规/精英/Boss 三线（现四关内容集）
    public void Era3_roster_actually_spawns_era3_content()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual, eras: new[] { 3 }), // 单时代内容集（era3 现四关集）
            new LcgRng(Seed));

        Assert.Equal(new[] { 3 }, session.Run.Config.Pool.EraIndexes);
        bool sawRegular = false, sawElite = false, sawBoss = false;
        for (int wave = 1; wave <= 30; wave++)
        {
            EndlessWavePlan plan = session.Run.BeginWave();
            sawRegular |= plan.Roster.Any(spawn => spawn.Role == EndlessEnemyRole.Regular && spawn.EraIndex == 3);
            sawElite |= plan.Roster.Any(spawn => spawn.Role == EndlessEnemyRole.Elite && spawn.EraIndex == 3);
            sawBoss |= plan.Roster.Any(spawn => spawn.Role == EndlessEnemyRole.Boss && spawn.EraIndex == 3);
            session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        Assert.True(sawRegular, "E3 常规线必须出现 era3 内容行");
        Assert.True(sawElite, "E3 精英线必须出现 era3 内容行");
        Assert.True(sawBoss, "E3 Boss 线必须出现 era3 内容行（堡垒号节奏波）");
    }

    [Fact] // legal：T-154 缺省翻转——词缀节奏缺省=滚词缀档（Rolling；生产缺省=EndlessAffixRollPolicy.Default）
    public void Entry_defaults_to_the_rolling_affix_schedule()
    {
        EndlessSession session = EndlessEntry.Begin(Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));

        Assert.Equal(EndlessAffixTuning.Default, session.Run.Config.Affixes); // 量级仍=SP-7b 生产默认档
        Assert.Equal(10, session.Run.Config.BossEveryWaves);                   // 每 10 波 Boss 节奏
        Assert.NotNull(session.Run.Config.AffixRolls);                          // 节奏缺省=滚档（业主裁定）
        Assert.Equal(EndlessAffixRollPolicy.Default, session.Run.Config.AffixRolls);
        Assert.Equal(EndlessAffixSchedule.Rolling, session.AffixSchedule);      // 界面明示面=滚词缀表

        EndlessFixture.PlayWaves(session.Run, 40);
        EndlessWavePlan wave41 = session.Run.BeginWave();
        Assert.NotEmpty(wave41.Modifiers.ActiveKinds); // 41 起有词缀（滚档块内激活）
        Assert.Equal(
            EndlessAffixRolls.KindsAt(Seed, 41, EndlessAffixRollPolicy.Default),
            wave41.Modifiers.ActiveKinds); // 与滚档派生序列逐 kind 一致
    }

    [Fact] // legal：经典固定表回切——ClassicFixed 档=SP-7b 冻结激活表（41 迅捷/51 叠硬化，量级=Tuning）
    public void Classic_fixed_schedule_restores_the_sp7b_frozen_table()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { AffixSchedule = EndlessAffixSchedule.ClassicFixed },
            new LcgRng(Seed));

        Assert.Null(session.Run.Config.AffixRolls);                            // null=SP-7b 冻结激活表
        Assert.Equal(EndlessAffixSchedule.ClassicFixed, session.AffixSchedule); // 界面明示面=经典固定表
        EndlessFixture.PlayWaves(session.Run, 40);

        EndlessWavePlan wave41 = session.Run.BeginWave();
        Assert.Equal(new[] { EndlessAffixKind.Swift }, wave41.Modifiers.ActiveKinds); // 41 波起迅捷
        Assert.Equal(1.25, wave41.Modifiers.SpeedMultiplier, 12);                     // SP-7b +25%
        session.Run.CompleteWave();
        EndlessFixture.PlayWaves(session.Run, 9);

        EndlessWavePlan wave51 = session.Run.BeginWave();
        Assert.Equal(
            new[] { EndlessAffixKind.Swift, EndlessAffixKind.Harden },
            wave51.Modifiers.ActiveKinds.OrderBy(kind => kind));                       // 51 波叠加硬化
        Assert.Equal(1.5, wave51.Modifiers.HpMultiplier, 12);                          // SP-7b HP +50%
    }

    // illegal：T-154 档位守卫——ClassicFixed 与显式 AffixRolls 互斥；未定义档位拒绝
    [Fact]
    public void Affix_schedule_guards()
    {
        Assert.Throws<ArgumentException>(() => EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with
            {
                AffixSchedule = EndlessAffixSchedule.ClassicFixed,
                AffixRolls = EndlessAffixRollPolicy.Default,
            },
            new LcgRng(Seed)));
        Assert.Throws<ArgumentException>(() => EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { AffixSchedule = (EndlessAffixSchedule)99 },
            new LcgRng(Seed)));
    }

    [Fact] // legal：T-154 缺省链——PolicyTable 递交=混编渐升生产档；不递交=无表兜底；显式 MixPolicy 最优先
    public void Policy_table_drives_the_mix_default_chain()
    {
        EndlessPolicyTable table = new(
            new EndlessMixPolicy(41, 9.0, 1.0, rampEndWave: 81, endStartEraWeight: 3.0, endOtherEraWeight: 1.0),
            EndlessAffixRollPolicy.Default);
        EndlessSession fromTable = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { PolicyTable = table },
            new LcgRng(Seed));
        Assert.Equal(81, fromTable.Run.Config.MixPolicy!.RampEndWave); // 表渐升档生效
        Assert.Equal(3.0, fromTable.Run.Config.MixPolicy.EndStartEraWeight);

        EndlessSession noTable = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));
        Assert.Null(noTable.Run.Config.MixPolicy!.RampEndWave); // 无表兜底=固定 1:1 档
        Assert.Equal(EndlessMixPolicy.Default, noTable.Run.Config.MixPolicy);

        var explicitPolicy = new EndlessMixPolicy(21, 1.0, 1.0);
        EndlessSession explicitMix = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { MixPolicy = explicitPolicy, PolicyTable = table },
            new LcgRng(Seed));
        Assert.Equal(explicitPolicy, explicitMix.Run.Config.MixPolicy); // 显式 > 表

        EndlessSession rollingFromTable = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with
            {
                PolicyTable = table,
                AffixRolls = new EndlessAffixRollPolicy(5, 5, 1, allowConsecutiveRepeat: false, repeatIntensityGain: 1.0),
            },
            new LcgRng(Seed));
        Assert.Equal(5, rollingFromTable.Run.Config.AffixRolls!.RollEveryWaves); // 显式滚动策略 > 表节奏
    }

    [Fact] // legal：换档在会话语境照常可用且不换榜（入口→会话→HUD→结算一条线）
    public void Session_exposes_midrun_switches_and_the_hud_stays_queryable()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(1, EndlessParticipationMode.FullAutopilot), new LcgRng(Seed));
        EndlessFixture.PlayWaves(session.Run, 6);

        Assert.True(session.Run.SwitchParticipation(EndlessParticipationMode.Assisted, "takeover:manual-build"));
        EndlessParticipationSnapshot snapshot = session.Participation; // 会话级 HUD 查询通道

        Assert.Equal(EndlessParticipationMode.FullAutopilot, snapshot.LockedMode);
        Assert.Equal(EndlessParticipationMode.Assisted, snapshot.CurrentMode);
        Assert.Equal(6, snapshot.CurrentWave);
        Assert.Equal(1, snapshot.SwitchCount);
        Assert.False(snapshot.IsOver);

        session.Run.RecordAction(byAutopilot: false);
        EndlessSettlement settlement = session.Die();
        Assert.Equal(EndlessBoardKind.Autopilot, settlement.Board); // 记档整局
        Assert.True(session.Participation.IsOver);                  // 终局后 HUD 仍可查
    }

    // ------------------------------------------------ T-154 两档分账/零漂移矩阵 -----

    [Fact] // legal：分账纪律——同 seed 两档=不同运行身份（词缀节奏入身份哈希）；ClassicFixed≡翻转前缺省面（直配 null）哈希全等
    public void The_two_affix_schedules_settle_into_distinct_identities()
    {
        EndlessSession rolling = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));
        EndlessSession classic = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { AffixSchedule = EndlessAffixSchedule.ClassicFixed },
            new LcgRng(Seed));
        // 翻转前缺省的等价面：直配 EndlessRunConfig（AffixRolls=null）——ClassicFixed 必须与它逐点全等
        EndlessRun directNull = new(
            new EndlessRunConfig(
                Seed, 3, 0.9, Era3Shape.Base, Era3Shape.Begin, Era3Shape.Spacing, Era3Shape.Scaling, 10,
                rolling.Run.Config.Pool,
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                classic.Run.Config.StartEraBaseline, // 入口冻结的同一起始基准（era3 面）
                mixPolicy: EndlessMixPolicy.Default,
                affixRolls: null),
            new LcgRng(Seed));

        for (int wave = 1; wave <= 60; wave++)
        {
            EndlessWavePlan rollingPlan = rolling.Run.BeginWave();
            EndlessWavePlan classicPlan = classic.Run.BeginWave();
            EndlessWavePlan directPlan = directNull.BeginWave();
            Assert.Equal(classicPlan.Roster, directPlan.Roster);   // 零漂移：经典档名册=翻转前缺省面
            // 词缀面全等（ActiveKinds 为 List 引用成员——record 相等性不做序列比较，逐通道断言）
            Assert.Equal(classicPlan.Modifiers.ActiveKinds, directPlan.Modifiers.ActiveKinds);
            Assert.Equal(classicPlan.Modifiers.Stacks, directPlan.Modifiers.Stacks);
            Assert.Equal(classicPlan.Modifiers.SpeedMultiplier, directPlan.Modifiers.SpeedMultiplier, 12);
            Assert.Equal(classicPlan.Modifiers.HpMultiplier, directPlan.Modifiers.HpMultiplier, 12);
            Assert.Equal(classicPlan.Modifiers.SuicideRadiusMultiplier, directPlan.Modifiers.SuicideRadiusMultiplier, 12);
            rolling.Run.CompleteWave(rollingPlan.EliteCount, rollingPlan.BossCount);
            classic.Run.CompleteWave(classicPlan.EliteCount, classicPlan.BossCount);
            directNull.CompleteWave(directPlan.EliteCount, directPlan.BossCount);
            if (wave is 41 or 60)
            {
                Assert.Equal(directNull.ComputeStateHash(), classic.Run.ComputeStateHash()); // 零漂移哈希锚
                Assert.NotEqual(rolling.Run.ComputeStateHash(), classic.Run.ComputeStateHash()); // 两档分账
            }
        }

        // 两档词缀节奏确有分野：滚档块界滚换 vs 固定表逐档累加（51 波固定表=Swift+Harden 双 kind）
        Assert.Null(classic.Run.Config.AffixRolls);
        Assert.NotNull(rolling.Run.Config.AffixRolls);
    }

    [Fact] // legal：分数口径走实际档——Rolling 局=SP-7c+滚词缀加权（块级）；ClassicFixed 局=SP-7c+固定表加权
    public void Both_schedules_score_by_their_own_affix_bonus()
    {
        EndlessSettlement Settle(EndlessAffixSchedule schedule)
        {
            EndlessSession session = EndlessEntry.Begin(
                Request(3, EndlessParticipationMode.Manual) with { AffixSchedule = schedule },
                new LcgRng(Seed));
            for (int i = 0; i < 100; i++) // 100 波死亡（跨 41/51/61/71/81/91 词缀界）
            {
                EndlessWavePlan plan = session.Run.BeginWave();
                session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
            }

            return session.Die();
        }

        EndlessSettlement rolling = Settle(EndlessAffixSchedule.Rolling);
        long rollingExpected = EndlessRules.AffixBonusPoints(
            100, Seed, EndlessScoreWeights.Default, EndlessAffixRollPolicy.Default);
        Assert.Equal(rollingExpected, rolling.AffixBonusPoints);
        Assert.Equal((100 * 100L) + (rolling.EliteKills * 20L) + (rolling.BossKills * 200L) + rollingExpected,
            rolling.Score);

        EndlessSettlement classic = Settle(EndlessAffixSchedule.ClassicFixed);
        long classicExpected = EndlessRules.AffixBonusPoints(100, EndlessScoreWeights.Default);
        Assert.Equal(classicExpected, classic.AffixBonusPoints); // 固定表口径（41..50 +1 kind/波 …）
        Assert.Equal((100 * 100L) + (classic.EliteKills * 20L) + (classic.BossKills * 200L) + classicExpected,
            classic.Score);

        Assert.NotEqual(rolling.AffixBonusPoints, classic.AffixBonusPoints); // 两档各自成账
    }

    [Fact] // legal：老榜零漂移——已有榜行的 port 上两档新局各自落账，老行原样保留、两档不混排
    public void Existing_board_rows_stay_untouched_by_new_schedule_runs()
    {
        var port = new InMemorySavePort();
        EndlessSession oldRun = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual) with { AffixSchedule = EndlessAffixSchedule.ClassicFixed },
            new LcgRng(Seed + 1), port);
        EndlessFixture.PlayWaves(oldRun.Run, 55);
        EndlessSettlement oldRow = oldRun.Die();
        long oldScore = oldRow.Score;
        long oldWaves = oldRow.WavesSurvived;

        EndlessBoards before = EndlessBoardStore.Load(port);
        Assert.Single(before.Of(EndlessBoardKind.Manual));

        EndlessSession rolling = EndlessEntry.Begin(
            Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed + 2), port);
        EndlessFixture.PlayWaves(rolling.Run, 70);
        rolling.Die();

        EndlessBoards after = EndlessBoardStore.Load(port);
        Assert.Equal(2, after.Of(EndlessBoardKind.Manual).Count); // 同榜追加（榜别=参与档，与词缀档无关）
        EndlessScoreRecord survived = Assert.Single(
            after.Of(EndlessBoardKind.Manual),
            row => row.WavesSurvived == oldWaves); // 老行原样（波数/分数零漂移）
        Assert.Equal(oldScore, survived.Score);
    }
}
