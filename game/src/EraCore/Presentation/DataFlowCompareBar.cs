namespace EraCore.Presentation;

/// <summary>One tier row of the BYOK data-flow compare bar (T-109, F-153).</summary>
/// <param name="Tier">Which agent tier the row describes.</param>
/// <param name="Zh">简体中文文案（玩家可见，含该档当前状态）。</param>
/// <param name="En">English copy (player-visible, same status).</param>
public sealed record DataFlowCompareRow(DataFlowTier Tier, string Zh, string En);

/// <summary>The three agent tiers of the compare bar (§11.5 L1/L2/L3).</summary>
public enum DataFlowTier
{
    /// <summary>L1 军令语法 — offline rule/dictionary parsing, always on.</summary>
    L1Grammar,

    /// <summary>L2 本地小模型 — local inference, later release (§17 条 14).</summary>
    L2LocalModel,

    /// <summary>L3 BYOK 云模型 — sends to the player-configured service.</summary>
    L3CloudByok,
}

/// <summary>
/// The "你的话发给谁" compare bar of the BYOK settings page (T-109, F-153
/// 声明/对比条; 方案 §11.5 三档 + §16.3 数据流向明示): three fixed rows
/// whose data-flow status differs per tier — L1 不发送任何数据, L2 本地
/// 推理不上传, L3 发送到你配置的服务（{base_url}，关闭时零网络）. Pure
/// copy + state function — the Godot presenter renders the rows verbatim;
/// no row ever embeds key material (RL5). L1 is constant by construction
/// (grammar parsing is offline code, 全平台恒可用); L2/L3 statuses derive
/// from the frozen <see cref="WebConstraints"/> capability table, so the
/// bar cannot drift from the platform gates.
/// </summary>
public static class DataFlowCompareBar
{
    /// <summary>Bar heading (zh).</summary>
    public const string HeadingZh = "你的话发给谁（三档对比）";

    /// <summary>Bar heading (en).</summary>
    public const string HeadingEn = "Where your words go (three tiers compared)";

    private const string L1Zh =
        "L1 军令语法（默认，离线）——不发送任何数据；解析全在本机完成。";
    private const string L1En =
        "L1 order grammar (default, offline) — no data is sent anywhere; parsing happens entirely on this machine.";

    private const string L2Zh =
        "L2 本地小模型（可选）——本地推理，不上传对话内容；模型文件自愿下载，保存在本机数据目录。";
    private const string L2En =
        "L2 local model (optional) — inference runs locally; your conversation is never uploaded; the model file is an opt-in download stored in this machine's data folder.";

    private const string L2WebZh =
        "L2 本地小模型——Web 版不可用（浏览器沙箱，零网络承诺）。";
    private const string L2WebEn =
        "L2 local model — unavailable on the Web build (browser sandbox, zero-network promise).";

    private const string L3OnZhFormat =
        "L3 云模型（BYOK，你已启用）——对话内容发送到你配置的服务（{0}）；密钥仅存本机。";
    private const string L3OnEnFormat =
        "L3 cloud model (BYOK, enabled) — your conversation is sent to the service you configured ({0}); the key stays on this machine.";

    private const string L3OffZh =
        "L3 云模型（BYOK，默认关）——当前关闭，不发送任何数据（开启=零网络）。";
    private const string L3OffEn =
        "L3 cloud model (BYOK, off by default) — currently off, no data is sent (switch on = zero network).";

    private const string L3WebZh =
        "L3 云模型（BYOK）——Web 版禁用（零网络承诺），不发送任何数据。";
    private const string L3WebEn =
        "L3 cloud model (BYOK) — disabled on the Web build (zero-network promise); no data is sent.";

    /// <summary>Insert when base_url is still blank (mirrors §11.6 常驻声明 blank insert).</summary>
    public const string BlankBaseUrlZh = "尚未配置 base_url";

    private const string L2ActiveZh =
        "L2 本地小模型（已选用）——军令交由本机模型推理，不上传对话内容；数据只在本机与 127.0.0.1 本地服务之间流动。";
    private const string L2ActiveEn =
        "L2 local model (selected) — orders are reasoned by the on-device model; your conversation is never uploaded; data stays between this machine and its own 127.0.0.1 local service.";

    /// <summary>
    /// Resolves the three rows for one platform/provider state.
    /// </summary>
    /// <param name="tiers">Platform capability row (<see cref="WebConstraints.Availability"/>).</param>
    /// <param name="l3Enabled">The active provider row's enable switch (false = 关闭，零网络).</param>
    /// <param name="baseUrl">The configured base_url (destination shown on the enabled L3 row).</param>
    public static IReadOnlyList<DataFlowCompareRow> Resolve(
        AgentTierAvailability tiers, bool l3Enabled, string baseUrl) =>
        Resolve(tiers, l3Enabled, baseUrl, l2Selected: false);

    /// <summary>
    /// Resolves the three rows for one platform/provider/L2 state (T-137:
    /// the L2 row now mirrors the player's real tier selection — 数据流向
    /// 声明口径与三档选择同步, the acceptance wording source).
    /// </summary>
    /// <param name="tiers">Platform capability row (<see cref="WebConstraints.Availability"/>).</param>
    /// <param name="l3Enabled">The active provider row's enable switch (false = 关闭，零网络).</param>
    /// <param name="baseUrl">The configured base_url (destination shown on the enabled L3 row).</param>
    /// <param name="l2Selected">True when the player routed the advisor to the L2 local lane.</param>
    public static IReadOnlyList<DataFlowCompareRow> Resolve(
        AgentTierAvailability tiers, bool l3Enabled, string baseUrl, bool l2Selected)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        string l3Zh = tiers.L3CloudByok
            ? (l3Enabled ? string.Format(L3OnZhFormat, BaseUrlOrBlank(baseUrl)) : L3OffZh)
            : L3WebZh;
        string l3En = tiers.L3CloudByok
            ? (l3Enabled ? string.Format(L3OnEnFormat, BaseUrlOrBlank(baseUrl)) : L3OffEn)
            : L3WebEn;

        return
        [
            new(DataFlowTier.L1Grammar, L1Zh, L1En),
            new(DataFlowTier.L2LocalModel, L2RowCopy(tiers, l2Selected, english: false),
                L2RowCopy(tiers, l2Selected, english: true)),
            new(DataFlowTier.L3CloudByok, l3Zh, l3En),
        ];
    }

    private static string L2RowCopy(AgentTierAvailability tiers, bool l2Selected, bool english)
    {
        if (!tiers.L2LocalModel)
        {
            return english ? L2WebEn : L2WebZh;
        }

        return (l2Selected, english) switch
        {
            (true, false) => L2ActiveZh,
            (true, true) => L2ActiveEn,
            (false, false) => L2Zh,
            _ => L2En,
        };
    }

    private static string BaseUrlOrBlank(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? BlankBaseUrlZh : baseUrl.Trim();
}
