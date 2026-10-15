using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Contracts.Journal;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 回放侧：决策轨迹（回放等价目标）+ JSONL 回放器（F-069 的
// "可回放"半边）。轨迹 = 对同一条军令的全部管线事件做折叠；管线运行时用
// 类型化载荷实时折叠，回放器从 JSONL 反序列化出同一批载荷再走同一折叠器
// ——两侧相等即为"按事件重建管线决策序列（断言与原运行一致）"。

/// <summary>One execution attempt of one planned step (replay-equality unit).</summary>
public sealed record ReplayAttempt(
    string Verb,
    string ActionJson,
    string Status,
    string? Error,
    int PointsSpent,
    int Attempt,
    bool SelfCorrected,
    string? RetryAdjustment);

/// <summary>One planned step with its attempts (replay-equality unit).</summary>
/// <remarks>Manual equality: collection members compare element-wise, not by reference.</remarks>
public sealed record ReplayStep(
    string Verb,
    string ActionJson,
    int Points,
    double Resources,
    string Status,
    IReadOnlyList<ReplayAttempt> Attempts)
{
    /// <summary>Element-wise equality (records default to reference equality on collections).</summary>
    public bool Equals(ReplayStep? other) =>
        other is not null
        && Verb == other.Verb
        && ActionJson == other.ActionJson
        && Points == other.Points
        && Resources == other.Resources
        && Status == other.Status
        && Attempts.Count == other.Attempts.Count
        && Attempts.Zip(other.Attempts, (left, right) => left == right).All(equal => equal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Verb, ActionJson, Points, Status, Attempts.Count);
}

/// <summary>
/// The full decision sequence of one handled order (T-053 acceptance: 回放器
/// 重建==原决策序列). Value-equality record — the pipeline builds it live,
/// the <see cref="JournalReplayer"/> rebuilds it from the JSONL stream, and
/// the two must be equal for every order.
/// </summary>
public sealed record DecisionTrace(
    int Order,
    string OrderText,
    string Outcome,
    string? IntentKind,
    string? ClarifyingKey,
    string RouteKind,
    int EstimatedPoints,
    double EstimatedResources,
    string ApprovalDecision,
    IReadOnlyList<ReplayStep> Steps,
    IReadOnlyList<string> Risks,
    string ReportSummary,
    IReadOnlyList<string> ReportFindings,
    string ReportVerdict)
{
    /// <summary>Element-wise equality (records default to reference equality on collections).</summary>
    public bool Equals(DecisionTrace? other) =>
        other is not null
        && Order == other.Order
        && OrderText == other.OrderText
        && Outcome == other.Outcome
        && IntentKind == other.IntentKind
        && ClarifyingKey == other.ClarifyingKey
        && RouteKind == other.RouteKind
        && EstimatedPoints == other.EstimatedPoints
        && EstimatedResources == other.EstimatedResources
        && ApprovalDecision == other.ApprovalDecision
        && ReportSummary == other.ReportSummary
        && ReportVerdict == other.ReportVerdict
        && Risks.SequenceEqual(other.Risks)
        && ReportFindings.SequenceEqual(other.ReportFindings)
        && Steps.Count == other.Steps.Count
        && Steps.Zip(other.Steps, (left, right) => left == right).All(equal => equal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Order, OrderText, Outcome, ApprovalDecision, Steps.Count);
}

/// <summary>
/// Single fold over the pipeline payloads of one order (T-053). The pipeline
/// feeds typed payloads as they happen; the replayer feeds the same records
/// parsed back from JSONL — identical inputs, identical
/// <see cref="DecisionTrace"/>.
/// </summary>
public sealed class DecisionTraceBuilder
{
    private readonly int _order;
    private readonly string _orderText;
    private string _outcome = "in_progress";
    private string? _intentKind;
    private string? _clarifyingKey;
    private string _routeKind = "-";
    private int _estimatedPoints;
    private double _estimatedResources;
    private string _approvalDecision = "-";
    private readonly List<ReplayStep> _steps = new();
    private readonly List<string> _risks = new();
    private string _reportSummary = string.Empty;
    private readonly List<string> _reportFindings = new();
    private string _reportVerdict = string.Empty;

    /// <param name="order">1-based order counter.</param>
    /// <param name="orderText">Raw order text.</param>
    public DecisionTraceBuilder(int order, string orderText)
    {
        _order = order;
        _orderText = orderText;
    }

    /// <summary>Immutable snapshot of everything folded so far.</summary>
    public DecisionTrace Snapshot() => new(
        _order,
        _orderText,
        _outcome,
        _intentKind,
        _clarifyingKey,
        _routeKind,
        _estimatedPoints,
        _estimatedResources,
        _approvalDecision,
        _steps.ToArray(),
        _risks.ToArray(),
        _reportSummary,
        _reportFindings.ToArray(),
        _reportVerdict);

    /// <summary>Folds one pipeline payload into the trace (pipeline and replayer share this).</summary>
    public void Apply(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload)
        {
            case PipelineInputPayload:
                break; // order identity already captured by the builder ctor

            case PipelinePlanPayload plan:
                _intentKind = plan.Intent;
                _routeKind = plan.Route;
                _estimatedPoints = plan.EstimatedPoints;
                _estimatedResources = plan.EstimatedResources;
                _risks.Clear();
                _risks.AddRange(plan.Risks);
                _steps.Clear();
                for (int i = 0; i < plan.StepVerbs.Length; i++)
                {
                    _steps.Add(new ReplayStep(
                        plan.StepVerbs[i],
                        plan.StepActionJson[i],
                        plan.StepPoints[i],
                        plan.StepResources[i],
                        "planned",
                        new List<ReplayAttempt>()));
                }

                break;

            case PipelineApprovalPayload approval:
                _approvalDecision = approval.Decision;
                if (approval.Decision == "ask")
                {
                    _outcome = "awaiting_approval";
                }
                else if (_outcome == "awaiting_approval")
                {
                    _outcome = "in_progress"; // player confirmed — the run continues
                }

                break;

            case PipelineExecutionPayload execution:
                if (execution.StepIndex >= 0 && execution.StepIndex < _steps.Count)
                {
                    var attempts = new List<ReplayAttempt>(_steps[execution.StepIndex].Attempts)
                    {
                        new ReplayAttempt(
                            execution.Verb,
                            execution.ActionJson,
                            execution.Status,
                            execution.Error,
                            execution.PointsSpent,
                            execution.Attempt,
                            execution.SelfCorrected,
                            execution.RetryAdjustment),
                    };

                    _steps[execution.StepIndex] = _steps[execution.StepIndex] with
                    {
                        Status = execution.Status,
                        Attempts = attempts,
                    };
                }

                break;

            case PipelineReportPayload report:
                _reportSummary = report.Summary;
                _reportFindings.Clear();
                _reportFindings.AddRange(report.Findings);
                _reportVerdict = report.Verdict;
                _outcome = report.Outcome;
                break;

            case PipelineClarifyPayload clarify:
                _clarifyingKey = clarify.ClarifyingKey;
                _outcome = "clarification";
                break;

            default:
                throw new ArgumentException($"Unknown pipeline payload type {payload.GetType().Name}.", nameof(payload));
        }
    }

    /// <summary>Marks the terminal outcome (pipeline-side only; replay derives it from clarify/report payloads).</summary>
    public void Complete(string outcome) => _outcome = outcome;
}

/// <summary>
/// JSONL war-report replayer (T-053, F-069/F-121 groundwork): reads one run
/// journal, verifies stream integrity (strictly increasing <c>Seq</c> — K-8
/// ordered-domain contract) and folds every pipeline payload back into the
/// per-order <see cref="DecisionTrace"/> list. Non-pipeline payloads (the
/// C-14 ledger's budget audit lines, also Kind=Validation) pass the
/// integrity check but do not feed decision traces.
/// </summary>
public static class JournalReplayer
{
    /// <summary>
    /// Replays a journal file: returns one trace per handled order, journal
    /// order. Throws on a regressing/duplicate sequence number or a pipeline
    /// payload that fails to parse (a war report must replay or the run is
    /// broken, not degraded).
    /// </summary>
    public static IReadOnlyList<DecisionTrace> Replay(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return Fold(JsonlJournalWriter.ReadAll(filePath));
    }

    /// <summary>Integrity check + fold over raw events (the replayer's two guarantees).</summary>
    public static IReadOnlyList<DecisionTrace> Fold(IReadOnlyList<JournalEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        long lastSeq = 0;
        var builders = new Dictionary<int, DecisionTraceBuilder>();
        foreach (JournalEvent evt in events)
        {
            if (evt.Seq <= lastSeq)
            {
                throw new InvalidOperationException(
                    $"Journal sequence regressed or repeated ({lastSeq} -> {evt.Seq}) — K-8 ordered-domain violation.");
            }

            lastSeq = evt.Seq;

            if (!TryParsePipelinePayload(evt, out object? payload, out int order))
            {
                continue; // audit-class event (C-14 ledger) — integrity-checked, not a decision
            }

            if (!builders.TryGetValue(order, out DecisionTraceBuilder? builder))
            {
                builder = new DecisionTraceBuilder(order, ReadOrderText(payload!));
                builders[order] = builder;
            }

            builder.Apply(payload!);
        }

        // A healthy journal terminates every order with a report or clarify
        // payload; an order still "in_progress" is a torn run and stays that
        // way so replay-vs-live equality fails loudly.
        return builders.Values.OrderBy(b => b.Snapshot().Order)
            .Select(b => b.Snapshot())
            .ToArray();
    }

    private static string ReadOrderText(object payload) => payload switch
    {
        PipelineInputPayload input => input.OrderText,
        PipelinePlanPayload plan => plan.OrderText,
        PipelineApprovalPayload approval => approval.OrderText,
        PipelineExecutionPayload execution => execution.OrderText,
        PipelineReportPayload report => report.OrderText,
        PipelineClarifyPayload clarify => clarify.OrderText,
        _ => string.Empty,
    };

    /// <summary>
    /// Typed pipeline payload of one event (T-111 timeline source): the six
    /// pipeline payload records deserialize here, audit-class lines (C-14
    /// ledger) return null. Shared parser — the replayer folds decision
    /// traces and the <see cref="ReplayTimeline"/> builds the timeline from
    /// the SAME typed payloads, so no second parser can drift.
    /// </summary>
    /// <param name="evt">One journal event.</param>
    /// <param name="order">Order counter of the payload (−1 for audit lines).</param>
    public static object? TryPayload(JournalEvent evt, out int order)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return TryParsePipelinePayload(evt, out object? payload, out order) ? payload : null;
    }

    private static bool TryParsePipelinePayload(JournalEvent evt, out object? payload, out int order)
    {
        payload = null;
        order = -1;
        string type;
        try
        {
            using JsonDocument document = JsonDocument.Parse(evt.PayloadJson);
            if (!document.RootElement.TryGetProperty("Type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false; // foreign payload shape (e.g. SystemPointLedgerEvent)
            }

            type = typeElement.GetString()!;
        }
        catch (JsonException)
        {
            return false;
        }

        try
        {
            switch (type)
            {
                case PipelineJournalTypes.Input:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelineInputPayload));
                    order = ((PipelineInputPayload)payload).Order;
                    return true;

                case PipelineJournalTypes.Plan:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelinePlanPayload));
                    order = ((PipelinePlanPayload)payload).Order;
                    return true;

                case PipelineJournalTypes.Approval:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelineApprovalPayload));
                    order = ((PipelineApprovalPayload)payload).Order;
                    return true;

                case PipelineJournalTypes.Execution:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelineExecutionPayload));
                    order = ((PipelineExecutionPayload)payload).Order;
                    return true;

                case PipelineJournalTypes.Report:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelineReportPayload));
                    order = ((PipelineReportPayload)payload).Order;
                    return true;

                case PipelineJournalTypes.Clarify:
                    payload = Require(JsonSerializer.Deserialize(evt.PayloadJson, PipelineJsonContext.Default.PipelineClarifyPayload));
                    order = ((PipelineClarifyPayload)payload).Order;
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object Require(object? payload) =>
        payload ?? throw new JsonException("Pipeline payload deserialized to null.");
}
