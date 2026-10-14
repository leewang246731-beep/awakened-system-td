using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// LlmGateway 逻辑测试（F-162）：整体禁用/未知 provider/缺 Key/域名白名单
/// 违例一律零网络（FakeWirePort 计数=0）；正常路径的请求线形态、错误分类
/// 透传、注入防御与双超时。
/// </summary>
// [F-162][F-079] C-16 T-072：网关编排与零网络断言
public class LlmGatewayTests
{
    private static readonly TimeSpan FastFirstByte = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FastIdle = TimeSpan.FromSeconds(30);

    private static (LlmGateway Gateway, FakeWirePort Port, InMemorySecretStore Secrets) Build(
        LlmConfig? config = null, LlmProviderConfig? activeOverride = null,
        LlmDomainWhitelist? whitelist = null,
        TimeSpan? firstByte = null, TimeSpan? idle = null)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store(config?.Providers.FirstOrDefault()?.Id ?? "test-provider", "synthetic-key-value-000");
        var resolved = config ?? LlmFixtures.TestConfig();
        if (activeOverride is not null)
        {
            resolved = resolved with { ActiveProvider = activeOverride.Id };
        }

        var gateway = new LlmGateway(
            resolved, port, secrets,
            firstByte ?? FastFirstByte, idle ?? FastIdle, whitelist);
        return (gateway, port, secrets);
    }

    private static Task<LlmGatewayCallResult> SendAsync(
        LlmGateway gateway, string toolsJson = LlmFixtures.BuildPlanToolsJson) =>
        gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new LlmChatMessage("user", "在东门建两座弩塔")],
            toolsJson);

    // ---------------------------------------------- 零网络拒绝（前置闸门）

    [Fact]
    public async Task Disabled_provider_refuses_every_call_with_zero_network()
    {
        // illegal: enable=false ⇒ 零网络（整体禁用断言）
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(enable: false)));

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.Disabled, result.ErrorKey);
        Assert.Null(result.ActionJson);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Missing_active_provider_refuses_with_zero_network()
    {
        // illegal: active_provider 指向不存在的行 ⇒ 零网络
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(activeId: "ghost"));

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.UnknownProvider, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Missing_key_refuses_with_zero_network()
    {
        // illegal: ISecretStore 无该 provider 的 key ⇒ 零网络（不猜、不发）
        var (gateway, port, secrets) = Build();
        secrets.Clear("test-provider");

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.MissingKey, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Endpoint_outside_the_whitelist_is_refused_with_zero_network()
    {
        // illegal: provider 域名不在冻结白名单 ⇒ 零网络（白名单端口注入，
        // 证明强制执行本身——生产路径由配置推导同一集合）
        var foreign = LlmDomainWhitelist.FromBaseUrls(["https://other.example.com/v1"]);
        var (gateway, port, _) = Build(whitelist: foreign);

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.DomainNotAllowed, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Malformed_tools_projection_is_refused_with_zero_network()
    {
        // illegal: 工具表非 JSON/非数组 ⇒ BadTools 且零网络
        var (gateway, port, _) = Build();

        var notJson = await SendAsync(gateway, toolsJson: "not-json");
        var notArray = await SendAsync(gateway, toolsJson: "{\"a\":1}");

        Assert.Equal(LlmErrorKeys.BadTools, notJson.ErrorKey);
        Assert.Equal(LlmErrorKeys.BadTools, notArray.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    // ---------------------------------------------- 正常路径（非流式）

    [Fact]
    public async Task Happy_path_non_stream_builds_wire_request_and_merges_action()
    {
        // legal: 非流式 openai——URL/认证头/请求体/工具合并全链
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(useStream: false)));
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await SendAsync(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Null(result.ErrorKey);
        Assert.Equal(200, result.StatusCode);
        var call = Assert.Single(port.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("https://api.test.example.com/v1/chat/completions", call.Url.AbsoluteUri);
        Assert.Equal("Bearer synthetic-key-value-000", call.Headers["Authorization"]);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("BuildPlan", body.RootElement.GetProperty("tools")[0]
            .GetProperty("function").GetProperty("name").GetString());

        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.Equal("arrow_t1", action.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(1, action.RootElement.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public async Task Anthropic_provider_routes_to_v1_messages_with_its_auth_shape()
    {
        // legal: 协议路由（F-083）——anthropic 端点/头/回包归约
        // （anthropic 基址=API 根，路径 v1/messages 由协议拼）
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(
                baseUrl: "https://api.test.example.com",
                protocol: LlmProtocol.Anthropic, useStream: false)));
        port.EnqueueJson(LlmFixtures.AnthropicNonStream);

        var result = await SendAsync(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        var call = Assert.Single(port.Calls);
        Assert.Equal("https://api.test.example.com/v1/messages", call.Url.AbsoluteUri);
        Assert.Equal("synthetic-key-value-000", call.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", call.Headers["anthropic-version"]);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Tool_arguments_cannot_override_the_action_discriminator()
    {
        // illegal: 模型在参数里夹带 action 字段冒充别的动词——合并层以工具名覆盖
        var smuggled =
            """
            {"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"c","type":"function","function":{"name":"BuildPlan","arguments":"{\"action\":\"SellTower\",\"tower_id\":\"arrow_t1\",\"cells\":[{\"x\":1,\"y\":2}]}"}}]},"finish_reason":"tool_calls"}]}
            """;
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(useStream: false)));
        port.EnqueueJson(smuggled);

        var result = await SendAsync(gateway);

        Assert.True(result.Ok);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Text_only_reply_is_classified_no_tool_call()
    {
        // boundary: 2xx 但只有文本（中转站丢工具调用的典型形态）
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(useStream: false)));
        port.EnqueueJson(
            """
            {"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"好的，已了解。"},"finish_reason":"stop"}]}
            """);

        var result = await SendAsync(gateway);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.NoToolCall, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"message\":\"bad key\"}}", LlmErrorKeys.Auth)]
    [InlineData(429, LlmFixtures.ZhipuCodingPlanBody, LlmErrorKeys.CodingPlan)]
    [InlineData(400, LlmFixtures.MimoUnsupportedModelBody, LlmErrorKeys.InvalidModel)]
    [InlineData(500, "oops", LlmErrorKeys.ServerError)]
    [InlineData(302, "", LlmErrorKeys.RedirectBlocked)]
    public async Task Http_errors_classify_through_the_gateway(
        int status, string body, string expectedKey)
    {
        // legal: HTTP 错误→分类键直达 K-9 结果（有网络、有分类）
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(useStream: false)));
        port.EnqueueError(status, body);

        var result = await SendAsync(gateway);

        Assert.Equal(expectedKey, result.ErrorKey);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(1, port.CallCount);
    }

    [Fact]
    public async Task Transport_failure_classifies_as_network()
    {
        // illegal: 传输层异常（DNS/TLS/拒连）→ network 分类
        var (gateway, port, _) = Build();
        port.NextFailure = new IOException("connection refused");

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.Network, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
    }

    // ---------------------------------------------- 双超时（§11.7）

    [Fact]
    public async Task First_byte_budget_covers_the_port_call_itself()
    {
        // boundary: 连接阶段就无应答（DNS/TLS 挂起）
        var port = new NeverPort();
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(), port, KeyStore("test-provider"),
            firstByteTimeout: TimeSpan.FromMilliseconds(150));

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.TimeoutFirstByte, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
    }

    [Fact]
    public async Task Idle_budget_fires_when_the_stream_stalls_mid_body()
    {
        // boundary: 首帧已到、后续 chunk 挂起
        var (gateway, port, _) = Build(
            firstByte: TimeSpan.FromSeconds(10),
            idle: TimeSpan.FromMilliseconds(150));
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null,
            new StalledStream("data: {\"choices\":[{\"delta\":{\"content\":\"收到\"}}]}\n\n")));

        var result = await SendAsync(gateway);

        Assert.Equal(LlmErrorKeys.TimeoutIdle, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
    }

    [Fact]
    public async Task Streaming_success_within_budgets_passes()
    {
        // legal: 流式正常完成不受预算影响
        var (gateway, port, _) = Build(
            firstByte: TimeSpan.FromSeconds(10), idle: TimeSpan.FromSeconds(10));
        port.EnqueueSse(LlmFixtures.OpenAiSse);

        var result = await SendAsync(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.NotNull(result.ActionJson);
    }

    // ---------------------------------------------- K-9 端口形态

    [Fact]
    public async Task K9_port_returns_two_field_projection()
    {
        // legal: ILlmTransport 冻结签名（ActionJson/ErrorKey 二元）
        var (gateway, port, _) = Build(LlmFixtures.TestConfig(
            active: LlmFixtures.TestProvider(useStream: false)));
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var transport = (ILlmTransport)gateway;

        var reply = await transport.SendAsync(new LlmTransportRequest(
            [new LlmChatMessage("user", "建塔")], LlmFixtures.BuildPlanToolsJson));

        Assert.NotNull(reply.ActionJson);
        Assert.Null(reply.ErrorKey);
    }

    private static InMemorySecretStore KeyStore(string providerId)
    {
        var secrets = new InMemorySecretStore();
        secrets.Store(providerId, "synthetic-key-value-000");
        return secrets;
    }
}
