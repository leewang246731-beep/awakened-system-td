using EraCore.Llm;
using Xunit;
using static EraCore.Llm.LlmErrorKeys;

namespace EraCore.Tests.Llm;

/// <summary>
/// 19 键错误分类全覆盖（F-089, T-091）：LlmErrorKeys 的每一个键都有一条
/// FakeWirePort/网关场景真实产出（含 3xx 重定向拒绝/NaN 响应/truncated body），
/// 并显式断言键集基数为 19——新键落进来自动红。
/// </summary>
// [F-089][F-162] C-16 T-091：错误键全集网关场景矩阵
public class LlmGatewayErrorMatrixTests
{
    private static async Task<LlmGatewayCallResult> Route(
        Func<FakeWirePort, HttpPortResponse?> script,
        LlmProviderConfig? provider = null,
        LlmDomainWhitelist? whitelist = null,
        bool clearKey = false,
        string? activeId = null)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var row = provider ?? LlmFixtures.TestProvider(useStream: false, retries: 0);
        secrets.Store(row.Id, "synthetic-key-value-000");
        if (clearKey)
        {
            secrets.Clear(row.Id);
        }

        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: row, activeId: activeId), port, secrets,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), whitelist);
        var wiredReply = script(port);
        if (wiredReply is not null)
        {
            port.EnqueueReply(_ => wiredReply);
        }

        return await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);
    }

    private static HttpPortResponse Error(int status, string body) => new(status, body, null);

    private static Task<LlmGatewayCallResult> RouteWithTools(string toolsJson) =>
        Route(port =>
        {
            port.EnqueueReply(_ => Error(200, LlmFixtures.OpenAiNonStream));
            return null;
        }, toolsJsonOverride: toolsJson);

    [Fact]
    public async Task Every_one_of_the_nineteen_keys_is_produced_by_a_gateway_scenario()
    {
        // legal: 19 键逐键可产出（零网络拒绝/线级错误/流级错误三族全覆盖）
        var produced = new Dictionary<string, LlmGatewayCallResult>
        {
            [Disabled] = await Route(_ => null,
                provider: LlmFixtures.TestProvider(enable: false, useStream: false)),
            [UnknownProvider] = await Route(_ => null, activeId: "ghost"),
            [MissingKey] = await Route(_ => null, clearKey: true),
            [DomainNotAllowed] = await Route(_ => null,
                whitelist: LlmDomainWhitelist.FromBaseUrls(["https://other.example.com/v1"])),
            [InvalidConfig] = await Route(_ => null,
                provider: LlmFixtures.TestProvider(baseUrl: "not a url", useStream: false)),
            [Auth] = await Route(_ => Error(401, "{\"error\":{\"message\":\"bad key\"}}")),
            [RateLimit] = await Route(_ => Error(429, "{\"error\":{\"message\":\"slow down\"}}")),
            [CodingPlan] = await Route(_ => Error(429, LlmFixtures.ZhipuCodingPlanBody)),
            [ContentBlocked] = await Route(_ => Error(400, LlmFixtures.ZhipuContentBlockedBody)),
            [InvalidModel] = await Route(_ => Error(400, LlmFixtures.MimoUnsupportedModelBody)),
            [BadRequest] = await Route(_ => Error(400, "{\"error\":{\"message\":\"bad args\"}}")),
            [ServerError] = await Route(_ => Error(503, "upstream down")),
            [RedirectBlocked] = await Route(_ => Error(302,
                "<html>302 Found Location: https://elsewhere.example.com/</html>")),
            [Network] = await Route(port =>
            {
                port.NextFailure = new IOException("connection refused");
                return null;
            }),
            [BadResponse] = await Route(_ => Error(200,
                "{\"choices\":[{\"message\":{\"content\":NaN}}]}")),
            [NoToolCall] = await Route(_ => Error(200,
                "{\"choices\":[{\"message\":{\"content\":\"好的\"},\"finish_reason\":\"stop\"}]}")),
        };

        // BadTools：坏工具表（route 助手固定 BuildPlan 表，单列一次）
        produced[BadTools] = await RouteWithTools("not-json");

        // 双超时两键：小预算真墙钟（端口级挂起/流级空闲挂起）
        produced[TimeoutFirstByte] = await RouteTimeoutScenario(firstByte: true);
        produced[TimeoutIdle] = await RouteTimeoutScenario(firstByte: false);

        // 基数断言：恰 19 键（键集漂移即红）
        Assert.Equal(19, produced.Count);
        foreach (var (key, result) in produced)
        {
            Assert.False(result.Ok, $"{key} 场景不应成功");
            Assert.Equal(key, result.ErrorKey);
        }
    }

    private static async Task<LlmGatewayCallResult> Route(
        Func<FakeWirePort, HttpPortResponse?> script, string toolsJsonOverride)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var row = LlmFixtures.TestProvider(useStream: false, retries: 0);
        secrets.Store(row.Id, "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: row), port, secrets,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _ = script(port);
        return await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            toolsJsonOverride);
    }

    private static async Task<LlmGatewayCallResult> RouteTimeoutScenario(bool firstByte)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(useStream: !firstByte, retries: 0)),
            port, secrets,
            firstByte ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5),
            firstByte ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        if (firstByte)
        {
            port.NextFailure = new OperationCanceledException(
                new CancellationTokenSource(TimeSpan.FromMilliseconds(10)).Token);
        }
        else
        {
            port.EnqueueReply(_ => new HttpPortResponse(200, null,
                new StalledStream("data: {\"choices\":[{\"delta\":{\"content\":\"收\"}}]}\n\n")));
        }

        return await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);
    }

    [Fact]
    public async Task Redirect_reply_is_refused_after_exactly_one_request()
    {
        // illegal: 3xx 重定向——分类拒绝且只发一次请求（域名锁不可被 Location 绕过）
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(useStream: false, retries: 3)),
            port, secrets, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        port.EnqueueReply(_ => new HttpPortResponse(
            307, "Location: https://evil.example.com/steal", null));

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.Equal(RedirectBlocked, result.ErrorKey);
        Assert.Equal(307, result.StatusCode);
        Assert.Equal(1, port.CallCount);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":NaN}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"收到")]
    [InlineData("")]
    [InlineData("null")]
    public async Task Unparseable_success_bodies_classify_as_bad_response(string body)
    {
        // illegal: 2xx 但 NaN/截断/空体/null——一律 bad-response，不向调用方抛原始异常
        var result = await Route(_ => new HttpPortResponse(200, body, null));

        Assert.Equal(BadResponse, result.ErrorKey);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Truncated_sse_body_without_done_still_reduces_arrived_frames()
    {
        // boundary: 截断的 SSE（无 [DONE]）——已到帧照常归约（半行拼接收尾）
        var truncated = LlmFixtures.OpenAiSse.Replace("data: [DONE]\n\n", string.Empty).TrimEnd();
        var result = await Route(_ => new HttpPortResponse(
            200, null, new ChunkedStream(truncated, 4)),
            provider: LlmFixtures.TestProvider(useStream: true, retries: 0));

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal("BuildPlan", result.Outcome!.ToolName);
    }
}
