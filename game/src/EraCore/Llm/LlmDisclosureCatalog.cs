namespace EraCore.Llm;

/// <summary>
/// Player-facing bilingual copy of the C-31 compliance disclosures (T-109,
/// F-147; 方案 §16.3 合规与披露, §11.6 常驻声明): ① the first-run data-flow
/// notice shown before any AI feature is enabled (数据去向=你配置的
/// base_url / 不收集遥测 / 随时可关 / 中英); ② the AI-generated-asset
/// labeling line for the settings + about surfaces (静态文案非模态,
/// manifest 链接随包分发). Data rows only — the Godot presenter renders
/// them verbatim; the persistent 常驻声明 of §11.6 stays with
/// <see cref="EraCore.Presentation.SettingsFormState.DeclarationFor"/>.
/// No row ever embeds key material (RL5).
/// </summary>
public static class LlmDisclosureCatalog
{
    // ---------------------------------------- first-run data-flow notice

    /// <summary>Dialog title (zh) — before the first AI feature is enabled.</summary>
    public const string DialogTitleZh = "启用 AI 功能前：数据流向声明";

    /// <summary>Dialog title (en).</summary>
    public const string DialogTitleEn = "Before enabling AI features: data-flow notice";

    /// <summary>
    /// Where the words go (zh). {0} = the player-configured base_url (the
    /// only destination the game will ever talk to; 方案 §16.3 数据流向明示).
    /// </summary>
    public const string DialogFlowZhFormat =
        "启用后，你的对话内容（军令与上下文）将发送到你配置的服务（{0}）；除此之外不发送任何其他数据。";

    /// <summary>Where the words go (en). {0} = base_url.</summary>
    public const string DialogFlowEnFormat =
        "Once enabled, your conversation (orders and context) is sent to the service you configured ({0}); nothing else leaves the game.";

    /// <summary>Insert when base_url is still blank (mirrors §11.6 常驻声明 blank insert).</summary>
    public const string DialogFlowBlankZh = "尚未配置 base_url";

    /// <summary>Telemetry line (zh) — the game itself collects nothing.</summary>
    public const string DialogTelemetryZh =
        "本游戏不收集遥测、不上报任何玩家数据；API Key 仅存本机凭据存储，永不写入配置文件或日志。";

    /// <summary>Telemetry line (en).</summary>
    public const string DialogTelemetryEn =
        "The game collects no telemetry and reports no player data; your API key lives only in this machine's credential store, never in config files or logs.";

    /// <summary>Optional-tier line (zh) — off means zero network, L1 stays offline-usable.</summary>
    public const string DialogOptionalZh =
        "AI 档完全可选、随时可在设置中关闭（关闭=零网络请求）；离线军令语法（L1）开箱即用，不配 Key 也完整可玩。";

    /// <summary>Optional-tier line (en).</summary>
    public const string DialogOptionalEn =
        "The AI tier is fully optional and can be switched off anytime in settings (off = zero network requests); the offline order grammar (L1) works out of the box with no key.";

    /// <summary>Confirm button (zh) — acknowledges and stops future prompts.</summary>
    public const string DialogConfirmZh = "我已了解，继续启用 AI（不再提示）";

    /// <summary>Confirm button (en).</summary>
    public const string DialogConfirmEn = "I understand — enable AI (don't ask again)";

    /// <summary>Decline button (zh) — closes the dialog without acknowledging (state stays 未启).</summary>
    public const string DialogDeclineZh = "暂不启用（下次打开仍会说明）";

    /// <summary>Decline button (en).</summary>
    public const string DialogDeclineEn = "Not now (the notice shows again next time)";

    /// <summary>Data-flow line with the current base_url inserted (blank → <see cref="DialogFlowBlankZh"/>).</summary>
    public static string DialogFlowZh(string baseUrl) => string.Format(
        DialogFlowZhFormat,
        string.IsNullOrWhiteSpace(baseUrl) ? DialogFlowBlankZh : baseUrl.Trim());

    /// <summary>Data-flow line (en) with the current base_url inserted.</summary>
    public static string DialogFlowEn(string baseUrl) => string.Format(
        DialogFlowEnFormat,
        string.IsNullOrWhiteSpace(baseUrl) ? DialogFlowBlankZh : baseUrl.Trim());

    // ---------------------------------------- AI-generated asset labeling

    /// <summary>
    /// Asset labeling line (zh) for the settings page and the about surface
    /// (§16.3 模型输出/素材"由 AI 生成"标注 — 静态文案非模态). The manifest
    /// path ships with the package (build_v020 装配 game/assets/expand-v1).
    /// </summary>
    public const string AiAssetLabelZh =
        "关于素材：部分视觉素材（章节插画/加载图/皮肤/过场视频）由 AI 生成后经人工筛选入库；逐条生成台账见 manifest（游戏目录 game/assets/expand-v1/，随包分发）。";

    /// <summary>Asset labeling line (en).</summary>
    public const string AiAssetLabelEn =
        "About the assets: part of the visual assets (chapter art, loading screens, skins, cinematics) are AI-generated, then human-curated; the per-item generation manifests ship with the game (game/assets/expand-v1/).";

    /// <summary>Repo/package-relative directory of the generation manifests (随仓库+随包).</summary>
    public const string ManifestDirPath = "game/assets/expand-v1/";
}
