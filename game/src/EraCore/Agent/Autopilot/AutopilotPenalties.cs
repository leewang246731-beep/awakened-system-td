using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Autopilot;

// T-081 (C-13 / F-093) 三罚则执法（v3.2 §11.8 全托管口径的后三条）：
// 首通不给成就 / 收益 ×0.8 / 仅无尽记分。参与判定只读
// <see cref="AutopilotParticipation"/>（导演的参与快照）；执行点在本模块
// —— CampaignLoop / Pipeline / Progression 零改动（T-081 泳道纪律），
// 宿主在结算 seam 调用这里。三罚则均为纯函数：无随机、无时钟（CS-1.4）。

/// <summary>
/// Participation snapshot the penalty rules judge (F-093 参与判定):
/// level scope decides 首通无成就 + 收益 ×0.8 + 记分隔离；wave scope
/// documents the 当波不回退 semantics after a takeover（接管不清零已
/// 武装的罚则——导演在轮执行时置位，无任何清零路径）。
/// </summary>
/// <param name="LevelParticipated">Autopilot issued at least one action in this level (整个关卡口径).</param>
/// <param name="WaveParticipated">Autopilot issued at least one action in the current wave (当波口径).</param>
/// <param name="Mode">Hosting mode at snapshot time (takeover already downgraded it).</param>
public sealed record AutopilotParticipation(
    bool LevelParticipated,
    bool WaveParticipated,
    AutopilotMode Mode);

/// <summary>
/// The three full-autopilot penalties (v3.2 §11.8 全托管口径罚则，T-081
/// 承接验收"首通无成就+收益 ×0.8"的执法点):
/// <list type="number">
/// <item>首通无成就 — a level the autopilot participated in grants NO
/// first-clear achievement (成就跳过钩子: <see cref="FilterFirstClearAchievements"/>
/// empties the grant list);</item>
/// <item>收益 ×0.8 — every banked loot line of a participated level is
/// scaled by <see cref="AutopilotYieldFactor"/> (验收断言: 100→80，
/// <see cref="ScaleYield"/>);</item>
/// <item>仅无尽全托管榜记分 — participated runs score ONLY on the
/// isolated endless autopilot channel (<see cref="EndlessAutopilotChannelId"/>,
/// T-080 三榜挂接点), never on the standard boards.</item>
/// </list>
/// All three fire on LEVEL participation and never roll back: a takeover
/// downgrades the mode but leaves the armed penalties in force for the
/// level（当波/当关已生效则不回退）.
/// </summary>
public static class AutopilotPenalties
{
    /// <summary>收益罚系数（§11.8"收益 ×0.8"冻结口径——非平衡数值，不调参）。</summary>
    public const double AutopilotYieldFactor = 0.8; // ci-allow: R7 T-081 v3.2 §11.8 全托管"收益 ×0.8"冻结口径（非平衡数值）

    /// <summary>
    /// 无尽全托管榜通道 id（仅无尽记分——隔离通道，T-080 三榜雏形的挂接预留：
    /// 无尽三榜实现方按本常量认领通道，标准榜对托管局一律拒收）。
    /// </summary>
    public const string EndlessAutopilotChannelId = "endless.autopilot";

    /// <summary>标准记分通道 id（手动局——托管局永不路由到这里）。</summary>
    public const string StandardChannelId = "standard";

    /// <summary>
    /// 罚则 ② 收益 ×0.8 的数值核：对一条已入账的战利品行执行收益罚。
    /// 口径：<paramref name="granted"/> × <see cref="AutopilotYieldFactor"/>
    /// 向下取整（与 LevelSession 银入行的 Floor 同族，保持整数入账与确定性；
    /// 验收断言 100→80）。维持行不变量 Granted = Accepted + Refused：
    /// 罚掉的差额并回 Refused（未入账），Accepted 至多收缩到新 Granted。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">granted is negative.</exception>
    public static ResourceGrant ApplyYieldPenalty(ResourceGrant grant)
    {
        double scaled = ScaleYield(grant.Granted);
        double accepted = Math.Min(grant.Accepted, scaled);
        return grant with { Granted = scaled, Accepted = accepted, Refused = scaled - accepted };
    }

    /// <summary>
    /// 罚则 ② 的批量核：对整张结算 Loot 表逐行执行（托管参与的关卡整表 ×0.8）。
    /// 空表/未参与由调用方判定——本函数只做执法。
    /// </summary>
    public static IReadOnlyList<ResourceGrant> ApplyYieldPenalty(IReadOnlyList<ResourceGrant> loot)
    {
        ArgumentNullException.ThrowIfNull(loot);
        var scaled = new List<ResourceGrant>(loot.Count);
        foreach (ResourceGrant grant in loot)
        {
            scaled.Add(ApplyYieldPenalty(grant));
        }

        return scaled;
    }

    /// <summary>
    /// 收益罚的标量核（验收数值断言的直接对象）：floor(x × 0.8)。
    /// 100 → 80；负数拒绝（收益不存在负口径）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">amount is negative.</exception>
    public static double ScaleYield(double amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Yield amounts are never negative.");
        }

        return Math.Floor(amount * AutopilotYieldFactor);
    }

    /// <summary>
    /// 罚则 ① 首通无成就——资格判定：托管参与过的关卡，其首通结算不授成就
    /// （手动重打首通仍可授——参与口径按局快照，不是按账号）。
    /// </summary>
    public static bool FirstClearAchievementEligible(AutopilotParticipation participation) =>
        !participation.LevelParticipated;

    /// <summary>
    /// 罚则 ① 的成就跳过钩子：首通成就授予列表的过滤器——托管局返回空表
    /// （跳过全部授予），手动局原样放行。T-084/A 泳道成就系统在授予 seam
    /// 调用本钩子；调用方不得自行判定参与（单一执法点）。
    /// </summary>
    public static IReadOnlyList<string> FilterFirstClearAchievements(
        AutopilotParticipation participation,
        IReadOnlyList<string> achievements)
    {
        ArgumentNullException.ThrowIfNull(achievements);
        return FirstClearAchievementEligible(participation)
            ? achievements
            : Array.Empty<string>();
    }

    /// <summary>
    /// 罚则 ③ 仅无尽全托管榜记分——标准榜资格：托管参与的局永不进标准榜
    /// （三榜互不混排的 B 侧半边；A 侧半边——无尽榜只收托管通道——由
    /// <see cref="RouteScoreChannel"/> 一并执法）。
    /// </summary>
    public static bool EligibleForStandardBoards(AutopilotParticipation participation) =>
        !participation.LevelParticipated;

    /// <summary>
    /// 罚则 ③ 的记分路由（T-080 三榜挂接点）：一局结算应记入哪个通道。
    /// <list type="bullet">
    /// <item>托管参与 + 无尽局 → <see cref="EndlessAutopilotChannelId"/>
    /// （隔离通道，唯一合法去处）；</item>
    /// <item>托管参与 + 非无尽局 → null（不记分——托管的主线/关卡局
    /// 无榜可进，防"系统代玩刷分"空心化，R11）；</item>
    /// <item>未托管参与 → <see cref="StandardChannelId"/>（手动局照常，
    /// 含无尽手动局——T-080 手动无尽榜与本通道分记）。</item>
    /// </list>
    /// </summary>
    public static string? RouteScoreChannel(AutopilotParticipation participation, bool isEndlessRun) =>
        participation.LevelParticipated
            ? (isEndlessRun ? EndlessAutopilotChannelId : null)
            : StandardChannelId;
}

/// <summary>一条全托管无尽记分（T-080 三榜雏形的挂接数据面）。</summary>
/// <param name="ChannelId">Score channel — always <see cref="AutopilotPenalties.EndlessAutopilotChannelId"/> for autopilot runs.</param>
/// <param name="Wave">Final wave reached (endless figure of merit).</param>
/// <param name="ActionsIssued">Audit figure from <see cref="AutopilotDirector.ActionsIssued"/>.</param>
/// <param name="PointsSpentByAutopilot">Audit figure from <see cref="AutopilotDirector.PointsSpentByAutopilot"/>.</param>
public sealed record AutopilotScoreEntry(
    string ChannelId,
    long Wave,
    long ActionsIssued,
    long PointsSpentByAutopilot);

/// <summary>
/// 无尽全托管榜的挂接点（T-080 落地实现）：T-081 只定义通道与数据面——
/// 宿主/无尽模式在结算时把 <see cref="AutopilotScoreEntry"/> 交给实现方。
/// 本挂接点不落盘、不排序；唯一实现是测试双 <c>NoScoreSink</c>。
/// </summary>
public interface IAutopilotScoreSink
{
    /// <summary>Records one settled autopilot endless run (channel-isolated by construction).</summary>
    void Record(AutopilotScoreEntry entry);
}
