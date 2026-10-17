using EraCore.Agent.Governance;

namespace EraCore.Cinematics;

/// <summary>
/// Pure decisions of the finale-truth performance (T-136, F-108): which of
/// the two switch-selected variants of the 终章播片 plays, and which trust
/// tier's exclusive lines ride it. Content ids and anchor wording live here
/// as frozen constants so the wiring (level hook row → script hook) and the
/// off-system parity guarantee (R2: 关闭系统可全通关全成就警报不缺失——
/// 播片面口径=系统关闭时真相以字幕/旁白载体完整传达) can never drift
/// apart between the two scripts.
/// </summary>
public static class FinalePlayback
{
    /// <summary>
    /// The system-ON variant id — the only script that claims the finale
    /// hook row (<c>campaign:hook-e6-finale-truth</c>, e6-final victory
    /// beat): one hook row, one performance; the off variant is the
    /// switch-selected alternate of the SAME beat, never a second claim.
    /// </summary>
    public const string OnScriptId = "cinematic:finale-zero";

    /// <summary>The system-OFF subtitle variant id（关闭系统字幕版，旁白载体）.</summary>
    public const string OffScriptId = "cinematic:finale-zero-off";

    /// <summary>
    /// The finale hook id: the victory-beat row of
    /// <c>campaign:level-e6-final</c>（守望者降临——堕落守望者坠落时刻=
    /// 真相揭示时刻，levels/e6.json 数据位）.
    /// </summary>
    public const string FinaleHookId = "campaign:hook-e6-finale-truth";

    /// <summary>
    /// The closed truth-anchor word set BOTH variants must carry in their
    /// speak lines (truth parity across the system switch — the off variant
    /// states the same facts through the narrator, nobody misses the truth
    /// by playing with the system off): 零拆记忆碎片 / 孪生归位 / 忠诚
    /// 反转 / 纪元之门合拢（§12.1 谱系 + F-098 真相线三真相）.
    /// </summary>
    public static readonly IReadOnlyList<string> TruthAnchors =
        new[] { "记忆碎片", "孪生", "忠诚", "纪元之门" };

    /// <summary>
    /// Which variant plays at the finale beat for one switch position
    /// (§10.7 关闭系统=功能无损的中性面——真相播片不因关系统而缺失，
    /// 只换载体：人格台词版 ↔ 字幕旁白版).
    /// </summary>
    /// <param name="systemEnabled">The C-22 system switch position.</param>
    public static string ScriptIdFor(bool systemEnabled) =>
        systemEnabled ? OnScriptId : OffScriptId;

    /// <summary>
    /// The <c>finale_trust_lines</c> tier key of one trust badge
    /// （story-e6.json 三档终章台词——devoted=高信任专属终章台词，§12.1
    /// 反转方向写死：奖励系统使用者，不搞背叛骗局）. Key casing matches
    /// the content table's tier names exactly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown badge tier.</exception>
    public static string TrustTierKey(TrustBadge badge) =>
        EraCore.Agent.Governance.TrustBadgeKeys.StrictKey(badge);
}
