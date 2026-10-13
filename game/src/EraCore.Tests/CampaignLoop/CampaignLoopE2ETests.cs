using EraCore.CampaignLoop;
using EraCore.Contracts.Sim;
using EraCore.Persistence;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.CampaignLoop;

// E2E 全循环（T-059 验收 08）：E1 一关从 Menu 到 Victory 全走通——程序驱动
// 玩家命令流（采集/加工/民夫/塔）→胜波达成→结算入库→返回 CampaignGrid→
// 选下一格——断言全链状态迁移与资源守恒。威胁度/胜波/波间隔全部由 Balance
// 公式与 seed 化网格推导（CS-4.4）；入 CheckpointHeavyCollection 串行。
// [C-12][F-001][F-005]
[Collection(CheckpointHeavyCollection.Name)]
public class CampaignLoopE2ETests : SqliteSavePortTestBase
{
    [Fact] // legal — 单局核心循环全链：选图→进关→采集→加工→建塔→守波→胜利→入库→返回选图→选下一格
    public void E1_full_loop_menu_to_victory_and_next_cell_selection()
    {
        // —— 选图（F-005）：主城在环上，取邻接敌营最少的可选关（seed 化布局）。
        CampaignState campaign = NewCampaign();
        CampaignGrid grid = campaign.Grid;
        LevelNodeInfo node = CheapestSelectableNode(grid);
        double woodBefore = campaign.Pool.AmountOf(Wood);

        // —— 进关（F-001）：Menu→InLevel，威胁度驱动关卡波系。
        var levelConfig = new LevelSessionConfig(
            LevelConfig(node.Threat), LoopSlotId, node.Threat, LevelSeed, StateHashInterval: 30); // 降频采样机制在链路上演练
        SqliteSavePort port = CreatePort();
        var session = new LevelSession(levelConfig, new SimRng(LevelSeed), port, campaign.Pool);
        Assert.Equal(GameFlowState.Menu, session.Machine.State);
        Assert.Equal(node.WinWaves, session.WinWaves);

        session.Machine.StartLevel();
        Assert.Equal(GameFlowState.InLevel, session.Machine.State);

        // —— 程序驱动的玩家命令流：人力→采集→加工→民夫×3→双塔（E1 里程碑链）。
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        // —— 守波到胜波达成且场清（胜波数=WinWaves(t)，公式推导的地平线只作上界）。
        session.RunFor(VictoryHorizonTicks(node.Threat));
        Assert.Equal(GameFlowState.Victory, session.Machine.State);

        // —— 胜利结算断言：胜波数/命令全应用/结算入库守恒。
        Assert.NotNull(session.Victory);
        VictoryOutcome outcome = session.Victory!;
        Assert.Equal(node.WinWaves, outcome.WinWaves);
        Assert.Equal(session.WinWaves, outcome.WinWaves);
        Assert.Equal(LevelSeed, outcome.Seed);
        Assert.Equal(session.Sim.AppliedCommandCount, session.IssuedCommands.Count); // 全链命令无一被拒
        Assert.True(outcome.Tick >= WinWaveStartTick(node.Threat), "胜利不得早于胜波起点");

        double lootAccepted = 0.0;
        double lootRefused = 0.0;
        foreach (ResourceGrant grant in outcome.Loot)
        {
            Assert.Equal(grant.Granted, grant.Accepted + grant.Refused, 9); // 守恒：授予=入库+拒收
            Assert.True(grant.Granted >= 0.0);
            lootAccepted += grant.Accepted;
            lootRefused += grant.Refused;
        }

        Assert.True(lootAccepted > 0.0, "满级资源入库应大于零（采集链全程运转）");
        Assert.Equal(0.0, lootRefused, 9); // 战役池远未满：零拒收
        Assert.Equal(woodBefore + lootAccepted, campaign.Pool.AmountOf(outcome.Loot[0].Resource), 9);
        double woodAfterVictory = campaign.Pool.AmountOf(Wood);

        // —— 返回 CampaignGrid（F-001）：Victory→Menu + 终局清退出快照。
        session.CompleteLevel();
        Assert.Equal(GameFlowState.Menu, session.Machine.State);
        Assert.Equal(
            new[] { GameFlowTransitionKind.StartLevel, GameFlowTransitionKind.Win, GameFlowTransitionKind.CompleteLevel },
            session.Machine.History.Select(transition => transition.Kind));
        session.ClearExitSnapshot();
        Assert.False(LevelSession.TryResume(levelConfig, new SimRng(LevelSeed), port, null, out LevelSession stale));
        Assert.Equal(GameFlowState.Menu, stale.Machine.State);

        // —— 占领入库（F-005）：奖励=每胜波×胜波数，入池守恒。
        OccupyOutcome occupy = grid.Occupy(node.Cell.X, node.Cell.Y);
        Assert.Equal(node, occupy.Node);
        ResourceGrant reward = Assert.Single(occupy.Rewards);
        Assert.Equal(RewardPerWinWave * node.WinWaves, reward.Granted, 9);
        Assert.Equal(reward.Granted, reward.Accepted + reward.Refused, 9);
        Assert.Equal(woodAfterVictory + reward.Accepted, campaign.Pool.AmountOf(Wood), 9);
        Assert.True(grid.IsOccupied(node.Cell.X, node.Cell.Y));

        // —— 选下一格（F-005）：新占领格邻域可选，第二关可进可玩可退出可重进。
        IReadOnlyList<CampaignCell> nextCells = grid.SelectableCells();
        Assert.Contains(nextCells, candidate =>
            Math.Abs(candidate.X - node.Cell.X) + Math.Abs(candidate.Y - node.Cell.Y) == 1);
        CampaignCell nextCell = nextCells[0];
        LevelNodeInfo nextNode = grid.NodeInfo(nextCell.X, nextCell.Y);

        var secondConfig = new LevelSessionConfig(
            LevelConfig(nextNode.Threat), LoopSlotId + ".m02", nextNode.Threat, LevelSeed + 1);
        var second = new LevelSession(secondConfig, new SimRng(LevelSeed + 1), port, campaign.Pool);
        second.Machine.StartLevel();
        second.RunFor(200);
        Assert.Equal(GameFlowState.InLevel, second.Machine.State);
        Assert.True(second.HashLedger.Count >= 6); // 200 tick ÷ 间隔 30 的采样下界

        second.Pause();
        long hashBeforeExit = second.Sim.ComputeStateHash();
        second.SaveAndExitToMenu();
        campaign.SavePoolTo(port);
        Assert.Equal(GameFlowState.Menu, second.Machine.State);

        Assert.True(LevelSession.TryResume(secondConfig, new SimRng(LevelSeed + 1), port, null, out LevelSession resumedSecond));
        Assert.Equal(GameFlowState.Paused, resumedSecond.Machine.State);
        Assert.Equal(hashBeforeExit, resumedSecond.Sim.ComputeStateHash()); // 第二关重进同样无损
    }
}
