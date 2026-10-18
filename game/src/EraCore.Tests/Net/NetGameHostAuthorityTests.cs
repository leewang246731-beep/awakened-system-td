using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraNet;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-139 宿主权威矩阵（08 验收词「listen server」的执法面）：盖章
// （客户端 IssuedAtTick=建议，宿主改写接收 tick+1）、迟到命令合法（队列
// 语义吸收网络抖动）、重复命令幂等（拒绝不变异）、伪造动作无消费路径
// （SystemAction/TimeControl 类命令被 ApplyOne 拒收——动作 JSON 只能经宿主
// K-5 管线生成）、广播序=权威入队序（两端 (tick,seq) 一致 ⇒ 哈希一致）。
public class NetGameHostAuthorityTests
{
    private static SimCommand BuildLaborCamp(long tick) =>
        SimCommands.BuildLaborCamp(tick, SimE1Fixture.LaborCampCell);

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

    /// <summary>
    /// 一个 lockstep 帧（同帧往返回环——RTT &lt; 1 帧的内存线）：①上一帧在途
    /// 落地→②宿主泵（盖戳+广播+到期摘要）→③权威步进→④本帧广播落地→
    /// ⑤镜像入队→⑥镜像同 tick 步进+对账。宿主与镜像每帧停在同一 tick，
    /// 命令在两端同 tick 应用——对账比较是真实比较而非偶然命中。
    /// </summary>
    private static void Frame(NetGameHost host, NetGameClient client, MemoryNetMesh mesh)
    {
        mesh.Deliver();
        host.PumpIncoming();
        host.AdvanceTick();
        mesh.Deliver();
        client.PumpIncoming();
        client.StepMirror();
    }

    [Fact] // legal — 盖章：客户端 IssuedAtTick 被宿主改写为接收 tick+1（建议不是命令）
    public void Client_tick_suggestions_are_restamped_to_host_receive_tick_plus_one()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        client.SubmitCommand(SimCommands.BuildLaborCamp(9999, SimE1Fixture.LaborCampCell)); // 荒谬的未来 tick

        mesh.Deliver();
        host.PumpIncoming(); // 宿主 tick 0 收到 → 盖章为 1

        Assert.Equal(1, host.BroadcastCount);
        mesh.Deliver();
        SimCommand broadcast = Assert.Single(endpoint.DrainIncoming());
        Assert.Equal(1L, broadcast.IssuedAtTick); // 广播里的=盖章值，不是 9999

        host.AdvanceTick(); // tick 1 应用
        Assert.Contains(host.Sim.CommandResults, result => result.Accepted && result.Tick == 1);
    }

    [Fact] // legal — 迟到命令合法：宿主已过目标 tick 后到达的命令在盖章 tick 应用（抖动容忍=既有队列语义）
    public void Late_commands_are_legal_and_apply_on_the_stamped_tick()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        for (int i = 0; i < 12; i++)
        {
            Frame(host, client, mesh);
        }

        client.SubmitCommand(BuildLaborCamp(0)); // IssuedAtTick=0（久远的过去）
        Frame(host, client, mesh); // 宿主 tick 12 收到 → 盖 13 → 本帧应用

        Assert.Equal(13L, host.Sim.CurrentTick);
        SimCommandResult applied = host.Sim.CommandResults.Single(result => result.Accepted);
        Assert.Equal(13L, applied.Tick);

        for (int i = 0; i < 6; i++)
        {
            Frame(host, client, mesh);
        }

        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // legal — 重复命令幂等：同命令两次上行，第二次被 sim 拒收（格已占）且权威终态哈希=只跑一次的哈希
    public void Duplicate_commands_are_idempotent_via_non_mutating_rejection()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        SimCommand command = BuildLaborCamp(5);

        client.SubmitCommand(command);
        client.SubmitCommand(command); // 重复
        for (int i = 0; i < 4; i++)
        {
            Frame(host, client, mesh);
        }

        Assert.Equal(2, host.BroadcastCount); // 两条都广播（两端见同流）
        Assert.Equal(2, host.Sim.CommandResults.Count);
        SimCommandResult first = host.Sim.CommandResults[0];
        SimCommandResult second = host.Sim.CommandResults[1];
        Assert.True(first.Accepted);
        Assert.False(second.Accepted); // 幂等：重复建同格被拒
        Assert.NotNull(second.RejectionReason);

        // 对照组：只跑一次的同 seed 双子，终态哈希与双上行宿主全等（拒绝不变异）
        var singleSim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        singleSim.Enqueue(command with { IssuedAtTick = 1 });
        singleSim.Run(4);
        Assert.Equal(singleSim.ComputeStateHash(), host.Sim.ComputeStateHash());

        // 镜像与权威仍全等（客户端见到的是同一条广播流）
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // boundary — 伪造动作无消费路径：客户端自造 SystemAction/TimeControl 类命令→宿主广播→ApplyOne 拒收，哈希与零命令双子全等
    public void Forged_non_player_command_kinds_have_no_consumption_path()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();

        var forgedSystemAction = new SimCommand(
            0, SimCommandKind.SystemAction, "{\"action\":\"BuildPlan\",\"tower_id\":\"era1:tower-wood-arrow\",\"cells\":[{\"x\":6,\"y\":21}]}");
        var forgedTimeControl = new SimCommand(0, SimCommandKind.TimeControl, "{\"speed\":9}");
        client.SubmitCommand(forgedSystemAction);
        client.SubmitCommand(forgedTimeControl);

        for (int i = 0; i < 3; i++)
        {
            Frame(host, client, mesh);
        }

        Assert.Equal(2, host.Sim.CommandResults.Count);
        Assert.All(host.Sim.CommandResults, result =>
        {
            Assert.False(result.Accepted);
            Assert.Contains("kind-unsupported", result.RejectionReason);
        });
        Assert.Equal(2, host.BroadcastCount); // 广播流如实携带（两端同流同拒）

        // 零命令双子对照：3 tick 无命令推进的哈希=权威哈希（拒绝不变异=零污染）
        var cleanTwin = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        cleanTwin.Run(3);
        Assert.Equal(cleanTwin.ComputeStateHash(), host.Sim.ComputeStateHash());
        Assert.Equal(cleanTwin.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // legal — 广播序=权威入队序：混排宿主本地与客户端命令，镜像按同序收到
    public void Broadcast_order_equals_authority_enqueue_order()
    {
        (NetGameHost host, NetGameClient client, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();

        host.SubmitLocal(BuildLaborCamp(1));
        client.SubmitCommand(SimCommands.BuildHarvester(2, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood));
        mesh.Deliver(); // 客户端提交落地宿主收件箱
        host.PumpIncoming(); // 本地（seq1）+远程（seq2）盖章入队并广播
        host.SubmitLocal(SimCommands.BuildConverter(3, SimE1Fixture.ConverterCell));
        host.PumpIncoming(); // 第三条（seq3）经同一咽喉

        mesh.Deliver();
        IReadOnlyList<SimCommand> journal = endpoint.DrainIncoming();

        Assert.Equal(3, journal.Count);
        Assert.Equal(new[]
        {
            SimCommands.BuildLaborCamp(1, SimE1Fixture.LaborCampCell).PayloadJson,
            SimCommands.BuildHarvester(2, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood).PayloadJson,
            SimCommands.BuildConverter(3, SimE1Fixture.ConverterCell).PayloadJson,
        }, journal.Select(command => command.PayloadJson));
        Assert.Equal(3, host.Sim.Commands.LastSeq); // 权威 seq 1..3 与广播期刊一一对应
    }

    [Fact] // legal — listen 语义：宿主本地命令与客户端命令走同一盖章环路
    public void Host_local_commands_stamp_through_the_same_throat()
    {
        (NetGameHost host, NetGameClient _, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        host.SubmitLocal(BuildLaborCamp(777)); // 宿主自己的输入也盖当前 tick+1
        host.PumpIncoming();

        Assert.Equal(1L, host.Sim.Commands.LastSeq);
        mesh.Deliver();
        Assert.Single(endpoint.DrainIncoming());
    }

    [Fact] // boundary — 客户端离线广播静默丢弃但权威 sim 照跑（广播面≠权威面；期刊仍记录=重连介质）
    public void Offline_broadcast_drops_but_authority_keeps_running()
    {
        (NetGameHost host, NetGameClient _, MemoryNetEndpoint endpoint, MemoryNetMesh mesh) = LiveRoom();
        endpoint.MarkOffline();

        host.SubmitLocal(BuildLaborCamp(1));
        host.PumpIncoming();
        host.AdvanceTick();

        Assert.Equal(1, host.BroadcastCount);
        mesh.Deliver();
        Assert.Empty(endpoint.DrainIncoming()); // 广播被丢（离线）
        Assert.Contains(host.Sim.CommandResults, result => result.Accepted);
    }
}
