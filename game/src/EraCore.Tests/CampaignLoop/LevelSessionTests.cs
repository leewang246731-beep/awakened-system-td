using EraCore.CampaignLoop;
using EraCore.Checkpoint;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.CampaignLoop;

// 一关会话（T-059）：中途暂停→退出→重进状态无损（StateHash+状态机史+
// 战役池逐字段）、哈希采样降频机制（spike1-pit#2）、失败分档出口（T-057
// seam）与非法操作拦截。运行时入 CheckpointHeavyCollection 串行（与
// spike-1 性能回归线互不叠 CPU——T-057 先例）。[C-12][F-001]
[Collection(CheckpointHeavyCollection.Name)]
public class LevelSessionTests : SqliteSavePortTestBase
{
    private const double Threat = 0.3; // Threat.Min 公式锚点（v3.2 §7 带下沿）

    // ---------------------------------------------------------- legal

    [Fact] // legal — 中途存读档无损：波中暂停退出，重进后哈希/状态机/资源池逐字段相等
    public void Mid_wave_exit_and_reentry_is_lossless()
    {
        // 退出点=第 1 波入场后 600 tick（波中、场上有敌、经济链运转中）。
        long exitTick = GraceTicks(Threat) + 600;
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        SqliteSavePort port = CreatePort();

        // 战役池带两资源余量（逐字段断言素材）。
        CampaignState campaign = NewCampaign();
        Assert.Equal(500.0, campaign.Pool.Deposit(Wood, 500.0), 9);
        Assert.Equal(120.0, campaign.Pool.Deposit(new ResourceKey("era1:res-stone"), 120.0), 9);

        var session = new LevelSession(config, new SimRng(LevelSeed), port, campaign.Pool);
        session.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        session.RunFor(exitTick);
        Assert.Equal(GameFlowState.InLevel, session.Machine.State);

        // —— 中途暂停→退出（level 域写 #exit 快照 + campaign 域写池）
        session.Pause();
        long hashBeforeExit = session.Sim.ComputeStateHash();
        IReadOnlyList<GameFlowTransition> historyBeforeExit = session.Machine.History.ToArray();
        session.SaveAndExitToMenu();
        campaign.SavePoolTo(port);
        Assert.Equal(GameFlowState.Menu, session.Machine.State);

        // —— 重进：TryResume 恢复
        CampaignState reentered = NewCampaign();
        Assert.True(LevelSession.TryResume(config, new SimRng(LevelSeed), port, reentered.Pool, out LevelSession resumed));

        // ① Simulation StateHash 无损（确定性重放=恢复格式）
        Assert.Equal(exitTick, resumed.Sim.CurrentTick);
        Assert.Equal(hashBeforeExit, resumed.Sim.ComputeStateHash());

        // ② GameStateMachine 态无损（Paused）+迁移史逐条相等
        Assert.Equal(GameFlowState.Paused, resumed.Machine.State);
        Assert.Equal(historyBeforeExit, resumed.Machine.History);

        // ③ 资源池逐字段无损（campaign 域 roundtrip 后重灌）
        foreach (ResourceGrant deposit in reentered.LoadPoolFrom(port))
        {
            Assert.Equal(0.0, deposit.Refused, 9); // 同容量同余量：全额重灌
        }

        Assert.Equal(campaign.Pool.WarehouseLevel, reentered.Pool.WarehouseLevel);
        Assert.Equal(campaign.Pool.Capacity, reentered.Pool.Capacity, 9);
        foreach (ResourceKey resource in campaign.Pool.Resources)
        {
            Assert.Equal(campaign.Pool.AmountOf(resource), reentered.Pool.AmountOf(resource), 9);
        }

        // —— 续跑与从未中断的孪生逐采样哈希相等（间隔 1 全 tick 采样）
        resumed.ResumePlaying();
        resumed.RunFor(1_500);
        var twinConfig = config with { LevelSlotId = LoopSlotId + ".twin" };
        var twin = new LevelSession(twinConfig, new SimRng(LevelSeed), CreatePort());
        twin.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            twin.Enqueue(command);
        }

        twin.RunFor(exitTick + 1_500);

        // 恢复会话的采样只从重进起记（重放段不经会话步进），孪生含全程——
        // 对齐公共区间逐采样比较。
        var twinHashByTick = new Dictionary<long, long>();
        foreach ((long tick, long hash) in twin.HashLedger)
        {
            twinHashByTick[tick] = hash;
        }

        Assert.Equal(1_500, resumed.HashLedger.Count);
        Assert.Equal(exitTick + 1_500, twinHashByTick.Count);
        foreach ((long tick, long hash) in resumed.HashLedger)
        {
            Assert.Equal(twinHashByTick[tick], hash); // (tick, hash) 逐采样相等
        }

        Assert.Equal(twin.Sim.ComputeStateHash(), resumed.Sim.ComputeStateHash());
    }

    [Fact] // legal — 波界存档在会话内自动发生（Checkpoint seam 接线验证）
    public void Session_observes_wave_boundaries_into_the_store()
    {
        double threat = Threat;
        // 跑过第 1 波边界（宽限+一个间隔）即应有一条波界代。
        long horizon = GraceTicks(threat) + IntervalTicks(threat) + 60;
        var config = new LevelSessionConfig(LevelConfig(threat), LoopSlotId, threat, LevelSeed);
        var session = new LevelSession(config, new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();
        session.RunFor(horizon);

        Assert.True(session.Checkpoints.TryRestoreLatestBoundary(out RestoredWaveSnapshot boundary));
        Assert.Equal(1, boundary.Wave);
        Assert.Equal(GraceTicks(threat) + IntervalTicks(threat), boundary.Tick);
        Assert.Equal(LevelSeed, boundary.Seed);
    }

    [Fact] // legal — 无检查点的普通关失败：从头重开（T-057 分档出口），机器入 Defeat→Menu
    public void Declare_defeat_runs_the_normal_failure_tier()
    {
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        var session = new LevelSession(config, new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        session.RunFor(GraceTicks(Threat) + 30); // 未跨越检查点波（10）——失败=从头重开
        NormalLevelFailureOutcome outcome = session.DeclareDefeat(firstDefeat: true);

        Assert.Equal(FailureScenarioKind.NormalLevel, session.FailureTier);
        Assert.Equal(0, outcome.RestoredFromWave);
        Assert.Equal(0, outcome.RestoredAtTick);
        Assert.Equal(GameFlowState.Defeat, session.Machine.State);
        session.AcknowledgeDefeat();
        Assert.Equal(GameFlowState.Menu, session.Machine.State);
    }

    // ---------------------------------------------------------- boundary

    [Fact] // boundary — 哈希采样机制（spike1-pit#2）：默认每 tick；间隔 N 只在倍数 tick 采样
    public void Hash_ledger_sampling_cadence_is_configurable()
    {
        var sparse = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed, StateHashInterval: 30);
        var session = new LevelSession(sparse, new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();
        session.RunFor(100);

        Assert.Equal(new[] { 30L, 60L, 90L }, session.HashLedger.Select(sample => sample.Tick));
        Assert.All(session.HashLedger, sample => Assert.NotEqual(0L, sample.Hash));

        // 默认 1：逐 tick 采样（金样与无损断言依赖的默认行为不变）。
        var dense = new LevelSession(
            new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed),
            new SimRng(LevelSeed), CreatePort());
        dense.Machine.StartLevel();
        dense.RunFor(5);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, dense.HashLedger.Select(sample => sample.Tick));
    }

    [Fact] // boundary — 无退出快照：TryResume 返回假且给出全新 Menu 会话
    public void Resume_without_exit_snapshot_starts_fresh()
    {
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        Assert.False(LevelSession.TryResume(config, new SimRng(LevelSeed), CreatePort(), null, out LevelSession fresh));
        Assert.Equal(GameFlowState.Menu, fresh.Machine.State);
        Assert.Equal(0, fresh.Sim.CurrentTick);
    }

    [Fact] // boundary — 终局清墓碑：ClearExitSnapshot 后不可再恢复
    public void Cleared_exit_snapshot_is_not_resumable()
    {
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        SqliteSavePort port = CreatePort();
        var session = new LevelSession(config, new SimRng(LevelSeed), port);
        session.Machine.StartLevel();
        session.RunFor(60);
        session.Pause();
        session.SaveAndExitToMenu();
        Assert.True(LevelSession.TryResume(config, new SimRng(LevelSeed), port, null, out _));

        session.ClearExitSnapshot();
        Assert.False(LevelSession.TryResume(config, new SimRng(LevelSeed), port, null, out LevelSession fresh));
        Assert.Equal(GameFlowState.Menu, fresh.Machine.State);
    }

    // ---------------------------------------------------------- illegal

    [Theory] // illegal — 非法操作确实被拒（具体异常断言）
    [InlineData("run-from-menu")]
    [InlineData("run-when-paused")]
    [InlineData("run-after-victory")]
    [InlineData("enqueue-when-paused")]
    [InlineData("exit-from-inlevel")]
    [InlineData("defeat-when-paused")]
    public void Illegal_session_operations_are_rejected(string scenario)
    {
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        var session = new LevelSession(config, new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();

        switch (scenario)
        {
            case "run-from-menu":
                var idle = new LevelSession(config, new SimRng(LevelSeed), CreatePort());
                Assert.Throws<InvalidOperationException>(() => idle.RunFor(1));
                break;
            case "run-when-paused":
                session.Pause();
                Assert.Throws<InvalidOperationException>(() => session.RunFor(1));
                break;
            case "run-after-victory":
                session.RunFor(60);
                session.Machine.Win();
                Assert.Throws<InvalidOperationException>(() => session.RunFor(1));
                break;
            case "enqueue-when-paused":
                session.Pause();
                Assert.Throws<InvalidOperationException>(
                    () => session.Enqueue(SimE1Fixture.MilestoneCommands()[0]));
                break;
            case "exit-from-inlevel":
                Assert.Throws<InvalidOperationException>(session.SaveAndExitToMenu);
                break;
            case "defeat-when-paused":
                session.Pause();
                Assert.Throws<InvalidOperationException>(() => session.DeclareDefeat(true));
                break;
        }
    }

    [Fact] // illegal — 构造参数越界：间隔 0/负、空槽位 id、负威胁、负 tick 数
    public void Constructor_and_run_reject_invalid_arguments()
    {
        Assert.Throws<ArgumentException>(() => new LevelSession(
            new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed, StateHashInterval: 0),
            new SimRng(LevelSeed), CreatePort()));
        Assert.Throws<ArgumentException>(() => new LevelSession(
            new LevelSessionConfig(LevelConfig(Threat), "  ", Threat, LevelSeed),
            new SimRng(LevelSeed), CreatePort()));
        Assert.Throws<ArgumentException>(() => new LevelSession(
            new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, -0.1, LevelSeed),
            new SimRng(LevelSeed), CreatePort()));

        var session = new LevelSession(
            new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed),
            new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.RunFor(-1));
    }
}
