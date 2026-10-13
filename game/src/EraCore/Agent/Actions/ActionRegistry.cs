using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Actions;

/// <summary>
/// One registry invocation: the typed <see cref="ActionResult"/> plus the
/// structured payload the handler produced (C-03 implementation detail —
/// K-3's result record stays payload-free; the C-13 pipeline consumes the
/// payload through this outcome type, T-053).
/// </summary>
/// <param name="Result">Outcome; anything but Ok is a failure.</param>
/// <param name="Payload">LookAroundSummary / BuildPlanDraft / AgentReport on the three T-051 verbs; null otherwise.</param>
public sealed record ActionOutcome(ActionResult Result, object? Payload);

/// <summary>
/// The C-03 action registry (T-051): the single lawful door every system
/// action passes through (RL6 — closed 19-verb list, closed-schema
/// validation, unlock-era gate, then the registered handler). What T-051
/// delivers:
/// <list type="bullet">
/// <item>3 real handlers — LookAround (battle overview), BuildPlan (draft
/// plan, never an execution), ReportToPlayer (bounded, link-filtered
/// report);</item>
/// <item>the other 16 verbs registered in a deliberate not-implemented
/// tier that returns a typed refusal (Err + explicit code) — an
/// undelivered handler NEVER reports success;</item>
/// <item>out-of-list refusals with a structured, secret-scrubbed
/// ILogSink record (F-062/F-070 acceptance: 清单外动作拒绝并记日志);</item>
/// <item>the unlock-era gate as data (SpawnScout in E1 →
/// ErrCapabilityLocked with clarifying copy, N9/A2).</item>
/// </list>
/// Point charging stays with the C-14 ledger (T-050): prices live in
/// <see cref="ActionCatalog"/> and successful outcomes report
/// PointsSpent=0 until the ledger is wired into the pipeline (T-053).
/// </summary>
public sealed class ActionRegistry : IActionExecutor
{
    private static readonly IReadOnlyList<SystemActionKind> ImplementedKinds = new[]
    {
        SystemActionKind.LookAround,
        SystemActionKind.BuildPlan,
        SystemActionKind.ReportToPlayer,
    };

    private readonly ILogSink _logSink;
    private readonly IActionWorldView? _worldView;
    private readonly JsonSchemaValidator _validator;
    private readonly Func<int> _eraOrdinal;
    private readonly LookAroundAction _lookAround = new();
    private readonly BuildPlanAction _buildPlan = new();
    private readonly ReportToPlayerAction _reportToPlayer = new();

    /// <param name="logSink">Diagnostic sink for every registry verdict (structured, secret-scrubbed).</param>
    /// <param name="worldView">World view the stateful handlers read; null makes BuildPlan refuse with Err until wired.</param>
    /// <param name="validator">Schema validator; when null a default one is built on <paramref name="eraOrdinalProvider"/>.</param>
    /// <param name="eraOrdinalProvider">Current era ordinal (E1=1 …); defaults to E1.</param>
    public ActionRegistry(
        ILogSink logSink,
        IActionWorldView? worldView = null,
        JsonSchemaValidator? validator = null,
        Func<int>? eraOrdinalProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        _logSink = logSink;
        _worldView = worldView;
        _eraOrdinal = eraOrdinalProvider ?? (static () => EraOrdinals.E1);
        _validator = validator ?? new JsonSchemaValidator(eraOrdinalProvider);
    }

    /// <summary>The closed verb list the registry accepts (all 19, K-3).</summary>
    public static IReadOnlyList<SystemActionKind> RegisteredKinds => ActionCatalog.All.Select(d => d.Kind).ToList();

    /// <summary>
    /// The single lawful entry (RL6): validates, gates and dispatches one
    /// action JSON. Unknown verbs are refused with ErrSchemaDenied and a
    /// structured log record that never contains credential material.
    /// </summary>
    public ActionOutcome Invoke(string actionJson)
    {
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return Deny(null, "payload-empty", actionJson);
        }

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

        // Dispatch to the registered handler.
        if (!ImplementedKinds.Contains(descriptor.Kind))
        {
            string reason = $"not-implemented:{descriptor.Verb}:handler lands with T-070 (W3 action groups); refusing rather than reporting success";
            _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.NotImplementedLine(descriptor.Verb, reason));
            return new ActionOutcome(new ActionResult(ActionResultStatus.Err, reason), null);
        }

        switch (descriptor.Kind)
        {
            case SystemActionKind.LookAround:
            {
                if (_worldView is null)
                {
                    return NoViewRefusal(descriptor.Verb);
                }

                LookAroundSummary summary = _lookAround.Run(_worldView);
                LogOk(descriptor.Verb, era);
                return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), summary);
            }

            case SystemActionKind.BuildPlan:
            {
                if (_worldView is null)
                {
                    return NoViewRefusal(descriptor.Verb);
                }

                BuildPlanArguments arguments = ParseBuildPlanArguments(actionJson);
                BuildPlanDraft? draft = _buildPlan.Draft(_worldView, arguments.TowerId, arguments.Cells, arguments.ConfirmOverwrite);
                if (draft is null)
                {
                    _logSink.Write(
                        LogLevel.Warn, LogTick(), ActionLog.DenyLine(descriptor.Verb, "target-invalid:tower-unknown", actionJson));
                    return new ActionOutcome(new ActionResult(
                        ActionResultStatus.ErrTargetInvalid,
                        $"target-invalid:tower-id '{arguments.TowerId}' is unknown to the level catalog (or locked); no draft was produced"), null);
                }

                LogOk(descriptor.Verb, era);
                return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), draft);
            }

            case SystemActionKind.ReportToPlayer:
            {
                ReportArguments arguments = ParseReportArguments(actionJson);
                AgentReport report = _reportToPlayer.Format(arguments.Summary, arguments.Findings, arguments.Verdict);
                LogOk(descriptor.Verb, era);
                return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), report);
            }

            default:
                throw new InvalidOperationException(
                    $"Verb {descriptor.Kind} is marked implemented but has no dispatch arm; registry table drift.");
        }
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

    private const int MaxLoggedErrors = 3;

    private ActionOutcome Deny(string? verb, string reason, string? actionJson)
    {
        // Out-of-list / invalid actions are DENIED and LOGGED (T-051
        // acceptance): structured line with the scrubbed payload digest —
        // the pre-scan guarantees no credential pattern survives.
        _logSink.Write(LogLevel.Warn, LogTick(), ActionLog.DenyLine(verb, reason, actionJson));
        return new ActionOutcome(new ActionResult(ActionResultStatus.ErrSchemaDenied, reason), null);
    }

    private ActionOutcome NoViewRefusal(string verb)
    {
        string reason = $"world-view-missing:{verb}:registry was built without an IActionWorldView; wire the simulation view first";
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
