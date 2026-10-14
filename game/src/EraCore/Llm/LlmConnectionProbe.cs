using EraCore.Contracts.Agent;

namespace EraCore.Llm;

/// <summary>
/// Outcome of one connection test (F-085 连接测试探针): classified verdict
/// with ready-to-render bilingual copy.
/// </summary>
/// <param name="Ok">True iff the provider answered AND called the probe tool.</param>
/// <param name="ErrorKey">Error key on failure; null on success.</param>
/// <param name="MessageZh">简体中文结果文案（成功含域名与耗时）。</param>
/// <param name="MessageEn">English result copy.</param>
/// <param name="ToolCallVerified">True when the model actually returned a tool call.</param>
/// <param name="SawReasoningChannel">True when the provider streamed a reasoning channel (F-087 hint).</param>
/// <param name="StatusCode">HTTP status when a reply arrived; else null.</param>
/// <param name="Elapsed">Round-trip time.</param>
public sealed record LlmProbeResult(
    bool Ok,
    string? ErrorKey,
    string MessageZh,
    string MessageEn,
    bool ToolCallVerified,
    bool SawReasoningChannel,
    int? StatusCode,
    TimeSpan Elapsed);

/// <summary>
/// C-16 connection probe (T-072, F-085; 方案 §11.6 连接测试): one minimal
/// message + exactly ONE tool declaration, so the settings UI can prove
/// the whole chain — auth, endpoint, model AND function-calling
/// passthrough (relays that drop tool calls are a §11.6 known pit) —
/// before the player saves. Goes through <see cref="LlmGateway.RouteAsync"/>,
/// so the domain whitelist, enable flag and key checks apply to probes
/// exactly as to game traffic.
/// </summary>
public sealed class LlmConnectionProbe
{
    /// <summary>The single probe tool's function name.</summary>
    public const string ProbeToolName = "EraPing";

    /// <summary>Token cap for the probe (keep the test cheap).</summary>
    public const int ProbeMaxTokens = 256;

    /// <summary>Success copy format (zh): {0}=host, {1}=elapsed ms.</summary>
    public const string SuccessZhFormat = "连接成功：{0} 应答，function calling 透传正常（{1}ms）。";

    /// <summary>Success copy format (en): {0}=host, {1}=elapsed ms.</summary>
    public const string SuccessEnFormat = "Connection OK: {0} answered and tool-call passthrough is verified ({1}ms).";

    private static readonly string ProbeToolsJson = BuildProbeToolsJson();

    private readonly LlmGateway _gateway;

    /// <param name="gateway">The same gateway instance game traffic uses.</param>
    public LlmConnectionProbe(LlmGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>The one-tool probe schema (OpenAI shape; protocols convert as needed).</summary>
    public static string BuildProbeToolsJson() =>
        // Fixed literal (no interleaving, no secrets) — the probe schema is
        // part of the tool contract, not user data.
        "[{\"type\":\"function\",\"function\":{\"name\":\"" + ProbeToolName + "\","
        + "\"description\":\"Connection probe: call this tool with {\\\"pong\\\":true} to answer.\","
        + "\"parameters\":{\"type\":\"object\",\"properties\":{\"pong\":{\"type\":\"boolean\"}},"
        + "\"required\":[\"pong\"]}}}]";

    /// <summary>Probes one provider by id; never throws network exceptions.</summary>
    public async Task<LlmProbeResult> ProbeAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("provider id 不能为空", nameof(providerId));
        }

        var provider = _gateway.FindProvider(providerId);
        var call = await _gateway.RouteAsync(
            provider,
            new[]
            {
                new LlmChatMessage("system", "你是连接测试探针。必须且只能通过调用 " + ProbeToolName + " 工具应答。"),
                new LlmChatMessage("user", "ping"),
            },
            ProbeToolsJson,
            cancellationToken).ConfigureAwait(false);

        if (call.Ok && call.Outcome is { SawToolCall: true })
        {
            var host = provider is not null ? provider.BaseUri.Host : string.Empty;
            var ms = (long)call.Elapsed.TotalMilliseconds;
            return new LlmProbeResult(
                true, null,
                string.Format(SuccessZhFormat, host, ms),
                string.Format(SuccessEnFormat, host, ms),
                ToolCallVerified: true,
                call.Outcome.SawReasoningChannel,
                call.StatusCode,
                call.Elapsed);
        }

        var errorKey = call.ErrorKey ?? LlmErrorKeys.NoToolCall;
        _ = LlmErrorCatalog.TryGetCopy(errorKey, out var zh, out var en);
        return new LlmProbeResult(
            false, errorKey, zh, en,
            ToolCallVerified: false,
            call.Outcome?.SawReasoningChannel ?? false,
            call.StatusCode,
            call.Elapsed);
    }
}
