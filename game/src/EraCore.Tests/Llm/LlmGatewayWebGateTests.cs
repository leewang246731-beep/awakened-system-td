using EraCore.Contracts.Agent;
using EraCore.Llm;
using EraCore.Secrets;
using EraCore.Tests.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// [F-164] LLM 网关平台门（T-106）：cloudTierEnabled=false（Web 版，表见
/// <c>WebConstraints.Availability(true)</c>）时，一切路由在其余所有门
/// （provider 存在/enable/白名单/Key）之前被拒——错误键
/// <see cref="LlmErrorKeys.WebTierDisabled"/>，零网络（NeverPort 零调用
/// 即证）、零 Key 访问、零重试。桌面默认（true）行为不变。
/// </summary>
// [F-164][F-162] C-16/C-26 T-106：Web 版 L3 禁用的网关执法点
public class LlmGatewayWebGateTests
{
    /// <summary>
    /// 最强拒绝场景：provider 启用 + Key 在库 + 域名在白名单——其余一切门
    /// 都会放行，唯独平台门拦截（证明门序在一切常规检查之前）。
    /// </summary>
    private static (LlmGateway Gateway, NeverPort Port, AccessCountingSecretStore Secrets)
        WebDisabledGateway(LlmCallLogRing? callLog = null)
    {
        var provider = ActiveProvider(); // enable=true, https://api.test.example.com/v1
        var secrets = new AccessCountingSecretStore();
        secrets.Store(provider.Id, "test-key-material-0000");
        var port = new NeverPort();
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: provider), port, secrets,
            callLog: callLog, cloudTierEnabled: false);
        return (gateway, port, secrets);
    }

    private static LlmProviderConfig ActiveProvider() => LlmFixtures.TestProvider();

    [Fact]
    public async Task Web_disabled_gateway_refuses_route_with_zero_network_and_zero_key_access()
    {
        // legal: Web 拒绝——错误键正确 + wire 端口零调用 + Key 零读取
        var (gateway, port, secrets) = WebDisabledGateway();

        var result = await gateway.RouteAsync(
            ActiveProvider(), [new LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.WebTierDisabled, result.ErrorKey);
        Assert.Null(result.ActionJson);
        Assert.Equal(0, port.CallCount);
        Assert.Equal(0, secrets.RetrieveCalls);
    }

    [Fact]
    public async Task Web_disabled_gateway_refuses_even_for_a_null_provider()
    {
        // boundary: 平台门先于 provider 存在性检查——配置缺失也无从联网
        var (gateway, port, _) = WebDisabledGateway();

        var result = await gateway.RouteAsync(null, [], "[]");

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.WebTierDisabled, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Web_disabled_gateway_refuses_the_transport_path_without_retry()
    {
        // boundary: K-9 SendAsync 路径同门拦截；Attempts=1（平台拒绝=终态，
        // 不进重试矩阵）
        var (gateway, port, _) = WebDisabledGateway();

        var transport = (ILlmTransport)gateway;
        var result = await transport.SendAsync(
            new LlmTransportRequest([new LlmChatMessage("user", "建塔")], "[]"))
            ;

        Assert.Null(result.ActionJson);
        Assert.Equal(LlmErrorKeys.WebTierDisabled, result.ErrorKey);
        Assert.Equal(0, port.CallCount);

        var routed = await gateway.RouteAsync(ActiveProvider(), [], "[]");
        Assert.Equal(1, routed.Attempts);
    }

    [Fact]
    public async Task Web_disabled_gateway_records_a_call_log_row()
    {
        // legal: 拒绝也留调用日志行（F-091 审计完整——平台拒绝可观测）
        var ring = new LlmCallLogRing(capacity: 4, clock: new FixedWallClock(0));
        var (gateway, _, _) = WebDisabledGateway(callLog: ring);

        _ = await gateway.RouteAsync(
            ActiveProvider(), [new LlmChatMessage("user", "建塔")], "[]")
            ;

        var row = Assert.Single(ring.Snapshot());
        Assert.Equal(LlmErrorKeys.WebTierDisabled, row.ErrorKey);
    }

    [Fact]
    public async Task Desktop_default_gateway_is_untouched_by_the_gate()
    {
        // boundary: 默认 cloudTierEnabled=true——平台门不存在，正常拒绝链
        // （此处禁用 provider 的 Disabled 键）照旧生效
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: LlmFixtures.TestProvider(enable: false)),
            new NeverPort(), new AccessCountingSecretStore());

        var result = await gateway.SendAsync(new LlmTransportRequest([], "[]"))
            ;

        Assert.Null(result.ActionJson);
        Assert.Equal(LlmErrorKeys.Disabled, result.ErrorKey); // 走的还是原 enable 门
    }

    /// <summary>
    /// InMemorySecretStore + Retrieve 计数：Web 拒绝必须做到 Key 零访问
    /// （连密钥存储都不触碰）。
    /// </summary>
    private sealed class AccessCountingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public int RetrieveCalls { get; private set; }

        public bool IsAvailable => _inner.IsAvailable;

        public string Backend => _inner.Backend;

        public void Store(string providerId, string? secretValue) =>
            _inner.Store(providerId, secretValue);

        public string Retrieve(string providerId)
        {
            RetrieveCalls++;
            return _inner.Retrieve(providerId);
        }

        public void Clear(string providerId) => _inner.Clear(providerId);

        public void ClearAll() => _inner.ClearAll();
    }
}
