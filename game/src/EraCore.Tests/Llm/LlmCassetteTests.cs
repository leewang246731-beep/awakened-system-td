using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// Cassette 回放（离线金样）：录制器产物 mimo-buildplan-tool.json 经真实网关
/// 解析层复刻在线结果（MiMo function calling 已验证形态）；同时断言 cassette
/// 永不携带密钥面（无认证头、无 key 形态串）。
/// </summary>
// [F-162] C-16 T-072：cassette 金样回放（录制器生成，禁手写——CS-3.4）
public class LlmCassetteTests
{
    private static string CassettePath(string name) =>
        Path.Combine(LlmCassettes.CassetteDir(), name + ".json");

    private static LlmCassette Load(string name)
    {
        var path = CassettePath(name);
        Assert.True(File.Exists(path), $"cassette 缺失：{path}（须由录制器生成，见 LlmOnlineTests）");
        return JsonSerializer.Deserialize<LlmCassette>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("cassette 反序列化为 null");
    }

    [Fact]
    public void Mimo_cassette_exists_and_stores_no_secret_surface()
    {
        // legal: 录制产物在库且无密钥面（RL5）
        var path = CassettePath("mimo-buildplan-tool");
        var rawText = File.ReadAllText(path);
        var cassette = JsonSerializer.Deserialize<LlmCassette>(rawText)!;

        Assert.Equal("xiaomi-mimo", cassette.ProviderId);
        Assert.Equal("openai", cassette.Protocol);
        Assert.Equal(200, cassette.ResponseStatus);
        Assert.True(cassette.ResponseIsSse);

        foreach (var header in cassette.RequestHeaders)
        {
            Assert.False(
                LlmCassettes.SecretHeaderNames.Contains(header.Key),
                $"cassette 携带认证头 {header.Key}");
        }

        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(rawText, "sk-[A-Za-z0-9]{8,}"),
            "cassette 含密钥形态串");
        Assert.DoesNotContain("Bearer ", rawText);
    }

    [Fact]
    public async Task Mimo_cassette_replays_to_the_verified_buildplan_tool_call()
    {
        // legal: 回放复刻在线验证结果（“在东门建两座弩塔”→BuildPlan 两格）
        var cassette = Load("mimo-buildplan-tool");

        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("xiaomi-mimo", "synthetic-replay-key");
        var provider = LlmProviderPresets.XiaomiMiMo() with
        {
            Enable = true,
            BaseUrl = cassette.BaseUrl,
            Model = cassette.Model,
        };
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: provider),
            port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        port.EnqueueReply(_ => new HttpPortResponse(
            200, null, new ChunkedStream(string.Join("\n", cassette.ResponseLines), 6)));

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey ?? "回放失败");
        Assert.Null(result.ErrorKey);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        var towerId = action.RootElement.GetProperty("tower_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(towerId));
        Assert.Equal(2, action.RootElement.GetProperty("cells").GetArrayLength());

        // 回放请求与录制的请求线一致（域名白名单锁同一域）
        var call = Assert.Single(port.Calls);
        Assert.Equal(new Uri(cassette.RequestUrl).Authority, call.Url.Authority);
    }

    [Fact]
    public void SerializeMasked_strips_auth_headers_and_keeps_the_rest()
    {
        // legal: 认证头剥离、普通头保留（RL5 单闸：认证头不进 cassette）
        var clean = new LlmCassette(
            "clean", "2026-10-13T00:00:00Z", "p", "openai", "m", "https://a.example.com/v1",
            "https://a.example.com/v1/chat/completions",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["x-api-key"] = "whatever",
            },
            "{}", 200, false, ["x"]);

        var json = LlmCassettes.SerializeMasked(clean, knownKey: "sk-not-present-here-000000");

        Assert.DoesNotContain("x-api-key", json);
        Assert.Contains("Content-Type", json);
    }

    [Fact]
    public void SerializeMasked_refuses_to_emit_a_key_hidden_outside_auth_headers()
    {
        // illegal: key 藏进请求体（中转站把 key 塞 body 的坑位）→ 拒写（RL5 双闸）
        var dirty = new LlmCassette(
            "dirty", "2026-10-13T00:00:00Z", "p", "openai", "m", "https://a.example.com/v1",
            "https://a.example.com/v1/chat/completions",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            "{\"extra_api_key\":\"sk-synthetic-1234567890abcdef\"}",
            200, false, ["x"]);

        Assert.Throws<InvalidOperationException>(
            () => LlmCassettes.SerializeMasked(dirty, "sk-synthetic-1234567890abcdef"));
    }
}
