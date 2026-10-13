using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 第 2/3 段：意图路由 + 计划器（F-065）。计划器是目标级模板的
// 首次落地：守住X（HoldRegion）→「侦察→建造→汇报」三步序列；建造（Build）
// →「侦察→建造步→汇报」；只读意图（敌情/储备）→「侦察→汇报」（AnswerDirectly
// 全 allow、0 点，仍走满五段）。计划只读世界（规划读取、动作执行时才改）。

/// <summary>
/// Stage-2 intent router (T-053, implements the frozen K-5
/// <see cref="IIntentRouter"/>): routes the T-052 parseable subset.
/// Read-only intents answer directly; mutating intents plan and approve.
/// </summary>
public sealed class IntentRouter : IIntentRouter
{
    /// <inheritdoc />
    public IntentRoute Route(Intent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Kind switch
        {
            IntentKind.QueryThreat => new IntentRoute(RouteKind.AnswerDirectly, "read-only:threat"),
            IntentKind.QueryStockpile => new IntentRoute(RouteKind.AnswerDirectly, "read-only:stockpile"),
            IntentKind.Build => new IntentRoute(RouteKind.PlanAndApprove, "build-steps"),
            IntentKind.HoldRegion => new IntentRoute(RouteKind.PlanAndApprove, "goal-template"),
            IntentKind.SetAlarm => new IntentRoute(RouteKind.PlanAndApprove, "management"),
            _ => new IntentRoute(RouteKind.Reject, $"intent-outside-t053-subset:{intent.Kind}"),
        };
    }
}

/// <summary>One step of the player-facing plan card (F-066 方案卡行).</summary>
/// <param name="Verb">Closed verb of the step.</param>
/// <param name="ActionJson">Planned action JSON (schema-validated at execution).</param>
/// <param name="Points">System-point price (tariff base).</param>
/// <param name="Resources">Cost-currency resource quote.</param>
/// <param name="Risks">Risk highlights shown red on the card (danger bits, rejections, shortfalls).</param>
public sealed record PlanCardStep(
    string Verb,
    string ActionJson,
    int Points,
    double Resources,
    IReadOnlyList<string> Risks);

/// <summary>
/// The plan card (F-066 方案卡): what the player confirms or refuses —
/// steps + point/resource cost + red-marked risks. Plain data for the C-22
/// presenter; built beside the frozen K-5 <see cref="Plan"/>.
/// </summary>
public sealed record PlanCard(
    string OrderText,
    string IntentKind,
    string RouteKind,
    IReadOnlyList<PlanCardStep> Steps,
    int TotalPoints,
    double TotalResources,
    IReadOnlyList<string> Risks);

/// <summary>
/// Planner output bundle: the frozen K-5 <see cref="Plan"/> (stage-4 gate
/// input) plus the card and resource quote the pipeline carries alongside.
/// </summary>
public sealed record PlannedOrder(Plan Plan, PlanCard Card, double EstimatedResources);

/// <summary>Wire shape of a BuildPlan action payload (source-gen serialized, never string-concatenated).</summary>
public sealed record BuildPlanActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("tower_id")] string TowerId,
    [property: JsonPropertyName("cells")] IReadOnlyList<CellJson> Cells);

/// <summary>Wire shape of one {x,y} cell inside action JSON.</summary>
public sealed record CellJson(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

/// <summary>Wire shape of a LookAround action payload.</summary>
public sealed record LookAroundActionJson(
    [property: JsonPropertyName("action")] string Action);

/// <summary>Wire shape of a ReportToPlayer action payload (findings omitted when empty).</summary>
public sealed record ReportActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<string>? Findings,
    [property: JsonPropertyName("verdict")] string Verdict);

/// <summary>Wire shape of a SetAlarm action payload.</summary>
public sealed record SetAlarmActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("after_seconds")] int AfterSeconds,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Stage-3 planner (T-053, implements the frozen K-5 <see cref="IPlanner"/>):
/// turns one routed intent into a concrete plan via per-goal templates.
/// Planning reads the world view but mutates nothing. Cell proposals are
/// deterministic: a Chebyshev ring scan (row-major) around the region
/// anchor — buildability is NOT filtered here on purpose, so state-level
/// rejections surface at execution where the ≤ 2 self-correction loop owns
/// them (带错误重入); the cost quote, however, counts only buildable cells
/// (the same <see cref="BuildPlanAction"/> draft the registry will run).
/// </summary>
public sealed class PipelinePlanner : IPlanner
{
    /// <summary>Upper search radius of the anchor ring scan (cells).</summary>
    public const int MaxScanRadius = 6;

    /// <summary>SetAlarm clamp bounds (K-3 frozen field table).</summary>
    public const int AlarmSecondsMin = 30;

    /// <summary>SetAlarm clamp bounds (K-3 frozen field table).</summary>
    public const int AlarmSecondsMax = 600;

    private static readonly LookAroundActionJson LookAroundJson =
        new(nameof(SystemActionKind.LookAround));

    private readonly IActionWorldView _view;
    private readonly IReadOnlyDictionary<string, CellCoord> _regionAnchors;
    private readonly CellCoord _fallbackAnchor;
    private readonly string _defaultDefenseTowerId;
    private readonly Func<int> _eraOrdinal;
    private readonly BuildPlanAction _buildDraft = new();

    /// <param name="view">Read-only world view (cell grid, stock, catalog).</param>
    /// <param name="regionAnchors">Language-neutral region id → anchor cell (lexicon ids).</param>
    /// <param name="fallbackAnchor">Anchor for orders without a where slot.</param>
    /// <param name="defaultDefenseTowerId">Tower the HoldRegion goal template builds.</param>
    /// <param name="eraOrdinal">Current era ordinal (1..6) — BuildPlan cell cap 4+2×era.</param>
    public PipelinePlanner(
        IActionWorldView view,
        IReadOnlyDictionary<string, CellCoord> regionAnchors,
        CellCoord fallbackAnchor,
        string defaultDefenseTowerId,
        Func<int> eraOrdinal)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(regionAnchors);
        ArgumentNullException.ThrowIfNull(fallbackAnchor);
        if (string.IsNullOrWhiteSpace(defaultDefenseTowerId))
        {
            throw new ArgumentException("Default defense tower id must not be empty.", nameof(defaultDefenseTowerId));
        }

        ArgumentNullException.ThrowIfNull(eraOrdinal);
        _view = view;
        _regionAnchors = regionAnchors;
        _fallbackAnchor = fallbackAnchor;
        _defaultDefenseTowerId = defaultDefenseTowerId;
        _eraOrdinal = eraOrdinal;
    }

    /// <summary>Frozen K-5 stage-3 surface (the pipeline uses <see cref="Prepare"/>).</summary>
    public Plan Plan(Intent intent) => Prepare(intent).Plan;

    /// <summary>
    /// Builds the planned order for one intent: K-5 plan + card + resource
    /// quote. Templates: Build/HoldRegion → LookAround → BuildPlan →
    /// ReportToPlayer（军令→侦察→建造→汇报闭环, F-169）; QueryThreat/
    /// QueryStockpile → LookAround → ReportToPlayer; SetAlarm → SetAlarm →
    /// ReportToPlayer.
    /// </summary>
    public PlannedOrder Prepare(Intent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var builder = new PlanBuilder(intent);
        switch (intent.Kind)
        {
            case IntentKind.HoldRegion:
            {
                // 目标级模板：守住X → 看一眼战局 → 在 X 处建防御塔 → 汇报。
                builder.AddLookAround();
                string anchorId = intent.Slots.TryGetValue("target", out string? target) ? target : string.Empty;
                AddBuildStep(builder, _defaultDefenseTowerId, anchorId, qty: 2);
                builder.AddReport();
                break;
            }

            case IntentKind.Build:
            {
                builder.AddLookAround();
                string towerId = intent.Slots.TryGetValue("tower", out string? tower) ? tower : _defaultDefenseTowerId;
                string whereId = intent.Slots.TryGetValue("where", out string? where) ? where : string.Empty;
                int qty = ReadQuantity(intent, defaultQty: 1);
                AddBuildStep(builder, towerId, whereId, qty);
                builder.AddReport();
                break;
            }

            case IntentKind.QueryThreat:
            case IntentKind.QueryStockpile:
                builder.AddLookAround();
                builder.AddReport();
                break;

            case IntentKind.SetAlarm:
                builder.AddSetAlarm(intent);
                builder.AddReport();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, "Intent outside the routed subset.");
        }

        return builder.Build();
    }

    private static int ReadQuantity(Intent intent, int defaultQty)
    {
        if (intent.Slots.TryGetValue("qty", out string? raw)
            && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out int qty)
            && qty > 0)
        {
            return qty;
        }

        return defaultQty;
    }

    private void AddBuildStep(PlanBuilder builder, string towerId, string whereId, int qty)
    {
        CellCoord anchor = ResolveAnchor(whereId);
        int eraCap = 4 + (2 * _eraOrdinal());
        int capped = Math.Min(qty, eraCap);
        if (capped < qty)
        {
            builder.Risks.Add($"qty-capped:{qty}->{capped}:cell-cap-{eraCap}");
        }

        IReadOnlyList<CellCoord> cells = RingCells(anchor, capped);
        var actionJson = new BuildPlanActionJson(
            nameof(SystemActionKind.BuildPlan), towerId, cells.Select(c => new CellJson(c.X, c.Y)).ToArray());
        string serialized = JsonSerializer.Serialize(actionJson, PipelinePlannerJsonContext.Default.BuildPlanActionJson);

        // Cost quote: the same draft the registry runs — counts buildable
        // cells only, so the atomic pre-check compares against what could
        // actually be placed.
        BuildPlanDraft? draft = _buildDraft.Draft(_view, towerId, cells, confirmOverwrite: false);
        double resources = draft?.TotalCost ?? 0;
        var risks = new List<string>();
        if (draft is not null)
        {
            risks.AddRange(draft.Risks);
        }

        builder.AddStep(SystemActionKind.BuildPlan, serialized, resources, risks);
    }

    private CellCoord ResolveAnchor(string whereId) =>
        !string.IsNullOrEmpty(whereId) && _regionAnchors.TryGetValue(whereId, out CellCoord? anchor)
            ? anchor
            : _fallbackAnchor;

    /// <summary>
    /// Deterministic Chebyshev ring scan: radius 0 then 1..MaxScanRadius,
    /// ring cells row-major (dy outer, dx inner). No buildability filter —
    /// see class remarks.
    /// </summary>
    private static IReadOnlyList<CellCoord> RingCells(CellCoord anchor, int count)
    {
        var cells = new List<CellCoord>(count);
        for (int radius = 0; radius <= MaxScanRadius && cells.Count < count; radius++)
        {
            for (int dy = -radius; dy <= radius && cells.Count < count; dy++)
            {
                for (int dx = -radius; dx <= radius && cells.Count < count; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue; // ring cells only (inner rings already taken)
                    }

                    cells.Add(new CellCoord(anchor.X + dx, anchor.Y + dy));
                }
            }
        }

        return cells;
    }

    private sealed class PlanBuilder
    {
        private readonly Intent _intent;
        private readonly List<PlanStep> _steps = new();
        private readonly List<double> _stepResources = new();
        private readonly List<List<string>> _stepRisks = new();
        public readonly List<string> Risks = new();

        public PlanBuilder(Intent intent)
        {
            _intent = intent;
        }

        public void AddStep(SystemActionKind verb, string actionJson, double resources, IReadOnlyList<string> risks)
        {
            int points = SystemActionTariffTable.Get(verb).PointCost;
            _steps.Add(new PlanStep(verb, actionJson, points));
            _stepResources.Add(resources);
            _stepRisks.Add(new List<string>(risks));
            Risks.AddRange(risks);
        }

        public void AddLookAround()
        {
            AddStep(
                SystemActionKind.LookAround,
                JsonSerializer.Serialize(LookAroundJson, PipelinePlannerJsonContext.Default.LookAroundActionJson),
                resources: 0,
                Array.Empty<string>());
        }

        public void AddReport()
        {
            // Plan-time draft of the report step; the pipeline synthesizes
            // the real payload from the run's outcomes at stage 5.
            var draft = new ReportActionJson(
                nameof(SystemActionKind.ReportToPlayer),
                $"军令「{_intent.RawText}」执行完毕汇报",
                Findings: null,
                Verdict: "CONFIRMED");
            AddStep(
                SystemActionKind.ReportToPlayer,
                JsonSerializer.Serialize(draft, PipelinePlannerJsonContext.Default.ReportActionJson),
                resources: 0,
                Array.Empty<string>());
        }

        public void AddSetAlarm(Intent intent)
        {
            int seconds = AlarmSecondsMin;
            if (intent.Slots.TryGetValue("after", out string? raw)
                && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                seconds = parsed;
            }

            int clamped = Math.Clamp(seconds, AlarmSecondsMin, AlarmSecondsMax);
            if (clamped != seconds)
            {
                Risks.Add($"after-clamped:{seconds}->{clamped}");
            }

            string reason = intent.Slots.TryGetValue("reason", out string? why) && !string.IsNullOrEmpty(why)
                ? why
                : "军令设闹";
            var alarm = new SetAlarmActionJson(nameof(SystemActionKind.SetAlarm), clamped, reason);
            AddStep(
                SystemActionKind.SetAlarm,
                JsonSerializer.Serialize(alarm, PipelinePlannerJsonContext.Default.SetAlarmActionJson),
                resources: 0,
                Array.Empty<string>());
        }

        public PlannedOrder Build()
        {
            // The last step is always the report step (stage 5 marker).
            var plan = new Plan(_intent, _steps.ToArray(), _steps.Sum(step => step.PointCost), Risks.ToArray());
            var card = new PlanCard(
                _intent.RawText,
                _intent.Kind.ToString(),
                "-",
                _steps.Select((step, i) => new PlanCardStep(
                    step.Action.ToString(),
                    step.ActionJson,
                    step.PointCost,
                    _stepResources[i],
                    _stepRisks[i])).ToArray(),
                plan.EstimatedPointCost,
                _stepResources.Sum(),
                Risks.ToArray());
            return new PlannedOrder(plan, card, _stepResources.Sum());
        }
    }
}

/// <summary>Source-gen context for the pipeline's action-JSON wire shapes (D-05).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildPlanActionJson))]
[JsonSerializable(typeof(LookAroundActionJson))]
[JsonSerializable(typeof(ReportActionJson))]
[JsonSerializable(typeof(SetAlarmActionJson))]
[JsonSerializable(typeof(BuildTowerCommandJson))]
public sealed partial class PipelinePlannerJsonContext : JsonSerializerContext
{
}
