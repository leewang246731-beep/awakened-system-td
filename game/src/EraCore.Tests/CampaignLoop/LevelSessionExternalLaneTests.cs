using EraCore.CampaignLoop;
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

// T-160b 引擎最小扩的执法面（REV-20261019-T160b §引擎最小扩）：①
// Simulation.CommandEnqueued 观察缝——外部泳道命令（AgentPipeline 执行器直投
// sim 的军令命令，不经 LevelSession.Enqueue）也必须进会话命令流
// （IssuedCommands），否则波界/退出快照的重放键漏掉这些建造，重进局与活体
// 局发散；② TryResume 的命令流种子化——重进会话的 IssuedCommands 此前为空
// （重放命令不回填），退出→重进→再退出的第二次快照会丢整段历史。两缝都
// 是纯观测/记账面（零 sim 状态、零哈希影响）。[C-12][F-001]
[Collection(CheckpointHeavyCollection.Name)]
public class LevelSessionExternalLaneTests : SqliteSavePortTestBase
{
    private const double Threat = 0.3; // Threat.Min 公式锚点（LevelSessionTests 同带）

    private static int TowerCount(LevelSession session) => session.Sim.Towers.Count;

    [Fact] // legal — 外部泳道命令（sim 直投）被观察缝记入会话命令流
    public void External_lane_command_is_recorded_into_the_issued_stream()
    {
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        var session = new LevelSession(config, new SimRng(LevelSeed), CreatePort());
        session.Machine.StartLevel();
        int commandsBefore = session.IssuedCommands.Count;

        // 外部泳道：军师执行器的等价投递（绕过 session.Enqueue，直投 sim 队列）。
        var external = new SimCommand(
            session.Sim.CurrentTick + 1, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\",\"cell\":{\"x\":9,\"y\":21}}");
        _ = session.Sim.Enqueue(external);

        Assert.Contains(external, session.IssuedCommands); // 观察缝记账
        Assert.Equal(commandsBefore, session.IssuedCommands.Count - 1); // 恰一条（无重复记账）
    }

    [Fact] // legal — 外部泳道命令参与退出快照：退出→重进无损（重放含军令建造）
    public void External_lane_command_survives_exit_and_resume_losslessly()
    {
        long exitTick = GraceTicks(Threat) + 600;
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        SqliteSavePort port = CreatePort();
        var session = new LevelSession(config, new SimRng(LevelSeed), port);
        session.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        session.RunFor(exitTick);
        int towersBeforeExternal = TowerCount(session);

        // 外部军令建造（执行器同款投递）+ 一段推进让其落地生效。落点=扫描
        // 得到的空 Plain 格（里程碑链占用外的首格——与地形抽签解耦）。
        (int buildX, int buildY)? free = FindFreePlainCell(session);
        Assert.NotNull(free);
        var external = new SimCommand(
            session.Sim.CurrentTick + 1, SimCommandKind.PlayerBuild,
            $"{{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\",\"cell\":{{\"x\":{free.Value.buildX},\"y\":{free.Value.buildY}}}}}");
        _ = session.Sim.Enqueue(external);
        session.RunFor(30);
        Assert.True(TowerCount(session) > towersBeforeExternal, "外部泳道建造未生效");
        Assert.Contains(external, session.IssuedCommands);

        long hashBeforeExit = session.Sim.ComputeStateHash();
        session.Pause();
        session.SaveAndExitToMenu();

        Assert.True(LevelSession.TryResume(config, new SimRng(LevelSeed), port, null, out LevelSession resumed));
        resumed.ResumePlaying();

        // 无损：哈希全等（重放键含外部泳道命令）+ 命令流种子化（含外部命令）。
        Assert.Equal(hashBeforeExit, resumed.Sim.ComputeStateHash());
        Assert.Equal(TowerCount(session), TowerCount(resumed));
        Assert.Contains(external, resumed.IssuedCommands);
    }

    [Fact] // legal — 退出→重进→再退出：重进会话的命令流种子化，第二次快照不丢历史
    public void Resumed_session_seeds_its_issued_stream_for_the_second_exit()
    {
        long firstExit = GraceTicks(Threat) + 600;
        var config = new LevelSessionConfig(LevelConfig(Threat), LoopSlotId, Threat, LevelSeed);
        SqliteSavePort port = CreatePort();
        var first = new LevelSession(config, new SimRng(LevelSeed), port);
        first.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            first.Enqueue(command);
        }

        first.RunFor(firstExit);
        int issuedBeforeExit = first.IssuedCommands.Count;
        long hashBeforeExit = first.Sim.ComputeStateHash();
        first.Pause();
        first.SaveAndExitToMenu();

        Assert.True(LevelSession.TryResume(config, new SimRng(LevelSeed), port, null, out LevelSession resumed));
        Assert.Equal(issuedBeforeExit, resumed.IssuedCommands.Count); // 种子化：重放命令回填命令流
        resumed.ResumePlaying();

        // 再推进+再退出：第二次退出快照必须携带完整历史（种子+新增）。
        resumed.RunFor(30);
        resumed.Pause();
        resumed.SaveAndExitToMenu();

        Assert.True(LevelSession.TryResume(config, new SimRng(LevelSeed), port, null, out LevelSession second));
        second.ResumePlaying();
        Assert.Equal(resumed.Sim.CurrentTick, second.Sim.CurrentTick);
        Assert.Equal(resumed.Sim.ComputeStateHash(), second.Sim.ComputeStateHash()); // 双重重进仍无损
        Assert.True(second.IssuedCommands.Count >= issuedBeforeExit);
    }

    /// <summary>首个既无建筑又是 Plain 的格（外部建造落点——与地形抽签解耦）.</summary>
    private static (int X, int Y)? FindFreePlainCell(LevelSession session)
    {
        for (int y = 0; y < session.Sim.World.Height; y++)
        {
            for (int x = 0; x < session.Sim.World.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (session.Sim.World.TerrainAt(cell) != EraCore.World.TerrainKind.Plain
                    || session.Sim.Buildings.Any(building => building.Cell == cell))
                {
                    continue;
                }

                return (x, y);
            }
        }

        return null;
    }
}
