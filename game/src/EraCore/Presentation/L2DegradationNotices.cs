using EraCore.Llm.L2;

namespace EraCore.Presentation;

/// <summary>
/// Player-visible copy for L2 degradation (T-137 验收面「失败降 L1+玩家可见
/// 提示」). Neutral tone by design — a downgrade is a capability fact, not a
/// crisis (先例 OffModeDowngrade 中性横幅: 白字/中性框, 不用风险红). The
/// copy lives ONLY here — panels render verbatim, never re-word (禁双份措辞).
/// </summary>
public static class L2DegradationNotices
{
    /// <summary>Neutral banner line (zh) for one degradation reason; empty for None.</summary>
    public static string BannerZh(L2DegradationReason reason) => reason switch
    {
        L2DegradationReason.None => string.Empty,
        L2DegradationReason.WebTier =>
            "本地军师（L2）在网页版不可用——已按 L1 军令语法执行。",
        L2DegradationReason.ModelMissing =>
            "本地军师（L2）模型未下载——已按 L1 军令语法执行；可在设置页「L2 本地模型」下载。",
        L2DegradationReason.RuntimeMissing =>
            "本地军师（L2）运行时缺失——已按 L1 军令语法执行；设置页可重新获取运行时。",
        L2DegradationReason.NotReady =>
            "本地军师（L2）启动中——先按 L1 军令语法执行。",
        L2DegradationReason.ProcessDead =>
            "本地军师（L2）进程已退出——已自动回退 L1 军令语法。",
        L2DegradationReason.FillTimeout =>
            "本地军师未在波间窗口内想好——这道令按 L1 处理，下一波间再试。",
        L2DegradationReason.FillFailed =>
            "本地军师没能解析这道令——已按 L1 军令语法处理。",
        _ => "本地军师暂不可用——已按 L1 军令语法执行。",
    };

    /// <summary>Neutral banner line (en) for one degradation reason; empty for None.</summary>
    public static string BannerEn(L2DegradationReason reason) => reason switch
    {
        L2DegradationReason.None => string.Empty,
        L2DegradationReason.WebTier =>
            "Local advisor (L2) is unavailable on the web build — handled by the L1 order grammar.",
        L2DegradationReason.ModelMissing =>
            "Local advisor (L2) model not downloaded — handled by the L1 grammar; download it on the Settings page.",
        L2DegradationReason.RuntimeMissing =>
            "Local advisor (L2) runtime missing — handled by the L1 grammar; re-fetch the runtime in Settings.",
        L2DegradationReason.NotReady =>
            "Local advisor (L2) is starting — using the L1 order grammar meanwhile.",
        L2DegradationReason.ProcessDead =>
            "Local advisor (L2) process exited — fell back to the L1 order grammar.",
        L2DegradationReason.FillTimeout =>
            "The local advisor did not finish within the wave gap — this order stays L1; retried next wave gap.",
        L2DegradationReason.FillFailed =>
            "The local advisor could not parse this order — handled by the L1 order grammar.",
        _ => "Local advisor unavailable — handled by the L1 order grammar.",
    };

    /// <summary>Every degradation reason must carry non-empty copy on both languages (tested — the banner can never render blank on a real degradation).</summary>
    public static readonly IReadOnlyList<L2DegradationReason> AllReasons =
    [
        L2DegradationReason.WebTier,
        L2DegradationReason.ModelMissing,
        L2DegradationReason.RuntimeMissing,
        L2DegradationReason.NotReady,
        L2DegradationReason.ProcessDead,
        L2DegradationReason.FillTimeout,
        L2DegradationReason.FillFailed,
    ];
}
