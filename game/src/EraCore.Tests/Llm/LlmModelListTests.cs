using EraCore.Llm;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// C-26 设置页「拉取列表」纯逻辑（T-077; 方案 §11.6 模型可 /models
/// 拉取+手填）：端点形状（openai {base}/models、anthropic {base}/v1/models）、
/// 认证头来自协议适配器、零网络拒绝矩阵（禁用/缺 key/域名不匹配/坏配置）、
/// 错误分类文案透传与成功文案（域名+数量，中英）。
/// </summary>
// [F-081] C-26 T-077：模型列表拉取（/models）
public class LlmModelListTests
{
    private static LlmProviderConfig Provider(
        bool enable = true,
        string baseUrl = "https://api.xiaomimimo.com/v1",
        LlmProtocol protocol = LlmProtocol.OpenAi) => new()
    {
        Id = "list-provider",
        Name = "列表测试行",
        Protocol = protocol,
        BaseUrl = baseUrl,
        Model = "mimo-v2.6-pro",
        Enable = enable,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = 2,
        HistoryRounds = 10,
        ContextBudgetTokens = 16000,
        UseStream = true,
    };

    private static (FakeWirePort Port, InMemorySecretStore Secrets) Wire(string? key = "synthetic-key-000")
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        if (key is not null)
        {
            secrets.Store("list-provider", key);
        }

        return (port, secrets);
    }

    // ---------------------------------------------- 成功路径

    [Fact]
    public async Task Openai_provider_fetches_base_models_with_bearer_auth()
    {
        // legal: openai 协议 GET {base}/models，Bearer 头来自协议适配器
        var (port, secrets) = Wire();
        port.EnqueueJson("""{"data":[{"id":"mimo-v2.6-pro"},{"id":"mimo-v2.6-flash"},{"id":""}]}""");

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets);

        Assert.True(result.Ok);
        Assert.Null(result.ErrorKey);
        // 空 id 条目被丢弃，服务器顺序保留
        Assert.Equal(new[] { "mimo-v2.6-pro", "mimo-v2.6-flash" }, result.ModelIds);
        var call = Assert.Single(port.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("https://api.xiaomimimo.com/v1/models", call.Url.ToString());
        Assert.Equal("Bearer synthetic-key-000", call.Headers["Authorization"]);
        Assert.Contains("api.xiaomimimo.com", result.MessageZh);
        Assert.Contains("2", result.MessageZh);
        Assert.Contains("api.xiaomimimo.com", result.MessageEn);
    }

    [Fact]
    public async Task Anthropic_provider_fetches_v1_models_with_api_key_header()
    {
        // legal: anthropic 协议 GET {base}/v1/models，x-api-key 头（版本段与 /v1/messages 同形）
        var (port, secrets) = Wire();
        port.EnqueueJson("""{"data":[{"id":"claude-x"}]}""");

        var result = await LlmModelList.FetchAsync(
            Provider(baseUrl: "https://api.anthropic.com", protocol: LlmProtocol.Anthropic),
            port, secrets);

        Assert.True(result.Ok);
        var call = Assert.Single(port.Calls);
        Assert.Equal("https://api.anthropic.com/v1/models", call.Url.ToString());
        Assert.Equal("synthetic-key-000", call.Headers["x-api-key"]);
        Assert.Equal(new[] { "claude-x" }, result.ModelIds);
    }

    [Fact]
    public async Task Empty_model_list_is_a_valid_success()
    {
        // boundary: 空列表=成功（消息数量为 0），不是错误
        var (port, secrets) = Wire();
        port.EnqueueJson("""{"data":[]}""");

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets);

        Assert.True(result.Ok);
        Assert.Empty(result.ModelIds);
        Assert.Contains("0", result.MessageZh);
    }

    // ---------------------------------------------- 零网络拒绝

    [Fact]
    public async Task Disabled_provider_refuses_with_zero_network()
    {
        // illegal: 禁用行拒绝（网关同款零网络纪律）
        var (port, secrets) = Wire();

        var result = await LlmModelList.FetchAsync(Provider(enable: false), port, secrets);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.Disabled, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
        Assert.False(string.IsNullOrEmpty(result.MessageZh));
        Assert.False(string.IsNullOrEmpty(result.MessageEn));
    }

    [Fact]
    public async Task Missing_key_refuses_with_zero_network()
    {
        // illegal: SecretStore 无 key ⇒ 不发请求（missing-key 分类文案）
        var (port, secrets) = Wire(key: null);

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.MissingKey, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Domain_not_in_whitelist_refuses_with_zero_network()
    {
        // illegal: 白名单不含 provider 域 ⇒ 零网络（设置页传网关冻结白名单同形）
        var (port, secrets) = Wire();
        var whitelist = LlmDomainWhitelist.FromProviders(
            [Provider(baseUrl: "https://open.bigmodel.cn/api/paas/v4")]);

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets, whitelist);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.DomainNotAllowed, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Broken_base_url_refuses_as_invalid_config()
    {
        // illegal: 启用行坏 base_url ⇒ InvalidConfig（零网络）
        var (port, secrets) = Wire();

        var result = await LlmModelList.FetchAsync(
            Provider(baseUrl: "not a url"), port, secrets);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.InvalidConfig, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    // ---------------------------------------------- 线上错误分类透传

    [Fact]
    public async Task Http_error_surfaces_the_classified_catalog_copy()
    {
        // illegal: 401 → auth 分类，中英文案来自 LlmErrorCatalog
        var (port, secrets) = Wire();
        port.EnqueueError(401, """{"error":{"message":"invalid api key"}}""");

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.Auth, result.ErrorKey);
        Assert.Equal(1, port.CallCount);
        Assert.Contains("密钥", result.MessageZh);
        Assert.False(string.IsNullOrEmpty(result.MessageEn));
    }

    [Fact]
    public async Task Unparsable_body_is_a_bad_response()
    {
        // illegal: 200 但非 JSON /models 形状 ⇒ bad-response
        var (port, secrets) = Wire();
        port.EnqueueJson("<html>gateway login page</html>");

        var result = await LlmModelList.FetchAsync(Provider(), port, secrets);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.BadResponse, result.ErrorKey);
    }

    [Fact]
    public async Task Transport_failure_and_timeout_classify_without_throwing()
    {
        // illegal: 传输异常 → network；取消（超时预算）→ timeout-first-byte
        var (port, secrets) = Wire();
        port.NextFailure = new IOException("connection refused");
        var network = await LlmModelList.FetchAsync(Provider(), port, secrets);
        Assert.Equal(LlmErrorKeys.Network, network.ErrorKey);

        port.NextFailure = new OperationCanceledException();
        var timeout = await LlmModelList.FetchAsync(Provider(), port, secrets);
        Assert.Equal(LlmErrorKeys.TimeoutFirstByte, timeout.ErrorKey);
    }

    // ---------------------------------------------- T-110 平台门（代码路径级）

    [Fact]
    public async Task Web_disabled_fetch_refuses_with_zero_network_and_zero_key_access()
    {
        // illegal: cloudTierEnabled=false（Web 档位行）⇒ web-tier-disabled——
        // 最强场景（启用行+Key 在库+域名在白名单）也在一切常规门之前被拒，
        // wire 端口零调用、密钥存储零读取（/models 绕过网关，门在 LlmModelList 本体）
        var port = new FakeWirePort();
        var secrets = new AccessCountingStore();
        secrets.Store("list-provider", "synthetic-key-000");

        var result = await LlmModelList.FetchAsync(
            Provider(), port, secrets, cloudTierEnabled: false);

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.WebTierDisabled, result.ErrorKey);
        Assert.Empty(result.ModelIds);
        Assert.Equal(0, port.CallCount);
        Assert.Equal(0, secrets.RetrieveCalls); // 平台拒绝不触碰密钥存储
        Assert.False(string.IsNullOrEmpty(result.MessageZh)); // 分类文案链照常供给
        Assert.False(string.IsNullOrEmpty(result.MessageEn));
    }

    [Fact]
    public async Task Web_disabled_fetch_gate_precedes_the_disabled_provider_check()
    {
        // boundary: 平台门先于 enable 门——禁用行+Web 档位行双叠时以平台键拒绝
        var (port, secrets) = Wire();

        var result = await LlmModelList.FetchAsync(
            Provider(enable: false), port, secrets, cloudTierEnabled: false);

        Assert.Equal(LlmErrorKeys.WebTierDisabled, result.ErrorKey);
        Assert.NotEqual(LlmErrorKeys.Disabled, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Desktop_default_fetch_is_untouched_by_the_platform_gate()
    {
        // boundary: 默认 cloudTierEnabled=true——门不存在，常规拒绝链照旧
        // （此处禁用行走 Disabled 键，与 T-106 前行为逐字节一致）
        var (port, secrets) = Wire();

        var result = await LlmModelList.FetchAsync(Provider(enable: false), port, secrets);

        Assert.Equal(LlmErrorKeys.Disabled, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    /// <summary>
    /// ISecretStore 七端口 fake（CS-3.4 白名单）：Retrieve 计数——T-110 平台门
    /// 的零 Key 访问证据（拒绝须发生在密钥存储被触碰之前）。
    /// </summary>
    private sealed class AccessCountingStore : ISecretStore
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
