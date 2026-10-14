using System.Text.Json;
using EraCore.Llm;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Llm;

/// <summary>
/// 合成 cassette（T-091 扩容）：错误场景 401 + 多工具并发交错 SSE 两件，经
/// RecordingPort 录制器在 FakeWirePort 之上生成（零成本，标注合成来源=
/// synthetic-fakewireport；D-20：文件由录制器产出，禁手写）。回放面：
/// 401→auth 且不重试；多工具交错→首工具合并无串槽。
/// </summary>
// [F-086][F-088][F-089][F-162] C-16 T-091：合成 cassette 扩容
public class LlmCassetteSyntheticTests
{
    private readonly ITestOutputHelper _output;

    public LlmCassetteSyntheticTests(ITestOutputHelper output) => _output = output;

    private static string CassettePath(string name) =>
        Path.Combine(LlmCassettes.CassetteDir(), name + ".json");

    private static LlmCassette Load(string name)
    {
        var path = CassettePath(name);
        Assert.True(File.Exists(path),
            $"cassette 缺失：{path}（须由录制器生成：ERA_RECORD_CASSETTE=1 dotnet test --filter LlmCassetteSynthetic）");
        return JsonSerializer.Deserialize<LlmCassette>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("cassette 反序列化为 null");
    }

    // ---------------------------------------------- 录制（显式请求，离线零成本）

    [Fact]
    public async Task Record_synthetic_cassettes_when_requested()
    {
        // boundary: 默认只回放不录制；ERA_RECORD_CASSETTE=1 时用 FakeWirePort
        // 脚本走真实录制器管线落盘（RL5：输出永不包含 key）
        if (Environment.GetEnvironmentVariable("ERA_RECORD_CASSETTE") != "1")
        {
            OnlineSkip.ReasonCaptured(_output, "未请求录制（ERA_RECORD_CASSETTE 未设为 1）");
            return;
        }

        var errorCassette = await RecordError401Async();
        var multiToolCassette = await RecordMultiToolAsync();

        Assert.True(File.Exists(errorCassette.Path));
        Assert.True(File.Exists(multiToolCassette.Path));
        Assert.Equal(LlmCassette.SyntheticSource, errorCassette.Cassette.Source);
        Assert.Equal(LlmCassette.SyntheticSource, multiToolCassette.Cassette.Source);
    }

    private static async Task<(LlmCassette Cassette, string Path)> RecordOverFakeWireAsync(
        string name, Func<FakeWirePort> scriptPort, LlmProviderConfig provider)
    {
        var fake = scriptPort();
        var recording = new RecordingPort(fake);
        var secrets = new InMemorySecretStore();
        secrets.Store(provider.Id, "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: provider), recording, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _ = await gateway.RouteAsync(
            gateway.FindProvider(provider.Id),
            [new EraCore.Contracts.Agent.LlmChatMessage("system", "你是建造指挥官。收到军令后必须调用工具。"),
             new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        var cassette = recording.BuildCassette(
            name, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            provider.Id, "openai", provider.Model, provider.BaseUrl,
            source: LlmCassette.SyntheticSource);
        var json = LlmCassettes.SerializeMasked(cassette, "synthetic-key-value-000");
        Directory.CreateDirectory(LlmCassettes.CassetteDir());
        var path = CassettePath(name);
        await File.WriteAllTextAsync(path, json);
        return (cassette, path);
    }

    private static async Task<(LlmCassette Cassette, string Path)> RecordError401Async()
    {
        var provider = LlmFixtures.TestProvider(useStream: false, retries: 0);
        return await RecordOverFakeWireAsync(
            "synthetic-error-401",
            () =>
            {
                var port = new FakeWirePort();
                port.EnqueueError(401, "{\"error\":{\"message\":\"Invalid API key\",\"type\":\"invalid_request_error\"}}");
                return port;
            },
            provider);
    }

    private static async Task<(LlmCassette Cassette, string Path)> RecordMultiToolAsync()
    {
        var provider = LlmFixtures.TestProvider(useStream: true, retries: 0);
        return await RecordOverFakeWireAsync(
            "synthetic-multi-tool-sse",
            () =>
            {
                var port = new FakeWirePort();
                port.EnqueueReply(_ => new HttpPortResponse(
                    200, null, new ChunkedStream(LlmFixtures.OpenAiSseMultiToolInterleaved, 7)));
                return port;
            },
            provider);
    }

    // ---------------------------------------------- 回放（必跑）

    [Fact]
    public void Synthetic_error_cassette_exists_is_annotated_and_key_free()
    {
        // legal: 合成来源标注在场；认证头/密钥形态不在（RL5）
        var cassette = Load("synthetic-error-401");
        var rawText = File.ReadAllText(CassettePath("synthetic-error-401"));

        Assert.Equal(LlmCassette.SyntheticSource, cassette.Source);
        Assert.Equal(401, cassette.ResponseStatus);
        Assert.False(cassette.ResponseIsSse);
        Assert.False(rawText.Contains("Bearer ", StringComparison.Ordinal));
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(rawText, "sk-[A-Za-z0-9]{8,}"),
            "cassette 含密钥形态串");
        foreach (var header in cassette.RequestHeaders)
        {
            Assert.False(LlmCassettes.SecretHeaderNames.Contains(header.Key),
                $"cassette 携带认证头 {header.Key}");
        }
    }

    [Fact]
    public async Task Synthetic_error_cassette_replays_to_auth_without_retry()
    {
        // legal: 401 回放→auth 分类；retries=3 也不重试（F-088 矩阵对 cassette 数据成立）
        var cassette = Load("synthetic-error-401");
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(baseUrl: cassette.BaseUrl, useStream: false, retries: 3)),
            port, secrets, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        port.EnqueueError(cassette.ResponseStatus, cassette.ResponseLines[0]);

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.Equal(LlmErrorKeys.Auth, result.ErrorKey);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(1, port.CallCount);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void Synthetic_multi_tool_cassette_exists_and_is_annotated()
    {
        // legal: 合成来源标注在场；SSE 形态
        var cassette = Load("synthetic-multi-tool-sse");

        Assert.Equal(LlmCassette.SyntheticSource, cassette.Source);
        Assert.Equal(200, cassette.ResponseStatus);
        Assert.True(cassette.ResponseIsSse);
        Assert.Contains(cassette.ResponseLines, l => l.StartsWith("data:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(4096)]
    public async Task Synthetic_multi_tool_cassette_replays_to_the_first_tool_without_cross_talk(int chunkSize)
    {
        // legal: 交错多工具回放（任意分片粒度）——首工具合并、参数无串槽
        var cassette = Load("synthetic-multi-tool-sse");
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(baseUrl: cassette.BaseUrl, useStream: true, retries: 0)),
            port, secrets, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        port.EnqueueReply(_ => new HttpPortResponse(200, null,
            new ChunkedStream(string.Join("\n", cassette.ResponseLines), chunkSize)));

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey);
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.Equal("crossbow_t2", action.RootElement.GetProperty("tower_id").GetString());
        Assert.Equal(1, action.RootElement.GetProperty("cells").GetArrayLength());
        Assert.NotNull(result.Outcome!.Usage);
        Assert.Equal(192, result.Outcome.Usage!.TotalTokens);
    }

    [Fact]
    public void Live_mimo_cassette_is_not_annotated_as_synthetic()
    {
        // boundary: 金样（真录）Source=null——与合成件可区分
        var cassette = Load("mimo-buildplan-tool");

        Assert.Null(cassette.Source);
        Assert.Equal("xiaomi-mimo", cassette.ProviderId);
    }
}
