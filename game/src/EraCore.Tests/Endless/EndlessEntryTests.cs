using EraCore.Agent.Autopilot;
using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Persistence;
using EraCore.Tests.Levels;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][F-123][T-118] 无尽入口正式化：从战役界面语境组装任选时代的无尽
// 会话（EndlessEntry.Begin——最小接入点裁定：战役选图/菜单语境经宿主调用，
// 无尽不进 LevelSession/检查点域）；死亡终局→记分→返回的最小会话流
// （Running→Settled→Returned）；E3 时代无尽可用（era3 现四关内容集真数据
// 混编池+词缀照 SP-7 生产默认档）；同 seed 确定性。IO 由测试自持
// （HandcraftedLevelContent 仓库相对读取 game/content，RL9）。
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

    [Fact] // legal：词缀照 SP-7——入口默认档=EndlessAffixTuning.Default（生产 SP-7b 表），41 波起迅捷、51 波硬化
    public void Entry_defaults_to_the_sp7_production_affix_tier()
    {
        EndlessSession session = EndlessEntry.Begin(Request(3, EndlessParticipationMode.Manual), new LcgRng(Seed));

        Assert.Equal(EndlessAffixTuning.Default, session.Run.Config.Affixes); // SP-7b 生产默认档
        Assert.Equal(10, session.Run.Config.BossEveryWaves);                   // 每 10 波 Boss/词缀节奏
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
}
