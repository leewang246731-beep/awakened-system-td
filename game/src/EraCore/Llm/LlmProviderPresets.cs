namespace EraCore.Llm;

/// <summary>
/// The §11.6 provider templates (F-082 双预置模板 + the custom/local rows
/// of the settings dropdown). These are CODE constants, not data-table
/// rows: they are the factory defaults every fresh llm.config.json starts
/// from, and the two vendor rows are transcribed verbatim from the design
/// schema. MiMo values are the live-verified ones (docs/engineering/
/// 09-env-locked.md + the T-072 MiMo cassette).
/// </summary>
public static class LlmProviderPresets
{
    /// <summary>智谱 GLM default template (openai protocol, standard pay-as-you-go endpoint).</summary>
    public static LlmProviderConfig ZhipuGlm() => new()
    {
        Id = "zhipu-glm",
        Name = "智谱 GLM（默认模板）",
        Protocol = LlmProtocol.OpenAi,
        BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
        Model = "glm-5.3",
        Enable = true,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = 2,
        HistoryRounds = 10,
        ContextBudgetTokens = 16000,
        UseStream = true,
        Note = "在 open.bigmodel.cn 创建按量 API Key。GLM Coding Plan 订阅密钥仅限官方编程工具白名单使用，"
            + "不能用于本游戏（服务端拒绝，错误码 1315）。",
    };

    /// <summary>小米 MiMo preset (openai protocol, live-verified function calling).</summary>
    public static LlmProviderConfig XiaomiMiMo() => new()
    {
        Id = "xiaomi-mimo",
        Name = "小米 MiMo（预置模板，已实测）",
        Protocol = LlmProtocol.OpenAi,
        BaseUrl = "https://api.xiaomimimo.com/v1",
        Model = "mimo-v2.6-pro",
        Enable = false,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = 2,
        HistoryRounds = 10,
        ContextBudgetTokens = 16000,
        UseStream = true,
        Note = "OpenAI 兼容端点 https://api.xiaomimimo.com/v1；Anthropic 兼容 https://api.xiaomimimo.com/anthropic。"
            + "模型 ID 为小写（mimo-v2.6-pro / mimo-v2.6-flash / mimo-v2.5-pro）。"
            + "推理模型：思考在 reasoning_content，正文在 content。已实测 function calling 返回合法动作 JSON。",
    };

    /// <summary>Custom / relay template — blank rows the player fills in (§11.6 custom-relay).</summary>
    public static LlmProviderConfig CustomRelay() => new()
    {
        Id = "custom-relay",
        Name = "自定义 / 中转站",
        Protocol = LlmProtocol.OpenAi,
        BaseUrl = string.Empty,
        Model = string.Empty,
        Enable = false,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = 2,
        HistoryRounds = 10,
        ContextBudgetTokens = 16000,
        UseStream = true,
        Note = "自填 base_url 与模型；中转站跨协议转换有已知丢失工具调用的坑，保存前先用“测试连接”。",
    };

    /// <summary>Local LM Studio template (settings-dropdown fourth entry, §11.6).</summary>
    public static LlmProviderConfig LocalLmStudio() => new()
    {
        Id = "local-lmstudio",
        Name = "本地 LM Studio",
        Protocol = LlmProtocol.OpenAi,
        BaseUrl = "http://localhost:1234/v1",
        Model = string.Empty,
        Enable = false,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = 2,
        HistoryRounds = 10,
        ContextBudgetTokens = 16000,
        UseStream = true,
        Note = "本机推理服务（LM Studio 默认端口）；仅本机回环，不出网。",
    };

    /// <summary>
    /// The fresh-install config: zhipu-glm active+enabled, MiMo and custom
    /// rows present but disabled (§11.6 sample document, minus key field).
    /// </summary>
    public static LlmConfig DefaultConfig() => new()
    {
        Version = LlmConfigLoader.SupportedVersion,
        ActiveProvider = ZhipuGlm().Id,
        Providers = [ZhipuGlm(), XiaomiMiMo(), CustomRelay()],
        Logging = new LlmLoggingConfig(),
    };
}
