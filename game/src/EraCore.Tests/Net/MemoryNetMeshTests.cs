using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;
using EraNet;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-139 内存回环网（零端口传输层）：ICommandTransport/
// ISnapshotChannel/IAdvisorOrderChannel/NetHostWire 的第一个实现——
// 生命周期（NetLinkStateMachine 词表）、链路态门控（离线拒绝/静默丢弃）、
// 双向 FIFO 保序、投递节拍（Deliver=一跳）、故障注入（摘要丢弃=尽力而为
// 语义；命令丢弃=喂重连场景）。
public class MemoryNetMeshTests
{
    private static SimCommand Command(long tick) => new(
        tick, SimCommandKind.PlayerBuild, "{\"verb\":\"build-converter\",\"cell\":{\"x\":9,\"y\":18}}");

    private static (MemoryNetMesh Mesh, MemoryNetEndpoint Client) LivePair()
    {
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint client = mesh.AddClient();
        client.Connect();
        client.CompleteHandshake();
        return (mesh, client);
    }

    [Fact] // legal — 全生命周期：mesh 建=宿主 Online；客户端 Offline→Connecting→Online→Offline
    public void Lifecycle_follows_the_shared_state_machine_vocabulary()
    {
        var mesh = new MemoryNetMesh();
        Assert.Equal(NetLinkStatus.Online, mesh.Host.Status);

        MemoryNetEndpoint client = mesh.AddClient();
        Assert.Equal(NetLinkStatus.Offline, client.Status);
        Assert.False(client.Submit(Command(1))); // Offline 拒收（链路态原因零异常）

        client.Connect();
        Assert.Equal(NetLinkStatus.Connecting, client.Status);
        Assert.False(client.Submit(Command(1))); // Connecting 仍拒收

        client.CompleteHandshake();
        Assert.Equal(NetLinkStatus.Online, client.Status);
        Assert.True(client.Submit(Command(1)));

        client.MarkOffline();
        Assert.Equal(NetLinkStatus.Offline, client.Status);
        client.MarkOffline(); // 双 drop 回调=合法空操作（状态机契约）
        Assert.Equal(NetLinkStatus.Offline, client.Status);
    }

    [Fact] // legal — 命令上行/广播下行双向 FIFO 保序（一跳投递模型）
    public void Commands_flow_both_directions_in_fifo_order()
    {
        (MemoryNetMesh mesh, MemoryNetEndpoint client) = LivePair();

        Assert.True(client.Submit(Command(1)));
        Assert.True(client.Submit(Command(2)));
        Assert.Empty(mesh.Host.DrainSubmissions()); // 未投递=不可读（在途）

        mesh.Deliver();
        IReadOnlyList<SimCommand> submissions = mesh.Host.DrainSubmissions();
        Assert.Equal(2, submissions.Count);
        Assert.Equal(1L, submissions[0].IssuedAtTick);
        Assert.Equal(2L, submissions[1].IssuedAtTick);

        mesh.Host.Broadcast(Command(3));
        mesh.Host.Broadcast(Command(4));
        mesh.Deliver();
        IReadOnlyList<SimCommand> incoming = client.DrainIncoming();
        Assert.Equal(new long[] { 3, 4 }, incoming.Select(command => command.IssuedAtTick));
    }

    [Fact] // legal — 摘要通道：Publish 推送、Drain 按发布序到达
    public void Digests_publish_and_drain_in_publish_order()
    {
        (MemoryNetMesh mesh, MemoryNetEndpoint client) = LivePair();

        mesh.Host.Publish(new SimHashSample(6, 111));
        mesh.Host.Publish(new SimHashSample(12, 222));
        mesh.Deliver();

        IReadOnlyList<SimHashSample> digests = client.DrainIncomingDigests();
        Assert.Equal(2, digests.Count);
        Assert.Equal(6L, digests[0].Tick);
        Assert.Equal(12L, digests[1].Tick);
        Assert.Equal(222L, digests[1].StateHash);
    }

    [Fact] // boundary — 离线静默丢弃：非 Online 的 Publish/Broadcast 不抛不缓存
    public void Offline_publish_and_broadcast_drop_silently()
    {
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint offline = mesh.AddClient();
        offline.Connect();
        offline.CompleteHandshake();
        offline.MarkOffline();

        Exception? thrown = Record.Exception(() =>
        {
            mesh.Host.Broadcast(Command(1));
            mesh.Host.Publish(new SimHashSample(6, 111));
        });
        Assert.Null(thrown);

        mesh.Deliver();
        Assert.Empty(offline.DrainIncoming());
        Assert.Empty(offline.DrainIncomingDigests());
    }

    [Fact] // boundary — 故障注入：在途摘要可丢弃且不影响同跳其它消息（尽力而为语义的执法面）
    public void Drop_pending_digests_leaves_commands_untouched()
    {
        (MemoryNetMesh mesh, MemoryNetEndpoint client) = LivePair();

        mesh.Host.Publish(new SimHashSample(6, 111));
        mesh.Host.Broadcast(Command(2));
        mesh.Host.Publish(new SimHashSample(12, 222));

        int dropped = mesh.DropPendingDigests(0, 1);
        mesh.Deliver();

        Assert.Equal(1, dropped);
        Assert.Single(client.DrainIncomingDigests());
        Assert.Single(client.DrainIncoming());
    }

    [Fact] // legal — 军师通道：请求上行（端内单调 id 重盖）/结论下行
    public void Advisor_requests_up_and_replies_down_correlate_by_request_id()
    {
        (MemoryNetMesh mesh, MemoryNetEndpoint client) = LivePair();

        Assert.True(client.Submit(new AdvisorOrderRequest(0, AdvisorOrderRequestKind.Order, "守住东门", -1)));
        Assert.True(client.Submit(new AdvisorOrderRequest(99, AdvisorOrderRequestKind.Confirm, null, 3)));
        mesh.Deliver();

        IReadOnlyList<AdvisorOrderRequest> requests = mesh.Host.DrainRequests();
        Assert.Equal(2, requests.Count);
        Assert.Equal(1L, requests[0].RequestId); // 端内单调重盖（防对端伪造撞号）
        Assert.Equal(2L, requests[1].RequestId);
        Assert.Equal(AdvisorOrderRequestKind.Order, requests[0].Kind);
        Assert.Equal("守住东门", requests[0].OrderText);
        Assert.Equal(3L, requests[1].PendingId);

        mesh.Host.Push(new AdvisorOrderReply(1, 1, "守住东门",
            EraCore.Agent.Pipeline.PipelineOutcomeKind.Completed, null, -1, "ok"));
        mesh.Deliver();
        AdvisorOrderReply reply = Assert.Single(client.DrainReplies());
        Assert.Equal(1L, reply.RequestId);
        Assert.Equal("ok", reply.ReportDigest);
    }

    [Fact] // boundary — 宿主端 Submit=恒拒（宿主没有「上行到自己」的消费路径）
    public void Host_endpoint_has_no_upstream()
    {
        var mesh = new MemoryNetMesh();
        Assert.False(mesh.Host.Submit(Command(1)));
        Assert.Empty(mesh.Host.DrainIncoming()); // 且不会误吞客户端提交流
    }

    [Fact] // legal — 多客户端广播面：每个在线客户端各得一份同序广播
    public void Broadcast_reaches_every_online_client()
    {
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint a = mesh.AddClient();
        MemoryNetEndpoint b = mesh.AddClient();
        a.Connect();
        a.CompleteHandshake();
        b.Connect();

        mesh.Host.Broadcast(Command(7));
        mesh.Deliver();

        Assert.Single(a.DrainIncoming());
        Assert.Empty(b.DrainIncoming()); // Connecting 客户端不在广播面
    }
}
