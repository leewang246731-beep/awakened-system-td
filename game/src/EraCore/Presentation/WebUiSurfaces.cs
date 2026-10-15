namespace EraCore.Presentation;

/// <summary>
/// One platform-conditional UI-surface row (T-110, F-164 Web UI 禁用适配):
/// which player-facing surfaces exist under the current agent-tier row.
/// Pure mirror-table pattern (OffModeDowngrade.Surfaces 先例) — the Godot
/// panels read one row and never re-derive visibility themselves, so the
/// Web adaptation cannot drift between the three panels that share it.
/// </summary>
/// <param name="ByokFormVisible">设置页 BYOK（L3）表单面——平台禁用时整面隐藏（T-106 已有，本表收编）。</param>
/// <param name="CompareBarVisible">T-109「你的话发给谁」三档对比条——披露面，任何平台都保留（Web 行文案由 DataFlowCompareBar 供给）。</param>
/// <param name="AiAdvancedSyntaxHintVisible">军令输入框旁「AI 高级语法」入口（L3 暗示性 UI，桌面可见/默认，Web 隐藏）。</param>
/// <param name="DesktopOnlyAnnotationActive">词汇表 desktop_only 词条标注「桌面版功能」是否生效（Web 生效，桌面不标）。</param>
public sealed record WebUiSurfacesRow(
    bool ByokFormVisible,
    bool CompareBarVisible,
    bool AiAdvancedSyntaxHintVisible,
    bool DesktopOnlyAnnotationActive);

/// <summary>
/// T-110 surface matrix: the single frozen mapping from the
/// <see cref="WebConstraints"/> tier row to UI-surface visibility. Derived
/// facts (设计 §17 条 15 「L2/L3 在 Web 版自动禁用+只保留 L1」+ §16.3
/// 数据流向明示=披露面不随档位隐藏):
/// <list type="bullet">
/// <item>BYOK 表单/AI 高级语法入口随 <c>L3CloudByok</c> 开关——L3 平台禁用即隐藏；</item>
/// <item>对比条与披露常显（零网络平台更需要明示「你的话哪儿都不去」）；</item>
/// <item>词汇表 desktop_only 标注只在 L3 平台禁用时激活。</item>
/// </list>
/// </summary>
public static class WebUiSurfaces
{
    /// <summary>
    /// Resolves the surface row for one tier row. The ONLY legal source of
    /// the row — panels mirror it verbatim (能力表驱动).
    /// </summary>
    /// <param name="tiers">Platform tier row (<see cref="WebConstraints.Availability"/>).</param>
    public static WebUiSurfacesRow Surfaces(AgentTierAvailability tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return new WebUiSurfacesRow(
            ByokFormVisible: tiers.L3CloudByok,
            CompareBarVisible: true,
            AiAdvancedSyntaxHintVisible: tiers.L3CloudByok,
            DesktopOnlyAnnotationActive: !tiers.L3CloudByok);
    }

    /// <summary>
    /// Whether one glossary entry carries the Web annotation right now:
    /// only desktop-only entries AND only while the L3 tier is platform-off.
    /// </summary>
    /// <param name="tiers">Platform tier row.</param>
    /// <param name="entryDesktopOnly">The entry's <c>desktop_only</c> flag (glossary.json).</param>
    public static bool AnnotateDesktopOnly(AgentTierAvailability tiers, bool entryDesktopOnly)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return entryDesktopOnly && !tiers.L3CloudByok;
    }

    /// <summary>Zh suffix appended to desktop-only glossary terms on the Web build.</summary>
    public const string DesktopOnlySuffixZh = "（桌面版功能）";

    /// <summary>En suffix appended to desktop-only glossary terms on the Web build.</summary>
    public const string DesktopOnlySuffixEn = "(desktop-only feature)";

    /// <summary>
    /// 军令输入框「AI 高级语法」提示文案（T-110 ①，L3 暗示性入口——桌面
    /// 默认可见、Web 隐藏）：指向设置页的 BYOK 云模型可选档；措辞锚定
    /// §11.5 L3 行（可选/默认关）与 FAQ ③（AI 档完全可选），不引入新机制。
    /// </summary>
    public const string AiAdvancedSyntaxHintZh =
        "AI 高级语法：自然语言自由对话（可选·云模型 BYOK，默认关——设置页配置）";

    /// <summary>En line of the AI-advanced-syntax hint (same facts, bilingual surface).</summary>
    public const string AiAdvancedSyntaxHintEn =
        "AI advanced syntax: free-form natural-language orders (optional, BYOK cloud model, off by default — configure in Settings)";
}
