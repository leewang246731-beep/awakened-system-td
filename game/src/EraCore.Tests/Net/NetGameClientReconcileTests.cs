using EraCore.Net;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraNet;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-139 客户端对账与重连（08 验收词「快照 200ms」+「断线重连=检查点
// 重放」）：摘要节拍（6 tick 网格）、哈希全等（MatchedDigests 逐行）、摘要
// 丢弃容忍（尽力而为）、偏差检测（镜像被注入局部突变→Divergences 记录）、
// RestoreFrom 重放重同步（重连后与不间断双子终态哈希全等——Recovery
// Equivalence 的网络化）。
public class NetGameClientReconcileTests
{
    private static (NetGameHost Host, NetGameClient Client, MemoryNetEndpoint Endpoint, MemoryNetMesh Mesh) LiveRoom()
    {
        SimulationConfig config = SimE1Fixture.Config();
        var hostSim = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        var mirror = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);

        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(hostSim, mesh.Host, mesh.Host, mesh.Host);
        var client = new NetGameClient(mirror, endpoint, endpoint, endpoint);
        return (host, client, endpoint, mesh);
    }

    private static void Frame(NetGameHost host, NetGameClient client, MemoryNetMesh mesh)
    {
        mesh.Deliver();
        host.PumpIncoming();
        host.AdvanceTick();
        mesh.Deliver();
        client.PumpIncoming();
        client.StepMirror();
    }

    [Fact] // legal — 摘要节拍与哈希全等：60 tick 联跑，每 6 tick 一行，全部命中
    public void Digests_match_every_six_ticks_over_a_live_run()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        for (int i = 0; i < 60; i++)
        {
            client.SubmitCommand(SimCommands.BuildLaborCamp(i + 1, SimE1Fixture.LaborCampCell));
            Frame(host, client, mesh);
        }

        Assert.Equal(60L, host.Sim.CurrentTick);
        Assert.Equal(60L, client.Mirror.CurrentTick);
        Assert.Equal(60L, host.LastDigestTick); // 最后一行落在 60（6 的整数倍网格）
        Assert.Equal(10L, client.MatchedDigests);
        Assert.Equal(0L, client.SkippedDigests);
        Assert.Empty(client.Divergences);
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // boundary — 摘要尽力而为：丢弃部分在途摘要不致命——未丢的全中，命令流无损终态仍全等
    public void Dropped_digests_are_survivable_next_grid_line_arrives()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        for (int i = 0; i < 30; i++)
        {
            mesh.Deliver();
            host.PumpIncoming();
            host.AdvanceTick();
            if ((i + 1) % 12 == 0)
            {
                int dropped = mesh.DropPendingDigests(0, 1); // tick 12/24 的在途摘要被丢
                Assert.Equal(1, dropped);
            }

            mesh.Deliver();
            client.PumpIncoming();
            client.StepMirror();
        }

        Assert.Equal(30L, host.Sim.CurrentTick);
        Assert.Equal(3L, client.MatchedDigests); // 5 行丢 2 行（12/24），余 3 行（6/18/30）全中
        Assert.Equal(0L, client.SkippedDigests); // 丢弃发生在传输层，不冒充「不可比跳过」
        Assert.Empty(client.Divergences);
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // boundary — 偏差检测：镜像被注入未经广播的局部命令（篡改/版本漂移模型）→下一行网格摘要即红
    public void Mirror_tampering_is_detected_at_the_next_digest()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        for (int i = 0; i < 6; i++)
        {
            Frame(host, client, mesh);
        }

        Assert.Empty(client.Divergences);
        client.Mirror.Enqueue(SimCommands.BuildLaborCamp(7, SimE1Fixture.LaborCampCell)); // 未走广播流的局部突变
        for (int i = 0; i < 6; i++)
        {
            Frame(host, client, mesh);
        }

        NetDivergence divergence = Assert.Single(client.Divergences);
        Assert.Equal(12L, divergence.Tick); // 注入后的第一行网格摘要（tick 12）
        Assert.NotEqual(divergence.LocalHash, divergence.HostHash);
    }

    [Fact] // legal — 偏差重同步（48 §3.3 路径 A）：宿主快照重放重建后恢复对账命中
    public void Resync_from_host_snapshot_recovers_the_match()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        client.SubmitCommand(SimCommands.BuildLaborCamp(1, SimE1Fixture.LaborCampCell));
        for (int i = 0; i < 12; i++)
        {
            Frame(host, client, mesh);
        }

        client.Mirror.Enqueue(SimCommands.BuildHarvester(13, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood)); // 局部污染
        for (int i = 0; i < 6; i++)
        {
            Frame(host, client, mesh);
        }

        Assert.Single(client.Divergences);

        client.RestoreFrom(host.BuildSessionSnapshot(), seed => new Simulation(
            SimE1Fixture.Config(), new SimRng(seed), seed));
        Assert.Equal(host.Sim.CurrentTick, client.Mirror.CurrentTick);
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash()); // 重建即对齐

        for (int i = 0; i < 12; i++)
        {
            Frame(host, client, mesh);
        }

        NetDivergence keptAuditRecord = Assert.Single(client.Divergences); // 旧记录保留（审计面），无新增
        Assert.Equal(18L, keptAuditRecord.Tick); // 污染后的下一行网格摘要（tick 18）
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // legal — 断线重连=检查点重放（08 验收词）：断线期间宿主照跑，重连后快照重放追平，与不间断双子全等
    public void Reconnect_replays_the_session_snapshot_to_catch_up()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        // 不间断双子（等价性对照）：同命令流（同 tick 同 seq 序）本地直跑
        var twin = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        twin.Enqueue(SimCommands.BuildLaborCamp(1, SimE1Fixture.LaborCampCell));

        client.SubmitCommand(SimCommands.BuildLaborCamp(1, SimE1Fixture.LaborCampCell)); // 盖章为 tick 1
        for (int i = 0; i < 8; i++)
        {
            Frame(host, client, mesh);
            twin.Step();
        }

        // —— 断线：客户端掉线，宿主继续推进 12 tick（广播对离线端被丢）
        endpoint.MarkOffline();
        host.SubmitLocal(SimCommands.BuildHarvester(9, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood)); // 盖章为 tick 9
        twin.Enqueue(SimCommands.BuildHarvester(9, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood));
        for (int i = 0; i < 12; i++)
        {
            mesh.Deliver();
            host.PumpIncoming();
            host.AdvanceTick();
            twin.Step();
        }

        Assert.Equal(20L, host.Sim.CurrentTick);
        Assert.Equal(8L, client.Mirror.CurrentTick); // 镜像停在断线处

        // —— 重连：握手+会话快照重放追平（K-7 介质网络化）
        endpoint.MarkOffline(); // 双 drop=合法空操作
        endpoint.Connect();
        endpoint.CompleteHandshake();
        client.RestoreFrom(host.BuildSessionSnapshot(), seed => new Simulation(
            SimE1Fixture.Config(), new SimRng(seed), seed));

        Assert.Equal(20L, client.Mirror.CurrentTick);
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
        Assert.Equal(twin.ComputeStateHash(), client.Mirror.ComputeStateHash()); // 与不间断双子终态全等

        // —— 重连后继续联跑：对账命中、无偏差、与双子保持全等
        for (int i = 0; i < 12; i++)
        {
            Frame(host, client, mesh);
            twin.Step();
        }

        Assert.Empty(client.Divergences);
        Assert.Equal(twin.ComputeStateHash(), client.Mirror.ComputeStateHash());
        Assert.Equal(twin.ComputeStateHash(), host.Sim.ComputeStateHash());
    }

    [Fact] // boundary — 离线上行被拒：SubmitCommand/军师上行在 Offline 返回 false 且零入队
    public void Offline_submissions_are_refused_without_side_effects()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        endpoint.MarkOffline();

        Assert.False(client.SubmitCommand(SimCommands.BuildLaborCamp(1, SimE1Fixture.LaborCampCell)));
        Assert.False(client.SubmitAdvisorOrder("守住东门"));
        Assert.Empty(client.SubmittedEcho);

        mesh.Deliver();
        host.PumpIncoming();
        Assert.Equal(0, host.BroadcastCount);
        Assert.Equal(0, host.Sim.Commands.LastSeq);
    }
}
