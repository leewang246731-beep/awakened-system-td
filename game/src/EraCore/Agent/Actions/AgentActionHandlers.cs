using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Actions;

// T-068 (C-13 全量) 13 个新落地动作 handler（F-065~F-068）：侦察组
// InspectTile/QueryWorld（IActionWorldState 只读投影）、指令组 SetRally/
// SetStockpile/FocusFire/SetTowerMode/TriggerScroll 与物流组
// RerouteLogistics（AgentActionState 治理态——Sim 无对应动词的动词在
// Agent 层落账，反向变更仍走 SimCommand 泳道，03-decomposition §1.2）、
// 管理组 AskPlayer/TodoBoard/SetAlarm/SpawnScout/StopTask（任务句柄统一
// 空间）。全部经注册表分发（RL6）；玩家可见文本一律过
// ReportToPlayerAction.FilterSuspiciousLinks（注入防护③，F-096）。

/// <summary>InspectTile report payload (one aspect of one cell, G.2).</summary>
/// <param name="Cell">The inspected cell.</param>
/// <param name="Aspect">Which aspect ran (stats/threat/supply).</param>
/// <param name="Lines">Machine-ordered fact lines (snapshot data only, no invented figures).</param>
public sealed record InspectTileReport(CellCoord Cell, string Aspect, IReadOnlyList<string> Lines);

/// <summary>
/// InspectTile handler (recon, allow, 0 pt, E1; G.2: cell + aspect
/// stats|threat|supply). Reads the world state projection only.
/// </summary>
public sealed class InspectTileAction
{
    /// <summary>Nearby-enemy scan radius for the threat aspect, in cells (Chebyshev).</summary>
    public const int ThreatScanRadiusCells = 5; // ci-allow: R7 T-068 侦察半径工程取值（非平衡数值；威胁面读数用）

    /// <param name="view">World state projection (read-only).</param>
    /// <param name="cell">The inspected cell.</param>
    /// <param name="aspect">stats|threat|supply (schema-guaranteed).</param>
    /// <returns>The report payload, or null with <paramref name="refusal"/> set when the cell is outside the grid.</returns>
    public InspectTileReport? Run(IActionWorldState view, CellCoord cell, string aspect, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cell);
        if (!view.IsInsideGrid(cell))
        {
            refusal = $"cell-out-of-bounds:({cell.X},{cell.Y}) is outside the level grid";
            return null;
        }

        var lines = new List<string>();
        switch (aspect)
        {
            case "stats":
                view.IsBuildable(cell, out string? rejection);
                lines.Add($"buildable={(rejection is null).ToString().ToLowerInvariant()}"
                    + (rejection is null ? string.Empty : $":{rejection}"));
                lines.Add($"occupied={view.IsOccupied(cell).ToString().ToLowerInvariant()}");
                break;

            case "threat":
                int nearby = view.LivingEnemies.Count(enemy =>
                    Math.Max(Math.Abs(CellOf(enemy.X) - cell.X), Math.Abs(CellOf(enemy.Y) - cell.Y)) <= ThreatScanRadiusCells);
                lines.Add($"enemies-within-{ThreatScanRadiusCells}={nearby}");
                lines.Add($"enemies-total={view.LivingEnemies.Count}");
                break;

            case "supply":
                foreach (ResourceStockLine resource in view.Resources)
                {
                    lines.Add($"{resource.Resource.Value}={resource.Amount.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                break;

            default:
                refusal = $"aspect-unknown:{aspect}";
                return null;
        }

        refusal = null;
        return new InspectTileReport(cell, aspect, lines);
    }

    private static int CellOf(float px) => (int)Math.Floor(px / EraCore.Sim.Simulation.CellSizePx);
}

/// <summary>QueryWorld report payload (key-scoped world facts, head_limit-bounded).</summary>
/// <param name="Key">wave|resource|threat|paths.</param>
/// <param name="Scope">Caller scope token (journal identity).</param>
/// <param name="Lines">Fact lines, at most head_limit of them.</param>
public sealed record QueryWorldReport(string Key, string Scope, IReadOnlyList<string> Lines);

/// <summary>
/// QueryWorld handler (recon, allow, 0 pt, E1; G.2: key wave|resource|threat|
/// paths + scope + head_limit≤20). Reads the world state projection only;
/// every line carries snapshot figures, never predictions.
/// </summary>
public sealed class QueryWorldAction
{
    /// <summary>Default head_limit when the optional field is absent (G.2 cap 20).</summary>
    public const int DefaultHeadLimit = 20; // ci-allow: R7 T-068 G.2 head_limit≤20 缺省取上限（冻结域常量）

    /// <param name="view">World state projection (read-only).</param>
    /// <param name="key">wave|resource|threat|paths (schema-guaranteed).</param>
    /// <param name="scope">Scope token carried onto the payload.</param>
    /// <param name="headLimit">Maximum line count (schema ≤ 20; caller normalizes null to the cap).</param>
    public QueryWorldReport Run(IActionWorldState view, string key, string scope, int? headLimit)
    {
        ArgumentNullException.ThrowIfNull(view);
        int limit = Math.Max(1, Math.Min(headLimit ?? DefaultHeadLimit, DefaultHeadLimit));
        var lines = new List<string>();
        switch (key)
        {
            case "wave":
                lines.Add($"wave={view.CurrentWave}");
                lines.Add($"enemies-present={view.EnemiesPresent}");
                lines.Add($"enemy-hp-present={view.EnemyHpPresent}");
                break;

            case "resource":
                foreach (ResourceStockLine resource in view.Resources)
                {
                    lines.Add($"{resource.Resource.Value}={resource.Amount.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                break;

            case "threat":
                lines.Add($"enemies-present={view.EnemiesPresent}");
                lines.Add($"enemy-hp-present={view.EnemyHpPresent}");
                foreach (EnemyLine enemy in view.LivingEnemies.Take(limit))
                {
                    lines.Add($"enemy:{enemy.Id}:{enemy.ContentId}:hp={enemy.Hp}{(enemy.IsBoss ? ":boss" : string.Empty)}");
                }

                break;

            case "paths":
                lines.Add($"carriers={view.CarrierCount}");
                lines.Add($"towers={view.Towers.Count}");
                break;

            default:
                lines.Add($"key-unknown:{key}");
                break;
        }

        return new QueryWorldReport(key, scope, lines.Take(limit).ToArray());
    }
}

/// <summary>SetRally acknowledgement payload.</summary>
/// <param name="Lane">Language-neutral lane id.</param>
/// <param name="Stance">hold|fall|push.</param>
public sealed record SetRallyAck(string Lane, string Stance);

/// <summary>
/// SetRally handler (construction, ask, 2 pt, E2; G.2: lane + stance): the
/// rally directive is agent-owned combat policy — recorded in the governed
/// state, consumed by the combat layer through the same seam.
/// </summary>
public sealed class SetRallyAction
{
    /// <param name="state">Agent-owned governed state.</param>
    /// <param name="lane">Language-neutral lane id.</param>
    /// <param name="stance">hold|fall|push (schema-guaranteed).</param>
    public SetRallyAck Run(AgentActionState state, string lane, string stance)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.SetRally(lane, stance);
        return new SetRallyAck(lane, stance);
    }
}

/// <summary>SetStockpile acknowledgement payload.</summary>
/// <param name="ItemId">Resource content id.</param>
/// <param name="MinAmount">Reserve floor recorded.</param>
public sealed record SetStockpileAck(string ItemId, double MinAmount);

/// <summary>
/// SetStockpile handler (logistics, ask, 1 pt, E2; G.2: item_id +
/// min_amount): the reserve floor is agent-owned logistics policy. The item
/// must already live in the era pool's key domain — a floor for an item the
/// pool never carries is a target error, not a guess.
/// </summary>
public sealed class SetStockpileAction
{
    /// <param name="state">Agent-owned governed state.</param>
    /// <param name="view">World state projection (item-key validation).</param>
    /// <param name="itemId">Resource content id.</param>
    /// <param name="minAmount">Reserve floor (schema-guaranteed ≥ 0).</param>
    /// <returns>The ack, or null with <paramref name="refusal"/> set when the item is unknown.</returns>
    public SetStockpileAck? Run(AgentActionState state, IActionWorldState view, string itemId, double minAmount, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(view);
        bool known = view.Resources.Any(resource => resource.Resource.Value == itemId)
            || view.CostCurrency.Value == itemId;
        if (!known)
        {
            refusal = $"item-unknown:{itemId} is not carried by the era pool; no floor was recorded";
            return null;
        }

        state.SetStockpileFloor(itemId, minAmount);
        refusal = null;
        return new SetStockpileAck(itemId, minAmount);
    }
}

/// <summary>FocusFire acknowledgement payload.</summary>
/// <param name="TargetId">Enemy entity id now focused.</param>
/// <param name="ContentId">Content id of the focused enemy.</param>
public sealed record FocusFireAck(long TargetId, string ContentId);

/// <summary>
/// FocusFire handler (combat, ask, 1 pt, E2; G.2: target_id, 持续至目标死亡
/// 或新指令): the focus directive is agent-owned combat policy recorded in
/// the governed state. The target must be a LIVING enemy right now — a dead
/// or unknown id is a target error, never a silent redirect.
/// </summary>
public sealed class FocusFireAction
{
    /// <param name="state">Agent-owned governed state.</param>
    /// <param name="view">World state projection (enemy roster).</param>
    /// <param name="targetId">Enemy entity id.</param>
    /// <returns>The ack, or null with <paramref name="refusal"/> set when no living enemy carries the id.</returns>
    public FocusFireAck? Run(AgentActionState state, IActionWorldState view, long targetId, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(view);
        EnemyLine? enemy = view.LivingEnemies.FirstOrDefault(candidate => candidate.Id == targetId);
        if (enemy is null)
        {
            refusal = $"target-invalid:{targetId} is not a living enemy (集火需要现役目标)";
            return null;
        }

        state.SetFocusTarget(targetId);
        refusal = null;
        return new FocusFireAck(targetId, enemy.ContentId);
    }
}

/// <summary>SetTowerMode acknowledgement payload.</summary>
/// <param name="TargetIds">Tower entity ids the mode now applies to.</param>
/// <param name="Mode">first|last|strong|closest.</param>
public sealed record SetTowerModeAck(IReadOnlyList<long> TargetIds, string Mode);

/// <summary>
/// SetTowerMode handler (combat, ask, 1 pt, E2; G.2: target_id[]≤6 + mode):
/// targeting modes are agent-owned combat policy per tower. Every id must be
/// a placed tower — partial application is refused (a card that quietly
/// dropped half its targets would lie).
/// </summary>
public sealed class SetTowerModeAction
{
    /// <param name="state">Agent-owned governed state.</param>
    /// <param name="view">World state projection (tower roster).</param>
    /// <param name="targetIds">1..6 tower entity ids (schema-guaranteed).</param>
    /// <param name="mode">first|last|strong|closest (schema-guaranteed).</param>
    /// <returns>The ack, or null with <paramref name="refusal"/> set when any id is not a tower.</returns>
    public SetTowerModeAck? Run(AgentActionState state, IActionWorldState view, IReadOnlyList<long> targetIds, string mode, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(targetIds);
        var unknown = new List<string>();
        foreach (long id in targetIds)
        {
            if (view.Towers.All(tower => tower.Id != id))
            {
                unknown.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (unknown.Count > 0)
        {
            refusal = "target-invalid:not-a-tower:" + string.Join(",", unknown);
            return null;
        }

        foreach (long id in targetIds)
        {
            state.SetTowerMode(id, mode);
        }

        refusal = null;
        return new SetTowerModeAck(targetIds, mode);
    }
}

/// <summary>TriggerScroll acknowledgement payload.</summary>
/// <param name="ScrollId">Content id of the triggered scroll.</param>
/// <param name="ReadyAgainTick">Tick the scroll may fire again (3 s cooldown).</param>
public sealed record TriggerScrollAck(string ScrollId, long ReadyAgainTick);

/// <summary>
/// TriggerScroll handler (combat, ask, 1 pt + 3 s internal cooldown, E2;
/// G.2 限已装备卷轴): armed-set + cooldown are enforced by the governed
/// state; the activation record is the input the C-18 runtime (T-071) binds
/// effects to.
/// </summary>
public sealed class TriggerScrollAction
{
    /// <param name="state">Agent-owned governed state (armed set + cooldown).</param>
    /// <param name="scrollId">Scroll content id.</param>
    /// <returns>The ack, or null with <paramref name="refusal"/> set (not equipped / cooling down).</returns>
    public TriggerScrollAck? Run(AgentActionState state, string scrollId, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        string? refused = state.TryTriggerScroll(scrollId, out long readyAgain);
        if (refused is not null)
        {
            refusal = refused;
            return null;
        }

        refusal = null;
        return new TriggerScrollAck(scrollId, readyAgain);
    }
}

/// <summary>RerouteLogistics validated order payload (the executor enqueues the carrier).</summary>
/// <param name="From">Supplier building entity id (validated against the building roster).</param>
/// <param name="To">Receiver building entity id (validated).</param>
/// <param name="Item">Resource content id of the rerouted flow.</param>
/// <param name="Rate">Priority share 0..1 (schema-guaranteed).</param>
public sealed record RerouteLogisticsPlan(long From, long To, string Item, double Rate);

/// <summary>
/// RerouteLogistics handler (logistics, ask, 1 pt, E2; G.2: from/to/item/
/// rate): validates both endpoints against the building roster and records
/// the flow policy, then hands the executor a validated order — the actual
/// carrier trip enters the simulation through the assign-carrier SimCommand
/// (the only lawful mutation path, 03-decomposition §1.2), where the sim
/// re-validates the supplier/receiver lane.
/// </summary>
public sealed class RerouteLogisticsAction
{
    /// <param name="view">World state projection (building roster).</param>
    /// <param name="from">Supplier building entity id.</param>
    /// <param name="to">Receiver building entity id.</param>
    /// <param name="item">Resource content id.</param>
    /// <param name="rate">Priority share 0..1.</param>
    /// <returns>The validated order, or null with <paramref name="refusal"/> set.</returns>
    public RerouteLogisticsPlan? Run(IActionWorldState view, long from, long to, string item, double rate, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Buildings.All(building => building.Id != from))
        {
            refusal = $"target-invalid:from-unknown:{from} is not a placed building";
            return null;
        }

        if (view.Buildings.All(building => building.Id != to))
        {
            refusal = $"target-invalid:to-unknown:{to} is not a placed building";
            return null;
        }

        if (from == to)
        {
            refusal = $"target-invalid:lane-degenerate:from==to=={from} — a reroute needs two distinct endpoints";
            return null;
        }

        refusal = null;
        return new RerouteLogisticsPlan(from, to, item, rate);
    }
}

/// <summary>AskPlayer card payload (the C-22 presenter renders it as the question card).</summary>
/// <param name="Question">Filtered question text.</param>
/// <param name="Options">Filtered option labels plus the mandatory 「其他」 tail (G.2 系统附其他).</param>
/// <param name="MultiSelect">Whether multiple options may be chosen.</param>
public sealed record AskPlayerCard(string Question, IReadOnlyList<string> Options, bool MultiSelect);

/// <summary>
/// AskPlayer handler (management, allow, 0 pt, E1; G.2: question + options
/// 2~4 label≤8 + multi_select): every player-visible string passes the
/// suspicious-link filter before it reaches the card (注入防护③: 汇报/提问
/// 文本过滤), and the mandatory 「其他」 option is appended — the player can
/// always answer outside the offered list.
/// </summary>
public sealed class AskPlayerAction
{
    /// <summary>The mandatory free-text option appended after the offered list (G.2).</summary>
    public const string OtherOptionLabel = "其他";

    /// <param name="question">Question text.</param>
    /// <param name="options">2..4 option labels (schema-guaranteed).</param>
    /// <param name="multiSelect">Multi-select flag.</param>
    public AskPlayerCard Run(string question, IReadOnlyList<string> options, bool multiSelect)
    {
        ArgumentException.ThrowIfNullOrEmpty(question);
        ArgumentNullException.ThrowIfNull(options);
        var filtered = new List<string>(options.Count + 1) { ReportToPlayerAction.FilterSuspiciousLinks(question) };
        // The filter runs per label; the label tail (其他) is system copy and
        // needs no filtering, it is appended verbatim.
        filtered.AddRange(options.Select(ReportToPlayerAction.FilterSuspiciousLinks));
        filtered.Add(OtherOptionLabel);
        return new AskPlayerCard(filtered[0], filtered.Skip(1).ToArray(), multiSelect);
    }
}

/// <summary>TodoBoard acknowledgement payload (the full rewritten board).</summary>
/// <param name="Todos">The board as written (全量重写, G.2).</param>
public sealed record TodoBoardSnapshot(IReadOnlyList<string> Todos);

/// <summary>
/// TodoBoard handler (management, allow, 0 pt, E2; G.2: todos[] 全量重写):
/// rewrites the whole board — absent entries are gone by contract, and
/// every line passes the link filter (player-visible text).
/// </summary>
public sealed class TodoBoardAction
{
    /// <param name="state">Agent-owned governed state.</param>
    /// <param name="todos">The full new board.</param>
    public TodoBoardSnapshot Run(AgentActionState state, IReadOnlyList<string> todos)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(todos);
        var filtered = todos.Select(ReportToPlayerAction.FilterSuspiciousLinks).ToArray();
        state.WriteTodos(filtered);
        return new TodoBoardSnapshot(filtered);
    }
}

/// <summary>SetAlarm acknowledgement payload.</summary>
/// <param name="TaskId">Unified task handle.</param>
/// <param name="DueTick">Tick the alarm rings (30 Hz domain).</param>
/// <param name="Reason">Filtered reason text.</param>
public sealed record SetAlarmAck(string TaskId, long DueTick, string Reason);

/// <summary>
/// SetAlarm handler (management, allow, 0 pt, E1; G.2: after_seconds
/// [30,600] + reason): registers the alarm in the governed task roster,
/// converting seconds to the deterministic K-1 tick domain. The reason is
/// player-visible text and passes the link filter.
/// </summary>
public sealed class SetAlarmAction
{
    /// <param name="state">Agent-owned governed state (task roster + tick).</param>
    /// <param name="afterSeconds">Clamped delay in seconds.</param>
    /// <param name="reason">Reason text.</param>
    public SetAlarmAck Run(AgentActionState state, double afterSeconds, string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        string filtered = ReportToPlayerAction.FilterSuspiciousLinks(reason);
        string taskId = state.AddAlarm(afterSeconds, filtered);
        AgentTaskRecord record = state.Tasks.Single(task => task.TaskId == taskId);
        return new SetAlarmAck(taskId, record.DueTick, filtered);
    }
}

/// <summary>SpawnScout acknowledgement payload.</summary>
/// <param name="TaskId">Unified task handle.</param>
/// <param name="Lane">Language-neutral lane id patrolled.</param>
/// <param name="DurationSeconds">Patrol duration as accepted (schema-clamped [15,120]).</param>
/// <param name="UntilTick">Tick the patrol stands down.</param>
public sealed record SpawnScoutAck(string TaskId, string Lane, double DurationSeconds, long UntilTick);

/// <summary>
/// SpawnScout handler (management/patrol, allow, 1 pt, E2 patrol tier — the
/// registry's unlock-era gate returns ErrCapabilityLocked in E1 with
/// clarifying copy, N9/A2; G.2: lane + duration [15,120]): registers the
/// patrol in the governed task roster. The recon reports a patrol produces
/// ride the same report channel (T-070's WaveEnd hook consumes expiries).
/// </summary>
public sealed class SpawnScoutAction
{
    /// <param name="state">Agent-owned governed state (task roster + tick).</param>
    /// <param name="lane">Language-neutral lane id.</param>
    /// <param name="durationSeconds">Patrol duration (schema-clamped [15,120]).</param>
    public SpawnScoutAck Run(AgentActionState state, string lane, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(lane);
        string taskId = state.AddPatrol(lane, durationSeconds);
        AgentTaskRecord record = state.Tasks.Single(task => task.TaskId == taskId);
        return new SpawnScoutAck(taskId, lane, durationSeconds, record.UntilTick);
    }
}

/// <summary>StopTask acknowledgement payload.</summary>
/// <param name="TaskId">Handle or <c>"all"</c> as requested.</param>
/// <param name="StoppedIds">Handles actually stopped, roster order.</param>
public sealed record StopTaskAck(string TaskId, IReadOnlyList<string> StoppedIds);

/// <summary>
/// StopTask handler (management, allow, 0 pt, E2; G.2: task_id 统一句柄):
/// stops alarms and patrols through the unified handle space. An unknown
/// handle is a target error — nothing is stopped and nothing is pretended.
/// </summary>
public sealed class StopTaskAction
{
    /// <param name="state">Agent-owned governed state (task roster).</param>
    /// <param name="taskId">Unified handle or <c>"all"</c>.</param>
    /// <returns>The ack, or null with <paramref name="refusal"/> set when no task matches.</returns>
    public StopTaskAck? Run(AgentActionState state, string taskId, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        IReadOnlyList<string> stopped = state.StopTasks(taskId);
        if (stopped.Count == 0)
        {
            refusal = $"target-invalid:task-unknown:{taskId} matches no alarm or patrol; nothing was stopped";
            return null;
        }

        refusal = null;
        return new StopTaskAck(taskId, stopped);
    }
}

/// <summary>
/// Argument parsing for the T-068 handler payloads (defensive re-reads
/// after the schema gate — same posture as the T-065 handlers).
/// </summary>
internal static class HandlerArgumentReader
{
    public static bool TryReadString(JsonElement root, string field, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(field, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(element.GetString())
            && (value = element.GetString()!) is not null;
    }

    public static bool TryReadNumber(JsonElement root, string field, out double value)
    {
        value = 0;
        return root.TryGetProperty(field, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }

    public static bool TryReadInt64(JsonElement root, string field, out long value)
    {
        value = 0;
        return root.TryGetProperty(field, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    public static bool TryReadCell(JsonElement root, string field, out CellCoord cell)
    {
        cell = null!;
        if (!root.TryGetProperty(field, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("x", out JsonElement x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int cx)
            || !element.TryGetProperty("y", out JsonElement y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out int cy))
        {
            return false;
        }

        cell = new CellCoord(cx, cy);
        return true;
    }

    public static bool TryReadStringArray(JsonElement root, string field, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (!root.TryGetProperty(field, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                return false;
            }

            parsed.Add(item.GetString()!);
        }

        values = parsed;
        return true;
    }

    public static bool TryReadInt64Array(JsonElement root, string field, out IReadOnlyList<long> values)
    {
        values = Array.Empty<long>();
        if (!root.TryGetProperty(field, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<long>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out long id))
            {
                return false;
            }

            parsed.Add(id);
        }

        values = parsed;
        return true;
    }

    /// <summary>
    /// Reads the AskPlayer options field: an array of option objects, each
    /// carrying one bounded <c>label</c> (K-3 OptionArray shape — the labels
    /// are the player-visible strings, never free-form text objects).
    /// </summary>
    public static bool TryReadOptionLabels(JsonElement root, string field, out IReadOnlyList<string> labels)
    {
        labels = Array.Empty<string>();
        if (!root.TryGetProperty(field, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out JsonElement label)
                || label.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(label.GetString()))
            {
                return false;
            }

            parsed.Add(label.GetString()!);
        }

        labels = parsed;
        return true;
    }
}
