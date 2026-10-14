using System.Diagnostics;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 双超时执法验证（F-088, §11.7 首字节 15s+chunk 空闲 30s, T-091）：流相首读
/// 无字节、空闲边界（恰在预算内/外）、首字节窗口中途断连、超时与断连后的
/// 资源清理（连接关闭断言）。时间可控注入=GapStream 真实墙钟小预算。
/// </summary>
// [F-088][F-162] C-16 T-091：双超时执法与资源清理
public class LlmGatewayTimeoutTests
{
    private const string FirstFrame =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"收到\"}}]}\n\n";
    private const string RestFrames =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"BuildPlan\",\"arguments\":\"{\\\"tower_id\\\":\\\"arrow_t1\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n";

    private static (LlmGateway Gateway, FakeWirePort Port) Build(
        TimeSpan firstByte, TimeSpan idle)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(), port, secrets, firstByte, idle);
        return (gateway, port);
    }

    private static Task<LlmGatewayCallResult> Send(
        (LlmGateway Gateway, FakeWirePort Port) built,
        Func<HttpPortRequest, HttpPortResponse>? replyFactory = null)
    {
        built.Port.EnqueueReply(replyFactory
            ?? (Func<HttpPortRequest, HttpPortResponse>)(_ => new HttpPortResponse(
                200, null, new ChunkedStream(LlmFixtures.OpenAiSse, 9))));
        return built.Gateway.RouteAsync(
            built.Gateway.FindProvider(built.Gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);
    }

    [Fact]
    public async Task Stream_phase_first_read_stall_classifies_as_first_byte_timeout()
    {
        // illegal: 2xx 头已到、体首字节永不出现——首字节预算覆盖流首读
        var tracked = new DisposeTrackingStream(new FirstByteStallStream());
        var built = Build(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(30));

        var result = await Send(built, _ => new HttpPortResponse(200, null, tracked));

        Assert.Equal(LlmErrorKeys.TimeoutFirstByte, result.ErrorKey);
        Assert.Equal(1, built.Port.CallCount);
        Assert.True(tracked.Disposed, "首字节超时后连接必须关闭");
    }

    [Fact]
    public async Task Idle_gap_below_the_budget_completes_successfully()
    {
        // legal: chunk 间隔恰在预算内（400ms 预算、100ms 间隔）——不误杀
        var built = Build(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400));

        var result = await Send(built, _ => new HttpPortResponse(200, null,
            new GapStream(FirstFrame, TimeSpan.FromMilliseconds(100), RestFrames)));

        Assert.Null(result.ErrorKey);
        Assert.True(result.Ok);
        Assert.Equal("BuildPlan", result.Outcome!.ToolName);
        Assert.Equal("收到", result.Outcome.ContentText);
    }

    [Fact]
    public async Task Idle_gap_above_the_budget_classifies_as_idle_timeout()
    {
        // illegal: chunk 空闲越过预算（400ms 预算、1500ms 间隔）——TimeoutIdle
        var tracked = new DisposeTrackingStream(
            new GapStream(FirstFrame, TimeSpan.FromMilliseconds(1500), RestFrames));
        var built = Build(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400));

        var result = await Send(built, _ => new HttpPortResponse(200, null, tracked));

        Assert.Equal(LlmErrorKeys.TimeoutIdle, result.ErrorKey);
        Assert.True(tracked.Disposed, "空闲超时后连接必须关闭");
    }

    [Fact]
    public void Default_budgets_are_pinned_by_the_design()
    {
        // boundary: §11.7 钉死缺省（首字节 15s+空闲 30s）——常数即门禁契约
        Assert.Equal(TimeSpan.FromSeconds(15), LlmGateway.DefaultFirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), LlmGateway.DefaultIdleTimeout);
    }

    [Fact]
    public async Task Mid_stream_disconnect_during_first_byte_window_classifies_as_network()
    {
        // illegal: 首字节窗口内连接重置（IOException 而非挂起）——network 分类、
        // 不向调用方泄漏原始网络异常
        var built = Build(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await Send(built, _ => new HttpPortResponse(
            200, null, new DisconnectStream("")));

        Assert.Equal(LlmErrorKeys.Network, result.ErrorKey);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Mid_body_disconnect_after_first_chunk_classifies_as_network()
    {
        // illegal: 流中途断连（首块已消费）——network 分类且不出半成品结果
        var built = Build(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await Send(built, _ => new HttpPortResponse(
            200, null, new DisconnectStream(FirstFrame)));

        Assert.Equal(LlmErrorKeys.Network, result.ErrorKey);
        Assert.Null(result.ActionJson);
    }

    [Fact]
    public async Task Successful_stream_run_closes_the_connection()
    {
        // legal: 正常完成也必须关闭（读循环 finally 清理）
        var tracked = new DisposeTrackingStream(new ChunkedStream(LlmFixtures.OpenAiSse, 9));
        var built = Build(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await Send(built, _ => new HttpPortResponse(200, null, tracked));

        Assert.True(result.Ok, result.ErrorKey);
        Assert.True(tracked.Disposed);
    }

    [Fact]
    public async Task Port_phase_first_byte_timeout_closes_out_immediately()
    {
        // boundary: 连接阶段（DNS/TLS/头）无应答——端口级首字节超时，与流相区分；
        // 执法验证=预算外立即收口（远小于空闲预算），而非无限等待
        var neverPort = new NeverPort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(), neverPort, secrets,
            firstByteTimeout: TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);

        stopwatch.Stop();
        Assert.Equal(LlmErrorKeys.TimeoutFirstByte, result.ErrorKey);
        Assert.Equal(1, neverPort.CallCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"首字节超时应立即收口，实际 {stopwatch.Elapsed}");
    }
}
