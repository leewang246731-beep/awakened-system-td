using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 第 2/3 段：意图路由 + 计划器（F-065）。计划器是目标级模板的
// 首次落地：守住X（HoldRegion）→「侦察→建造→汇报」三步序列；建造（Build）
// →「侦察→建造步→汇报」；只读意图（敌情/储备）→「侦察→汇报」（AnswerDirectly
// 全 allow、0 点，仍走满五段）。计划只读世界（规划读取、动作执行时才改）。
//
// T-068 (C-13 全量) 扩到 SP-2 十二意图：模板表=IntentActionMap（数据），
// Upgrade/Sell/FocusFire/ChangeFocusMode/RerouteLogistics/Patrol/
// ScrollControl 七个新模板 + QueryStockpile/QueryThreat 的动作化收口；
// 槽位→动作实参的解析（塔指认/敌指认/分区→建筑）只读 IActionWorldState，
// 解析不出时宁可落 risk 红标也不猜（Honest planner：规划层零编造）。

/// <summary>
/// Stage-2 intent router (T-053; full 12-intent routing since T-068):
/// the two read-only intents answer directly, every other SP-2 intent
/// plans and approves (the mapping itself lives in
/// <see cref="IntentActionMap"/> — this class only classifies).
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
            _ => new IntentRoute(RouteKind.PlanAndApprove, $"template:{intent.Kind}"),
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
/// presenter; built beside the frozen K-5 <see cref="Plan"/>. Injection
/// guard ② (F-096): the card renders ONLY structured fields — step
/// payloads are serialized action JSON and risks are machine-readable
/// codes, never free-form LLM/mod text.
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
    [property: JsonPropertyName("after_seconds")] double AfterSeconds,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Wire shape of an UpgradeTower action payload (T-068).</summary>
public sealed record UpgradeTowerActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_id")] long TargetId,
    [property: JsonPropertyName("to_tier")] int ToTier);

/// <summary>Wire shape of a SellTower action payload (T-068).</summary>
public sealed record SellTowerActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_id")] long TargetId,
    [property: JsonPropertyName("confirm_sell")] bool ConfirmSell);

/// <summary>Wire shape of a FocusFire action payload (T-068).</summary>
public sealed record FocusFireActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_id")] long TargetId);

/// <summary>Wire shape of a SetTowerMode action payload (T-068).</summary>
public sealed record SetTowerModeActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_id")] IReadOnlyList<long> TargetIds,
    [property: JsonPropertyName("mode")] string Mode);

/// <summary>Wire shape of a SetRally action payload (T-068).</summary>
public sealed record SetRallyActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("stance")] string Stance);

/// <summary>Wire shape of a RerouteLogistics action payload (T-068).</summary>
public sealed record RerouteLogisticsActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("from")] long From,
    [property: JsonPropertyName("to")] long To,
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("rate")] double Rate);

/// <summary>Wire shape of a QueryWorld action payload (T-068).</summary>
public sealed record QueryWorldActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("head_limit")] int? HeadLimit);

/// <summary>Wire shape of an InspectTile action payload (T-068).</summary>
public sealed record InspectTileActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("cell")] CellJson Cell,
    [property: JsonPropertyName("aspect")] string Aspect);

/// <summary>Wire shape of a SpawnScout action payload (T-068).</summary>
public sealed record SpawnScoutActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("duration")] double Duration);

/// <summary>Wire shape of a TriggerScroll action payload (T-068).</summary>
public sealed record TriggerScrollActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("scroll_id")] string ScrollId);

/// <summary>Wire shape of a StopTask action payload (T-068).</summary>
public sealed record StopTaskActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("task_id")] string TaskId);

/// <summary>
/// Stage-3 planner (T-053; twelve SP-2 templates since T-068, implements
/// the frozen K-5 <see cref="IPlanner"/>): turns one routed intent into a
/// concrete plan via per-goal templates read from
/// <see cref="IntentActionMap"/>. Planning reads the world view but
/// mutates nothing. Cell proposals are deterministic: a Chebyshev ring
/// scan (row-major) around the region anchor — buildability is NOT
/// filtered here on purpose, so state-level rejections surface at
/// execution where the ≤ 2 self-correction loop owns them (带错误重入);
/// the cost quote, however, counts only buildable cells (the same
/// <see cref="BuildPlanAction"/> draft the registry will run). Target
/// slots (tower/enemy/building designations) resolve against the live
/// roster through <see cref="IActionWorldState"/> — an unresolvable
/// designation marks a risk red and plans an out-of-range id 0, which the
/// schema gate denies honestly at execution (规划层零编造).
/// </summary>
public sealed class PipelinePlanner : IPlanner
{
    /// <summary>Upper search radius of the anchor ring scan (cells).</summary>
    public const int MaxScanRadius = 6;

    /// <summary>SetAlarm clamp bounds (K-3 frozen field table).</summary>
    public const int AlarmSecondsMin = 30;

    /// <summary>SetAlarm clamp bounds (K-3 frozen field table).</summary>
    public const int AlarmSecondsMax = 600;

    /// <summary>SpawnScout duration clamp bounds (K-3 frozen field table, G.2).</summary>
    public const double ScoutDurationMin = 15.0;

    /// <summary>SpawnScout duration clamp bounds (K-3 frozen field table, G.2).</summary>
    public const double ScoutDurationMax = 120.0;

    /// <summary>Default patrol duration when the parse carried none (SP-2 #11 缺省 60 s).</summary>
    public const double ScoutDurationDefault = 60.0; // ci-allow: R7 T-068 SP-2 #11 巡逻 duration 缺省 60s（冻结口径）

    /// <summary>Default reroute priority share when the grammar captured none (G.2 rate 0~1).</summary>
    public const double RerouteRateDefault = 1.0; // ci-allow: R7 T-068 G.2 rate 域内工程缺省=全量改道（非平衡数值）

    /// <summary>SetTowerMode hard cap on tower ids (G.2 target_id[]≤6).</summary>
    public const int TowerModeTargetCap = 6; // ci-allow: R7 T-068 G.2 target_id[]≤6（冻结域常量）

    private static readonly LookAroundActionJson LookAroundJson =
        new(nameof(SystemActionKind.LookAround));

    private readonly IActionWorldView _view;
    private readonly IReadOnlyDictionary<string, CellCoord> _regionAnchors;
    private readonly CellCoord _fallbackAnchor;
    private readonly string _defaultDefenseTowerId;
    private readonly Func<int> _eraOrdinal;
    private readonly BuildPlanAction _buildDraft = new();

    /// <param name="view">Read-only world view (cell grid, stock, catalog; the roster surface when it implements <see cref="IActionWorldState"/>).</param>
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
    /// quote. Template shapes per <see cref="IntentActionMap"/>; every
    /// template ends with the stage-5 ReportToPlayer marker.
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

            case IntentKind.Upgrade:
            {
                builder.AddLookAround();
                string target = intent.Slots.TryGetValue("target", out string? upgradeTarget) ? upgradeTarget : string.Empty;
                int toTier = ReadIntSlot(intent, "to_tier", defaultValue: 2);
                long towerId = ResolveTowerTarget(builder, target);
                builder.AddStep(
                    SystemActionKind.UpgradeTower,
                    JsonSerializer.Serialize(
                        new UpgradeTowerActionJson(nameof(SystemActionKind.UpgradeTower), towerId, toTier),
                        PipelinePlannerJsonContext.Default.UpgradeTowerActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.Sell:
            {
                builder.AddLookAround();
                string target = intent.Slots.TryGetValue("target", out string? sellTarget) ? sellTarget : string.Empty;
                long towerId = ResolveTowerTarget(builder, target);
                builder.AddStep(
                    SystemActionKind.SellTower,
                    JsonSerializer.Serialize(
                        new SellTowerActionJson(nameof(SystemActionKind.SellTower), towerId, ConfirmSell: true),
                        PipelinePlannerJsonContext.Default.SellTowerActionJson),
                    resources: 0,
                    new[] { "danger:confirm_sell:true (永不自动允许)" });
                builder.AddReport();
                break;
            }

            case IntentKind.FocusFire:
            {
                builder.AddLookAround();
                string target = intent.Slots.TryGetValue("target", out string? fireTarget) ? fireTarget : string.Empty;
                long enemyId = ResolveEnemyTarget(builder, target);
                builder.AddStep(
                    SystemActionKind.FocusFire,
                    JsonSerializer.Serialize(
                        new FocusFireActionJson(nameof(SystemActionKind.FocusFire), enemyId),
                        PipelinePlannerJsonContext.Default.FocusFireActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.ChangeFocusMode:
            {
                string mode = intent.Slots.TryGetValue("mode", out string? modeSlot) ? modeSlot : "first";
                string towers = intent.Slots.TryGetValue("target_towers", out string? towersSlot) ? towersSlot : "all";
                IReadOnlyList<long> ids = ResolveTowerTargets(builder, towers, TowerModeTargetCap);
                builder.AddStep(
                    SystemActionKind.SetTowerMode,
                    JsonSerializer.Serialize(
                        new SetTowerModeActionJson(nameof(SystemActionKind.SetTowerMode), ids, mode),
                        PipelinePlannerJsonContext.Default.SetTowerModeActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.RerouteLogistics:
            {
                string fromSlot = intent.Slots.TryGetValue("from", out string? from) ? from : string.Empty;
                string toSlot = intent.Slots.TryGetValue("to", out string? to) ? to : string.Empty;
                string item = intent.Slots.TryGetValue("item", out string? itemSlot) ? itemSlot : _view.CostCurrency.Value;
                double rate = ReadDoubleSlot(intent, "rate", RerouteRateDefault);
                long fromId = ResolveEndpoint(builder, fromSlot, supplierSide: true);
                long toId = ResolveEndpoint(builder, toSlot, supplierSide: false);
                builder.AddStep(
                    SystemActionKind.RerouteLogistics,
                    JsonSerializer.Serialize(
                        new RerouteLogisticsActionJson(nameof(SystemActionKind.RerouteLogistics), fromId, toId, item, rate),
                        PipelinePlannerJsonContext.Default.RerouteLogisticsActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.QueryStockpile:
            {
                builder.AddLookAround();
                string item = intent.Slots.TryGetValue("item", out string? itemSlot) ? itemSlot : "all";
                builder.AddStep(
                    SystemActionKind.QueryWorld,
                    JsonSerializer.Serialize(
                        new QueryWorldActionJson(nameof(SystemActionKind.QueryWorld), "resource", item, HeadLimit: null),
                        PipelinePlannerJsonContext.Default.QueryWorldActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.QueryThreat:
            {
                AddThreatReconStep(builder, intent);
                builder.AddReport();
                break;
            }

            case IntentKind.SetAlarm:
                builder.AddSetAlarm(intent);
                builder.AddReport();
                break;

            case IntentKind.Patrol:
            {
                string lane = intent.Slots.TryGetValue("lane", out string? laneSlot) ? laneSlot : "lane-main";
                double duration = ReadDoubleSlot(intent, "duration", ScoutDurationDefault);
                double clamped = Math.Clamp(duration, ScoutDurationMin, ScoutDurationMax);
                if (Math.Abs(clamped - duration) > double.Epsilon)
                {
                    builder.Risks.Add($"duration-clamped:{duration:0.#}->{clamped:0.#}");
                }

                builder.AddStep(
                    SystemActionKind.SpawnScout,
                    JsonSerializer.Serialize(
                        new SpawnScoutActionJson(nameof(SystemActionKind.SpawnScout), lane, clamped),
                        PipelinePlannerJsonContext.Default.SpawnScoutActionJson),
                    resources: 0,
                    Array.Empty<string>());
                builder.AddReport();
                break;
            }

            case IntentKind.ScrollControl:
            {
                if (intent.Slots.TryGetValue("scroll", out string? scroll) && scroll.Length > 0)
                {
                    builder.AddStep(
                        SystemActionKind.TriggerScroll,
                        JsonSerializer.Serialize(
                            new TriggerScrollActionJson(nameof(SystemActionKind.TriggerScroll), scroll),
                            PipelinePlannerJsonContext.Default.TriggerScrollActionJson),
                        resources: 0,
                        Array.Empty<string>());
                }
                else if (intent.Slots.TryGetValue("task", out string? task) && task.Length > 0)
                {
                    builder.AddStep(
                        SystemActionKind.StopTask,
                        JsonSerializer.Serialize(
                            new StopTaskActionJson(nameof(SystemActionKind.StopTask), task),
                            PipelinePlannerJsonContext.Default.StopTaskActionJson),
                        resources: 0,
                        Array.Empty<string>());
                }
                else
                {
                    // Neither slot filled (a ScrollControl pattern always
                    // fills one; defensive): clarification is the honest
                    // answer — refuse to guess a scroll or a task.
                    builder.Risks.Add("scroll-control:no-slot-resolved");
                    builder.AddStep(
                        SystemActionKind.StopTask,
                        JsonSerializer.Serialize(
                            new StopTaskActionJson(nameof(SystemActionKind.StopTask), string.Empty),
                            PipelinePlannerJsonContext.Default.StopTaskActionJson),
                        resources: 0,
                        Array.Empty<string>());
                }

                builder.AddReport();
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, "Intent outside the SP-2 frozen table.");
        }

        return builder.Build();
    }

    /// <summary>
    /// Host-driven single-action plan (T-068): wraps one schema-shaped
    /// action JSON into the same five-stage plan the intent templates
    /// produce — the C-22 panel buttons, the T-081 全托管 lane and the
    /// three verbs without an intent row (SetRally/SetStockpile/AskPlayer/
    /// TodoBoard/HeroOrder) enter the pipeline through this door. The
    /// caller's JSON faces the SAME registry gates at execution; the
    /// synthesized intent is display-only attribution
    /// (<see cref="IntentActionMap.AttributionIntent"/>).
    /// </summary>
    public PlannedOrder PrepareSingle(SystemActionKind verb, string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        var intent = new Intent(IntentActionMap.AttributionIntent(verb), $"system:{verb}", new Dictionary<string, string>());
        var builder = new PlanBuilder(intent);
        builder.AddStep(verb, actionJson, resources: 0, Array.Empty<string>());
        builder.AddReport();
        return builder.Build();
    }

    // ------------------------------------------------ threat recon step --

    /// <summary>
    /// Scope-dependent recon step (SP-2 #9): next_wave → QueryWorld{wave};
    /// a known region id → InspectTile{threat} at its anchor; anything
    /// else (current wave included) → LookAround.
    /// </summary>
    private void AddThreatReconStep(PlanBuilder builder, Intent intent)
    {
        string scope = intent.Slots.TryGetValue("scope", out string? scopeSlot) ? scopeSlot : string.Empty;
        if (scope == "next_wave")
        {
            builder.AddStep(
                SystemActionKind.QueryWorld,
                JsonSerializer.Serialize(
                    new QueryWorldActionJson(nameof(SystemActionKind.QueryWorld), "wave", "next", HeadLimit: null),
                    PipelinePlannerJsonContext.Default.QueryWorldActionJson),
                resources: 0,
                Array.Empty<string>());
            return;
        }

        if (scope.Length > 0 && _regionAnchors.TryGetValue(scope, out CellCoord? anchor))
        {
            builder.AddStep(
                SystemActionKind.InspectTile,
                JsonSerializer.Serialize(
                    new InspectTileActionJson(
                        nameof(SystemActionKind.InspectTile),
                        new CellJson(anchor.X, anchor.Y),
                        "threat"),
                    PipelinePlannerJsonContext.Default.InspectTileActionJson),
                resources: 0,
                Array.Empty<string>());
            return;
        }

        builder.AddLookAround();
    }

    // ------------------------------------------------ slot → argument resolution --

    /// <summary>
    /// Resolves one canonical tower designation to a tower entity id.
    /// Accepted forms: <c>ord:N</c> (id-ordered Nth tower), a tower
    /// content id, <c>region+any</c> / <c>region+content</c> (nearest to
    /// the region anchor), <c>cell:x,y</c> (tower standing on the cell).
    /// Unresolvable ⇒ id 0 plus a risk line (schema denies 0 honestly).
    /// </summary>
    private long ResolveTowerTarget(PlanBuilder builder, string designation)
    {
        IReadOnlyList<long> ids = ResolveTowerDesignation(builder, designation, cap: 1);
        return ids.Count > 0 ? ids[0] : 0;
    }

    /// <summary>Resolves one designation to up to <paramref name="cap"/> tower ids (mode rows).</summary>
    private IReadOnlyList<long> ResolveTowerTargets(PlanBuilder builder, string designation, int cap)
    {
        IReadOnlyList<long> ids = ResolveTowerDesignation(builder, designation, cap);
        return ids.Count > 0 ? ids : Array.Empty<long>();
    }

    private IReadOnlyList<long> ResolveTowerDesignation(PlanBuilder builder, string designation, int cap)
    {
        if (_view is not IActionWorldState state)
        {
            builder.Risks.Add($"target-unresolved:{designation}:registry view lacks the tower roster");
            return Array.Empty<long>();
        }

        var towers = state.Towers.OrderBy(tower => tower.Id).ToList();
        if (towers.Count == 0)
        {
            builder.Risks.Add($"target-unresolved:{designation}:no-tower-placed");
            return Array.Empty<long>();
        }

        IEnumerable<TowerLine> ordered = towers;
        if (designation.StartsWith("ord:", StringComparison.Ordinal)
            && int.TryParse(designation["ord:".Length..], System.Globalization.CultureInfo.InvariantCulture, out int ordinal)
            && ordinal >= 1)
        {
            var nth = towers.Skip(ordinal - 1).Take(cap);
            if (!nth.Any())
            {
                builder.Risks.Add($"target-unresolved:{designation}:only-{towers.Count}-towers");
                return Array.Empty<long>();
            }

            return nth.Select(tower => tower.Id).ToArray();
        }

        string contentFilter = designation;
        if (designation == "all")
        {
            ordered = towers;
            contentFilter = string.Empty;
        }
        else if (designation.StartsWith("cell:", StringComparison.Ordinal))
        {
            string[] parts = designation["cell:".Length..].Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out int cx)
                && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int cy))
            {
                var at = towers.Where(tower => tower.Cell.X == cx && tower.Cell.Y == cy).ToList();
                if (at.Count == 0)
                {
                    builder.Risks.Add($"target-unresolved:{designation}:no-tower-at-cell");
                    return Array.Empty<long>();
                }

                return at.Take(cap).Select(tower => tower.Id).ToArray();
            }

            builder.Risks.Add($"target-unresolved:{designation}:malformed-cell");
            return Array.Empty<long>();
        }
        else if (designation.Contains('+'))
        {
            string[] pair = designation.Split('+', 2);
            if (!_regionAnchors.TryGetValue(pair[0], out CellCoord? anchor))
            {
                anchor = _fallbackAnchor;
            }

            ordered = towers.OrderBy(tower => Chebyshev(tower.Cell, anchor));
            contentFilter = pair[1] == "any" ? string.Empty : pair[1];
        }
        else if (designation.Contains(':'))
        {
            // A bare content id — first placed tower of that row.
            ordered = towers;
        }

        if (contentFilter.Length > 0)
        {
            ordered = ordered.Where(tower => tower.ContentId == contentFilter);
        }

        var matched = ordered.Take(cap).ToList();
        if (matched.Count == 0)
        {
            builder.Risks.Add($"target-unresolved:{designation}:no-tower-matches");
            return Array.Empty<long>();
        }

        return matched.Select(tower => tower.Id).ToArray();
    }

    /// <summary>
    /// Resolves one canonical enemy designation to a living enemy id.
    /// Accepted forms: <c>any</c> (first living), <c>ord:N</c> (Nth living,
    /// spawn order), an enemy content id, a region id (nearest living to
    /// the region anchor), <c>side:left|right|front|back</c> (relative to
    /// the fallback anchor) and <c>cell:x,y</c> (nearest living).
    /// </summary>
    private long ResolveEnemyTarget(PlanBuilder builder, string designation)
    {
        if (_view is not IActionWorldState state)
        {
            builder.Risks.Add($"target-unresolved:{designation}:registry view lacks the enemy roster");
            return 0;
        }

        var enemies = state.LivingEnemies;
        if (enemies.Count == 0)
        {
            builder.Risks.Add($"target-unresolved:{designation}:no-living-enemy");
            return 0;
        }

        if (designation == "any" || designation.Length == 0)
        {
            return enemies[0].Id;
        }

        if (designation.StartsWith("ord:", StringComparison.Ordinal)
            && int.TryParse(designation["ord:".Length..], System.Globalization.CultureInfo.InvariantCulture, out int ordinal)
            && ordinal >= 1)
        {
            if (ordinal > enemies.Count)
            {
                builder.Risks.Add($"target-unresolved:{designation}:only-{enemies.Count}-enemies");
                return 0;
            }

            return enemies[ordinal - 1].Id;
        }

        if (designation.StartsWith("side:", StringComparison.Ordinal))
        {
            string side = designation["side:".Length..];
            var anchor = _fallbackAnchor;
            IEnumerable<EnemyLine> filtered = side switch
            {
                "left" => enemies.Where(enemy => CellOf(enemy.X) < anchor.X),
                "right" => enemies.Where(enemy => CellOf(enemy.X) >= anchor.X),
                "front" => enemies.Where(enemy => CellOf(enemy.Y) < anchor.Y),
                "back" => enemies.Where(enemy => CellOf(enemy.Y) >= anchor.Y),
                _ => null!,
            };
            EnemyLine? first = filtered?.FirstOrDefault();
            if (first is null)
            {
                builder.Risks.Add($"target-unresolved:{designation}:no-enemy-on-side");
                return 0;
            }

            return first.Id;
        }

        if (designation.StartsWith("cell:", StringComparison.Ordinal))
        {
            string[] parts = designation["cell:".Length..].Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out int cx)
                && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int cy))
            {
                return enemies
                    .OrderBy(enemy => Math.Max(Math.Abs(CellOf(enemy.X) - cx), Math.Abs(CellOf(enemy.Y) - cy)))
                    .First().Id;
            }

            builder.Risks.Add($"target-unresolved:{designation}:malformed-cell");
            return 0;
        }

        if (_regionAnchors.TryGetValue(designation, out CellCoord? regionAnchor))
        {
            return enemies
                .OrderBy(enemy => Math.Max(Math.Abs(CellOf(enemy.X) - regionAnchor.X), Math.Abs(CellOf(enemy.Y) - regionAnchor.Y)))
                .First().Id;
        }

        EnemyLine? byContent = enemies.FirstOrDefault(enemy => enemy.ContentId == designation);
        if (byContent is not null)
        {
            return byContent.Id;
        }

        builder.Risks.Add($"target-unresolved:{designation}:no-enemy-matches");
        return 0;
    }

    /// <summary>
    /// Resolves one reroute endpoint: a region id → the building nearest
    /// its anchor (Chebyshev, ties by lowest id); a missing supplier side
    /// → the first harvester, a missing receiver side → the warehouse.
    /// </summary>
    private long ResolveEndpoint(PlanBuilder builder, string slotValue, bool supplierSide)
    {
        if (_view is not IActionWorldState state)
        {
            builder.Risks.Add($"endpoint-unresolved:{slotValue}:registry view lacks the building roster");
            return 0;
        }

        var buildings = state.Buildings;
        if (buildings.Count == 0)
        {
            builder.Risks.Add($"endpoint-unresolved:{slotValue}:no-building-placed");
            return 0;
        }

        if (slotValue.Length > 0 && _regionAnchors.TryGetValue(slotValue, out CellCoord? anchor))
        {
            return buildings
                .OrderBy(building => Chebyshev(building.Cell, anchor))
                .ThenBy(building => building.Id)
                .First().Id;
        }

        if (slotValue.Length == 0)
        {
            SimBuildingKind kind = supplierSide ? SimBuildingKind.Harvester : SimBuildingKind.Warehouse;
            BuildingLine? byKind = buildings.FirstOrDefault(building => building.Kind == kind);
            if (byKind is null)
            {
                builder.Risks.Add($"endpoint-unresolved:default:{kind}");
                return 0;
            }

            return byKind.Id;
        }

        builder.Risks.Add($"endpoint-unresolved:{slotValue}:not-a-known-region");
        return 0;
    }

    private static int Chebyshev(CellCoord a, CellCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static int CellOf(float px) => (int)Math.Floor(px / EraCore.Sim.Simulation.CellSizePx);

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

    private static int ReadIntSlot(Intent intent, string slot, int defaultValue)
    {
        if (intent.Slots.TryGetValue(slot, out string? raw)
            && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return defaultValue;
    }

    private static double ReadDoubleSlot(Intent intent, string slot, double defaultValue)
    {
        if (intent.Slots.TryGetValue(slot, out string? raw)
            && double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        return defaultValue;
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
[JsonSerializable(typeof(UpgradeTowerActionJson))]
[JsonSerializable(typeof(SellTowerActionJson))]
[JsonSerializable(typeof(FocusFireActionJson))]
[JsonSerializable(typeof(SetTowerModeActionJson))]
[JsonSerializable(typeof(SetRallyActionJson))]
[JsonSerializable(typeof(RerouteLogisticsActionJson))]
[JsonSerializable(typeof(QueryWorldActionJson))]
[JsonSerializable(typeof(InspectTileActionJson))]
[JsonSerializable(typeof(SpawnScoutActionJson))]
[JsonSerializable(typeof(TriggerScrollActionJson))]
[JsonSerializable(typeof(StopTaskActionJson))]
[JsonSerializable(typeof(UpgradeTowerCommandJson))]
[JsonSerializable(typeof(SellTowerCommandJson))]
[JsonSerializable(typeof(HeroOrderCommandJson))]
[JsonSerializable(typeof(AssignCarrierCommandJson))]
public sealed partial class PipelinePlannerJsonContext : JsonSerializerContext
{
}
