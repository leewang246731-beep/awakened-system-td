using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-123][T-118] 三榜分记互不混排（验收核心）：局面参与状态机
// [纯手动/军师辅助/全托管]——开局档随配置冻结，中途换档不换榜
// （罚则当波、记档整局——T-081「当波降级」语义对齐）；3×3 混排矩阵
// （三种开局档 × 三榜，对角线外一律无记录）；HUD 实时查询快照；运行身份
// 哈希含锁档与换档流。
public class EndlessParticipationTests
{
    // ------------------------------------------------ 3×3 混排矩阵 ---

    /// <summary>
    /// 9 行矩阵：(开局档, 榜) → 该榜是否应出现这条成绩。对角线=true，
    /// 其余 6 格=false——互不混排的负向断言主体。每种开局档配一段
    /// 「与锁档最大冲突」的动作流（手动档全程托管动作/辅助档五五开/
    /// 托管档全程手动动作），证明占比冲突也搬不动榜别。
    /// </summary>
    public static TheoryData<EndlessParticipationMode, EndlessBoardKind, bool> MatrixRows
    {
        get
        {
            var rows = new TheoryData<EndlessParticipationMode, EndlessBoardKind, bool>();
            foreach (EndlessParticipationMode mode in new[]
                     {
                         EndlessParticipationMode.Manual,
                         EndlessParticipationMode.Assisted,
                         EndlessParticipationMode.FullAutopilot,
                     })
            {
                foreach (EndlessBoardKind board in new[]
                         {
                             EndlessBoardKind.Manual,
                             EndlessBoardKind.Assisted,
                             EndlessBoardKind.Autopilot,
                         })
                {
                    rows.Add(mode, board, EndlessParticipation.BoardOf(mode) == board);
                }
            }

            return rows;
        }
    }

    [Theory] // legal：3×3 矩阵——开局档的成绩只落对角榜；其余六格零记录（互不混排）
    [MemberData(nameof(MatrixRows))]
    public void Boards_never_mix_runs_across_the_matrix(
        EndlessParticipationMode locked,
        EndlessBoardKind board,
        bool expectedPresent)
    {
        var port = new InMemorySavePort();
        var run = new EndlessRun(
            EndlessFixture.Config(participation: locked),
            new LcgRng(EndlessFixture.Seed),
            port);
        EndlessFixture.PlayWaves(run, 12);

        // 与锁档最大冲突的动作流：手动档喂全托管流、托管档喂全手动流——占比
        // 审计列必然与锁档冲突，若路由仍按占比，矩阵非对角格就会漏进成绩。
        bool byAutopilot = locked switch
        {
            EndlessParticipationMode.Manual => true,          // share 100% → 占比式=托管榜（锁档=手动榜）
            EndlessParticipationMode.Assisted => false,       // share 0% → 占比式=手动榜（锁档=辅助榜）
            EndlessParticipationMode.FullAutopilot => false,  // share 0% → 占比式=手动榜（锁档=托管榜）
            _ => throw new ArgumentOutOfRangeException(nameof(locked)),
        };
        for (int i = 0; i < 30; i++)
        {
            run.RecordAction(byAutopilot);
        }

        EndlessSettlement settlement = run.Defeat();
        EndlessBoards boards = EndlessBoardStore.Load(port);

        Assert.Equal(locked, settlement.LockedParticipation);
        Assert.Equal(EndlessParticipation.BoardOf(locked), settlement.Board);
        if (expectedPresent)
        {
            EndlessScoreRecord stored = Assert.Single(boards.Of(board));
            Assert.Equal(settlement.Score, stored.Score);
            Assert.Equal(settlement.Record.Arrival, stored.Arrival);
            Assert.Equal(board, stored.Board);
        }
        else
        {
            Assert.Empty(boards.Of(board)); // 负向断言：其他两榜绝无本局成绩
        }

        // 三榜合计恰一条（本局只入一榜）且其余榜行数为 0 的组合恒成立
        int total = boards.Of(EndlessBoardKind.Manual).Count
            + boards.Of(EndlessBoardKind.Assisted).Count
            + boards.Of(EndlessBoardKind.Autopilot).Count;
        Assert.Equal(1, total);
    }

    [Fact] // legal：三局三榜各一条——同一存档连续三档开局，三榜各收恰一条且到达序全局递增
    public void Three_locked_runs_land_on_three_separate_boards()
    {
        var port = new InMemorySavePort();
        (EndlessParticipationMode mode, int waves)[] runs =
        {
            (EndlessParticipationMode.Manual, 10),
            (EndlessParticipationMode.Assisted, 20),
            (EndlessParticipationMode.FullAutopilot, 30),
        };

        foreach ((EndlessParticipationMode mode, int waves) in runs)
        {
            var run = new EndlessRun(
                EndlessFixture.Config(participation: mode),
                new LcgRng(EndlessFixture.Seed + waves),
                port);
            EndlessFixture.PlayWaves(run, waves);
            run.Defeat();
        }

        EndlessBoards boards = EndlessBoardStore.Load(port);
        Assert.Single(boards.Of(EndlessBoardKind.Manual));
        Assert.Single(boards.Of(EndlessBoardKind.Assisted));
        Assert.Single(boards.Of(EndlessBoardKind.Autopilot));
        Assert.Equal(3, boards.Arrival);

        // 排序视图（查询口径）：各榜 Score DESC+Arrival ASC；三榜互不混排下
        // 每榜第一条=本榜唯一一条。
        foreach (EndlessBoardKind board in new[]
                     { EndlessBoardKind.Manual, EndlessBoardKind.Assisted, EndlessBoardKind.Autopilot })
        {
            Assert.Equal(boards.Of(board), boards.Ranking(board));
        }
    }

    // ------------------------------------------------ 换档不换榜 -----

    [Fact] // legal：中途换档不换榜——托管开局第 5 波接管降级到辅助、再回手动，榜别/开局档恒定
    public void Midrun_switches_never_change_the_board()
    {
        var run = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(run, 5);

        Assert.True(run.SwitchParticipation(EndlessParticipationMode.Assisted, "takeover:manual-build"));
        EndlessFixture.PlayWaves(run, 5);
        Assert.True(run.SwitchParticipation(EndlessParticipationMode.Manual, "takeover:full-manual"));
        EndlessFixture.PlayWaves(run, 5);
        for (int i = 0; i < 10; i++)
        {
            run.RecordAction(byAutopilot: false); // 玩家接管后的手动动作流
        }

        Assert.Equal(EndlessBoardKind.Autopilot, run.Board); // 记档整局：榜别不随换档漂移
        Assert.Equal(EndlessParticipationMode.FullAutopilot, run.LockedParticipation);
        Assert.Equal(EndlessParticipationMode.Manual, run.CurrentParticipation);
        Assert.Equal(2, run.ParticipationSwitches.Count);
        Assert.Equal(5, run.ParticipationSwitches[0].Wave);
        Assert.Equal(EndlessParticipationMode.FullAutopilot, run.ParticipationSwitches[0].From);
        Assert.Equal(EndlessParticipationMode.Assisted, run.ParticipationSwitches[0].To);
        Assert.Equal(10, run.ParticipationSwitches[1].Wave);

        EndlessSettlement settlement = run.Defeat();
        Assert.Equal(EndlessBoardKind.Autopilot, settlement.Board); // 罚则当波、记档整局（T-081 对齐）
        Assert.Equal(EndlessParticipationMode.FullAutopilot, settlement.LockedParticipation);
        Assert.Equal(EndlessParticipationMode.Manual, settlement.CurrentParticipation);
        Assert.Equal(2, settlement.SwitchCount);
        Assert.Equal(EndlessBoardKind.Manual, settlement.ShareImpliedBoard); // 占比审计列如实=手动
    }

    [Fact] // legal：反向换档同样不换榜——手动开局中途全托管化，仍入手动榜；sink 不收
    public void Manual_locked_run_engaging_autopilot_stays_on_the_manual_board()
    {
        var sink = new RecordingScoreSink();
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed), scoreSink: sink);
        EndlessFixture.PlayWaves(run, 5);
        Assert.True(run.SwitchParticipation(EndlessParticipationMode.FullAutopilot, "engage:player-opt-in"));
        EndlessFixture.PlayWaves(run, 5);
        for (int i = 0; i < 40; i++)
        {
            run.RecordAction(byAutopilot: true); // 换档后全程托管动作流
        }

        EndlessSettlement settlement = run.Defeat();

        Assert.Equal(EndlessBoardKind.Manual, settlement.Board);
        Assert.Equal(EndlessBoardKind.Autopilot, settlement.ShareImpliedBoard); // 占比=100% 托管
        Assert.Empty(sink.Entries); // 非全托管开局档不进 T-081 隔离通道
    }

    // boundary：同档调用是 no-op（返回 false、不记审计行）；终局后换档拒绝
    [Fact]
    public void Switch_guards_reject_noop_and_post_terminal_switches()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        Assert.False(run.SwitchParticipation(EndlessParticipationMode.Manual, "noop"));
        Assert.Empty(run.ParticipationSwitches);

        EndlessFixture.PlayWaves(run, 3);
        run.Defeat();
        Assert.Throws<InvalidOperationException>(
            () => run.SwitchParticipation(EndlessParticipationMode.Assisted, "late"));
    }

    // illegal：未定义档位 / 空原因拒绝（配置侧与换档侧同守卫）
    [Fact]
    public void Undefined_mode_and_blank_reason_are_rejected()
    {
        const EndlessParticipationMode undefined = (EndlessParticipationMode)99;
        Assert.Throws<ArgumentException>(() => EndlessFixture.Config(participation: undefined));

        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        Assert.Throws<ArgumentException>(
            () => run.SwitchParticipation(EndlessParticipationMode.Assisted, ""));
        Assert.Throws<ArgumentException>(
            () => run.SwitchParticipation(undefined, "x"));
    }

    // ------------------------------------------------ HUD 实时查询 --

    [Fact] // legal：HUD 快照实时演进——开局态/动作累计态/换档态/终局态逐点可查
    public void Participation_snapshot_tracks_the_run_in_real_time()
    {
        var run = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.Assisted),
            new LcgRng(EndlessFixture.Seed));

        EndlessParticipationSnapshot initial = run.Participation;
        Assert.Equal(EndlessParticipationMode.Assisted, initial.LockedMode);
        Assert.Equal(EndlessParticipationMode.Assisted, initial.CurrentMode);
        Assert.Equal(0, initial.CurrentWave);
        Assert.Equal(0, initial.ActionsTotal);
        Assert.Equal(0, initial.AutopilotActions);
        Assert.Equal(0.0, initial.AutopilotShare, 12);
        Assert.Equal(0, initial.SwitchCount);
        Assert.False(initial.AdvisorParticipated); // T-081 参与位：军师尚未执行过动作
        Assert.False(initial.IsOver);

        EndlessFixture.PlayWaves(run, 4);
        run.RecordAction(byAutopilot: false);
        run.RecordAction(byAutopilot: true, pointsSpentByAutopilot: 2); // 军师令消耗>0 的参与位
        run.RecordAction(byAutopilot: true);

        EndlessParticipationSnapshot assisted = run.Participation;
        Assert.Equal(4, assisted.CurrentWave);
        Assert.Equal(3, assisted.ActionsTotal);
        Assert.Equal(2, assisted.AutopilotActions);
        Assert.Equal(2.0 / 3.0, assisted.AutopilotShare, 12);
        Assert.True(assisted.AdvisorParticipated);

        run.SwitchParticipation(EndlessParticipationMode.Manual, "takeover:manual-order");
        EndlessParticipationSnapshot switched = run.Participation;
        Assert.Equal(EndlessParticipationMode.Assisted, switched.LockedMode);   // 锁档不动
        Assert.Equal(EndlessParticipationMode.Manual, switched.CurrentMode);   // 当前档跟进
        Assert.Equal(1, switched.SwitchCount);

        run.Defeat();
        EndlessParticipationSnapshot over = run.Participation;
        Assert.True(over.IsOver);
        Assert.Equal(EndlessParticipationMode.Assisted, over.LockedMode); // 终局后快照仍可读
    }

    // boundary：参与位=军师执行过动作（T-081 口径——按动作数，不按军师令消耗：
    // 失败动作 0 点消耗但已参与）
    [Fact]
    public void Advisor_participation_counts_actions_not_points()
    {
        var run = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        run.RecordAction(byAutopilot: true, pointsSpentByAutopilot: 0); // 0 点消耗（失败动作口径）
        Assert.True(run.Participation.AdvisorParticipated);

        EndlessSettlement settlement = run.Defeat();
        Assert.True(settlement.AdvisorParticipated);
        Assert.Equal(1, settlement.AutopilotActions);
    }

    // ------------------------------------------------ 运行身份哈希 --

    [Fact] // legal：锁档与换档流参与运行身份——同 seed 异档/异换档流哈希必异
    public void Lock_and_switch_streams_participate_in_the_run_hash()
    {
        var manual = new EndlessRun(EndlessFixture.Config(), new LcgRng(EndlessFixture.Seed));
        var autopilot = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(manual, 10);
        EndlessFixture.PlayWaves(autopilot, 10);
        Assert.NotEqual(manual.ComputeStateHash(), autopilot.ComputeStateHash()); // 开局档=身份

        var switched = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(switched, 5);
        switched.SwitchParticipation(EndlessParticipationMode.Assisted, "takeover:manual-build");
        EndlessFixture.PlayWaves(switched, 5);
        Assert.Equal(autopilot.WavesSurvived, switched.WavesSurvived);
        Assert.NotEqual(autopilot.ComputeStateHash(), switched.ComputeStateHash()); // 换档流=身份

        var replay = new EndlessRun(
            EndlessFixture.Config(participation: EndlessParticipationMode.FullAutopilot),
            new LcgRng(EndlessFixture.Seed));
        EndlessFixture.PlayWaves(replay, 5);
        replay.SwitchParticipation(EndlessParticipationMode.Assisted, "takeover:manual-build");
        EndlessFixture.PlayWaves(replay, 5);
        Assert.Equal(switched.ComputeStateHash(), replay.ComputeStateHash()); // 同换档流=同哈希
    }

    // ------------------------------------------------ 档→榜映射 -----

    [Fact] // legal：开局档→榜别→通道 id 三段唯一映射；三通道两两互异（分记的物质基础）
    public void BoardOf_maps_the_three_modes_one_to_one()
    {
        Assert.Equal(EndlessBoardKind.Manual, EndlessParticipation.BoardOf(EndlessParticipationMode.Manual));
        Assert.Equal(EndlessBoardKind.Assisted, EndlessParticipation.BoardOf(EndlessParticipationMode.Assisted));
        Assert.Equal(EndlessBoardKind.Autopilot, EndlessParticipation.BoardOf(EndlessParticipationMode.FullAutopilot));

        string[] channels =
        {
            EndlessBoardChannels.Of(EndlessBoardKind.Manual),
            EndlessBoardChannels.Of(EndlessBoardKind.Assisted),
            EndlessBoardChannels.Of(EndlessBoardKind.Autopilot),
        };
        Assert.Equal(3, channels.Distinct(StringComparer.Ordinal).Count()); // 三榜三通道零混同
    }

    // illegal：档→榜映射拒绝未定义档
    [Fact]
    public void BoardOf_rejects_undefined_modes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessParticipation.BoardOf((EndlessParticipationMode)99));
        Assert.False(EndlessParticipation.IsDefined((EndlessParticipationMode)(-1)));
    }
}
