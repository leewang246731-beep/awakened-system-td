using EraCore.Agent.Autopilot;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Units;

namespace EraCore.Endless;

/// <summary>
/// One era's playable contribution to an endless entry (T-118 任选时代): the
/// enemy rows plus the era's wave shape (基数/起/间隔/成长 — the ≤40 regular
/// segment owner, SP-7a). The START era's shape drives the run; every
/// provided era's rows feed the 混编池. Caller owns the IO (parsed content
/// handed over); pure data record, host-supplied (RL7).
/// </summary>
/// <param name="EraIndex">Era index (1..6).</param>
/// <param name="Enemies">Parsed enemy rows of the era (e.g. era3 = the current four-level content set).</param>
/// <param name="WaveBaseCount">Wave 基数 of the era's wave table.</param>
/// <param name="WaveBegin">Wave 起 of the ramp.</param>
/// <param name="WaveSpacing">Wave 间隔 of the ramp (≤0 disables).</param>
/// <param name="WaveScaling">成长 divisor of the ramp.</param>
public sealed record EndlessEraContent(
    int EraIndex,
    IReadOnlyList<EnemySpec> Enemies,
    int WaveBaseCount,
    int WaveBegin,
    int WaveSpacing,
    double WaveScaling);

/// <summary>
/// 无尽入口请求（T-118 入口正式化，SP-7 入口规则行「任选时代、混编敌人、
/// 奇观带入」+ T-118 开局选档）: everything the entry composes the run from —
/// the chosen era, the LOCKED opening participation mode (三档之一——开局
/// 声明，整局锁榜), the loaded era content sets (任选时代=已装载内容集中的
/// 任一时代), the threat level, the carried-in wonder ids and the optional
/// tuning overrides (词缀照 SP-7 生产默认档 <see cref="EndlessAffixTuning.Default"/>).
/// </summary>
public sealed record EndlessEntryRequest(
    long Seed,
    int StartEraIndex,
    EndlessParticipationMode Participation,
    double ThreatLevel,
    IReadOnlyList<EndlessEraContent> EraContent,
    IReadOnlyList<string> WonderIds,
    int ElitesPerEra = EndlessEraPools.DefaultElitesPerEra,
    EndlessAffixTuning? Affixes = null,
    EndlessScoreWeights? Scores = null,
    int? BossEveryWaves = null);

/// <summary>
/// 无尽会话状态（T-118 死亡终局→记分→返回 的最小会话流）：Running（波次
/// 循环中——无胜利波）→ Settled（死亡终局已记分落账）→ Returned（宿主已
/// 返回战役界面）；Abandoned = 中途退出的诚实终态（不记分——死亡终局是
/// 唯一记分终态）。本状态机是 CampaignLoop（F-001 GameStateMachine）之外
/// 的特殊会话通道：无尽不进 LevelSession/检查点域，宿主从战役选图/菜单
/// 语境调 <see cref="EndlessEntry.Begin"/>，死亡记分后调
/// <see cref="EndlessSession.Return"/> 返回。
/// </summary>
public enum EndlessSessionState
{
    /// <summary>波次循环中（BeginWave/CompleteWave 合法）。</summary>
    Running = 0,

    /// <summary>死亡终局已结算记分（Die 已调用；波次 API 关闭）。</summary>
    Settled = 1,

    /// <summary>中途退出（不记分——终局=死亡唯一记分终态）。</summary>
    Abandoned = 2,

    /// <summary>已返回战役界面（会话闭环）。</summary>
    Returned = 3,
}

/// <summary>
/// 无尽入口（T-118 入口正式化）：从战役界面语境组装一局无尽会话。入口
/// 裁定（最小接入点）：战役侧现无无尽可寻址的 UI 面（presentation 未接
/// CampaignLoop），因此正式入口=本 <see cref="Begin"/> 组装 seam——宿主在
/// 战役选图/菜单语境（F-001 <c>GameFlowState.Menu</c> 等价站位）调用，
/// 无尽作为特殊会话接入战役循环（不进 LevelSession/检查点域；Godot 按钮
/// 绑定归 C-21 表现层任务挂接本 seam）。入口规则=SP-7 行：任选时代（开始
/// 时代必须在已装载内容集中）、混编敌人（<see cref="EndlessEraPools.Build"/>
/// 跨时代池）、奇观带入（id 序列透传配置冻结）、每 10 波词缀（默认节奏，
/// 词缀量级=<see cref="EndlessAffixTuning.Default"/> SP-7b 生产默认档）+
/// T-118 开局选档（三档之一随配置冻结=记档口径）。纯组装与校验：无 IO、
/// 无随机（rng 注入）、无时钟（CS-1.4）。
/// </summary>
public static class EndlessEntry
{
    /// <summary>
    /// 默认 Boss 节奏：每 10 波 1 只（T-080 [NEW-原型校准]——与 SP-7b
    /// 「每 10 波词缀」同节奏锚）。
    /// </summary>
    public const int DefaultBossEveryWaves = 10; // ci-allow: R7 T-080 [NEW-原型校准] Boss 节奏（T-118 入口默认档沿用）

    /// <summary>
    /// Assembles the endless session: resolves the start era's wave shape,
    /// builds the cross-era pool from every provided set (任选时代的内容
    /// 边界=装载集），freezes the run config（含开局参与档锁）and hands
    /// the deps through — port/campaignMeta wire the K-7 落账, scoreSink
    /// receives the T-081 autopilot entries.
    /// </summary>
    /// <exception cref="ArgumentNullException">request or rng is null.</exception>
    /// <exception cref="ArgumentException">
    /// Start era not among the provided content sets, no content sets, an
    /// undefined participation mode, or via <see cref="EndlessEraPools.Build"/>/
    /// <see cref="EndlessRunConfig"/> validation.
    /// </exception>
    public static EndlessSession Begin(
        EndlessEntryRequest request,
        IRng rng,
        ISavePort? port = null,
        ICampaignMeta? campaignMeta = null,
        IAutopilotScoreSink? scoreSink = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rng);
        if (!EndlessParticipation.IsDefined(request.Participation))
        {
            throw new ArgumentException(
                $"Participation mode must be a defined three-board mode (got {request.Participation}).",
                nameof(request));
        }

        EndlessEraContent? startEra = request.EraContent.FirstOrDefault(era =>
            era is not null && era.EraIndex == request.StartEraIndex);
        if (startEra is null)
        {
            throw new ArgumentException(
                $"Start era {request.StartEraIndex} is not among the loaded endless content sets " +
                $"[{string.Join(", ", request.EraContent.Select(era => era?.EraIndex.ToString() ?? "null"))}] — 任选时代限于已装载内容集.",
                nameof(request));
        }

        EndlessEnemyPool pool = EndlessEraPools.Build(
            request.EraContent.Where(era => era is not null)
                .Select(era => new EndlessEraEnemySet(era.EraIndex, era.Enemies))
                .ToList(),
            request.ElitesPerEra);
        var config = new EndlessRunConfig(
            request.Seed,
            request.StartEraIndex,
            request.ThreatLevel,
            startEra.WaveBaseCount,
            startEra.WaveBegin,
            startEra.WaveSpacing,
            startEra.WaveScaling,
            request.BossEveryWaves ?? DefaultBossEveryWaves,
            pool,
            request.Affixes ?? EndlessAffixTuning.Default,
            request.Scores ?? EndlessScoreWeights.Default,
            request.WonderIds,
            request.Participation);
        return new EndlessSession(new EndlessRun(config, rng, port, campaignMeta, scoreSink));
    }
}

/// <summary>
/// One live endless session (T-118 无尽作为特殊会话接入战役循环的通道):
/// wraps the <see cref="EndlessRun"/>, exposes the real-time participation
/// HUD snapshot (<see cref="Participation"/>) and owns the minimal session
/// flow Running → Settled/Abandoned → Returned. 死亡终局=<see cref="Die"/>
/// （记分+落账+T-081 sink——见 <see cref="EndlessRun.Defeat"/>）；
/// <see cref="Abandon"/> is the honest no-score exit（终局=死亡唯一记分终态）;
/// <see cref="Return"/> closes the loop back to the campaign surface.
/// </summary>
public sealed class EndlessSession
{
    internal EndlessSession(EndlessRun run)
    {
        Run = run;
    }

    /// <summary>The wrapped endless run (wave loop, action audit, settle).</summary>
    public EndlessRun Run { get; }

    /// <summary>Session flow state (Running/Settled/Abandoned/Returned).</summary>
    public EndlessSessionState State { get; private set; } = EndlessSessionState.Running;

    /// <summary>
    /// 局内参与状态实时查询（T-118 HUD 位）：开局锁档+当前档+动作审计+换档
    /// 计数——表现层从此处取值，每帧可查。
    /// </summary>
    public EndlessParticipationSnapshot Participation => Run.Participation;

    /// <summary>
    /// 死亡终局→记分（T-118）: settles the run exactly once — board = the
    /// locked opening mode (三榜分记互不混排), the K-7 campaign domain
    /// persists when a port was wired, and the T-081 sink receives the
    /// autopilot entries. Terminal wave APIs die with the run.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not Running, or already settled/returned.</exception>
    public EndlessSettlement Die()
    {
        RequireRunning("die at the death terminal");
        EndlessSettlement settlement = Run.Defeat();
        State = EndlessSessionState.Settled;
        return settlement;
    }

    /// <summary>
    /// 中途退出（不记分）: the honest exit for a run the player walks away
    /// from — 死亡终局 is the ONLY scoring terminal, so an abandoned run
    /// records nothing (负向断言锚点：弃局不产生任何榜行).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not Running.</exception>
    public void Abandon()
    {
        RequireRunning("abandon a live session");
        State = EndlessSessionState.Abandoned;
    }

    /// <summary>死亡记分后返回战役界面（会话闭环）；弃局亦可返回。</summary>
    /// <exception cref="InvalidOperationException">Still Running (die/abandon first), or already returned.</exception>
    public void Return()
    {
        if (State == EndlessSessionState.Running)
        {
            throw new InvalidOperationException("An endless session returns only after dying or abandoning it.");
        }

        if (State == EndlessSessionState.Returned)
        {
            throw new InvalidOperationException("The session already returned to the campaign surface.");
        }

        State = EndlessSessionState.Returned;
    }

    private void RequireRunning(string action)
    {
        if (State != EndlessSessionState.Running)
        {
            throw new InvalidOperationException($"Cannot {action}: the session is {State}.");
        }
    }
}
