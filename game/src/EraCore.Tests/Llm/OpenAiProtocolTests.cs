using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// OpenAI 协议适配器（F-083）：端点拼接、Bearer 头、请求体构造、SSE 归约
/// （content/reasoning_content 双通道 + tool_calls 分片拼接 + [DONE]）与非流式。
/// </summary>
// [F-083][F-086][F-087] C-16 T-072：openai 协议路由与流式归约
public class OpenAiProtocolTests
{
    private readonly OpenAiProtocol _protocol = new();

    private static LlmChatRequestSpec Spec(bool stream = true) => new(
        "mimo-v2.6-pro",
        new[] { new LlmChatMessage("system", "你是指挥官"), new LlmChatMessage("user", "在东门建两座弩塔") },
        LlmFixtures.BuildPlanToolsJson,
        Temperature: 0.7,
        MaxTokens: 1024,
        Stream: stream);

    [Theory]
    [InlineData("https://api.xiaomimimo.com/v1", "https://api.xiaomimimo.com/v1/chat/completions")]
    [InlineData("https://api.xiaomimimo.com/v1/", "https://api.xiaomimimo.com/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("http://localhost:1234", "http://localhost:1234/chat/completions")]
    public void Endpoint_appends_chat_completions_once(string baseUrl, string expected)
    {
        // legal: 尾斜杠归一，路径只拼一次
        Assert.Equal(expected, _protocol.BuildEndpoint(new Uri(baseUrl)).AbsoluteUri);
    }

    [Fact]
    public void Endpoint_rejects_non_http_base()
    {
        // illegal: 非 http(s) 基址
        Assert.Throws<UriFormatException>(
            () => _protocol.BuildEndpoint(new Uri("ftp://x.example.com")));
    }

    [Fact]
    public void Auth_header_is_bearer_and_body_carries_every_field()
    {
        // legal: 认证头 + 请求体字段全集 + tools 透传
        var headers = new Dictionary<string, string>();
        _protocol.ApplyAuthHeaders(headers, "test-key-synthetic");

        Assert.Equal("Bearer test-key-synthetic", headers["Authorization"]);

        var body = JsonDocument.Parse(_protocol.BuildRequestBody(Spec()));
        var root = body.RootElement;
        Assert.Equal("mimo-v2.6-pro", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("在东门建两座弩塔", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("BuildPlan", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public void Non_stream_body_omits_the_stream_flag()
    {
        // boundary: use_stream=false 不写 stream 字段
        var root = JsonDocument.Parse(_protocol.BuildRequestBody(Spec(stream: false))).RootElement;

        Assert.False(root.TryGetProperty("stream", out _));
    }

    [Fact]
    public void Empty_tools_array_is_omitted_from_the_body()
    {
        // boundary: 空工具表不外发 tools 字段（部分端点对 [] 报错）
        var spec = Spec() with { ToolsJson = "[]" };
        var root = JsonDocument.Parse(_protocol.BuildRequestBody(spec)).RootElement;

        Assert.False(root.TryGetProperty("tools", out _));
    }

    [Fact]
    public void Non_array_tools_json_is_rejected()
    {
        // illegal: 工具表不是数组
        Assert.Throws<JsonException>(() => _protocol.BuildRequestBody(Spec() with { ToolsJson = "{}" }));
    }

    [Fact]
    public void Sse_frames_reduce_content_reasoning_and_fragmented_tool_calls()
    {
        // legal: 全量样例（含 delta.reasoning_content 双通道与 arguments 三段分片）
        var reducer = _protocol.CreateReducer();
        var sawData = false;
        foreach (var line in LlmFixtures.OpenAiSse.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                sawData = true;
                reducer.OnDataLine(line["data:".Length..].TrimStart());
            }
        }

        Assert.True(sawData);
        Assert.True(reducer.IsDone);
        var outcome = reducer.BuildOutcome();
        Assert.Equal("收到，正在规划建造。", outcome.ContentText);
        Assert.Equal("分析军令：东门，两座弩塔。查看塔表。", outcome.ReasoningText);
        Assert.True(outcome.SawReasoningChannel);
        Assert.Equal("BuildPlan", outcome.ToolName);
        Assert.Equal("tool_calls", outcome.FinishReason);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("crossbow_t2", args.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(2, args.RootElement.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public void Done_marker_sets_terminal_even_without_choices()
    {
        // boundary: [DONE] 单独到达
        var reducer = _protocol.CreateReducer();

        reducer.OnDataLine("[DONE]");

        Assert.True(reducer.IsDone);
        Assert.False(reducer.BuildOutcome().SawToolCall);
    }

    [Fact]
    public void Non_stream_reply_reduces_tool_call_and_reasoning()
    {
        // legal: 非流式 message.tool_calls + reasoning_content
        var outcome = _protocol.ParseNonStreamResponse(LlmFixtures.OpenAiNonStream);

        Assert.Equal("BuildPlan", outcome.ToolName);
        Assert.Equal("思考中", outcome.ReasoningText);
        Assert.True(outcome.SawToolCall);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("arrow_t1", args.RootElement.GetProperty("tower_id").GetString());
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Malformed_non_stream_replies_throw_json_exception(string body)
    {
        // illegal: 缺 choices/空 choices/非对象
        Assert.Throws<JsonException>(() => _protocol.ParseNonStreamResponse(body));
    }
}
