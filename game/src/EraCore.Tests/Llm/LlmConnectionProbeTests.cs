using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 连接测试探针（F-085）：最小消息+单工具探针走网关全链；成功文案带域名与
/// 耗时（中英），失败取分类文案表；禁用/401/纯文本各归其类。
/// </summary>
// [F-085] C-16 T-072：连接测试探针
public class LlmConnectionProbeTests
{
    private const string ProbeSseOk =
        """
        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"EraPing","arguments":""}}]}}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pong\":true}"}}]}}]}

        data: [DONE]

        """;

    private static (LlmConnectionProbe Probe, FakeWirePort Port) Build(
        LlmProviderConfig? active = null)
    {
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var provider = active ?? LlmFixtures.TestProvider();
        secrets.Store(provider.Id, "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: provider), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return (new LlmConnectionProbe(gateway), port);
    }

    [Fact]
    public async Task Successful_probe_verifies_tool_passthrough_with_bilingual_copy()
    {
        // legal: 200+SSE 工具调用 → 双语成功文案（域名+耗时）
        var (probe, port) = Build();
        port.EnqueueSse(ProbeSseOk);

        var result = await probe.ProbeAsync("test-provider");

        Assert.True(result.Ok, result.ErrorKey ?? result.MessageZh);
        Assert.Null(result.ErrorKey);
        Assert.True(result.ToolCallVerified);
        Assert.Contains("api.test.example.com", result.MessageZh);
        Assert.Contains("api.test.example.com", result.MessageEn);
        Assert.Contains("透传", result.MessageZh);
        Assert.Contains("verified", result.MessageEn);
        var call = Assert.Single(port.Calls);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("EraPing", body.RootElement.GetProperty("tools")[0]
            .GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Disabled_provider_probe_is_refused_with_zero_network()
    {
        // illegal: 禁用行探针也零网络
        var (probe, port) = Build(LlmFixtures.TestProvider(enable: false));

        var result = await probe.ProbeAsync("test-provider");

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.Disabled, result.ErrorKey);
        Assert.Contains("未启用", result.MessageZh);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Auth_failure_probe_surfaces_catalog_copy()
    {
        // boundary: 401 → 分类文案（密钥无效）
        var (probe, port) = Build();
        port.EnqueueError(401, "{\"error\":{\"message\":\"bad key\"}}");

        var result = await probe.ProbeAsync("test-provider");

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.Auth, result.ErrorKey);
        Assert.Contains("密钥无效", result.MessageZh);
        Assert.False(result.ToolCallVerified);
    }

    [Fact]
    public async Task Text_only_reply_probe_reports_the_relay_tool_loss_hint()
    {
        // boundary: 通了但模型只回文本（§11.6 中转站坑位提示）
        var (probe, port) = Build();
        port.EnqueueSse(
            """
            data: {"choices":[{"index":0,"delta":{"content":"pong"}}]}

            data: [DONE]

            """);

        var result = await probe.ProbeAsync("test-provider");

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.NoToolCall, result.ErrorKey);
        Assert.False(result.ToolCallVerified);
        Assert.Contains("中转站", result.MessageZh);
    }

    [Fact]
    public async Task Unknown_provider_probe_fails_cleanly()
    {
        // illegal: 未知 id
        var (probe, port) = Build();

        var result = await probe.ProbeAsync("no-such-provider");

        Assert.False(result.Ok);
        Assert.Equal(LlmErrorKeys.UnknownProvider, result.ErrorKey);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task Probe_of_a_second_configured_provider_targets_its_own_domain()
    {
        // legal: 多 provider 各自锁域——探 second-provider 走其自身端点
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("second-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var probe = new LlmConnectionProbe(gateway);
        port.EnqueueSse(ProbeSseOk);

        var result = await probe.ProbeAsync("second-provider");

        Assert.True(result.Ok, result.MessageZh);
        var call = Assert.Single(port.Calls);
        Assert.Equal("https://relay.test.example.com/v1/chat/completions", call.Url.AbsoluteUri);
    }
}
