using EraCore.Agent.Governance;
using EraCore.Presentation;

namespace EraCore.Cinematics;

/// <summary>
/// The four system personas (§10.7 系统人格可切换：毒舌/参谋/沙雕/傲娇；
/// F-109 四套，T-089/T-126 拥有完整人格台词库——本枚举是结算演出缝
/// (T-087) 的先期冻结面：人格只改表达不改能力)。
/// </summary>
public enum SystemPersona
{
    /// <summary>毒舌 — cold sarcastic system voice.</summary>
    Acid = 0,

    /// <summary>参谋 — the 老夫 advisor voice (T-075 TrustBadgeLines 同嗓音).</summary>
    Advisor = 1,

    /// <summary>沙雕 — goofy hype voice.</summary>
    Goofy = 2,

    /// <summary>傲娇 — tsundere voice.</summary>
    Tsundere = 3,
}

/// <summary>Persona vocabulary of the settlement seam (labels + closed set).</summary>
public static class SystemPersonas
{
    /// <summary>The four personas in enum order (matrix tests exhaust over this).</summary>
    public static readonly IReadOnlyList<SystemPersona> All = new[]
    {
        SystemPersona.Acid, SystemPersona.Advisor, SystemPersona.Goofy, SystemPersona.Tsundere,
    };

    /// <summary>Chinese display label of one persona (毒舌/参谋/沙雕/傲娇).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown persona value.</exception>
    public static string LabelZh(SystemPersona persona) => persona switch
    {
        SystemPersona.Acid => "毒舌",
        SystemPersona.Advisor => "参谋",
        SystemPersona.Goofy => "沙雕",
        SystemPersona.Tsundere => "傲娇",
        _ => throw new ArgumentOutOfRangeException(nameof(persona), persona, "unknown system persona."),
    };
}

/// <summary>
/// 结算一句话点评 copy (T-087, F-104: 转盘+宝箱之后系统按人格点评一句)。
/// 模板面 = 6+4 条：
/// <list type="bullet">
/// <item><b>6 条（复用）</b>：信任徽记跨档台词 = T-075
/// <see cref="TrustBadgeLines"/>（3 档 × 2 向，含两格结构不可达极值）——
/// 结算时若本关内徽记档位发生了跨档，点评追加这条复用台词（单一来源，
/// 两个消费面永不漂移）。</item>
/// <item><b>4 条（结算向扩展）</b>：每人格一条结算模板，按信任档位
/// （<see cref="TrustBadge"/> 三档，措辞只调制表达 F.4）填出 12 句成品。</item>
/// </list>
/// 纯模板填充（L1 档口径，非 LLM 调用）；RL12 先定后演：点评句在结算
/// 决策时刻一并定死，演出只回放。
/// </summary>
public static class SettlementCommentary
{
    /// <summary>
    /// The settlement one-liner of one persona at one trust badge
    /// (4 模板 × 3 档 = 12 句；句句带档位徽记措辞，人格间互不相同)。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown persona or badge.</exception>
    public static string Compose(SystemPersona persona, TrustBadge badge) => (persona, badge) switch
    {
        // 参谋（老夫嗓音——与 T-075 TrustBadgeLines 同一人物）
        (SystemPersona.Advisor, TrustBadge.Skeptical) => "宿主此战尚有疏漏，老夫记档：将信将疑。赏赐已按档入册，请过目。",
        (SystemPersona.Advisor, TrustBadge.Trustworthy) => "宿主用兵有度，老夫记账：可托付。赏赐已按档入册，请过目。",
        (SystemPersona.Advisor, TrustBadge.Devoted) => "宿主算无遗策，老夫执令簿随行：言听计从。赏赐已按档入册，请过目。",
        // 毒舌
        (SystemPersona.Acid, TrustBadge.Skeptical) => "叮！结算完成。以将信将疑的水准打出这档成绩，宿主真是稳定得可怕。",
        (SystemPersona.Acid, TrustBadge.Trustworthy) => "叮！结算完成。可托付的宿主也能摸到这档——行吧，本系统收回半句刻薄话。",
        (SystemPersona.Acid, TrustBadge.Devoted) => "叮！结算完成。言听计从的宿主配这档成绩……行，本系统承认看走眼的是自己。",
        // 沙雕
        (SystemPersona.Goofy, TrustBadge.Skeptical) => "叮叮！宝箱开啦！（悄悄记一笔：将信将疑）下一关继续一起莽！",
        (SystemPersona.Goofy, TrustBadge.Trustworthy) => "叮叮！宝箱开啦！可托付的宿主今天也超——级靠谱！",
        (SystemPersona.Goofy, TrustBadge.Devoted) => "叮叮！宝箱开啦！言听计从！宿主说往东，本系统绝不往西！",
        // 傲娇
        (SystemPersona.Tsundere, TrustBadge.Skeptical) => "哼、将信将疑归将信将疑，这档成绩……也、也不是不能看啦。",
        (SystemPersona.Tsundere, TrustBadge.Trustworthy) => "可托付什么的……是宿主自己挣来的，本系统只是如实记录而已！",
        (SystemPersona.Tsundere, TrustBadge.Devoted) => "言听计从……别、别误会！本系统只是懒得反对你！……奖励收好。",
        _ => throw new ArgumentOutOfRangeException(
            nameof(persona), $"{persona}/{badge}", "unknown persona or trust badge."),
    };

    /// <summary>
    /// 信任徽记跨档回声（6 条复用面）：转发 T-075 台词库
    /// <see cref="TrustBadgeLines.Line"/>——结算缝不另写一份跨档词，
    /// 两个消费面（面板自嘲/结算回声）单一来源。
    /// </summary>
    /// <exception cref="ArgumentException">from equals to（同档无跨档台词）。</exception>
    public static string TrustShiftEcho(TrustBadge from, TrustBadge to) =>
        TrustBadgeLines.Line(from, to);
}
