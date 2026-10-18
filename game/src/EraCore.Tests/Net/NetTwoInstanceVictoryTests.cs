using EraCore.CampaignLoop;
using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Endless;
using EraCore.Tests.Sim;
using EraNet;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.Net;

// [F-161] T-139 集成冒烟（08 验收词「两实例联机跑一局到 Victory」的 headless
// 形态）：两个进程内实例——宿主（权威 LevelSession：波系/波界检查点/胜利谓词
// 全走生产 CampaignLoop）+客户端（镜像 Simulation 消费广播流）——经内存回环
// 联机跑完整一关：里程碑命令流由客户端按原始 tick 节奏上行（盖章后=原
// IssuedAtTick，与 CampaignLoopE2ETests 同经济节拍），断言：①宿主 Victory
// 结算（胜波数全等）；②两端同 tick 同哈希（终局对账）；③摘要逐行全中
// （每 6 tick 一行零跳过零偏差）；④命令流全应用零拒收。入
// CheckpointHeavyCollection 串行（全关运行与金样套件互叠会挤占性能预算）。
// [F-001][F-161]
[Collection(CheckpointHeavyCollection.Name)]
public class NetTwoInstanceVictoryTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public NetTwoInstanceVictoryTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact] // legal — 两实例内存联机跑一局到 Victory：胜利结算+哈希对账全等
    public void Two_instances_over_loopback_run_one_level_to_victory_with_hash_parity()
    {
        // —— 关卡选择：与 CampaignLoopE2ETests 同口径（邻接敌营最少的可选关）。
        CampaignState campaign = NewCampaign();
        LevelNodeInfo node = CheapestSelectableNode(campaign.Grid);
        SimulationConfig config = LevelConfig(node.Threat);
        var port = new InMemorySavePort();

        // —— 实例一（宿主）：生产 LevelSession 权威驱动。
        var session = new LevelSession(
            new LevelSessionConfig(config, "level.e1.net01", node.Threat, LevelSeed, StateHashInterval: 30),
            new SimRng(LevelSeed),
            port);
        session.Machine.StartLevel();

        // —— 实例二（客户端）：同 config 同 seed 镜像。
        var mirror = new Simulation(config, new SimRng(LevelSeed), LevelSeed);

        // —— 联机：内存回环（零端口）。
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(session.Sim, mesh.Host, mesh.Host, mesh.Host);
        var client = new NetGameClient(mirror, endpoint, endpoint, endpoint);

        // —— 里程碑命令流（客户端=玩家一的输入面；宿主 listen 同路）。
        var pending = new Queue<SimCommand>(SimE1Fixture.MilestoneCommands());
        long horizon = VictoryHorizonTicks(node.Threat);

        for (long frame = 0; frame < horizon && session.Machine.State == GameFlowState.InLevel; frame++)
        {
            // 同帧往返回环：CurrentTick+1 == 目标 tick 时上行，宿主盖章后 IssuedAtTick
            // 与单机基准逐 tick 相同（经济节拍不变——胜利谓词的前提）。
            while (pending.Count > 0 && host.Sim.CurrentTick + 1 >= pending.Peek().IssuedAtTick)
            {
                client.SubmitCommand(pending.Dequeue());
            }

            mesh.Deliver();          // 客户端上行落地宿主收件箱
            host.PumpIncoming();     // 盖章→权威入队→广播（本帧将应用的命令先行广播）
            session.RunFor(1);       // 权威步进（LevelSession：波系/检查点/胜利谓词）
            host.PublishDueDigest(); // 到期摘要（6 tick 网格）
            mesh.Deliver();          // 广播+摘要落地客户端
            client.PumpIncoming();   // 镜像按广播序入队
            client.StepMirror();     // 镜像同 tick 应用+对账

            Assert.Empty(client.Divergences); // 逐帧红：一偏即停（偏差留 tick 现场更可诊断）
        }

        // —— ① 宿主胜利结算。
        Assert.Equal(GameFlowState.Victory, session.Machine.State);
        Assert.NotNull(session.Victory);
        Assert.Equal(node.WinWaves, session.Victory!.WinWaves);
        Assert.Equal(session.WinWaves, session.Victory.WinWaves);

        // —— ④ 命令流全应用零拒收（经济面全合法——同 E2E 断言口径）。
        Assert.Equal(SimE1Fixture.MilestoneCommands().Count, session.Sim.AppliedCommandCount);
        Assert.All(session.Sim.CommandResults, result => Assert.True(result.Accepted));

        // —— ② 两端同 tick 同哈希（终局对账）。
        Assert.Equal(session.Sim.CurrentTick, client.Mirror.CurrentTick);
        Assert.Equal(session.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
        Assert.Equal(session.Victory.Tick, client.Mirror.CurrentTick); // 镜像同步到胜利刻

        // —— ③ 摘要逐行全中：每 6 tick 一行，零跳过零偏差。
        long expectedDigests = session.Sim.CurrentTick / NetTiming.SnapshotCadenceTicks;
        Assert.Equal(expectedDigests, client.MatchedDigests);
        Assert.Equal(0L, client.SkippedDigests);
        Assert.Empty(client.Divergences);

        // —— 战果摘要（REV 验收证据行：威胁/胜波/胜利刻/摘要行数/终局哈希）。
        Assert.True(expectedDigests >= 1, "至少一行摘要被对账");
        _output.WriteLine(
            $"NET_VICTORY threat={node.Threat} winWaves={node.WinWaves} victoryTick={session.Victory.Tick} " +
            $"digests={client.MatchedDigests} hash={session.Sim.ComputeStateHash()} " +
            $"towers={client.Mirror.Towers.Count} buildings={client.Mirror.Buildings.Count}");
    }
}
