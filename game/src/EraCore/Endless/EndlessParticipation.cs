namespace EraCore.Endless;

/// <summary>
/// 开局参与档（T-118 局面参与状态机，F-006/F-123「三类游玩方式分记互不混排」）:
/// 纯手动 / 军师辅助 / 全托管 — 三档在开局声明并随
/// <see cref="EndlessRunConfig"/> 冻结（开局档），整局锁定为记档口径：
/// 中途换档（<see cref="EndlessRun.SwitchParticipation"/>) 只改当前档
/// （HUD 与审计可见），<b>绝不换榜</b>。T-081「当波降级」语义对齐——
/// 罚则当波（接管降级只影响当波导演轮），记档整局（结算榜别=开局档）。
/// </summary>
public enum EndlessParticipationMode
{
    /// <summary>纯手动 — 军师不执行任何动作（开局档锁 <see cref="EndlessBoardKind.Manual"/>）.</summary>
    Manual = 0,

    /// <summary>军师辅助 — 军师在玩家主导下参与执行（开局档锁 <see cref="EndlessBoardKind.Assisted"/>）.</summary>
    Assisted = 1,

    /// <summary>全托管 — 导演波内循环主导执行（开局档锁 <see cref="EndlessBoardKind.Autopilot"/>）.</summary>
    FullAutopilot = 2,
}

/// <summary>
/// 一次中途换档的审计行（T-118）：波次号 + 档位迁移 + 宿主给出的机读
/// 原因（如 <c>takeover:manual-build</c> — 与 T-081
/// <c>AutopilotDirector.NotifyManualIntervention</c> 的 reason 同风格）。
/// 换档<b>不换榜</b>——本行只进运行哈希与结算审计，不参与榜别路由。
/// </summary>
/// <param name="Wave">换档时的波次号（0=首波前）。</param>
/// <param name="From">换档前当前档。</param>
/// <param name="To">换档后当前档（≠ From——同档调用不产生审计行）。</param>
/// <param name="Reason">机读换档原因（宿主标注）。</param>
public sealed record EndlessParticipationSwitch(
    int Wave,
    EndlessParticipationMode From,
    EndlessParticipationMode To,
    string Reason);

/// <summary>
/// 局内参与状态实时查询快照（T-118 HUD 位/UI 查询接口）：一次只读取值
/// 拿到开局锁档、当前档、动作审计与换档计数——HUD 每帧可查，无锁、无
/// 拷贝循环。快照即接口：表现层（C-21 Godot 绑定）从任一会话
/// （<see cref="EndlessSession.Participation"/> / <see cref="EndlessRun.Participation"/>）
/// 取值渲染，本类型不依赖表现层。
/// </summary>
/// <param name="LockedMode">开局档（记档口径——结算榜别的唯一依据）。</param>
/// <param name="CurrentMode">当前档（中途换档后≠<paramref name="LockedMode"/>；不换榜）。</param>
/// <param name="CurrentWave">最近开始的波次号（0=尚未开波）。</param>
/// <param name="ActionsTotal">全程动作数（手动+军师，SP-7c 占比分母）。</param>
/// <param name="AutopilotActions">其中军师/系统执行的动作数（SP-7c 占比分子）。</param>
/// <param name="AutopilotShare">系统执行动作占比 ∈ [0,1]（0 当无动作）——审计列，不路由榜别。</param>
/// <param name="SwitchCount">中途换档次数。</param>
/// <param name="AdvisorParticipated">军师参与位（T-081 参与位语义：军师实际执行过 ≥1 动作——按动作数不按军师令消耗，失败动作不扣点但已参与）。</param>
/// <param name="IsOver">运行已死亡结算（终局后快照只读）。</param>
public sealed record EndlessParticipationSnapshot(
    EndlessParticipationMode LockedMode,
    EndlessParticipationMode CurrentMode,
    int CurrentWave,
    long ActionsTotal,
    long AutopilotActions,
    double AutopilotShare,
    int SwitchCount,
    bool AdvisorParticipated,
    bool IsOver);

/// <summary>
/// 参与档 → 榜别 的唯一映射（T-118 整局锁定口径）：开局档在结算时路由到
/// 对应三榜之一——纯手动→手动榜、军师辅助→系统辅助榜、全托管→全托管榜。
/// 与 SP-7c 占比式判定（<see cref="EndlessRun.RouteBoard"/>）的关系：占比
/// 保留为结算审计列（ShareImpliedBoard），<b>不参与路由</b>——T-118 任务卡
/// 裁定「中途换档不换榜（整局锁定开局档；罚则当波、记档整局）」。
/// </summary>
public static class EndlessParticipation
{
    /// <summary>
    /// 开局档 → 榜别（记档整局的唯一路由依据）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">mode is not a defined mode.</exception>
    public static EndlessBoardKind BoardOf(EndlessParticipationMode mode) => mode switch
    {
        EndlessParticipationMode.Manual => EndlessBoardKind.Manual,
        EndlessParticipationMode.Assisted => EndlessBoardKind.Assisted,
        EndlessParticipationMode.FullAutopilot => EndlessBoardKind.Autopilot,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown participation mode."),
    };

    /// <summary>校验枚举值有定义（配置/换档入参的守卫核）。</summary>
    public static bool IsDefined(EndlessParticipationMode mode) =>
        mode is EndlessParticipationMode.Manual or EndlessParticipationMode.Assisted
            or EndlessParticipationMode.FullAutopilot;
}
