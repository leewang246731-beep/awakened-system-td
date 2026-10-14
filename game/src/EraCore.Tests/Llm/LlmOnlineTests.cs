using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Llm;

/// <summary>
/// 在线集成（MiMo 实测复刻）：SKIP_ONLINE=1 或无 config/secrets.local.json 时
/// 明确跳过（跳过语义断言）；在线时真调 api.xiaomimimo.com——探针 +
/// “在东门建两座弩塔”→BuildPlan function calling。ERA_RECORD_CASSETTE=1 时
/// 用录制端口写 cassette（RL5：输出永不包含 key）。
/// </summary>
// [F-079][F-082][F-085][F-162] C-16 T-072：MiMo 在线验证
public class LlmOnlineTests
{
    private readonly ITestOutputHelper _output;

    public LlmOnlineTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<LlmChatMessage> BuildPlanOrderMessages = new[]
    {
        new LlmChatMessage("system", "你是塔防游戏的建造指挥官。收到军令后必须调用 BuildPlan 工具。"),
        new LlmChatMessage("user", "在东门建两座弩塔"),
    };

    private static bool TryPrepareMiMo(out LlmProviderConfig provider, out InMemorySecretStore secrets)
    {
        provider = null!;
        secrets = new InMemorySecretStore();
        if (!LlmCassettes.TryLoadMiMo(out var key, out var baseUrl, out var model))
        {
            return false;
        }

        baseUrl = baseUrl.Length > 0 ? baseUrl : LlmProviderPresets.XiaomiMiMo().BaseUrl;
        model = model.Length > 0 ? model : LlmProviderPresets.XiaomiMiMo().Model;
        provider = LlmProviderPresets.XiaomiMiMo() with
        {
            BaseUrl = baseUrl,
            Model = model,
            Enable = true,
        };
        secrets.Store(provider.Id, key);
        return true;
    }

    private static LlmGateway BuildGateway(
        LlmProviderConfig provider, InMemorySecretStore secrets, IHttpPort port) =>
        new(LlmFixtures.TestConfig(active: provider), port, secrets,
            firstByteTimeout: TimeSpan.FromSeconds(45),
            idleTimeout: TimeSpan.FromSeconds(90));

    [Fact]
    public async Task MiMo_live_function_calling_parses_buildplan()
    {
        // legal: 在线真调（复刻已验证结果：军令→BuildPlan 工具调用）
        if (LlmCassettes.OnlineSkipped(out var skipReason) || !TryPrepareMiMo(out var provider, out var secrets))
        {
            OnlineSkip.ReasonCaptured(_output, skipReason.Length > 0
                ? skipReason : "secrets.local.json 缺少 xiaomi_mimo.key");
            return;
        }

        // 在线真调对推理模型偶发的“只回文本/超时”容忍一次重试（测试级去抖，
        // 非网关重试——产品重试属 T-091）；两次都必须以工具调用成功收口。
        var result = new LlmGatewayCallResult(
            false, null, "no-attempt", null, null, TimeSpan.Zero);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var port = new HttpClientLlmPort();
            var gateway = BuildGateway(provider, secrets, port);
            result = await gateway.RouteAsync(
                gateway.FindProvider(provider.Id), BuildPlanOrderMessages, LlmFixtures.BuildPlanToolsJson);
            _output.WriteLine(
                $"MiMo live attempt {attempt}: status={result.StatusCode} ok={result.Ok} key={result.ErrorKey ?? "-"} elapsed={result.Elapsed.TotalMilliseconds:F0}ms");
            if (result.Ok)
            {
                break;
            }
        }

        Assert.True(result.Ok, $"在线调用失败（2 次尝试）：{result.ErrorKey}");
        Assert.Null(result.ErrorKey);
        // 结构级断言（任务卡口径：结构对齐即可，语义消费属 T-068/ISchemaValidator）：
        // 工具调用已解析为候选动作 JSON，动词=BuildPlan，两参数在场。
        // （cells 具体形态由模型决定——MiMo 偶发字符串化数组，正是 T-068 校验层
        // 要拦的不可信输入，此处不越权断言。）
        using var action = JsonDocument.Parse(result.ActionJson!);
        Assert.Equal("BuildPlan", action.RootElement.GetProperty("action").GetString());
        Assert.True(
            action.RootElement.TryGetProperty("tower_id", out var towerId)
                && towerId.ValueKind is JsonValueKind.String
                && towerId.GetString() is { Length: > 0 },
            $"tower_id 缺失或为空：{result.ActionJson}");
        Assert.True(
            action.RootElement.TryGetProperty("cells", out _),
            $"cells 缺失：{result.ActionJson}");
    }

    [Fact]
    public async Task MiMo_live_connection_probe_verifies_tool_passthrough()
    {
        // legal: 在线探针（F-085 全链：认证/端点/模型/工具透传）
        if (LlmCassettes.OnlineSkipped(out var skipReason) || !TryPrepareMiMo(out var provider, out var secrets))
        {
            OnlineSkip.ReasonCaptured(_output, skipReason.Length > 0
                ? skipReason : "secrets.local.json 缺少 xiaomi_mimo.key");
            return;
        }

        using var port = new HttpClientLlmPort();
        var gateway = BuildGateway(provider, secrets, port);
        var probe = new LlmConnectionProbe(gateway);
        var result = await probe.ProbeAsync(provider.Id);

        _output.WriteLine(
            $"MiMo probe: ok={result.Ok} key={result.ErrorKey ?? "-"} elapsed={result.Elapsed.TotalMilliseconds:F0}ms");
        Assert.True(result.Ok, $"探针失败：{result.ErrorKey} / {result.MessageZh}");
        Assert.True(result.ToolCallVerified);
        Assert.Contains(provider.BaseUri.Host, result.MessageZh);
    }

    [Fact]
    public async Task Record_MiMo_cassette_when_requested()
    {
        // boundary: 默认只回放不录制；ERA_RECORD_CASSETTE=1 时录制并落盘
        var recordRequested = Environment.GetEnvironmentVariable("ERA_RECORD_CASSETTE") == "1";
        string skipReason;
        if (!recordRequested)
        {
            skipReason = "未请求录制（ERA_RECORD_CASSETTE 未设为 1）";
        }
        else if (LlmCassettes.OnlineSkipped(out var onlineReason))
        {
            skipReason = onlineReason;
        }
        else
        {
            skipReason = string.Empty;
        }

        if (skipReason.Length > 0)
        {
            OnlineSkip.ReasonCaptured(_output, skipReason);
            return;
        }

        if (!TryPrepareMiMo(out var provider, out var secrets)
            || !LlmCassettes.TryLoadMiMo(out var key, out _, out _))
        {
            OnlineSkip.ReasonCaptured(_output, "secrets.local.json 缺少 xiaomi_mimo.key");
            return;
        }

        using var innerPort = new HttpClientLlmPort();
        var recordingPort = new RecordingPort(innerPort);
        var gateway = BuildGateway(provider, secrets, recordingPort);
        var result = await gateway.RouteAsync(
            gateway.FindProvider(provider.Id), BuildPlanOrderMessages, LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, $"录制前真调失败：{result.ErrorKey}");
        var cassette = recordingPort.BuildCassette(
            "mimo-buildplan-tool",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            provider.Id, "openai", provider.Model, provider.BaseUrl);
        var maskedJson = LlmCassettes.SerializeMasked(cassette, key);

        Directory.CreateDirectory(LlmCassettes.CassetteDir());
        var target = Path.Combine(LlmCassettes.CassetteDir(), "mimo-buildplan-tool.json");
        await File.WriteAllTextAsync(target, maskedJson);

        _output.WriteLine($"cassette 已写出：{Path.GetFileName(target)}（{maskedJson.Length} 字符，key 已打码）");
        Assert.True(File.Exists(target));
        Assert.False(maskedJson.Contains(key, StringComparison.Ordinal));
    }
}

/// <summary>Skip-path assertion helper（xunit 2.x 无动态 Skip——沿用仓库 GodotSharpGuardTests 先例）。</summary>
internal static class OnlineSkip
{
    internal static void ReasonCaptured(ITestOutputHelper output, string reason)
    {
        output.WriteLine($"SKIP（语义明确，非通过）：{reason}");
        Assert.False(string.IsNullOrWhiteSpace(reason), "跳过原因必须明确非空");
    }
}
