using EraCore.Agent.Pipeline;
using EraCore.Net;
using EraCore.Sim;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using EraNet;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-139 系统动作服务器审批（08 验收词最后一项）：客户端军师订单文本
// 上行→宿主 K-5 五级管线审批（ask 挂起→Confirm 上行→执行）→执行产生的
// PlayerBuild 命令随广播流下发（镜像同步落塔）→结论摘要下行（Request 关联）。
// 系统点只在宿主账本结算（镜像无管线无账本）；无管线宿主诚实拒绝；澄清
// 类订单零命令零突变。
public class NetAdvisorApprovalTests
{
    /// <summary>
    /// 联机房（真实管线版）：宿主权威 sim+管线来自 PipelineHarness（E1 内容
    /// era2 智能体层，与 T-053 夹具同口径）；镜像=同 config 同 seed 双子。
    /// </summary>
    private static (NetGameHost Host, NetGameClient Client, MemoryNetMesh Mesh, PipelineHarness Harness)
        LiveRoomWithPipeline()
    {
        PipelineHarness harness = new();
        var mirror = new Simulation(PipelineHarness.Config(), new SimRng(PipelineHarness.Seed), PipelineHarness.Seed);

        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(harness.Simulation, mesh.Host, mesh.Host, mesh.Host, harness.Pipeline);
        var client = new NetGameClient(mirror, endpoint, endpoint, endpoint);
        return (host, client, mesh, harness);
    }

    /// <summary>
    /// 管线版 lockstep 帧：PumpIncoming 可能内部步进（管线执行的立即 flush），
    /// 帧末以宿主当前 tick 为准把镜像补步到同 tick——两端每帧停齐。
    /// </summary>
    private static void FrameSync(NetGameHost host, NetGameClient client, MemoryNetMesh mesh)
    {
        mesh.Deliver();
        host.PumpIncoming();
        host.AdvanceTick();
        long target = host.Sim.CurrentTick;
        mesh.Deliver();
        client.PumpIncoming();
        while (client.Mirror.CurrentTick < target)
        {
            client.StepMirror();
        }
    }

    [Fact] // legal — ask→Confirm 全链：订单上行→宿主挂起→确认上行→执行→广播落塔→镜像同步
    public void Ask_confirm_order_executes_host_side_and_broadcasts_to_the_mirror()
    {
        (NetGameHost host, NetGameClient client, MemoryNetMesh mesh, PipelineHarness harness) =
            LiveRoomWithPipeline();
        int ceiling = harness.Pipeline.Ledger.Ceiling;

        // —— 订单文本上行（客户端只发文本，动作 JSON 不存在）
        Assert.True(client.SubmitAdvisorOrder("守住东门"));
        FrameSync(host, client, mesh);

        AdvisorOrderReply awaiting = Assert.Single(client.AdvisorReplies);
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        Assert.True(awaiting.PendingId >= 0);
        Assert.Equal(0, host.BroadcastCount); // 未审批=零命令（伪造/抢跑没有消费路径）
        Assert.Equal(ceiling, harness.Pipeline.Ledger.Points);

        // —— 确认上行→宿主执行→命令经同一咽喉广播
        Assert.True(client.ConfirmPending(awaiting.PendingId));
        int repliesBefore = client.AdvisorReplies.Count;
        FrameSync(host, client, mesh);

        AdvisorOrderReply done = client.AdvisorReplies.Skip(repliesBefore).Single();
        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.StartsWith("已照办", done.ReportDigest, StringComparison.Ordinal);
        Assert.True(host.BroadcastCount > 0); // 审批通过的动作随广播流下发
        Assert.Equal(2, harness.Simulation.Towers.Count); // 宿主权威侧落 2 塔
        Assert.Equal(2, client.Mirror.Towers.Count); // 镜像经广播流同步落 2 塔
        Assert.Equal(ceiling - 2, harness.Pipeline.Ledger.Points); // 系统点宿主侧结算（建造 2 点）
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash()); // 两端同 tick 同哈希
    }

    [Fact] // legal — Refuse 上行：方案卡被拒，零命令零扣点
    public void Refused_plan_card_executes_nothing()
    {
        (NetGameHost host, NetGameClient client, MemoryNetMesh mesh, PipelineHarness harness) =
            LiveRoomWithPipeline();

        client.SubmitAdvisorOrder("守住东门");
        FrameSync(host, client, mesh);
        long pendingId = client.AdvisorReplies.Single(reply => reply.Kind == PipelineOutcomeKind.AwaitingApproval).PendingId;

        Assert.True(client.RefusePending(pendingId));
        FrameSync(host, client, mesh);

        AdvisorOrderReply refused = client.AdvisorReplies.Last();
        Assert.Equal(PipelineOutcomeKind.ApprovalRefused, refused.Kind);
        Assert.Equal(0, host.BroadcastCount);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(harness.Pipeline.Ledger.Ceiling, harness.Pipeline.Ledger.Points);
        Assert.Equal(host.Sim.ComputeStateHash(), client.Mirror.ComputeStateHash());
    }

    [Fact] // boundary — 无管线宿主诚实拒绝：不装军师的房间对订单回 Refused+可诊断摘要，零命令
    public void Room_without_pipeline_refuses_orders_honestly()
    {
        SimulationConfig config = PipelineHarness.Config();
        var hostSim = new Simulation(config, new SimRng(PipelineHarness.Seed), PipelineHarness.Seed);
        var mirror = new Simulation(config, new SimRng(PipelineHarness.Seed), PipelineHarness.Seed);

        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(hostSim, mesh.Host, mesh.Host, mesh.Host, pipeline: null);
        var client = new NetGameClient(mirror, endpoint, endpoint, endpoint);

        Assert.True(client.SubmitAdvisorOrder("守住东门"));
        FrameSync(host, client, mesh);

        AdvisorOrderReply reply = Assert.Single(client.AdvisorReplies);
        Assert.Equal(PipelineOutcomeKind.Refused, reply.Kind);
        Assert.Equal("agent.pipeline.absent", reply.ClarifyingKey);
        Assert.Equal(0, host.BroadcastCount);
    }

    [Fact] // boundary — 澄清类订单（无法解析）零命令零突变：上行文本→宿主 L1 拒→澄清卡下行
    public void Unparseable_order_clarifies_without_any_command()
    {
        (NetGameHost host, NetGameClient client, MemoryNetMesh mesh, PipelineHarness harness) =
            LiveRoomWithPipeline();

        Assert.True(client.SubmitAdvisorOrder("这局句法不通的胡话"));
        FrameSync(host, client, mesh);

        AdvisorOrderReply reply = Assert.Single(client.AdvisorReplies);
        Assert.Equal(PipelineOutcomeKind.Clarification, reply.Kind);
        Assert.NotNull(reply.ClarifyingKey);
        Assert.Equal(0, host.BroadcastCount);
        Assert.Equal(0, host.Sim.Commands.LastSeq);
        Assert.Equal(harness.Pipeline.Ledger.Ceiling, harness.Pipeline.Ledger.Points);
    }

    [Fact] // legal — 请求关联：两条订单的 reply.RequestId 与上行序一一对应
    public void Replies_correlate_with_request_ids()
    {
        (NetGameHost host, NetGameClient client, MemoryNetMesh mesh, PipelineHarness _) =
            LiveRoomWithPipeline();

        client.SubmitAdvisorOrder("这局句法不通的胡话");
        client.SubmitAdvisorOrder("再来一句无法解析的");
        FrameSync(host, client, mesh);

        Assert.Equal(2, client.AdvisorReplies.Count);
        Assert.Equal(1L, client.AdvisorReplies[0].RequestId);
        Assert.Equal(2L, client.AdvisorReplies[1].RequestId);
    }
}
