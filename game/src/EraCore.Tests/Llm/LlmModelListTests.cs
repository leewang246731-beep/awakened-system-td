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
}
