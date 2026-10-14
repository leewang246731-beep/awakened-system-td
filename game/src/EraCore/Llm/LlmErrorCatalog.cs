namespace EraCore.Llm;

/// <summary>
/// Error-classification keys of the C-16 gateway (K-9
/// <c>LlmTransportResult.ErrorKey</c> vocabulary). Keys are stable wire
/// identifiers; the player-facing copy lives in <see cref="LlmErrorCatalog"/>
/// (数据化中英表, 方案 §11.7 错误码分类文案 — F-089 首版, T-091 补全重试矩阵).
/// </summary>
public static class LlmErrorKeys
{
    /// <summary>Provider or the whole L3 tier disabled — refused with zero network.</summary>
    public const string Disabled = "disabled";

    /// <summary>Provider id not present in the config snapshot.</summary>
    public const string UnknownProvider = "unknown-provider";

    /// <summary>No key in ISecretStore for the provider.</summary>
    public const string MissingKey = "missing-key";

    /// <summary>Endpoint host is not one of the player-configured base_url domains.</summary>
    public const string DomainNotAllowed = "domain-not-allowed";

    /// <summary>Base URL/model invalid at call time (config snapshot corrupted).</summary>
    public const string InvalidConfig = "invalid-config";

    /// <summary>401/403 — key rejected.</summary>
    public const string Auth = "auth";

    /// <summary>429 without provider code 1315.</summary>
    public const string RateLimit = "rate-limit";

    /// <summary>429 + GLM code 1315 — Coding Plan key used outside official tools.</summary>
    public const string CodingPlan = "coding-plan";

    /// <summary>GLM code 1301 — content filtered by provider safety policy.</summary>
    public const string ContentBlocked = "content-blocked";

    /// <summary>400/404 naming the model — e.g. MiMo ids must be lowercase.</summary>
    public const string InvalidModel = "invalid-model";

    /// <summary>Other 4xx bad request.</summary>
    public const string BadRequest = "bad-request";

    /// <summary>5xx upstream failure.</summary>
    public const string ServerError = "server-error";

    /// <summary>3xx — redirects are refused so the domain lock cannot be bypassed.</summary>
    public const string RedirectBlocked = "redirect-blocked";

    /// <summary>DNS/TLS/refused/unreachable — check network and proxy.</summary>
    public const string Network = "network";

    /// <summary>No first SSE byte/headers within the first-byte budget (§11.7 双超时).</summary>
    public const string TimeoutFirstByte = "timeout-first-byte";

    /// <summary>Stream stalled mid-body longer than the chunk-idle budget (§11.7 双超时).</summary>
    public const string TimeoutIdle = "timeout-idle";

    /// <summary>2xx body that is not a parseable protocol reply.</summary>
    public const string BadResponse = "bad-response";

    /// <summary>The tools JSON projection is not a valid tool array.</summary>
    public const string BadTools = "bad-tools";

    /// <summary>2xx reply with text only — model did not call any tool.</summary>
    public const string NoToolCall = "no-tool-call";
}

/// <summary>One bilingual copy row of the error catalog.</summary>
/// <param name="ErrorKey">Key from <see cref="LlmErrorKeys"/>.</param>
/// <param name="Zh">简体中文文案（玩家可见）。</param>
/// <param name="En">English copy (player-visible).</param>
public sealed record LlmErrorCopyRow(string ErrorKey, string Zh, string En);

/// <summary>
/// Player-facing bilingual copy for every gateway error key (方案 §11.7
/// 错误码分类文案表). Data rows, no branching — the UI/probe renders
/// <see cref="TryGetCopy"/> output verbatim. Copy for 401/1315/1301/model
/// ids mirrors the design wording; no row ever embeds key material.
/// </summary>
public static class LlmErrorCatalog
{
    private static readonly IReadOnlyList<LlmErrorCopyRow> Rows = new LlmErrorCopyRow[]
    {
        new(LlmErrorKeys.Disabled,
            "L3 云模型未启用：该服务在设置中处于关闭状态，本次调用未发出任何网络请求。",
            "Cloud model tier disabled: this provider is switched off in settings; no network request was sent."),
        new(LlmErrorKeys.UnknownProvider,
            "未找到该服务配置（提供方 id 不在配置文件中），请重新保存设置。",
            "Provider not found (id missing from the config file); re-save your settings."),
        new(LlmErrorKeys.MissingKey,
            "未配置 API Key：密钥只存本机凭据存储，请在设置界面录入后重试。",
            "No API key configured: keys live only in this machine's credential store; enter one in settings."),
        new(LlmErrorKeys.DomainNotAllowed,
            "目标域名不在你配置的 base_url 白名单内，已拒绝联网（零网络请求）。",
            "Target domain is not in your configured base_url whitelist; the call was refused with zero network."),
        new(LlmErrorKeys.InvalidConfig,
            "服务配置无效（base_url/model），请重新检查设置。",
            "Invalid provider configuration (base_url/model); please review settings."),
        new(LlmErrorKeys.Auth,
            "密钥无效（401/403）：请检查 API Key 是否正确、是否属于该服务商。",
            "Invalid key (401/403): check that the API key is correct and belongs to this provider."),
        new(LlmErrorKeys.RateLimit,
            "触发限流（429）：请求过于频繁或额度不足，请稍后重试。",
            "Rate limited (429): too many requests or quota exhausted; retry later."),
        new(LlmErrorKeys.CodingPlan,
            "Coding Plan 密钥不能用于本游戏，请用按量 API Key（服务端错误码 1315）。",
            "A Coding Plan key cannot be used here (server code 1315); use a pay-as-you-go API key."),
        new(LlmErrorKeys.ContentBlocked,
            "内容被安全策略拦截（1301），请调整措辞后重试。",
            "Content blocked by the provider's safety policy (1301); rephrase and retry."),
        new(LlmErrorKeys.InvalidModel,
            "模型不可用：模型 ID 需与服务商一致且注意大小写（如 mimo-v2.6-pro 为全小写）。",
            "Model unavailable: the model id must match the provider exactly, note lower-case ids (e.g. mimo-v2.6-pro)."),
        new(LlmErrorKeys.BadRequest,
            "请求被服务端拒绝（400）：配置或参数有误，请检查协议与模型设置。",
            "Request rejected (400): configuration or parameters are wrong; check protocol and model settings."),
        new(LlmErrorKeys.ServerError,
            "服务端错误（5xx）：服务商暂时不可用，请稍后重试。",
            "Server error (5xx): the provider is temporarily unavailable; retry later."),
        new(LlmErrorKeys.RedirectBlocked,
            "端点返回重定向，已拒绝：请把 base_url 直接配置为最终端点地址。",
            "Endpoint answered with a redirect, which is refused: point base_url at the final endpoint directly."),
        new(LlmErrorKeys.Network,
            "网络不可达：请检查网络连接与代理设置。",
            "Network unreachable: check your connection and proxy settings."),
        new(LlmErrorKeys.TimeoutFirstByte,
            "首字节超时：服务端在限定时间内没有开始响应，请稍后重试。",
            "First-byte timeout: the server did not start responding in time; retry later."),
        new(LlmErrorKeys.TimeoutIdle,
            "流式响应中断：超过限定时间没有收到新数据。",
            "Stream stalled: no new data arrived within the idle-time budget."),
        new(LlmErrorKeys.BadResponse,
            "服务端返回了无法解析的响应，请重试或检查端点协议是否匹配。",
            "Unparseable server reply; retry or check that the endpoint protocol matches."),
        new(LlmErrorKeys.BadTools,
            "动作工具表投影无效（内部错误），请反馈给开发者。",
            "Invalid tool projection (internal error); please report to the developers."),
        new(LlmErrorKeys.NoToolCall,
            "模型未调用动作工具（只回了文本）：中转站跨协议转换可能丢失工具调用，请用“测试连接”验证。",
            "The model replied with text instead of calling a tool; relays sometimes drop tool calls — verify with the connection probe."),
    };

    /// <summary>Looks up bilingual copy for one error key.</summary>
    public static bool TryGetCopy(string errorKey, out string zh, out string en)
    {
        var row = Rows.FirstOrDefault(r => r.ErrorKey == errorKey);
        zh = row?.Zh ?? string.Empty;
        en = row?.En ?? string.Empty;
        return row is not null;
    }

    /// <summary>All rows (UI/test use; ordered as authored).</summary>
    public static IReadOnlyList<LlmErrorCopyRow> AllRows => Rows;
}
