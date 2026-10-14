using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// Anthropic 协议适配器（F-083）：/v1/messages 端点、x-api-key+version 头、
/// system 提取与角色映射、OpenAI→Anthropic 工具转换、SSE 事件归约与非流式。
/// </summary>
// [F-083][F-086] C-16 T-072：anthropic 协议路由与流式归约
public class AnthropicProtocolTests
{
    private readonly AnthropicProtocol _protocol = new();

    private static LlmChatRequestSpec Spec(bool stream = true) => new(
        "test-model",
        new[]
        {
            new LlmChatMessage("system", "你是塔防指挥官"),
            new LlmChatMessage("user", "在东门建两座弩塔"),
            new LlmChatMessage("assistant", "收到"),
            new LlmChatMessage("tool", "tool-result-text"),
        },
        LlmFixtures.BuildPlanToolsJson,
        Temperature: 0.5,
        MaxTokens: 512,
        Stream: stream);

    [Theory]
    [InlineData("https://api.xiaomimimo.com/anthropic", "https://api.xiaomimimo.com/anthropic/v1/messages")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/messages")]
    public void Endpoint_appends_v1_messages(string baseUrl, string expected)
    {
        // legal: §11.6 MiMo anthropic 兼容端点形态
        Assert.Equal(expected, _protocol.BuildEndpoint(new Uri(baseUrl)).AbsoluteUri);
    }

    [Fact]
    public void Auth_headers_are_x_api_key_and_pinned_version()
    {
        // legal: 认证形态与版本头
        var headers = new Dictionary<string, string>();
        _protocol.ApplyAuthHeaders(headers, "test-key-synthetic");

        Assert.Equal("test-key-synthetic", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
    }

    [Fact]
    public void Body_lifts_system_out_and_maps_roles()
    {
        // legal: system 合并顶层；user/assistant 原样；tool 结构透传为 user
        var root = JsonDocument.Parse(_protocol.BuildRequestBody(Spec())).RootElement;

        Assert.Equal("你是塔防指挥官", root.GetProperty("system").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("在东门建两座弩塔", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("tool-result-text", messages[2].GetProperty("content").GetString());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.5, root.GetProperty("temperature").GetDouble());
        Assert.True(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Tools_convert_from_openai_shape_to_anthropic_shape()
    {
        // legal: K-3 投影（OpenAI 形）→ {name,description,input_schema}
        var root = JsonDocument.Parse(_protocol.BuildRequestBody(Spec())).RootElement;
        var tool = root.GetProperty("tools")[0];

        Assert.Equal("BuildPlan", tool.GetProperty("name").GetString());
        Assert.Equal("按坐标批量建造塔", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("input_schema").GetProperty("required").GetArrayLength() >= 2);
    }

    [Fact]
    public void Drifting_tool_projection_fails_loudly()
    {
        // illegal: 工具声明缺 function.name（投影漂移必须在边界抛出）
        Assert.Throws<JsonException>(() => _protocol.BuildRequestBody(
            Spec() with { ToolsJson = "[{\"type\":\"function\",\"function\":{\"description\":\"no name\"}}]" }));
    }

    [Fact]
    public void System_only_prompt_still_builds_a_valid_body()
    {
        // boundary: 全 system 消息（messages 数组为空是非法请求，但构造层不崩）
        var spec = Spec() with
        {
            Messages = new[] { new LlmChatMessage("system", "only system") },
        };

        var root = JsonDocument.Parse(_protocol.BuildRequestBody(spec)).RootElement;
        Assert.Equal(0, root.GetProperty("messages").GetArrayLength());
        Assert.Equal("only system", root.GetProperty("system").GetString());
    }

    [Fact]
    public void Sse_events_reduce_text_and_fragmented_input_json()
    {
        // legal: content_block_start(tool_use) + input_json_delta 分片 + message_stop
        var reducer = _protocol.CreateReducer();
        var sawData = false;
        foreach (var line in LlmFixtures.AnthropicSse.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                sawData = true;
                reducer.OnDataLine(line["data:".Length..].TrimStart());
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                reducer.OnEventLine(line["event:".Length..].Trim());
            }
        }

        Assert.True(sawData);
        Assert.True(reducer.IsDone);
        var outcome = reducer.BuildOutcome();
        Assert.Equal("收到。", outcome.ContentText);
        Assert.Equal("BuildPlan", outcome.ToolName);
        Assert.Equal("tool_use", outcome.FinishReason);
        Assert.Equal("{\"tower_id\":\"crossbow_t2\"}", outcome.ToolArgumentsJson);
    }

    [Fact]
    public void Thinking_delta_accumulates_into_the_reasoning_channel()
    {
        // legal: thinking_delta → reasoning（F-087 推理模型兼容）
        var reducer = _protocol.CreateReducer();

        reducer.OnDataLine("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"先看地图。\"}}");
        reducer.OnDataLine("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"再选塔。\"}}");

        var outcome = reducer.BuildOutcome();
        Assert.Equal("先看地图。再选塔。", outcome.ReasoningText);
        Assert.True(outcome.SawReasoningChannel);
    }

    [Fact]
    public void Non_stream_reply_reduces_tool_use_block()
    {
        // legal: content[] 里 text + tool_use
        var outcome = _protocol.ParseNonStreamResponse(LlmFixtures.AnthropicNonStream);

        Assert.Equal("好的", outcome.ContentText);
        Assert.Equal("BuildPlan", outcome.ToolName);
        Assert.Equal("tool_use", outcome.FinishReason);
        using var args = JsonDocument.Parse(outcome.ToolArgumentsJson);
        Assert.Equal("arrow_t1", args.RootElement.GetProperty("tower_id").GetString());
    }

    [Theory]
    [InlineData("{\"content\":\"not-array\"}")]
    [InlineData("{\"no_content\":1}")]
    [InlineData("[]")]
    public void Malformed_non_stream_replies_throw(string body)
    {
        // illegal: 非法非流式体
        Assert.Throws<JsonException>(() => _protocol.ParseNonStreamResponse(body));
    }
}
