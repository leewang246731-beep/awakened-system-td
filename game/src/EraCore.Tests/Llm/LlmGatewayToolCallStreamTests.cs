using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// tool_calls 流式分片拼接全量矩阵（F-086, T-091）：index 乱序到达/name 在
/// 首片/arguments 空片/多工具并发交错/name 重复下发/缺 index 归 0；外加
/// reasoning_content 与 content 交错流（F-087）与 usage 尾帧捕获（F-091）。
/// 归约器直测 + 真实网关读循环（ChunkedStream 任意粒度）双面覆盖。
/// </summary>
// [F-086][F-087][F-091][F-162] C-16 T-091：tool_calls 分片矩阵
public class LlmGatewayToolCallStreamTests
{
    private static void Feed(IChatStreamReducer reducer, string rawSse)
    {
        foreach (var line in rawSse.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                reducer.OnDataLine(line["data:".Length..].TrimStart());
            }
        }
    }

    private static LlmStreamOutcome Reduce(string rawSse, LlmProtocol protocol = LlmProtocol.OpenAi)
    {
        var reducer = ChatProtocols.Resolve(protocol).CreateReducer();
        Feed(reducer, rawSse);
        return reducer.BuildOutcome();
    }

    // ---------------------------------------------- 归约器直测（矩阵单元）

    [Fact]
    public void Out_of_order_index_arrival_keeps_slots_independent()
    {
        // boundary: index=1 片段先到——首工具仍取最小 index（0），两槽互不污染
        var outcome = Reduce(LlmFixtures.OpenAiSseIndexOutOfOrder);

        Assert.True(outcome.SawToolCall);
        Assert.Equal("BuildPlan", outcome.ToolName);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("crossbow_t2", args.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal("tool_calls", outcome.FinishReason);
    }

    [Fact]
    public void Multi_tool_interleaved_fragments_assemble_per_slot()
    {
        // legal: 两工具 index 0/1 参数交错、index 1 先开口、空串参数片、name 重复下发
        var outcome = Reduce(LlmFixtures.OpenAiSseMultiToolInterleaved);

        Assert.Equal("BuildPlan", outcome.ToolName);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("crossbow_t2", args.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(1, args.RootElement.GetProperty("cells").GetArrayLength());
        // 交错双通道：r/c/r/c 顺序在各自通道内保持
        Assert.Equal("两步：先建塔。再出售旧塔。", outcome.ReasoningText);
        Assert.Equal("收到。正在执行。", outcome.ContentText);
        // usage 尾帧（F-091 token 数）
        Assert.NotNull(outcome.Usage);
        Assert.Equal(128, outcome.Usage!.PromptTokens);
        Assert.Equal(64, outcome.Usage.CompletionTokens);
        Assert.Equal(192, outcome.Usage.TotalTokens);
    }

    [Fact]
    public void Missing_index_defaults_to_slot_zero()
    {
        // boundary: 中转站丢 index 字段——归 0 槽，不炸不丢
        var outcome = Reduce(
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"function":{"name":"BuildPlan","arguments":"{\"tower_id\":\"arrow_t1\"}"}}]}}]}

            data: [DONE]

            """);

        Assert.Equal("BuildPlan", outcome.ToolName);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("arrow_t1", args.RootElement.GetProperty("tower_id").GetString());
    }

    [Fact]
    public void Empty_argument_fragments_and_absent_name_do_not_corrupt_slots()
    {
        // boundary: arguments 空串片/后续片无 name 字段——拼接只吃非空串，name 保持首片
        var outcome = Reduce(
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"BuildPlan","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tower_id\":\"arrow_t1\"}"}}]}}]}

            data: [DONE]

            """);

        Assert.Equal("BuildPlan", outcome.ToolName);
        Assert.Equal("{\"tower_id\":\"arrow_t1\"}", outcome.ToolArgumentsJson);
    }

    [Fact]
    public void Repeated_name_fragment_overwrites_instead_of_concatenating()
    {
        // boundary: 部分供应商在后续片重发完整 name——覆盖语义（不拼成 BuildPlanBuildPlan）
        var outcome = Reduce(
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"BuildPlan","arguments":"{\"to"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"BuildPlan","arguments":"wer_id\":\"arrow_t1\"}"}}]}}]}

            data: [DONE]

            """);

        Assert.Equal("BuildPlan", outcome.ToolName);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("arrow_t1", args.RootElement.GetProperty("tower_id").GetString());
    }

    [Fact]
    public void Reasoning_and_content_interleave_per_channel()
    {
        // legal: r→c→r→c 交错帧——两通道各自保序累加（F-087 思考型兼容）
        var outcome = Reduce(
            """
            data: {"choices":[{"index":0,"delta":{"reasoning_content":"思考一。"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"正文一。"}}]}

            data: {"choices":[{"index":0,"delta":{"reasoning_content":"思考二。"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"正文二。"}}]}

            data: [DONE]

            """);

        Assert.Equal("思考一。思考二。", outcome.ReasoningText);
        Assert.Equal("正文一。正文二。", outcome.ContentText);
        Assert.True(outcome.SawReasoningChannel);
    }

    // ---------------------------------------------- 网关端到端（读循环内矩阵）

    private static (LlmGateway Gateway, FakeWirePort Port) Build()
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return (gateway, port);
    }

    private static Task<LlmGatewayCallResult> Send(
        (LlmGateway Gateway, FakeWirePort Port) built, string rawSse, int chunkSize)
    {
        built.Port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(rawSse, chunkSize)));
        return built.Gateway.RouteAsync(
            built.Gateway.FindProvider(built.Gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task Multi_tool_interleaved_stream_reduces_through_the_gateway_loop(int chunkSize)
    {
        // legal: 交错矩阵经真实读循环（任意分片粒度）——首工具合并且无串槽
        var built = Build();

        var result = await Send(built, LlmFixtures.OpenAiSseMultiToolInterleaved, chunkSize);

        Assert.True(result.Ok, result.ErrorKey);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.Equal("crossbow_t2", action.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(1, action.RootElement.GetProperty("cells").GetArrayLength());
        Assert.NotNull(result.Outcome!.Usage);
        Assert.Equal(192, result.Outcome.Usage!.TotalTokens);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task Out_of_order_index_stream_reduces_through_the_gateway_loop(int chunkSize)
    {
        // legal: 乱序 index 经读循环——最小 index 槽胜出
        var built = Build();

        var result = await Send(built, LlmFixtures.OpenAiSseIndexOutOfOrder, chunkSize);

        Assert.True(result.Ok, result.ErrorKey);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Anthropic_interleaved_tool_blocks_and_usage_reduce_through_the_gateway()
    {
        // legal: anthropic 双 tool_use 块交错 + thinking/text 交错 + 分离 usage 事件
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: LlmFixtures.TestProvider(protocol: LlmProtocol.Anthropic)),
            port, secrets, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        port.EnqueueReply(_ => new HttpPortResponse(200, null, new ChunkedStream(
            """
            event: message_start
            data: {"type":"message_start","message":{"id":"m","usage":{"input_tokens":42}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"先想。"}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"收到。"}}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"t2","name":"SellTower","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"tower_id\":\"arrow"}}

            event: content_block_start
            data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"t3","name":"BuildPlan","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"tower_id\":\"crossbow_t2\",\"cells\":[{\"x\":1,\"y\":2}]}"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"_t1\"}"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":17}}

            event: message_stop
            data: {"type":"message_stop"}
            """, 6)));

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey);
        // 首个 tool_use 块（index 2，SellTower）胜出，index 3 的参数不串槽
        Assert.Equal("SellTower", result.Outcome!.ToolName);
        using var args = JsonDocument.Parse(result.Outcome.ToolArgumentsJson);
        Assert.Equal("arrow_t1", args.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal("先想。", result.Outcome.ReasoningText);
        Assert.Equal("收到。", result.Outcome.ContentText);
        Assert.Equal(42, result.Outcome.Usage!.PromptTokens);
        Assert.Equal(17, result.Outcome.Usage.CompletionTokens);
    }
}
