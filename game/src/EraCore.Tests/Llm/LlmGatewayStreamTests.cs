using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 网关 SSE 端到端（F-086）：FakeWirePort 以 7 字节小片喂入真实网关读循环，
/// 行缓冲/半行拼接/[DONE]/双通道在完整链路上成立；含“请求流式但服务端回了
/// 普通 JSON”的容错回退。
/// </summary>
// [F-086][F-087] C-16 T-072：流式内核端到端
public class LlmGatewayStreamTests
{
    private static (LlmGateway Gateway, FakeWirePort Port) Build(LlmProviderConfig? active = null)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store(active?.Id ?? "test-provider", "synthetic-key-value-000");
        var config = LlmFixtures.TestConfig(active: active ?? LlmFixtures.TestProvider());
        return (new LlmGateway(
            config, port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), port);
    }

    private static Task<LlmGatewayCallResult> Send(LlmGateway gateway) =>
        gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Openai_sse_reduces_tool_call_at_any_chunk_boundary(int chunkSize)
    {
        // legal: 任意分片粒度下工具调用与双文本通道一致
        var (gateway, port) = Build();
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(LlmFixtures.OpenAiSse, chunkSize)));

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.NotNull(result.Outcome);
        Assert.Equal("收到，正在规划建造。", result.Outcome.ContentText);
        Assert.True(result.Outcome.SawReasoningChannel);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.Equal("crossbow_t2", action.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(2, action.RootElement.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public async Task Anthropic_sse_reduces_through_the_same_gateway_loop()
    {
        // legal: anthropic 流式经同一读循环（event:/data: 分派）
        var (gateway, port) = Build(LlmFixtures.TestProvider(protocol: LlmProtocol.Anthropic));
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(LlmFixtures.AnthropicSse, 5)));

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal("收到。", result.Outcome!.ContentText);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.Equal("crossbow_t2", action.RootElement.GetProperty("tower_id").GetString());
    }

    [Fact]
    public async Task Stream_requested_but_plain_json_answered_falls_back_to_non_stream_parse()
    {
        // boundary: 中转站吞掉 SSE，只回普通 JSON——网关回退归约
        var (gateway, port) = Build();
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(LlmFixtures.OpenAiNonStream, 11)));

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal("BuildPlan", JsonDocument.Parse(result.ActionJson!)
            .RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Stream_truncated_without_done_marker_still_reduces_what_arrived()
    {
        // boundary: 流中断（无 [DONE]）——已到帧照常归约
        var truncated = LlmFixtures.OpenAiSse.Replace("data: [DONE]\n\n", string.Empty);
        var (gateway, port) = Build();
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(truncated, 9)));

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal("BuildPlan", result.Outcome!.ToolName);
    }

    [Fact]
    public async Task Unparseable_stream_body_is_classified_bad_response()
    {
        // illegal: 2xx 但帧体非法
        var (gateway, port) = Build();
        port.EnqueueSse("data: not-json\n\n");

        var result = await Send(gateway);

        Assert.Equal(LlmErrorKeys.BadResponse, result.ErrorKey);
        Assert.Null(result.ActionJson);
    }

    [Fact]
    public async Task Wire_request_asks_for_event_stream_when_streaming()
    {
        // legal: 流式请求的 Accept 头与 body.stream
        var (gateway, port) = Build();
        port.EnqueueSse(LlmFixtures.OpenAiSse);

        await Send(gateway);

        var call = Assert.Single(port.Calls);
        Assert.Equal("text/event-stream", call.Headers["Accept"]);
        using var body = JsonDocument.Parse(call.Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }
}
