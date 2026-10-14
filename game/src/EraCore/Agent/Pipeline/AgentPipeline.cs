using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Contracts.Journal;
using EraCore.Sim;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 五段管线薄版编排（F-065/F-066/F-169）：
// 军令文本 → L1IntentParser（意图）→ IntentRouter（路由）→ PipelinePlanner
// （多步草案：动作 JSON+系统点+资源+风险红标）→ ApprovalGate（T-050：
// allow 直过 / ask 产方案卡挂起等玩家 / deny 拒）→ PipelineExecutor
// （注册表执行；动作层失败自纠 ≤2 次带错误重入）→ AgentReporter（大白话
// 汇报：做了什么/花了多少/下一步建议）。全程 K-8 JSONL 落盘（F-069）。
// 意图层失败=澄清卡+快捷轮盘（T-052 话术）；预算/资源不足=整案拒绝。

/// <summary>Terminal outcome class of one handled order.</summary>
public enum PipelineOutcomeKind
{
    /// <summary>The order ran its plan and was reported (failures included, honestly).</summary>
    Completed,

    /// <summary>Parse/route failure — clarification card plus the quick wheel.</summary>
    Clarification,

    /// <summary>Gate said ask: the plan card is out, the run waits for Confirm/Refuse.</summary>
    AwaitingApproval,

    /// <summary>Refused before execution (gate deny or atomic pre-check failure).</summary>
    Denied,

    /// <summary>The player refused the plan card.</summary>
    ApprovalRefused,

    /// <summary>Router rejected the intent (outside the delivered subset).</summary>
    Refused,
}

/// <summary>Outcome of one handled order (or of Confirm/Refuse).</summary>
/// <param name="Report">The final AgentReport (null while awaiting approval / on clarification cards).</param>
/// <param name="PendingCard">The plan card when <see cref="PipelineOutcomeKind.AwaitingApproval"/>.</param>
/// <param name="PendingId">Pending id to Confirm/Refuse (-1 when none).</param>
/// <param name="ClarifyingKey">T-052 clarifying key on clarification/refusal.</param>
/// <param name="QuickWheel">Quick-wheel options shipped with a clarification card.</param>
/// <param name="Trace">Live decision trace — the JSONL replayer must rebuild exactly this.</param>
public sealed record OrderOutcome(
    PipelineOutcomeKind Kind,
    int Order,
    string OrderText,
    AgentReport? Report,
    PlanCard? PendingCard,
    long PendingId,
    string? ClarifyingKey,
    IReadOnlyList<string> QuickWheel,
    DecisionTrace Trace);

/// <summary>
/// The five-stage pipeline orchestrator (T-053, C-13 / F-065; spike④ closed
/// loop F-169: 军令→LookAround→BuildPlan→汇报). Deterministic end to end:
/// every randomizable input lives in the simulation (seeded) and every
/// pipeline decision is a pure function of the order text and the sim state
/// — same seed + same order sequence ⇒ byte-identical JSONL (F-069).
/// </summary>
public sealed class AgentPipeline
{
    /// <summary>Maximum self-correction retries per step (C-13: 自纠 ≤2 次).</summary>
    public const int MaxSelfCorrections = 2;

    private readonly IIntentParser _parser;
    private readonly IIntentRouter _router;
    private readonly PipelinePlanner _planner;
    private readonly ApprovalGate _gate;
    private readonly SystemPointLedger _ledger;
    private readonly PipelineExecutor _executor;
    private readonly ActionRegistry _registry;
    private readonly AgentReporter _reporter;
    private readonly IJournalWriter _journal;
    private readonly IActionWorldView _view;
    private readonly Func<long> _tick;
    private readonly IReadOnlyList<string> _quickWheel;

    private readonly Dictionary<long, PendingOrder> _pending = new();
    private int _orderSeq;

    /// <param name="parser">K-4 parse stage (L1 grammar / L2 / L3).</param>
    /// <param name="router">K-5 stage-2 router.</param>
    /// <param name="planner">K-5 stage-3 planner (goal templates).</param>
    /// <param name="gate">K-5 stage-4 approval gate (T-050).</param>
    /// <param name="ledger">C-14 budget chokepoint (T-050) — the ONLY point charger.</param>
    /// <param name="executor">Stage-5 execution lane (registry + SimCommand placement).</param>
    /// <param name="registry">C-03 action registry (sole lawful action door).</param>
    /// <param name="reporter">Stage-5 reporter.</param>
    /// <param name="journal">K-8 sink — MUST be sequence-ordered per run (see <see cref="SequencingJournalWriter"/>).</param>
    /// <param name="view">Read-only world view (atomic pre-check + report figures).</param>
    /// <param name="tick">Current simulation tick for journal events.</param>
    /// <param name="quickWheel">Quick-wheel options for clarification cards.</param>
    public AgentPipeline(
        IIntentParser parser,
        IIntentRouter router,
        PipelinePlanner planner,
        ApprovalGate gate,
        SystemPointLedger ledger,
        PipelineExecutor executor,
        ActionRegistry registry,
        AgentReporter reporter,
        IJournalWriter journal,
        IActionWorldView view,
        Func<long> tick,
        IReadOnlyList<string>? quickWheel = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(tick);
        _parser = parser;
        _router = router;
        _planner = planner;
        _gate = gate;
        _ledger = ledger;
        _executor = executor;
        _registry = registry;
        _reporter = reporter;
        _journal = journal;
        _view = view;
        _tick = tick;
        _quickWheel = quickWheel ?? L1IntentParser.SupportedIntents.Select(kind => kind.ToString()).ToArray();
    }

    /// <summary>The budget chokepoint (host refills per wave via BeginWave).</summary>
    public SystemPointLedger Ledger => _ledger;

    /// <summary>The approval gate (host persists the level whitelist via Export/RestoreSession).</summary>
    public ApprovalGate Gate => _gate;

    /// <summary>The registry's governed agent state (T-068: directives, tasks, scroll bookkeeping).</summary>
    public EraCore.Agent.Actions.AgentActionState State => _registry.State;

    /// <summary>Composition root over one live simulation (the spike④ wiring; full 19-verb coverage since T-068).</summary>
    /// <param name="simulation">The live C-12 simulation (mutations enter via SimCommand).</param>
    /// <param name="config">The config that simulation was built with.</param>
    /// <param name="parser">Intent parse stage (L1 grammar).</param>
    /// <param name="eraIndex">Agent-era ordinal 1..6 (E1 keeps construction verbs locked).</param>
    /// <param name="journal">K-8 sink for the run's JSONL war report.</param>
    /// <param name="logSink">Registry diagnostics sink.</param>
    /// <param name="regionAnchors">Region id (lexicon) → anchor cell for build placement.</param>
    /// <param name="defaultTowerId">Tower the HoldRegion template builds; null = first weapon tower in the catalog.</param>
    /// <param name="armedScrolls">Equipped scroll ids (G.2); null = none armed (TriggerScroll refuses honestly).</param>
    public static AgentPipeline OverSimulation(
        Simulation simulation,
        SimulationConfig config,
        IIntentParser parser,
        int eraIndex,
        IJournalWriter journal,
        ILogSink logSink,
        IReadOnlyDictionary<string, CellCoord> regionAnchors,
        string? defaultTowerId = null,
        IReadOnlySet<string>? armedScrolls = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(logSink);
        ArgumentNullException.ThrowIfNull(regionAnchors);

        var view = new SimulationActionView(simulation, config);
        var sequencer = new SequencingJournalWriter(journal);
        var clock = new SimTickClock(() => view.Tick);
        EraCore.Units.HeroOrderAction? heroOrder = simulation.Hero is null
            ? null
            : new EraCore.Units.HeroOrderAction(
                () => simulation.Hero,
                () => config.EntryCells is { Count: > 0 } ? config.EntryCells[0] : config.BaseCell,
                () => simulation.Units,
                Simulation.CellSizePx)
            {
                TickSource = () => simulation.CurrentTick,
            };
        var registry = new ActionRegistry(
            logSink,
            view,
            eraOrdinalProvider: () => eraIndex,
            heroOrder: heroOrder,
            towerEstate: simulation.Estate,
            armedScrolls: armedScrolls);
        return new AgentPipeline(
            parser,
            new IntentRouter(),
            new PipelinePlanner(view, regionAnchors, config.BaseCell, defaultTowerId ?? FirstWeaponTowerId(config), () => eraIndex),
            new ApprovalGate(eraIndex),
            new SystemPointLedger(eraIndex, sequencer, clock),
            new PipelineExecutor(
                registry,
                simulation,
                view,
                heroAdvanceCell: () => config.EntryCells is { Count: > 0 } ? config.EntryCells[0] : config.BaseCell),
            registry,
            new AgentReporter(),
            sequencer,
            view,
            () => view.Tick);
    }

    /// <summary>First catalog tower with a weapon cycle (HoldRegion default build target).</summary>
    public static string FirstWeaponTowerId(SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TowerCatalog is not null)
        {
            foreach (var spec in config.TowerCatalog)
            {
                if (spec.ReloadTicks is > 0)
                {
                    return spec.Id;
                }
            }
        }

        throw new ArgumentException("Tower catalog has no weapon tower to default the HoldRegion template to.", nameof(config));
    }

    /// <summary>
    /// Handles one military order through all five stages. Ask-tier plans
    /// return <see cref="PipelineOutcomeKind.AwaitingApproval"/> with the
    /// plan card; <see cref="Confirm"/> then executes.
    /// </summary>
    public OrderOutcome Handle(string orderText)
    {
        ArgumentNullException.ThrowIfNull(orderText);
        int order = ++_orderSeq;
        var trace = new DecisionTraceBuilder(order, orderText);
        Emit(new PipelineInputPayload(PipelineJournalTypes.Input, order, orderText), JournalEventKind.Input, trace);

        IntentParseResult parsed = _parser.Parse(orderText);
        if (parsed.Failure is { } failure)
        {
            return ClarifyAnswer(trace, order, orderText, failure.ClarifyingKey, PipelineOutcomeKind.Clarification);
        }

        Intent intent = parsed.Intent!;
        IntentRoute route = _router.Route(intent);
        return route.Kind switch
        {
            RouteKind.Reject => ClarifyAnswer(
                trace, order, orderText, "agent.intent.unsupported", PipelineOutcomeKind.Refused),
            RouteKind.Clarify => ClarifyAnswer(
                trace, order, orderText, L1IntentParser.DefaultClarifyingKey, PipelineOutcomeKind.Clarification),
            RouteKind.AnswerDirectly or RouteKind.PlanAndApprove => PlanAndApprove(intent, route, trace, order),
            _ => ClarifyAnswer(trace, order, orderText, "agent.route.unknown", PipelineOutcomeKind.Refused),
        };
    }

    /// <summary>
    /// Host-driven single-action entry (T-068): runs ONE schema-shaped
    /// action JSON through the same five stages an order goes through
    /// (plan card → gate → [await/confirm] → execute → report). The C-22
    /// panel buttons, the T-081 全托管 lane and the five verbs without an
    /// SP-2 intent row (SetRally/SetStockpile/AskPlayer/TodoBoard/
    /// HeroOrder) enter here; the payload faces the SAME registry gates at
    /// execution — no bypass (RL6).
    /// </summary>
    /// <param name="verb">The closed verb of the payload (must match its discriminator).</param>
    /// <param name="actionJson">Schema-shaped action JSON.</param>
    public OrderOutcome HandleSystemAction(SystemActionKind verb, string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        int order = ++_orderSeq;
        var intent = new Contracts.Intents.Intent(
            IntentActionMap.AttributionIntent(verb), $"system:{verb}", new Dictionary<string, string>());
        var trace = new DecisionTraceBuilder(order, intent.RawText);
        Emit(new PipelineInputPayload(PipelineJournalTypes.Input, order, intent.RawText), JournalEventKind.Input, trace);
        return PlanAndApprove(
            intent,
            new IntentRoute(RouteKind.PlanAndApprove, "system-action"),
            trace,
            order,
            prepared: _planner.PrepareSingle(verb, actionJson));
    }

    /// <summary>Player confirmed a pending plan card — executes it and reports.</summary>
    /// <exception cref="ArgumentException">No pending plan with this id.</exception>
    public OrderOutcome Confirm(long pendingId)
    {
        PendingOrder pending = TakePending(pendingId);
        Emit(
            new PipelineApprovalPayload(PipelineJournalTypes.Approval, pending.Id, pending.Intent.RawText, "confirmed", Reason: null, PlanCostForcedAsk: false),
            JournalEventKind.Plan,
            pending.Trace);
        return RunApproved(pending.Intent, pending.Planned, pending.Trace, pending.Id);
    }

    /// <summary>Player refused a pending plan card — reports the refusal.</summary>
    /// <exception cref="ArgumentException">No pending plan with this id.</exception>
    public OrderOutcome Refuse(long pendingId)
    {
        PendingOrder pending = TakePending(pendingId);
        Emit(
            new PipelineApprovalPayload(PipelineJournalTypes.Approval, pending.Id, pending.Intent.RawText, "player-refused", Reason: null, PlanCostForcedAsk: false),
            JournalEventKind.Plan,
            pending.Trace);
        AgentReport report = ReportStage(
            pending.Intent, Array.Empty<StepDigest>(), pending.Trace, "approval_refused", "方案卡被拒绝，未执行任何步骤。");
        return new OrderOutcome(
            PipelineOutcomeKind.ApprovalRefused, pending.Id, pending.Intent.RawText, report,
            PendingCard: null, PendingId: -1, ClarifyingKey: null, QuickWheel: Array.Empty<string>(),
            pending.Trace.Snapshot());
    }

    private OrderOutcome PlanAndApprove(
        Intent intent,
        IntentRoute route,
        DecisionTraceBuilder trace,
        int order,
        PlannedOrder? prepared = null)
    {
        PlannedOrder planned = prepared ?? _planner.Prepare(intent);
        PlanCard card = planned.Card with { RouteKind = route.Kind.ToString() };
        Emit(
            new PipelinePlanPayload(
                PipelineJournalTypes.Plan, order, intent.RawText,
                intent.Kind.ToString(), route.Kind.ToString(),
                planned.Plan.Steps.Select(step => step.Action.ToString()).ToArray(),
                planned.Plan.Steps.Select(step => step.ActionJson).ToArray(),
                planned.Plan.Steps.Select(step => step.PointCost).ToArray(),
                card.Steps.Select(step => step.Resources).ToArray(),
                planned.Plan.EstimatedPointCost,
                planned.EstimatedResources,
                planned.Plan.Risks.ToArray()),
            JournalEventKind.Plan,
            trace);

        // 方案卡原子预检（预算+资源不足整案拒绝 — C-13）。
        string? precheck = AtomicPrecheckFailure(planned);
        ApprovalDecision decision;
        bool costForcedAsk = false;
        if (precheck is not null)
        {
            decision = new ApprovalDecision(ApprovalDecisionKind.Deny, $"atomic-precheck:{precheck}");
        }
        else
        {
            decision = _gate.Decide(planned.Plan);
            var costVerdict = PlanCostGate.Evaluate(
                new Dictionary<ResourceKey, double> { [_view.CostCurrency] = planned.EstimatedResources },
                new Dictionary<ResourceKey, double> { [_view.CostCurrency] = _view.ResourceAmount(_view.CostCurrency) });
            costForcedAsk = costVerdict.ForceAsk;
            decision = PlanCostGate.Compose(decision, costVerdict);
        }

        Emit(
            new PipelineApprovalPayload(
                PipelineJournalTypes.Approval, order, intent.RawText,
                decision.Kind.ToString().ToLowerInvariant(), decision.Reason, costForcedAsk),
            JournalEventKind.Plan,
            trace);

        if (decision.Kind == ApprovalDecisionKind.Deny)
        {
            string copy = decision.Reason is not null && decision.Reason.Contains("capability_locked", StringComparison.Ordinal)
                ? "这道令对应的本事还没解锁（时代未到），我先记下了。"
                : "这道令现在办不了（预检未过），资源和点数都没有动。";
            AgentReport report = ReportStage(intent, Array.Empty<StepDigest>(), trace, "denied", copy);
            return new OrderOutcome(
                PipelineOutcomeKind.Denied, order, intent.RawText, report,
                PendingCard: null, PendingId: -1, ClarifyingKey: null, QuickWheel: Array.Empty<string>(),
                trace.Snapshot());
        }

        if (decision.Kind == ApprovalDecisionKind.Ask)
        {
            _pending[order] = new PendingOrder(order, intent, planned, trace);
            return new OrderOutcome(
                PipelineOutcomeKind.AwaitingApproval, order, intent.RawText, Report: null,
                card, PendingId: order, ClarifyingKey: null, QuickWheel: Array.Empty<string>(),
                trace.Snapshot());
        }

        return RunApproved(intent, planned, trace, order);
    }

    private string? AtomicPrecheckFailure(PlannedOrder planned)
    {
        if (!_ledger.CanCoverPlan(planned.Plan.EstimatedPointCost))
        {
            return $"budget:{planned.Plan.EstimatedPointCost}pt";
        }

        double stock = _view.ResourceAmount(_view.CostCurrency);
        if (planned.EstimatedResources > stock)
        {
            return $"resources:need-{planned.EstimatedResources:0.#}-have-{stock:0.#}";
        }

        return null;
    }

    private OrderOutcome RunApproved(Intent intent, PlannedOrder planned, DecisionTraceBuilder trace, int order)
    {
        IReadOnlyList<StepDigest> digests = ExecuteSteps(planned.Plan, trace, order);
        AgentReport report = ReportStage(intent, digests, trace, "completed", reportText: string.Empty);
        return new OrderOutcome(
            PipelineOutcomeKind.Completed, order, intent.RawText, report,
            PendingCard: null, PendingId: -1, ClarifyingKey: null, QuickWheel: Array.Empty<string>(),
            trace.Snapshot());
    }

    /// <summary>
    /// Executes every step except the trailing report marker, with ≤
    /// <see cref="MaxSelfCorrections"/> self-corrections per step
    /// (错误重入，T-068 全动作统一): every verb shares the same retry loop —
    /// on failure the error feeds <see cref="PipelineExecutor.TryAdjust"/>
    /// which rebuilds the payload from live state (BuildPlan cell triage /
    /// SetTowerMode dead-tower triage / FocusFire retarget; every other
    /// verb has no useful fix and reports honestly on the first attempt).
    /// Charging order per step: budget pre-check → execute → charge on
    /// success only (failed actions never bill).
    /// </summary>
    private IReadOnlyList<StepDigest> ExecuteSteps(Plan plan, DecisionTraceBuilder trace, int order)
    {
        var digests = new List<StepDigest>();
        // The last step is the stage-5 report marker — it runs in ReportStage.
        for (int i = 0; i < plan.Steps.Count - 1; i++)
        {
            PlanStep step = plan.Steps[i];
            string actionJson = step.ActionJson;
            string? lastAdjustment = null;
            ActionResult result = new(ActionResultStatus.Err, "not-run");
            object? payload = null;
            int attempt = 0;
            int pointsSpent = 0;

            while (true)
            {
                attempt++;
                if (!_ledger.CanCoverPlan(step.PointCost))
                {
                    result = new ActionResult(
                        ActionResultStatus.ErrBudgetDenied, $"wave-budget:{step.Action}:{step.PointCost}pt");
                }
                else
                {
                    ActionOutcome outcome = _executor.InvokeStep(step.Action, actionJson);
                    result = outcome.Result;
                    payload = outcome.Payload;
                    if (result.Status == ActionResultStatus.Ok)
                    {
                        ActionResult spend = _ledger.TrySpend(step.Action);
                        pointsSpent = spend.PointsSpent; // 0 for free verbs; refusal here is an anomaly, never re-billed
                    }
                }

                Emit(
                    new PipelineExecutionPayload(
                        PipelineJournalTypes.Execution, order, plan.SourceIntent.RawText,
                        i, step.Action.ToString(), actionJson,
                        result.Status.ToString(), result.ErrorMessage, pointsSpent,
                        attempt, attempt > 1, attempt > 1 ? lastAdjustment : null),
                    JournalEventKind.Validation,
                    trace);

                if (result.Status == ActionResultStatus.Ok)
                {
                    break;
                }

                // Budget refusals never self-correct (adjusting the payload
                // cannot refill the wave cap) — report honestly instead.
                // Every other failure feeds the UNIFIED adjuster; a null
                // adjustment means no useful retry exists.
                if (result.Status != ActionResultStatus.ErrBudgetDenied
                    && attempt <= MaxSelfCorrections)
                {
                    string? adjusted = _executor.TryAdjust(step.Action, actionJson, result.ErrorMessage ?? string.Empty);
                    if (adjusted is null)
                    {
                        break; // no useful retry exists — report honestly
                    }

                    lastAdjustment = DescribeAdjustment(step.Action, actionJson, adjusted);
                    actionJson = adjusted;
                    continue;
                }

                break;
            }

            digests.Add(DigestOf(step, result, pointsSpent, payload, attempt > 1));
        }

        return digests;
    }

    private static string DescribeAdjustment(SystemActionKind verb, string before, string after)
    {
        switch (verb)
        {
            case SystemActionKind.BuildPlan:
                return $"adjusted-cells:{CountCells(before)}->{CountCells(after)}";
            case SystemActionKind.SetTowerMode:
                return $"dropped-tower-ids:{CountTowerIds(before)}->{CountTowerIds(after)}";
            case SystemActionKind.FocusFire:
                return $"retarget-first-living:{TargetIdOf(after)}";
            default:
                return "adjusted-payload";
        }
    }

    private static int CountTowerIds(string setTowerModeJson)
    {
        try
        {
            SetTowerModeActionJson? parsed = JsonSerializer.Deserialize(
                setTowerModeJson, PipelinePlannerJsonContext.Default.SetTowerModeActionJson);
            return parsed?.TargetIds.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static long TargetIdOf(string focusFireJson)
    {
        try
        {
            FocusFireActionJson? parsed = JsonSerializer.Deserialize(
                focusFireJson, PipelinePlannerJsonContext.Default.FocusFireActionJson);
            return parsed?.TargetId ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int CountCells(string buildPlanJson)
    {
        try
        {
            BuildPlanActionJson? parsed = JsonSerializer.Deserialize(
                buildPlanJson, PipelinePlannerJsonContext.Default.BuildPlanActionJson);
            return parsed?.Cells.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static StepDigest DigestOf(PlanStep step, ActionResult result, int pointsSpent, object? payload, bool selfCorrected)
    {
        string detail = result.Status == ActionResultStatus.Ok
            ? DetailOfSuccess(step.Action, payload, result)
            : string.Empty;
        double resources = result.Status == ActionResultStatus.Ok && payload is BuildPlanDraft draft
            ? draft.TotalCost
            : 0;
        return new StepDigest(
            step.Action.ToString(), result.Status.ToString(), result.ErrorMessage, pointsSpent, resources, detail, selfCorrected);
    }

    private static string DetailOfSuccess(SystemActionKind verb, object? payload, ActionResult result)
    {
        switch (verb)
        {
            case SystemActionKind.LookAround:
                return payload is LookAroundSummary summary
                    ? $"现存敌 {summary.EnemiesPresent}、资源 {summary.Resources.Count} 类"
                    : "战局已侦察";
            case SystemActionKind.BuildPlan:
                if (payload is BuildPlanDraft draft)
                {
                    string cells = string.Join(
                        ",", draft.Steps.Where(s => s.Buildable).Select(s => $"({s.Cell.X},{s.Cell.Y})"));
                    return $"{draft.Steps.Count(s => s.Buildable)} 座已就位 {cells}";
                }

                goto default;
            case SystemActionKind.InspectTile:
                return payload is InspectTileReport tile
                    ? $"({tile.Cell.X},{tile.Cell.Y}) {tile.Aspect} 已查验"
                    : "格子已查验";
            case SystemActionKind.QueryWorld:
                return payload is QueryWorldReport query
                    ? $"{query.Key} 查得 {query.Lines.Count} 行"
                    : "世界已查询";
            case SystemActionKind.SetRally:
                return payload is SetRallyAck rally
                    ? $"{rally.Lane} 集结姿态={rally.Stance}"
                    : "集结点已设";
            case SystemActionKind.SetStockpile:
                return payload is SetStockpileAck stock
                    ? $"{stock.ItemId} 储备下限={stock.MinAmount:0.#}"
                    : "储备下限已设";
            case SystemActionKind.FocusFire:
                return payload is FocusFireAck fire
                    ? $"集火目标 {fire.TargetId}（{fire.ContentId}）"
                    : "集火已下达";
            case SystemActionKind.SetTowerMode:
                return payload is SetTowerModeAck mode
                    ? $"{mode.TargetIds.Count} 座塔模式={mode.Mode}"
                    : "塔模式已设";
            case SystemActionKind.TriggerScroll:
                return payload is TriggerScrollAck scroll
                    ? $"{scroll.ScrollId} 已触发（冷却至 tick {scroll.ReadyAgainTick}）"
                    : "卷轴已触发";
            case SystemActionKind.RerouteLogistics:
                return payload is RerouteLogisticsPlan reroute
                    ? $"{reroute.Item} 改道 {reroute.From}→{reroute.To}（份额 {reroute.Rate:0.#}）"
                    : "物流已改道";
            case SystemActionKind.AskPlayer:
                return payload is AskPlayerCard card
                    ? $"提问已递出（{card.Options.Count} 个选项）"
                    : "提问已递出";
            case SystemActionKind.TodoBoard:
                return payload is TodoBoardSnapshot todos
                    ? $"任务板已重写（{todos.Todos.Count} 项）"
                    : "任务板已重写";
            case SystemActionKind.SetAlarm:
                return payload is SetAlarmAck alarm
                    ? $"闹钟 {alarm.TaskId} 定于 tick {alarm.DueTick}"
                    : "闹钟已设";
            case SystemActionKind.SpawnScout:
                return payload is SpawnScoutAck scout
                    ? $"巡逻 {scout.TaskId} 于 {scout.Lane} 至 tick {scout.UntilTick}"
                    : "巡逻已派出";
            case SystemActionKind.StopTask:
                return payload is StopTaskAck stop
                    ? $"已停止 {stop.StoppedIds.Count} 项：{string.Join(",", stop.StoppedIds)}"
                    : "任务已停止";
            default:
                return verb == SystemActionKind.ReportToPlayer ? "汇报已递出" : $"{verb} 完成";
        }
    }

    /// <summary>Stage 5: composes the report and ships it through the registry's ReportToPlayer door.</summary>
    private AgentReport ReportStage(
        Intent intent, IReadOnlyList<StepDigest> steps, DecisionTraceBuilder trace, string outcome, string reportText)
    {
        AgentReport composed = _reporter.Compose(intent, outcome, steps, reportText);
        var wire = new ReportActionJson(
            nameof(SystemActionKind.ReportToPlayer),
            composed.Summary,
            composed.Findings.Select(f => f.Text).ToArray(),
            Verdict: nameof(FindingVerdict.Confirmed).ToUpperInvariant());
        ActionOutcome shipped = _executor.InvokeStep(
            SystemActionKind.ReportToPlayer,
            JsonSerializer.Serialize(wire, PipelinePlannerJsonContext.Default.ReportActionJson));
        AgentReport report = shipped.Payload as AgentReport ?? composed;
        Emit(
            new PipelineReportPayload(
                PipelineJournalTypes.Report, trace.Snapshot().Order, intent.RawText, outcome,
                report.Summary, report.Findings.Select(f => f.Text).ToArray(),
                nameof(FindingVerdict.Confirmed).ToUpperInvariant()),
            JournalEventKind.Report,
            trace);
        _reporter.Report(intent, report);
        return report;
    }

    private OrderOutcome ClarifyAnswer(
        DecisionTraceBuilder trace, int order, string orderText, string clarifyingKey, PipelineOutcomeKind kind)
    {
        Emit(
            new PipelineClarifyPayload(PipelineJournalTypes.Clarify, order, orderText, clarifyingKey, _quickWheel.ToArray()),
            JournalEventKind.Report,
            trace);
        return new OrderOutcome(
            kind, order, orderText, Report: null, PendingCard: null, PendingId: -1,
            clarifyingKey, _quickWheel, trace.Snapshot());
    }

    private PendingOrder TakePending(long pendingId)
    {
        if (!_pending.TryGetValue(pendingId, out PendingOrder? pending))
        {
            throw new ArgumentException($"No pending plan card with id {pendingId}.", nameof(pendingId));
        }

        _pending.Remove(pendingId);
        return pending;
    }

    private void Emit(object payload, JournalEventKind kind, DecisionTraceBuilder trace)
    {
        string json = payload switch
        {
            PipelineInputPayload input => JsonSerializer.Serialize(input, PipelineJsonContext.Default.PipelineInputPayload),
            PipelinePlanPayload plan => JsonSerializer.Serialize(plan, PipelineJsonContext.Default.PipelinePlanPayload),
            PipelineApprovalPayload approval => JsonSerializer.Serialize(approval, PipelineJsonContext.Default.PipelineApprovalPayload),
            PipelineExecutionPayload execution => JsonSerializer.Serialize(execution, PipelineJsonContext.Default.PipelineExecutionPayload),
            PipelineReportPayload report => JsonSerializer.Serialize(report, PipelineJsonContext.Default.PipelineReportPayload),
            PipelineClarifyPayload clarify => JsonSerializer.Serialize(clarify, PipelineJsonContext.Default.PipelineClarifyPayload),
            _ => throw new ArgumentException($"Unknown pipeline payload {payload.GetType().Name}.", nameof(payload)),
        };

        _journal.Append(new JournalEvent(kind, _tick(), 0, json));
        trace.Apply(payload);
    }

    private sealed record PendingOrder(int Id, Intent Intent, PlannedOrder Planned, DecisionTraceBuilder Trace);
}

/// <summary>
/// K-1 clock adapter over the simulation tick (CS-1.4: no wall clock in
/// EraCore): <see cref="IClock.Tick"/> mirrors the sim domain for journal
/// stamps; <see cref="IClock.NowMs"/> reports 0 — deterministic diagnostics
/// only, the pipeline never branches on it.
/// </summary>
public sealed class SimTickClock(Func<long> tick) : IClock
{
    private readonly Func<long> _tick = tick ?? throw new ArgumentNullException(nameof(tick));

    /// <inheritdoc />
    public long Tick => _tick();

    /// <inheritdoc />
    public long NowMs => 0;
}
