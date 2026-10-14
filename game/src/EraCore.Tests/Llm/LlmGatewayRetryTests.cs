using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 网关重试矩阵端到端（F-088）：可重试类（429/5xx/网络/1302/1305）按指数退避
/// 重试至成功或耗尽（≤3）；401/400 类零重试直接上浮；退避节奏经注入的
/// sleeper 记录断言（不真等）。
/// </summary>
// [F-088][F-162] C-16 T-091：网关重试编排
public class LlmGatewayRetryTests
{
    private static (LlmGateway Gateway, FakeWirePort Port, List<TimeSpan> Backoffs) Build(
        int retries, LlmProviderConfig? provider = null)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var row = provider ?? LlmFixtures.TestProvider(useStream: false, retries: retries);
        secrets.Store(row.Id, "synthetic-key-value-000");
        var backoffs = new List<TimeSpan>();
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: row), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            retryDelay: (delay, _) =>
            {
                backoffs.Add(delay);
                return Task.CompletedTask;
            });
        return (gateway, port, backoffs);
    }

    private static Task<LlmGatewayCallResult> Send(LlmGateway gateway) =>
        gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

    [Fact]
    public async Task Rate_limited_call_retries_to_success_with_exponential_backoff()
    {
        // legal: 429→429→200（两次退避 0.5s/1s 注入记录，不真等）
        var (gateway, port, backoffs) = Build(retries: 3);
        port.EnqueueError(429, "{\"error\":{\"message\":\"rate limited\"}}");
        port.EnqueueError(429, "{\"error\":{\"message\":\"rate limited\"}}");
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal(3, port.CallCount);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)],
            backoffs);
    }

    [Fact]
    public async Task Server_error_exhausts_the_retry_budget_and_reports_the_last_verdict()
    {
        // boundary: 5xx ×(1+3) 次全失败——第 4 次后停（≤3 封顶），键=server-error
        var (gateway, port, backoffs) = Build(retries: 9); // 超上限配置 → 钳制 3
        for (var i = 0; i < 4; i++)
        {
            port.EnqueueError(500, "upstream down");
        }

        var result = await Send(gateway);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.ServerError, result.ErrorKey);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(4, port.CallCount);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(3, backoffs.Count);
    }

    [Fact]
    public async Task Transport_failure_is_retried_like_a_server_error()
    {
        // legal: 网络错（拒连类）属可重试集——第 2 次尝试成功（首访抛、次访答；
        // retries=1 ⇒ 总尝试数=2）
        var (gateway, port, backoffs) = Build(retries: 1);
        port.EnqueueReply(_ => throw new IOException("connection refused"));
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal(2, port.CallCount);
        Assert.Equal(2, result.Attempts);
        Assert.Single(backoffs);
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"message\":\"bad key\"}}", LlmErrorKeys.Auth)]
    [InlineData(400, "{\"error\":{\"message\":\"bad args\"}}", LlmErrorKeys.BadRequest)]
    [InlineData(429, LlmFixtures.ZhipuCodingPlanBody, LlmErrorKeys.CodingPlan)]
    [InlineData(302, "", LlmErrorKeys.RedirectBlocked)]
    public async Task Non_retryable_verdicts_return_after_one_attempt(
        int status, string body, string expectedKey)
    {
        // illegal: 401/400/1315/3xx 零重试（引导修配置），退避表为空
        var (gateway, port, backoffs) = Build(retries: 3);
        port.EnqueueError(status, body);

        var result = await Send(gateway);

        Assert.Equal(expectedKey, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
        Assert.Equal(1, result.Attempts);
        Assert.Empty(backoffs);
    }

    [Fact]
    public async Task Transient_provider_code_inside_a_4xx_shell_still_retries()
    {
        // legal: 中转站把 1302 包进 400——键=bad-request 但码可重试（§11.7 钉死集）
        var (gateway, port, backoffs) = Build(retries: 2);
        port.EnqueueError(400, "{\"error\":{\"code\":\"1302\",\"message\":\"busy\"}}");
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await Send(gateway);

        Assert.True(result.Ok, result.ErrorKey);
        Assert.Equal(2, port.CallCount);
        Assert.Single(backoffs);
    }

    [Fact]
    public async Task Zero_retry_budget_is_single_shot()
    {
        // boundary: retries=0 配置——可重试错误也不重试
        var (gateway, port, backoffs) = Build(retries: 0);
        port.EnqueueError(500, "down");
        port.EnqueueError(500, "down"); // 若误重试会吃掉这行并暴露为成功

        var result = await Send(gateway);

        Assert.Equal(LlmErrorKeys.ServerError, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
        Assert.Equal(1, result.Attempts);
        Assert.Empty(backoffs);
    }

    [Fact]
    public async Task Timeouts_do_not_retry_even_with_budget()
    {
        // boundary: 首字节超时不在 §11.7 可重试集——立即上浮（双超时已经封顶等待）
        var (gateway, port, backoffs) = Build(retries: 3);
        port.NextFailure = new OperationCanceledException(
            "first-byte budget", new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token);

        var result = await Send(gateway);

        Assert.Equal(LlmErrorKeys.TimeoutFirstByte, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
        Assert.Empty(backoffs);
    }

    [Fact]
    public async Task Retries_reuse_the_same_trimmed_context()
    {
        // legal: 裁剪每路由一次（F-090 组合断言）——两次重试的请求体逐字节一致
        var (gateway, port, _) = Build(retries: 2);
        port.EnqueueError(429, "{\"error\":{\"message\":\"rate limited\"}}");
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);
        var history = new List<LlmChatMessage>
        {
            new("system", "你是建造指挥官。"),
            new("user", "早期军令"),
            new("assistant", "已建箭楼。"),
            new("user", "在东门建两座弩塔"),
        };

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            history, LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey);
        var bodies = port.Calls.Select(c => c.Body).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        // history_rounds=10（TestProvider）下 4 条消息全保留——重试不带早轮漂移
        using var sent = System.Text.Json.JsonDocument.Parse(bodies[0]);
        Assert.Equal(4, sent.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Caller_cancellation_during_backoff_propagates()
    {
        // illegal: 玩家在退避窗口取消——上抛 OperationCanceledException（不吞成分类键）
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var row = LlmFixtures.TestProvider(useStream: false, retries: 3);
        secrets.Store(row.Id, "synthetic-key-value-000");
        using var cancel = new CancellationTokenSource();
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: row), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            retryDelay: (delay, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled(token);
            });
        port.EnqueueError(429, "{\"error\":{\"message\":\"rate limited\"}}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.RouteAsync(
                gateway.FindProvider(gateway.ActiveProviderId),
                [new LlmChatMessage("user", "建塔")],
                LlmFixtures.BuildPlanToolsJson,
                cancel.Token));
    }
}
