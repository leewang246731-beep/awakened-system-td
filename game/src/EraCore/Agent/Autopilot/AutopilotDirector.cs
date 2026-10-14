using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Sim;
using EraCore.Supply;

namespace EraCore.Agent.Autopilot;

// T-081 (C-13 / F-093) 全托管导演：波内循环——每轮[军师令预算允许时]生成
// 目标级意图（HoldRegion 优先 / 威胁响应[断供瓶颈事件→RerouteLogistics 等]）
// → 走 T-068 五段管线（HandleSystemAction 单动作泳道）→ ≤3 动作/轮硬限
// （AutopilotRoundQuota 轮内配额执法，第 4 动作拒入本轮）→ 轮末汇报。
// 迭代上限 10 轮/波且与军师令点数取小（min(10, 点数)）；轮间等待（下一波
// 事件或供给事件驱动——BeginWave / NotifySupplyBottleneck 两个唤醒源）。
// 三罚则（首通无成就 / 收益 ×0.8 / 仅无尽全托管榜记分）的参与判定与执行
// 在 AutopilotPenalties；CampaignLoop/Pipeline 只读复用，零改动。

/// <summary>Hosting mode of the autopilot lane (F-093; 接管降级语义).</summary>
public enum AutopilotMode
{
    /// <summary>Not hosting (the system only answers orders).</summary>
    Off = 0,

    /// <summary>Assist caliber: manual orders flow, autopilot rounds are OFF.</summary>
    Assisted = 1,

    /// <summary>Full autopilot: the director runs the wave loop under the F-093 caliber.</summary>
    FullAutopilot = 2,
}

/// <summary>One planned action of an autopilot round (verb + schema-shaped JSON).</summary>
public sealed record AutopilotPlannedAction(SystemActionKind Verb, string ActionJson);

/// <summary>
/// One round's goal-level intent draft (T-081 意图段): the intent label the
/// round reports plus its planned action list. The default chooser builds
/// these from live state; a host (or test) may inject its own source.
/// </summary>
public sealed record AutopilotIntentDraft(IntentKind Kind, IReadOnlyList<AutopilotPlannedAction> Actions);

/// <summary>Execution digest of one autopilot-issued action.</summary>
/// <param name="Verb">The issued verb.</param>
/// <param name="Status">Final pipeline status of the verb's step (Ok / Err… / outcome kind).</param>
/// <param name="PointsSpent">Points actually billed (failed actions never bill).</param>
/// <param name="SelfCorrected">Whether the pipeline's ≤2 self-correction loop fired.</param>
public sealed record AutopilotActionRecord(SystemActionKind Verb, string Status, int PointsSpent, bool SelfCorrected);

/// <summary>
/// Outcome of one autopilot round (1 轮迭代 = 意图→计划→≤3 动作→汇报,
/// v3.2 §11.8). The round-final 汇报 is a real ReportToPlayer order and is
/// NOT part of the ≤3 action quota (the design sentence lists 汇报 as the
/// closing stage after the action stage).
/// </summary>
/// <param name="Round">1-based round number inside the wave.</param>
/// <param name="Intent">The goal-level intent the round pursued.</param>
/// <param name="Trigger">What woke the round (wave-N / supply-bottleneck:seq / custom).</param>
/// <param name="Actions">Executed action digests, in issue order.</param>
/// <param name="QuotaUsed">Quota slots consumed this round (≤ <see cref="AutopilotRoundQuota.ActionsPerRound"/>).</param>
/// <param name="QuotaRefused">True when an action was refused entry (第 4 动作拒入本轮).</param>
/// <param name="ReportSummary">The round-final report text shipped to the player.</param>
public sealed record AutopilotRoundResult(
    int Round,
    IntentKind Intent,
    string Trigger,
    IReadOnlyList<AutopilotActionRecord> Actions,
    int QuotaUsed,
    bool QuotaRefused,
    string ReportSummary);

/// <summary>
/// The full-autopilot director (T-081, C-13 / F-093): drives the T-068
/// five-stage pipeline autonomously, wave by wave, under the §11.8 全托管
/// caliber:
/// <list type="bullet">
/// <item>1 round = intent → plan → ≤3 actions → report; the in-round quota
/// (<see cref="AutopilotRoundQuota"/>) refuses a 4th action entry.</item>
/// <item>Iteration cap = min(10, wave points) rounds per wave
/// (<see cref="IterationCap"/>); points come from the shared
/// <see cref="Governance.SystemPointLedger"/> (波初 BeginWave 后余额).</item>
/// <item>Between rounds the director WAITS — it never spins: wake sources
/// are the next wave event (<see cref="BeginWave"/>) and supply events
/// (<see cref="NotifySupplyBottleneck"/>).</item>
/// <item>Full-autopilot authority caliber: the director issues every action
/// through <see cref="AgentPipeline.HandleSystemAction"/> (the documented
/// T-081 lane) and self-confirms its own ask-tier plan cards via the
/// pipeline's public <c>Confirm</c> — the player handed the confirmation
/// right over by engaging full autopilot; every confirmation stays in the
/// K-8 JSONL audit trail.</item>
/// <item>Takeover (接管降级): any manual order/build while hosting downgrades
/// the mode to <see cref="AutopilotMode.Assisted"/> for the rest of the
/// wave — and it stays assisted until the host re-engages (接管即撤回授权);
/// penalties already armed for the wave do NOT roll back (语义: 罚则当波
/// 已生效则不回退).</item>
/// </list>
/// Deterministic (F-093 确定性口径): no RNG, no wall clock — every choice
/// is a pure function of the world view and the director counters; same
/// seed + same wake sequence ⇒ equal <see cref="ComputeStateHash"/>
/// sequence and equal sim hashes.
/// </summary>
public sealed class AutopilotDirector
{
    /// <summary>迭代上限：10 轮/波（§11.8"10 轮/波×点数取小"的常量半边）。</summary>
    public const int MaxIterationsPerWave = 10; // ci-allow: R7 T-081 v3.2 §11.8 全托管"10 轮/波"冻结口径（白名单值亦含 10）

    /// <summary>HoldRegion 轮的建塔数——与 SP-2 #1 目标级模板同对（2 座）。</summary>
    public const int HoldRegionTowerQty = 2; // ci-allow: R7 T-081 与 PipelinePlanner HoldRegion 模板 qty:2 同源口径

    /// <summary>分区"已设防"判定半径（Chebyshev，格）：锚点该半径内无塔 → HoldRegion 轮。</summary>
    public const int DefenseRadiusCells = 3; // ci-allow: R7 T-081 分区设防判定的工程半径（非平衡数值）

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly AgentPipeline _pipeline;
    private readonly IActionWorldState _view;
    private readonly IReadOnlyList<KeyValuePair<string, CellCoord>> _anchors;
    private readonly string _defaultTowerId;
    private readonly Func<AutopilotDirector, AutopilotIntentDraft?>? _intentSource;
    private readonly AutopilotRoundQuota _quota = new();
    private readonly List<SupplyBottleneckEvent> _bottlenecks = new();

    private int _roundsUsedThisWave;
    private int _iterationCapThisWave;
    private long _wave;
    private bool _waveParticipated;
    private bool _levelParticipated;
    private long _actionsIssued;
    private long _pointsSpent;
    private long _takeovers;

    /// <param name="pipeline">The T-068 five-stage pipeline (public API only — read-reuse).</param>
    /// <param name="view">Read-only world-state projection (towers/enemies/buildings/buildability).</param>
    /// <param name="regionAnchors">Language-neutral region id → anchor cell (HoldRegion targets).</param>
    /// <param name="defaultTowerId">Tower the HoldRegion round builds.</param>
    /// <param name="intentSource">Optional goal-intent strategy; null = the built-in priority chooser.</param>
    public AutopilotDirector(
        AgentPipeline pipeline,
        IActionWorldState view,
        IReadOnlyDictionary<string, CellCoord> regionAnchors,
        string defaultTowerId,
        Func<AutopilotDirector, AutopilotIntentDraft?>? intentSource = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(regionAnchors);
        if (string.IsNullOrWhiteSpace(defaultTowerId))
        {
            throw new ArgumentException("Default defense tower id must not be empty.", nameof(defaultTowerId));
        }

        _pipeline = pipeline;
        _view = view;
        _defaultTowerId = defaultTowerId;
        _intentSource = intentSource;
        // Anchor order = region id ordinal: deterministic iteration whatever
        // the dictionary's internal layout does.
        _anchors = regionAnchors.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Mode = AutopilotMode.Off;
    }

    /// <summary>Composition root over one live simulation (mirrors the pipeline's own).</summary>
    /// <param name="pipeline">The pipeline built over the same simulation.</param>
    /// <param name="simulation">The live C-12 simulation.</param>
    /// <param name="config">The config that simulation was built with.</param>
    /// <param name="regionAnchors">Region id → anchor cell.</param>
    /// <param name="defaultTowerId">HoldRegion build target; null = first weapon tower in the catalog.</param>
    /// <param name="intentSource">Optional goal-intent strategy; null = built-in chooser.</param>
    public static AutopilotDirector OverSimulation(
        AgentPipeline pipeline,
        Simulation simulation,
        SimulationConfig config,
        IReadOnlyDictionary<string, CellCoord> regionAnchors,
        string? defaultTowerId = null,
        Func<AutopilotDirector, AutopilotIntentDraft?>? intentSource = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(config);
        return new AutopilotDirector(
            pipeline,
            new SimulationActionView(simulation, config),
            regionAnchors,
            defaultTowerId ?? AgentPipeline.FirstWeaponTowerId(config),
            intentSource);
    }

    /// <summary>Current hosting mode (takeover downgrades FullAutopilot → Assisted).</summary>
    public AutopilotMode Mode { get; private set; }

    /// <summary>Rounds executed in the current wave (≤ <see cref="IterationCapThisWave"/>).</summary>
    public int RoundsUsedThisWave => _roundsUsedThisWave;

    /// <summary>Effective iteration cap of the current wave = min(10, wave points at BeginWave).</summary>
    public int IterationCapThisWave => _iterationCapThisWave;

    /// <summary>Wave number of the current wave (0 before the first BeginWave).</summary>
    public long Wave => _wave;

    /// <summary>Reason of the latest manual takeover (null when none happened).</summary>
    public string? LastTakeoverReason { get; private set; }

    /// <summary>
    /// Iteration cap formula (§11.8: 10 轮/波×点数取小): min(10, points),
    /// clamped at 0. Pure — the table-driven tests pin (points → cap).
    /// </summary>
    public static int IterationCap(int wavePoints) =>
        Math.Min(MaxIterationsPerWave, Math.Max(0, wavePoints));

    /// <summary>Player opt-in: arms full autopilot (also the re-arm after a takeover).</summary>
    public void EngageFullAutopilot() => Mode = AutopilotMode.FullAutopilot;

    /// <summary>Host-side off switch: the autopilot lane stops issuing anything.</summary>
    public void Disengage() => Mode = AutopilotMode.Off;

    /// <summary>
    /// Player takeover (接管降级): any manual military order or build while
    /// hosting downgrades the mode to <see cref="AutopilotMode.Assisted"/>
    /// — 当波内退出全托管口径. Penalty semantics: flags already armed for
    /// the wave/level stay armed (罚则当波已生效则不回退); the mode stays
    /// assisted until the host re-engages (接管即撤回授权).
    /// </summary>
    /// <param name="reason">Machine-readable takeover cause (host labels it).</param>
    public void NotifyManualIntervention(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (Mode == AutopilotMode.FullAutopilot)
        {
            Mode = AutopilotMode.Assisted;
            _takeovers++;
            LastTakeoverReason = reason;
        }
    }

    /// <summary>
    /// Next-wave wake source: refills the shared ledger (BeginWave), resets
    /// the round counter, recomputes the iteration cap from the wave's point
    /// allotment and drops stale bottleneck events (each episode re-emits).
    /// </summary>
    /// <returns>false when the ledger rejected the (forged/duplicate) wave.</returns>
    public bool BeginWave(long waveNumber, bool isBossWave)
    {
        if (!_pipeline.Ledger.BeginWave(waveNumber, isBossWave))
        {
            return false;
        }

        _wave = waveNumber;
        _roundsUsedThisWave = 0;
        _iterationCapThisWave = IterationCap(_pipeline.Ledger.Points);
        _waveParticipated = false;
        _bottlenecks.Clear();
        return true;
    }

    /// <summary>
    /// The wave's iteration loop: rounds until the iteration cap, a takeover
    /// or an idle choice (无可行动意图 — 轮间等待下一唤醒源). Never spins.
    /// </summary>
    public IReadOnlyList<AutopilotRoundResult> RunWave()
    {
        var rounds = new List<AutopilotRoundResult>();
        while (Mode == AutopilotMode.FullAutopilot && _roundsUsedThisWave < _iterationCapThisWave)
        {
            AutopilotRoundResult? round = RunRound($"wave-{_wave}");
            if (round is null)
            {
                break;
            }

            rounds.Add(round);
        }

        return rounds;
    }

    /// <summary>
    /// Supply-event wake source (供给事件驱动): queues the bottleneck and
    /// immediately attempts one threat-response round under the same caps
    /// (quota + iteration). Returns the round, or null when the director is
    /// downgraded/capped — the event then stays queued for a later round.
    /// </summary>
    public AutopilotRoundResult? NotifySupplyBottleneck(SupplyBottleneckEvent bottleneckEvent)
    {
        ArgumentNullException.ThrowIfNull(bottleneckEvent);
        _bottlenecks.Add(bottleneckEvent);
        return RunRound($"supply-bottleneck:{bottleneckEvent.Seq}");
    }

    /// <summary>
    /// Runs ONE round from the (injected or built-in) intent chooser.
    /// Returns null — without consuming an iteration — when the mode is not
    /// full autopilot, the iteration cap is reached, or no intent is
    /// actionable (idle: the honest answer is waiting, not inventing work).
    /// </summary>
    public AutopilotRoundResult? RunRound(string trigger)
    {
        ArgumentException.ThrowIfNullOrEmpty(trigger);
        if (Mode != AutopilotMode.FullAutopilot || _roundsUsedThisWave >= _iterationCapThisWave)
        {
            return null;
        }

        Func<AutopilotDirector, AutopilotIntentDraft?> chooser =
            _intentSource ?? (_ => DefaultIntentSource());
        AutopilotIntentDraft? draft = chooser(this);
        if (draft is null || draft.Actions.Count == 0)
        {
            return null;
        }

        return ExecuteRound(draft, trigger);
    }

    /// <summary>
    /// Executes one prepared round (意图→计划→≤3 动作→汇报): arms the
    /// participation flags, opens a fresh quota window, issues each planned
    /// action through the pipeline (quota-refused actions are NOT issued —
    /// 拒入本轮), then ships the round-final report.
    /// </summary>
    /// <exception cref="ArgumentNullException">draft is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Mode is not full autopilot, or the wave's iteration cap is reached,
    /// or the draft exceeds the round quota by more than the report verb.
    /// </exception>
    public AutopilotRoundResult ExecuteRound(AutopilotIntentDraft draft, string trigger)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(trigger);
        if (Mode != AutopilotMode.FullAutopilot)
        {
            throw new InvalidOperationException($"Autopilot rounds need full autopilot (mode={Mode}).");
        }

        if (_roundsUsedThisWave >= _iterationCapThisWave)
        {
            throw new InvalidOperationException(
                $"Iteration cap reached: {_roundsUsedThisWave}/{_iterationCapThisWave} rounds in wave {_wave}.");
        }

        int counted = draft.Actions.Count(action => action.Verb != SystemActionKind.ReportToPlayer);
        if (counted > AutopilotRoundQuota.ActionsPerRound)
        {
            throw new InvalidOperationException(
                $"A round plans {counted} actions — the §11.8 hard limit is {AutopilotRoundQuota.ActionsPerRound}.");
        }

        _roundsUsedThisWave++;
        _waveParticipated = true; // 罚则武装：当波已生效不回退（NotifyManualIntervention 不清零）
        _levelParticipated = true;
        _quota.BeginRound();
        int round = _roundsUsedThisWave;

        var executed = new List<AutopilotActionRecord>();
        bool refused = false;
        foreach (AutopilotPlannedAction action in draft.Actions)
        {
            if (!_quota.TryEnter(action.Verb))
            {
                // 第 4 动作拒入本轮：不进管线、不留排队——如实记录。
                refused = true;
                break;
            }

            executed.Add(IssueThroughPipeline(action));
        }

        // 轮末汇报（"→汇报"收尾段）：真实 ReportToPlayer 军令，不占 3 动作配额。
        string summary =
            $"全托管第 {round} 轮（{trigger}，意图 {draft.Kind}）："
            + $"执行 {executed.Count}/{draft.Actions.Count} 动作，"
            + $"配额 {_quota.Used}/{AutopilotRoundQuota.ActionsPerRound}，"
            + $"剩余军师令 {_pipeline.Ledger.Points} 点";
        _ = IssueThroughPipeline(new AutopilotPlannedAction(
            SystemActionKind.ReportToPlayer,
            JsonSerializer.Serialize(
                new ReportActionJson(nameof(SystemActionKind.ReportToPlayer), summary, Findings: null, Verdict: "CONFIRMED"),
                PipelinePlannerJsonContext.Default.ReportActionJson)));
        return new AutopilotRoundResult(round, draft.Kind, trigger, executed, _quota.Used, refused, summary);
    }

    /// <summary>
    /// Participation snapshot for the penalty hooks (F-093 三罚则参与判定):
    /// level scope feeds 首通无成就 + 收益 ×0.8; wave scope documents the
    /// 当波不回退 semantics after a takeover.
    /// </summary>
    public AutopilotParticipation Participation => new(_levelParticipated, _waveParticipated, Mode);

    /// <summary>Total points the director's actions actually billed (audit figure).</summary>
    public long PointsSpentByAutopilot => _pointsSpent;

    /// <summary>Total actions the director issued into the pipeline (audit figure).</summary>
    public long ActionsIssued => _actionsIssued;

    /// <summary>
    /// FNV-1a hash over the director's observable state (mode/wave/counters/
    /// participation/quota/bottleneck queue). Same seed + same wake sequence
    /// ⇒ equal hashes — the determinism assertion pairs this with
    /// <see cref="Simulation.ComputeStateHash"/>.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)Mode);
        hash = Mix(hash, (ulong)_wave);
        hash = Mix(hash, (ulong)_roundsUsedThisWave);
        hash = Mix(hash, (ulong)_iterationCapThisWave);
        hash = Mix(hash, (ulong)_anchors.Count);
        hash = Mix(hash, _waveParticipated ? 1UL : 0UL);
        hash = Mix(hash, _levelParticipated ? 1UL : 0UL);
        hash = Mix(hash, (ulong)_bottlenecks.Count);
        hash = Mix(hash, (ulong)_actionsIssued);
        hash = Mix(hash, (ulong)_pointsSpent);
        hash = Mix(hash, (ulong)_takeovers);
        hash = Mix(hash, (ulong)_quota.Used);
        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    // ------------------------------------------------意图段（目标级，确定性优先序）--

    /// <summary>
    /// Built-in goal-intent chooser (每轮[军师令预算允许时]生成目标级意图):
    /// ① HoldRegion 优先 — first undefended anchor (region id ordinal)
    /// plans recon + a defense pair; ② threat response — a queued supply
    /// bottleneck plans recon + RerouteLogistics (断供瓶颈→RerouteLogistics);
    /// ③ combat — living enemies with a stale focus target plan recon +
    /// FocusFire. Drafts the ledger cannot afford (军师令预算不允许) are
    /// skipped — the round then idles (等待下一唤醒源), it never forces a
    /// doomed issue. Unresolvable bottleneck events are consumed honestly.
    /// </summary>
    private AutopilotIntentDraft? DefaultIntentSource()
    {
        // ① HoldRegion 优先。
        foreach (KeyValuePair<string, CellCoord> anchor in _anchors)
        {
            if (IsDefended(anchor.Value))
            {
                continue;
            }

            IReadOnlyList<CellCoord> cells = BuildableRingCells(anchor.Value, HoldRegionTowerQty);
            if (cells.Count == 0)
            {
                continue;
            }

            var draft = new AutopilotIntentDraft(
                IntentKind.HoldRegion,
                new[]
                {
                    new AutopilotPlannedAction(
                        SystemActionKind.LookAround,
                        JsonSerializer.Serialize(
                            new LookAroundActionJson(nameof(SystemActionKind.LookAround)),
                            PipelinePlannerJsonContext.Default.LookAroundActionJson)),
                    new AutopilotPlannedAction(
                        SystemActionKind.BuildPlan,
                        JsonSerializer.Serialize(
                            new BuildPlanActionJson(
                                nameof(SystemActionKind.BuildPlan),
                                _defaultTowerId,
                                cells.Select(cell => new CellJson(cell.X, cell.Y)).ToArray()),
                            PipelinePlannerJsonContext.Default.BuildPlanActionJson)),
                });
            if (Affordable(draft))
            {
                return draft;
            }
        }

        // ② 威胁响应：断供瓶颈 → RerouteLogistics（事件按序消费，解不出的如实丢弃）。
        while (_bottlenecks.Count > 0)
        {
            SupplyBottleneckEvent bottleneck = _bottlenecks[0];
            _bottlenecks.RemoveAt(0);
            if (BuildRerouteDraft(bottleneck) is { } reroute)
            {
                return reroute;
            }
        }

        // ③ 战斗：场上有活敌且无现行集火目标。
        if (BuildCombatDraft() is { } combat)
        {
            return combat;
        }

        return null;
    }

    /// <summary>Threat-response draft: reroute the ammo chain onto the starved tower.</summary>
    private AutopilotIntentDraft? BuildRerouteDraft(SupplyBottleneckEvent bottleneck)
    {
        // Supplier preference: the lowest-id converter (ammo source), then
        // the lowest-id harvester; receiver: the starved tower itself (towers
        // are building-roster members — the E1 milestone feeds towers the
        // same way). Either side unresolvable ⇒ null (honest skip).
        long? from = FirstBuildingIdOf(SimBuildingKind.Converter)
            ?? FirstBuildingIdOf(SimBuildingKind.Harvester);
        long towerId = bottleneck.TowerId.Value;
        if (from is not { } supplier
            || _view.Buildings.All(building => building.Id != towerId)
            || supplier == towerId)
        {
            return null;
        }

        var draft = new AutopilotIntentDraft(
            IntentKind.RerouteLogistics,
            new[]
            {
                new AutopilotPlannedAction(
                    SystemActionKind.LookAround,
                    JsonSerializer.Serialize(
                        new LookAroundActionJson(nameof(SystemActionKind.LookAround)),
                        PipelinePlannerJsonContext.Default.LookAroundActionJson)),
                new AutopilotPlannedAction(
                    SystemActionKind.RerouteLogistics,
                    JsonSerializer.Serialize(
                        new RerouteLogisticsActionJson(
                            nameof(SystemActionKind.RerouteLogistics),
                            supplier,
                            towerId,
                            _view.CostCurrency.Value,
                            PipelinePlanner.RerouteRateDefault),
                        PipelinePlannerJsonContext.Default.RerouteLogisticsActionJson)),
            });
        return Affordable(draft) ? draft : null;
    }

    /// <summary>Combat draft: recon + focus the first living enemy when no live focus target is set.</summary>
    private AutopilotIntentDraft? BuildCombatDraft()
    {
        IReadOnlyList<EnemyLine> enemies = _view.LivingEnemies;
        if (enemies.Count == 0)
        {
            return null;
        }

        long? focus = _pipeline.State.FocusTarget;
        if (focus is { } current && enemies.Any(enemy => enemy.Id == current))
        {
            return null; // 集火目标仍在场——不重复下达
        }

        var draft = new AutopilotIntentDraft(
            IntentKind.FocusFire,
            new[]
            {
                new AutopilotPlannedAction(
                    SystemActionKind.LookAround,
                    JsonSerializer.Serialize(
                        new LookAroundActionJson(nameof(SystemActionKind.LookAround)),
                        PipelinePlannerJsonContext.Default.LookAroundActionJson)),
                new AutopilotPlannedAction(
                    SystemActionKind.FocusFire,
                    JsonSerializer.Serialize(
                        new FocusFireActionJson(nameof(SystemActionKind.FocusFire), enemies[0].Id),
                        PipelinePlannerJsonContext.Default.FocusFireActionJson)),
            });
        return Affordable(draft) ? draft : null;
    }

    /// <summary>军师令预算允许 gate: the ledger must cover the draft's priced total.</summary>
    private bool Affordable(AutopilotIntentDraft draft) =>
        _pipeline.Ledger.CanCoverPlan(draft.Actions.Sum(action => _pipeline.Ledger.PriceOf(action.Verb)));

    /// <summary>Anchor defended ⇔ some placed tower within <see cref="DefenseRadiusCells"/> (Chebyshev).</summary>
    private bool IsDefended(CellCoord anchor) =>
        _view.Towers.Any(tower =>
            Math.Max(Math.Abs(tower.Cell.X - anchor.X), Math.Abs(tower.Cell.Y - anchor.Y)) <= DefenseRadiusCells);

    private long? FirstBuildingIdOf(SimBuildingKind kind)
    {
        BuildingLine? first = _view.Buildings
            .Where(building => building.Kind == kind)
            .OrderBy(building => building.Id)
            .FirstOrDefault();
        return first is null ? null : first.Id;
    }

    /// <summary>
    /// Deterministic buildable ring scan (radius 0 then 1..planner
    /// <see cref="PipelinePlanner.MaxScanRadius"/>, ring cells row-major).
    /// Unlike the planner's raw scan this FILTERS buildability: the director
    /// owns the cell choice and spends its quota on cells that can host.
    /// </summary>
    private IReadOnlyList<CellCoord> BuildableRingCells(CellCoord anchor, int count)
    {
        var cells = new List<CellCoord>(count);
        for (int radius = 0; radius <= PipelinePlanner.MaxScanRadius && cells.Count < count; radius++)
        {
            for (int dy = -radius; dy <= radius && cells.Count < count; dy++)
            {
                for (int dx = -radius; dx <= radius && cells.Count < count; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue; // ring cells only
                    }

                    CellCoord cell = new(anchor.X + dx, anchor.Y + dy);
                    if (_view.IsBuildable(cell, out string? rejection))
                    {
                        cells.Add(cell);
                    }
                }
            }
        }

        return cells;
    }

    // ------------------------------------------------动作段（五段管线单动作泳道）--

    /// <summary>
    /// Issues one action through the T-068 pipeline (HandleSystemAction —
    /// the documented T-081 lane) and self-confirms ask-tier cards (全托管
    /// 授权转让的确认权行使——每次确认都在 K-8 JSONL 里留痕). Never throws
    /// for in-world failures: the honest digest comes back instead.
    /// </summary>
    private AutopilotActionRecord IssueThroughPipeline(AutopilotPlannedAction action)
    {
        OrderOutcome outcome = _pipeline.HandleSystemAction(action.Verb, action.ActionJson);
        if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
        {
            outcome = _pipeline.Confirm(outcome.PendingId);
        }

        _actionsIssued++;
        (string status, int points, bool selfCorrected) = Digest(outcome, action.Verb);
        _pointsSpent += points;
        return new AutopilotActionRecord(action.Verb, status, points, selfCorrected);
    }

    private static (string Status, int PointsSpent, bool SelfCorrected) Digest(OrderOutcome outcome, SystemActionKind verb)
    {
        foreach (ReplayStep step in outcome.Trace.Steps)
        {
            if (step.Verb == verb.ToString())
            {
                return (step.Status, step.Attempts.Sum(attempt => attempt.PointsSpent), step.Attempts.Count > 1);
            }
        }

        // No executed step (atomic precheck denial / clarification / refusal):
        // the outcome kind IS the honest status, nothing was billed.
        return (outcome.Kind.ToString(), 0, false);
    }
}
