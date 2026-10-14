using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Units;

namespace EraCore.Agent.Actions;

/// <summary>
/// One registry invocation: the typed <see cref="ActionResult"/> plus the
/// structured payload the handler produced (C-03 implementation detail —
/// K-3's result record stays payload-free; the C-13 pipeline consumes the
/// payload through this outcome type, T-053).
/// </summary>
/// <param name="Result">Outcome; anything but Ok is a failure.</param>
/// <param name="Payload">Handler payload (LookAroundSummary / BuildPlanDraft / AgentReport / the T-068 ack records); null on failure.</param>
public sealed record ActionOutcome(ActionResult Result, object? Payload);

/// <summary>
/// The C-03 action registry (T-051): the single lawful door every system
/// action passes through (RL6 — closed 19-verb list, closed-schema
/// validation, unlock-era gate, then the registered handler). What T-051
/// delivered, T-065/T-066/T-068 extended:
/// <list type="bullet">
/// <item>T-068 (C-13 full pipeline) made ALL 19 verbs real: the recon pair
/// InspectTile/QueryWorld reads the <see cref="IActionWorldState"/> roster
/// projection; the directives (SetRally/SetStockpile/FocusFire/
/// SetTowerMode/TriggerScroll), the management verbs (AskPlayer/TodoBoard/
/// SetAlarm/SpawnScout/StopTask) and RerouteLogistics run over the governed
/// <see cref="AgentActionState"/> — every handler either executes against
/// real state or returns a typed refusal, never a fake success;</item>
/// <item>the T-065 estate pair (UpgradeTower/SellTower) stays live only
/// while a <see cref="TowerEstateOps"/> is wired into the constructor —
/// an unwired estate keeps the honest not-implemented refusal tier;</item>
/// <item>out-of-list refusals with a structured, secret-scrubbed ILogSink
/// record (F-062/F-070 acceptance: 清单外动作拒绝并记日志);</item>
/// <item>the unlock-era gate as data (SpawnScout in E1 →
/// ErrCapabilityLocked with clarifying copy, N9/A2);</item>
/// <item>T-070's PreAction hook chain (rewrite/intercept between the era
/// gate and dispatch, F-075) and the TriggerScroll 3 s cooldown gate
/// (DEBT-3) run before dispatch.</item>
/// </list>
/// Dispatch is single-sourced in ONE arm table built at construction
/// (<see cref="_arms"/> — the Shotgun-Surgery debt T-070 registered shrinks
/// to "register the arm"): <see cref="HasLiveHandler"/>, the
/// not-implemented tier and the per-verb dispatch all read the same table,
/// so a verb cannot be marked implemented without its arm.
/// <para>
/// Point charging stays with the C-14 ledger (T-050): prices live in
/// <see cref="ActionCatalog"/> and successful outcomes report
/// PointsSpent=0 until the pipeline's ledger charges the step.
/// </para>
/// </summary>
public sealed class ActionRegistry : IActionExecutor
{
    private readonly ILogSink _logSink;
    private readonly IActionWorldView? _worldView;
    private readonly JsonSchemaValidator _validator;
    private readonly Func<int> _eraOrdinal;
    private readonly PreActionHookChain _preActionHooks;
    private readonly ScrollTriggerCooldown _scrollCooldown = new();
    private readonly LookAroundAction _lookAround = new();
    private readonly BuildPlanAction _buildPlan = new();
    private readonly ReportToPlayerAction _reportToPlayer = new();
    private readonly HeroOrderAction? _heroOrder;
    private readonly TowerEstateOps? _towerEstate;
    private readonly UpgradeTowerAction? _upgradeTower;
    private readonly SellTowerAction? _sellTower;
    private readonly InspectTileAction _inspectTile = new();
    private readonly QueryWorldAction _queryWorld = new();
    private readonly SetRallyAction _setRally = new();
    private readonly SetStockpileAction _setStockpile = new();
    private readonly FocusFireAction _focusFire = new();
    private readonly SetTowerModeAction _setTowerMode = new();
    private readonly TriggerScrollAction _triggerScroll = new();
    private readonly RerouteLogisticsAction _rerouteLogistics = new();
    private readonly AskPlayerAction _askPlayer = new();
    private readonly TodoBoardAction _todoBoard = new();
    private readonly SetAlarmAction _setAlarm = new();
    private readonly SpawnScoutAction _spawnScout = new();
    private readonly StopTaskAction _stopTask = new();

    /// <summary>The single dispatch table: every live verb's arm (built once, read everywhere).</summary>
    private readonly Dictionary<SystemActionKind, Func<string, ActionOutcome>> _arms;

    /// <summary>The agent-owned governed state the T-068 verbs run over (directives, tasks, scroll bookkeeping).</summary>
    public AgentActionState State { get; }

    /// <param name="logSink">Diagnostic sink for every registry verdict (structured, secret-scrubbed).</param>
    /// <param name="worldView">World view the stateful handlers read; null makes the view-bound verbs refuse with Err until wired.</param>
    /// <param name="validator">Schema validator; when null a default one is built on <paramref name="eraOrdinalProvider"/>.</param>
    /// <param name="eraOrdinalProvider">Current era ordinal (E1=1 …); defaults to E1.</param>
    /// <param name="heroOrder">
    /// HeroOrder handler (T-066); null keeps the verb in the
    /// explicit-refusal tier — the hero feature is optional (F-039).
    /// </param>
    /// <param name="towerEstate">
    /// Combat tower estate (T-065): when provided, UpgradeTower/SellTower
    /// dispatch to their real combat handlers; null keeps both verbs in the
    /// not-implemented tier — an unwired estate must refuse, never guess.
    /// </param>
    /// <param name="preActionHooks">
    /// PreAction hook chain (T-070, F-075): registered hooks may rewrite
    /// the action JSON or block it (ErrHookBlocked) between the era gate
    /// and dispatch. Null installs the empty chain — zero behavior drift;
    /// the pipeline composition registers the built-ins it wants
    /// (BuildPlacementConflictHook/MemorialProtectionHook).
    /// </param>
    /// <param name="agentState">
    /// Agent-owned governed state for the T-068 verbs (directives, tasks,
    /// scroll bookkeeping); null creates a fresh one clocked off the world
    /// view (tick 0 without a view).
    /// </param>
    /// <param name="armedScrolls">Equipped scroll ids (G.2 限已装备卷轴); null = none armed.</param>
    public ActionRegistry(
        ILogSink logSink,
        IActionWorldView? worldView = null,
        JsonSchemaValidator? validator = null,
        Func<int>? eraOrdinalProvider = null,
        HeroOrderAction? heroOrder = null,
        TowerEstateOps? towerEstate = null,
        IReadOnlyList<IPreActionHook>? preActionHooks = null,
        AgentActionState? agentState = null,
        IReadOnlySet<string>? armedScrolls = null)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        _logSink = logSink;
        _worldView = worldView;
        _eraOrdinal = eraOrdinalProvider ?? (static () => EraOrdinals.E1);
        _validator = validator ?? new JsonSchemaValidator(eraOrdinalProvider);
        _heroOrder = heroOrder;
        _towerEstate = towerEstate;
        _preActionHooks = preActionHooks is null ? PreActionHookChain.Empty : new PreActionHookChain(preActionHooks);
        State = agentState ?? new AgentActionState(() => _worldView?.Tick ?? 0, armedScrolls);
        if (towerEstate is not null)
        {
            _upgradeTower = new UpgradeTowerAction(towerEstate);
            _sellTower = new SellTowerAction(towerEstate);
        }

        _arms = BuildArms();
    }

    /// <summary>The closed verb list the registry accepts (all 19, K-3).</summary>
    public static IReadOnlyList<SystemActionKind> RegisteredKinds => ActionCatalog.All.Select(d => d.Kind).ToList();

    /// <summary>
    /// The single lawful entry (RL6): validates, gates and dispatches one
    /// action JSON. Unknown verbs are refused with ErrSchemaDenied and a
    /// structured log record that never contains credential material.
    /// Order: schema-declared clamps (T-070 DEBT-4) → closed-schema gate →
    /// unlock-era gate → PreAction hooks (rewrite/intercept, F-075) →
    /// TriggerScroll cooldown (T-070 DEBT-3) → dispatch.
    /// </summary>
    public ActionOutcome Invoke(string actionJson)
    {
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return Deny(null, "payload-empty", actionJson);
        }

        // DEBT-4 (T-070): schema-declared clamps (SetAlarm.after_seconds)
        // rewrite BEFORE the closed-schema gate — 越界钳到边界而非拒绝
        // (SP-2 #10 ruling, G.2 钳制 wording).
        actionJson = ActionSchemaClamper.Clamp(actionJson);

        // Closed-schema gate (RL6): unknown verb OR any field violation.
        SchemaValidationResult validation = _validator.Validate(actionJson);
        if (!validation.IsValid)
        {
            string? verb = JsonSchemaValidator.TryReadVerb(actionJson, out string? read) ? read : null;
            string reasons = string.Join(
                "; ", validation.Errors.Select(e => $"{e.Pointer}:{e.Message}").Take(MaxLoggedErrors));
            return Deny(verb, "schema:" + reasons, actionJson);
        }

        // Verb is known and in-list past this point.
        _ = JsonSchemaValidator.TryReadVerb(actionJson, out string? verbName);
        if (!ActionCatalog.TryResolveName(verbName, out SystemActionKind kind))
        {
            throw new InvalidOperationException("Validated verb failed to resolve; table drift.");
        }

        ActionDescriptor descriptor = ActionCatalog.Descriptor(kind);

        // Unlock-era gate (N9/A2): refused with clarifying copy, never dropped.
        int era = _eraOrdinal();
        if (descriptor.UnlockEra > era)
        {
            _logSink.Write(
                LogLevel.Info, LogTick(), ActionLog.EraLockedLine(descriptor.Verb, descriptor.UnlockEra, era));
            return new ActionOutcome(new ActionResult(
                ActionResultStatus.ErrCapabilityLocked,
                $"capability-locked:{descriptor.Verb}:unlocks-at-era:{descriptor.UnlockEra}"
                + " (answer the player with clarifying copy — N9/A2; the call is not silently dropped)"), null);
        }

        // PreAction hook chain (F-075, T-070): rewrite/intercept before
        // dispatch. A rewrite still faces the closed schema — a hook can
        // never smuggle a field past RL6; a block surfaces as
        // Err + ErrHookBlocked:<hook>:<reason> (the K-3 status set stays
        // frozen; the structured reason carries the typed marker).
        PreActionChainResult hooks = _preActionHooks.Apply(kind, actionJson, LogTick(), _worldView);
        if (hooks.Blocked)
        {
            string reason = $"ErrHookBlocked:{hooks.BlockedBy}:{hooks.BlockReason}";
            _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(descriptor.Verb, reason, actionJson));
            return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
        }

        if (!ReferenceEquals(hooks.ActionJson, actionJson))
        {
            SchemaValidationResult revalidation = _validator.Validate(hooks.ActionJson);
            if (!revalidation.IsValid)
            {
                string reasons = string.Join(
                    "; ", revalidation.Errors.Select(e => $"{e.Pointer}:{e.Message}").Take(MaxLoggedErrors));
                return Deny(descriptor.Verb, "hook-rewrite-schema:" + reasons, hooks.ActionJson);
            }

            actionJson = hooks.ActionJson;
            _logSink.Write(
                LogLevel.Info, LogTick(), ActionLog.HookRewriteLine(descriptor.Verb, string.Join("; ", hooks.Notes)));
        }

        // DEBT-3 (T-070): TriggerScroll 3 s cooldown (90 ticks @ 30 Hz,
        // §11.4/F.1) — held in this executor; blocked before dispatch and
        // consumed only by a successful trigger (failed calls never bill).
        if (descriptor.Kind is SystemActionKind.TriggerScroll
            && _scrollCooldown.OnCooldown(LogTick(), out int remainingTicks))
        {
            string reason = $"scroll-cooldown:{descriptor.Verb}:remaining-ticks:{remainingTicks}:window-{ScrollTriggerCooldown.CooldownTicks}";
            _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(descriptor.Verb, reason, actionJson));
            return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
        }

        ActionOutcome outcome = Dispatch(descriptor, actionJson, era);
        if (outcome.Result.Status == ActionResultStatus.Ok
            && descriptor.Kind is SystemActionKind.TriggerScroll)
        {
            _scrollCooldown.Accept(LogTick());
        }

        return outcome;
    }

    /// <summary>
    /// Handler dispatch after every gate passed. The arm table is the
    /// single source of live verbs: a kind without an arm falls to the
    /// honest not-implemented tier (the T-065 estate pair without a wired
    /// estate — every other verb has had a real handler since T-068).
    /// </summary>
    private ActionOutcome Dispatch(ActionDescriptor descriptor, string actionJson, int era)
    {
        if (!_arms.TryGetValue(descriptor.Kind, out Func<string, ActionOutcome>? arm))
        {
            string reason = IsEstateVerb(descriptor.Kind)
                ? $"not-implemented:{descriptor.Verb}:combat tower-estate not wired (T-065 handlers run once a TowerEstateOps is provided); refusing rather than reporting success"
                : $"not-implemented:{descriptor.Verb}:handler not registered; refusing rather than reporting success";
            _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.NotImplementedLine(descriptor.Verb, reason));
            return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
        }

        ActionOutcome outcome = arm(actionJson);
        if (outcome.Result.Status == ActionResultStatus.Ok)
        {
            LogOk(descriptor.Verb, era);
        }

        return outcome;
    }

    /// <summary>
    /// K-5 pipeline entry (stage 5): same single path as
    /// <see cref="Invoke"/>; the typed kind must match the payload's verb
    /// (a mismatched pair is a schema denial, not a different action).
    /// </summary>
    public ActionResult Execute(SystemActionKind kind, string actionJson)
    {
        if (!JsonSchemaValidator.TryReadVerb(actionJson, out string? verb)
            || !ActionCatalog.TryResolveName(verb, out SystemActionKind parsed)
            || parsed != kind)
        {
            return Deny(verb, "verb-kind-mismatch", actionJson).Result;
        }

        return Invoke(actionJson).Result;
    }

    /// <summary>
    /// Validation-only entry for execution lanes that mutate through another
    /// lawful door (the K-6 SimCommand lane the pipeline executor uses for
    /// upgrade-tower/sell-tower/hero-order/assign-carrier —
    /// 03-decomposition §1.2): runs the SAME closed-schema gate plus the
    /// unlock-era gate, executes nothing, charges nothing. Unknown or
    /// malformed payloads deny; valid unlocked ones return Ok.
    /// </summary>
    public ActionResult ValidateOnly(string actionJson)
    {
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return Deny(null, "payload-empty", actionJson).Result;
        }

        actionJson = ActionSchemaClamper.Clamp(actionJson);
        SchemaValidationResult validation = _validator.Validate(actionJson);
        if (!validation.IsValid)
        {
            string? verb = JsonSchemaValidator.TryReadVerb(actionJson, out string? read) ? read : null;
            string reasons = string.Join(
                "; ", validation.Errors.Select(e => $"{e.Pointer}:{e.Message}").Take(MaxLoggedErrors));
            return Deny(verb, "schema:" + reasons, actionJson).Result;
        }

        _ = JsonSchemaValidator.TryReadVerb(actionJson, out string? verbName);
        if (!ActionCatalog.TryResolveName(verbName, out SystemActionKind kind))
        {
            throw new InvalidOperationException("Validated verb failed to resolve; table drift.");
        }

        ActionDescriptor descriptor = ActionCatalog.Descriptor(kind);
        int era = _eraOrdinal();
        if (descriptor.UnlockEra > era)
        {
            _logSink.Write(
                LogLevel.Info, LogTick(), ActionLog.EraLockedLine(descriptor.Verb, descriptor.UnlockEra, era));
            return new ActionResult(
                ActionResultStatus.ErrCapabilityLocked,
                $"capability-locked:{descriptor.Verb}:unlocks-at-era:{descriptor.UnlockEra}");
        }

        return new ActionResult(ActionResultStatus.Ok);
    }

    private const int MaxLoggedErrors = 3;

    /// <summary>
    /// Whether one verb has a live handler under THIS registry instance:
    /// exactly the arms the constructor registered (every non-estate verb
    /// since T-068, plus the estate pair while a TowerEstateOps is wired).
    /// </summary>
    public bool HasLiveHandler(SystemActionKind kind) => _arms.ContainsKey(kind);

    private static bool IsEstateVerb(SystemActionKind kind) =>
        kind is SystemActionKind.UpgradeTower or SystemActionKind.SellTower;

    /// <summary>Builds the single dispatch table (one arm per live verb).</summary>
    private Dictionary<SystemActionKind, Func<string, ActionOutcome>> BuildArms()
    {
        var arms = new Dictionary<SystemActionKind, Func<string, ActionOutcome>>
        {
            [SystemActionKind.LookAround] = json =>
            {
                if (_worldView is null)
                {
                    return NoViewRefusal(nameof(SystemActionKind.LookAround));
                }

                return new ActionOutcome(
                    new ActionResult(ActionResultStatus.Ok), _lookAround.Run(_worldView));
            },
            [SystemActionKind.BuildPlan] = json =>
            {
                if (_worldView is null)
                {
                    return NoViewRefusal(nameof(SystemActionKind.BuildPlan));
                }

                BuildPlanArguments arguments = ParseBuildPlanArguments(json);
                BuildPlanDraft? draft = _buildPlan.Draft(_worldView, arguments.TowerId, arguments.Cells, arguments.ConfirmOverwrite);
                return draft is null
                    ? Refuse(
                        nameof(SystemActionKind.BuildPlan),
                        ActionResultStatus.ErrTargetInvalid,
                        $"target-invalid:tower-id '{arguments.TowerId}' is unknown to the level catalog (or locked); no draft was produced",
                        json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), draft);
            },
            [SystemActionKind.ReportToPlayer] = json =>
            {
                ReportArguments arguments = ParseReportArguments(json);
                return new ActionOutcome(
                    new ActionResult(ActionResultStatus.Ok),
                    _reportToPlayer.Format(arguments.Summary, arguments.Findings, arguments.Verdict));
            },
            [SystemActionKind.InspectTile] = json =>
            {
                if (WorldState(nameof(SystemActionKind.InspectTile)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.InspectTile));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return !HandlerArgumentReader.TryReadCell(root, "cell", out CellCoord cell)
                    || !HandlerArgumentReader.TryReadString(root, "aspect", out string aspect)
                    ? Refuse(
                        nameof(SystemActionKind.InspectTile), ActionResultStatus.Err,
                        "inspect-tile-arguments:cell/aspect unreadable after schema gate", json)
                    : RunInspectTile(state, cell, aspect, json);
            },
            [SystemActionKind.QueryWorld] = json =>
            {
                if (WorldState(nameof(SystemActionKind.QueryWorld)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.QueryWorld));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadString(root, "key", out string key)
                    || !HandlerArgumentReader.TryReadString(root, "scope", out string scope))
                {
                    return Refuse(
                        nameof(SystemActionKind.QueryWorld), ActionResultStatus.Err,
                        "query-world-arguments:key/scope unreadable after schema gate", json);
                }

                int? headLimit = root.TryGetProperty("head_limit", out JsonElement limit)
                    && limit.ValueKind == JsonValueKind.Number
                    && limit.TryGetInt32(out int parsedLimit)
                        ? parsedLimit
                        : null;
                return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), _queryWorld.Run(state, key, scope, headLimit));
            },
            [SystemActionKind.SetRally] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return !HandlerArgumentReader.TryReadString(root, "lane", out string lane)
                    || !HandlerArgumentReader.TryReadString(root, "stance", out string stance)
                    ? Refuse(
                        nameof(SystemActionKind.SetRally), ActionResultStatus.Err,
                        "set-rally-arguments:lane/stance unreadable after schema gate", json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), _setRally.Run(State, lane, stance));
            },
            [SystemActionKind.SetStockpile] = json =>
            {
                if (WorldState(nameof(SystemActionKind.SetStockpile)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.SetStockpile));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadString(root, "item_id", out string itemId)
                    || !HandlerArgumentReader.TryReadNumber(root, "min_amount", out double minAmount))
                {
                    return Refuse(
                        nameof(SystemActionKind.SetStockpile), ActionResultStatus.Err,
                        "set-stockpile-arguments:item_id/min_amount unreadable after schema gate", json);
                }

                SetStockpileAck? ack = _setStockpile.Run(State, state, itemId, minAmount, out string? refusal);
                return ack is null
                    ? Refuse(nameof(SystemActionKind.SetStockpile), ActionResultStatus.ErrTargetInvalid, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
            },
            [SystemActionKind.FocusFire] = json =>
            {
                if (WorldState(nameof(SystemActionKind.FocusFire)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.FocusFire));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadInt64(root, "target_id", out long targetId))
                {
                    return Refuse(
                        nameof(SystemActionKind.FocusFire), ActionResultStatus.Err,
                        "focus-fire-arguments:target_id unreadable after schema gate", json);
                }

                FocusFireAck? ack = _focusFire.Run(State, state, targetId, out string? refusal);
                return ack is null
                    ? Refuse(nameof(SystemActionKind.FocusFire), ActionResultStatus.ErrTargetInvalid, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
            },
            [SystemActionKind.SetTowerMode] = json =>
            {
                if (WorldState(nameof(SystemActionKind.SetTowerMode)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.SetTowerMode));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadInt64Array(root, "target_id", out IReadOnlyList<long> targetIds)
                    || !HandlerArgumentReader.TryReadString(root, "mode", out string mode))
                {
                    return Refuse(
                        nameof(SystemActionKind.SetTowerMode), ActionResultStatus.Err,
                        "set-tower-mode-arguments:target_id/mode unreadable after schema gate", json);
                }

                SetTowerModeAck? ack = _setTowerMode.Run(State, state, targetIds, mode, out string? refusal);
                return ack is null
                    ? Refuse(nameof(SystemActionKind.SetTowerMode), ActionResultStatus.ErrTargetInvalid, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
            },
            [SystemActionKind.TriggerScroll] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadString(root, "scroll_id", out string scrollId))
                {
                    return Refuse(
                        nameof(SystemActionKind.TriggerScroll), ActionResultStatus.Err,
                        "trigger-scroll-arguments:scroll_id unreadable after schema gate", json);
                }

                TriggerScrollAck? ack = _triggerScroll.Run(State, scrollId, out string? refusal);
                return ack is null
                    ? Refuse(nameof(SystemActionKind.TriggerScroll), ActionResultStatus.Err, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
            },
            [SystemActionKind.RerouteLogistics] = json =>
            {
                if (WorldState(nameof(SystemActionKind.RerouteLogistics)) is not { } state)
                {
                    return NoStateRefusal(nameof(SystemActionKind.RerouteLogistics));
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadInt64(root, "from", out long from)
                    || !HandlerArgumentReader.TryReadInt64(root, "to", out long to)
                    || !HandlerArgumentReader.TryReadString(root, "item", out string item)
                    || !HandlerArgumentReader.TryReadNumber(root, "rate", out double rate))
                {
                    return Refuse(
                        nameof(SystemActionKind.RerouteLogistics), ActionResultStatus.Err,
                        "reroute-logistics-arguments:from/to/item/rate unreadable after schema gate", json);
                }

                RerouteLogisticsPlan? plan = _rerouteLogistics.Run(state, from, to, item, rate, out string? refusal);
                return plan is null
                    ? Refuse(nameof(SystemActionKind.RerouteLogistics), ActionResultStatus.ErrTargetInvalid, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), plan);
            },
            [SystemActionKind.AskPlayer] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadString(root, "question", out string question)
                    || !HandlerArgumentReader.TryReadOptionLabels(root, "options", out IReadOnlyList<string> options)
                    || !root.TryGetProperty("multi_select", out JsonElement multi)
                    || multi.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Refuse(
                        nameof(SystemActionKind.AskPlayer), ActionResultStatus.Err,
                        "ask-player-arguments:question/options/multi_select unreadable after schema gate", json);
                }

                return new ActionOutcome(
                    new ActionResult(ActionResultStatus.Ok), _askPlayer.Run(question, options, multi.GetBoolean()));
            },
            [SystemActionKind.TodoBoard] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return !HandlerArgumentReader.TryReadStringArray(root, "todos", out IReadOnlyList<string> todos)
                    ? Refuse(
                        nameof(SystemActionKind.TodoBoard), ActionResultStatus.Err,
                        "todo-board-arguments:todos unreadable after schema gate", json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), _todoBoard.Run(State, todos));
            },
            [SystemActionKind.SetAlarm] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return !HandlerArgumentReader.TryReadNumber(root, "after_seconds", out double afterSeconds)
                    || !HandlerArgumentReader.TryReadString(root, "reason", out string reason)
                    ? Refuse(
                        nameof(SystemActionKind.SetAlarm), ActionResultStatus.Err,
                        "set-alarm-arguments:after_seconds/reason unreadable after schema gate", json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), _setAlarm.Run(State, afterSeconds, reason));
            },
            [SystemActionKind.SpawnScout] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return !HandlerArgumentReader.TryReadString(root, "lane", out string lane)
                    || !HandlerArgumentReader.TryReadNumber(root, "duration", out double duration)
                    ? Refuse(
                        nameof(SystemActionKind.SpawnScout), ActionResultStatus.Err,
                        "spawn-scout-arguments:lane/duration unreadable after schema gate", json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), _spawnScout.Run(State, lane, duration));
            },
            [SystemActionKind.StopTask] = json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!HandlerArgumentReader.TryReadString(root, "task_id", out string taskId))
                {
                    return Refuse(
                        nameof(SystemActionKind.StopTask), ActionResultStatus.Err,
                        "stop-task-arguments:task_id unreadable after schema gate", json);
                }

                StopTaskAck? ack = _stopTask.Run(State, taskId, out string? refusal);
                return ack is null
                    ? Refuse(nameof(SystemActionKind.StopTask), ActionResultStatus.ErrTargetInvalid, refusal!, json)
                    : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
            },
        };

        if (_heroOrder is not null)
        {
            arms[SystemActionKind.HeroOrder] = json =>
            {
                HeroOrderResult heroOutcome = _heroOrder.Invoke(json);
                return heroOutcome.Ok
                    ? new ActionOutcome(new ActionResult(ActionResultStatus.Ok), heroOutcome.Ack)
                    : Refuse(
                        nameof(SystemActionKind.HeroOrder), ActionResultStatus.Err,
                        heroOutcome.Reason ?? "hero-order-refused", json);
            };
        }

        if (_towerEstate is not null)
        {
            arms[SystemActionKind.UpgradeTower] = json =>
            {
                (ActionResult result, UpgradeTowerReceipt? receipt) = _upgradeTower!.Invoke(json);
                return result.Status == ActionResultStatus.Ok
                    ? new ActionOutcome(result, receipt)
                    : LogFailure(nameof(SystemActionKind.UpgradeTower), result, json);
            };
            arms[SystemActionKind.SellTower] = json =>
            {
                (ActionResult result, SellTowerReceipt? receipt) = _sellTower!.Invoke(json);
                return result.Status == ActionResultStatus.Ok
                    ? new ActionOutcome(result, receipt)
                    : LogFailure(nameof(SystemActionKind.SellTower), result, json);
            };
        }

        return arms;
    }

    /// <summary>Runs the InspectTile handler against the state projection (split out for clarity).</summary>
    private ActionOutcome RunInspectTile(IActionWorldState state, CellCoord cell, string aspect, string json)
    {
        InspectTileReport? report = _inspectTile.Run(state, cell, aspect, out string? refusal);
        return report is null
            ? Refuse(nameof(SystemActionKind.InspectTile), ActionResultStatus.ErrTargetInvalid, refusal!, json)
            : new ActionOutcome(new ActionResult(ActionResultStatus.Ok), report);
    }

    /// <summary>The world view cast to the state projection, or null when it is absent or lacks the surface.</summary>
    private IActionWorldState? WorldState(string verb)
    {
        if (_worldView is null)
        {
            _ = NoViewRefusal(verb); // refusal line logged; the arm returns the same outcome
            return null;
        }

        if (_worldView is not IActionWorldState state)
        {
            _ = NoStateRefusal(verb);
            return null;
        }

        return state;
    }

    private ActionOutcome Deny(string? verb, string reason, string? actionJson)
    {
        // Out-of-list / invalid actions are DENIED and LOGGED (T-051
        // acceptance): structured line with the scrubbed payload digest —
        // the pre-scan guarantees no credential pattern survives.
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(verb, reason, actionJson));
        return new ActionOutcome(new ActionResult(ActionResultStatus.ErrSchemaDenied, reason), null);
    }

    private ActionOutcome Refuse(string verb, ActionResultStatus status, string reason, string actionJson)
    {
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(verb, reason, actionJson));
        return new ActionOutcome(new ActionResult(status, reason), null);
    }

    private ActionOutcome LogFailure(string verb, ActionResult result, string actionJson)
    {
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(verb, result.ErrorMessage ?? $"{verb}-refused", actionJson));
        return new ActionOutcome(result, null);
    }

    private ActionOutcome NoViewRefusal(string verb)
    {
        string reason = $"world-view-missing:{verb}:registry was built without an IActionWorldView; wire the simulation view first";
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.NotImplementedLine(verb, reason));
        return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
    }

    private ActionOutcome NoStateRefusal(string verb)
    {
        string reason = $"state-view-missing:{verb}:registry was built without an IActionWorldState (SimulationActionView provides one); refusing rather than guessing";
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.NotImplementedLine(verb, reason));
        return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
    }

    private void LogOk(string verb, int era) => _logSink.Write(LogLevel.Debug, LogTick(), ActionLog.OkLine(verb, era));

    private long LogTick() => _worldView?.Tick ?? -1;

    private static BuildPlanArguments ParseBuildPlanArguments(string actionJson)
    {
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        string towerId = root.GetProperty("tower_id").GetString()!;
        var cells = new List<CellCoord>();
        foreach (JsonElement cell in root.GetProperty("cells").EnumerateArray())
        {
            cells.Add(new CellCoord(cell.GetProperty("x").GetInt32(), cell.GetProperty("y").GetInt32()));
        }

        bool confirmOverwrite = root.TryGetProperty("confirm_overwrite", out JsonElement overwrite)
            && overwrite.ValueKind == JsonValueKind.True;
        return new BuildPlanArguments(towerId, cells, confirmOverwrite);
    }

    private static ReportArguments ParseReportArguments(string actionJson)
    {
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        string summary = root.GetProperty("summary").GetString()!;
        string verdict = root.GetProperty("verdict").GetString()!;
        var findings = new List<string>();
        if (root.TryGetProperty("findings", out JsonElement findingsElement)
            && findingsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement finding in findingsElement.EnumerateArray())
            {
                findings.Add(finding.GetString()!);
            }
        }

        return new ReportArguments(summary, findings, verdict);
    }

    private sealed record BuildPlanArguments(string TowerId, IReadOnlyList<CellCoord> Cells, bool ConfirmOverwrite);

    private sealed record ReportArguments(string Summary, IReadOnlyList<string> Findings, string Verdict);
}
