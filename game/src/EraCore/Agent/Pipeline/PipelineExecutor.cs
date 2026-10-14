using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 第 5 段执行通道（F-065/F-071）：动作注册表是唯一合法门
// （RL6），建造步在注册表 BuildPlan 校验通过后经 SimCommand 泳道真正落
// 子（03-decomposition §1.2 快照原则——agent 层永不直改模拟内部）。动作层
// 失败的自纠（≤2 次、带错误重入）由 <see cref="AgentPipeline"/> 的重试循环
// 驱动，本类提供错误→新动作 JSON 的调整器。
//
// T-068 (C-13 全量) 三项扩展：①UpgradeTower/SellTower/HeroOrder 三动词走
// 同一 SimCommand 泳道（T-067 的 upgrade-tower/sell-tower/hero-order 命令
// 动词——仿真侧全量记账（武器重建/分组替换/残骸占格），注册表
// ValidateOnly 先行把关）；②RerouteLogistics 在注册表校验+政策落账后经
// assign-carrier 命令落子；③TryAdjust 分发表=全动作统一自纠入口
// （BuildPlan 原有 + SetTowerMode 剔除失效塔 + FocusFire 改打首个现役敌）。

/// <summary>
/// Stage-5 execution lane (T-053; full 19-verb coverage since T-068):
/// dispatches one plan step through the C-03 registry (closed-schema gate,
/// unlock-era gate, PreAction hooks, handlers) and turns the validated
/// payloads into real mutations through the K-6 SimCommand queue — the
/// only lawful mutation path (build placements, estate verbs, hero orders,
/// carrier assignments). A step is FAILED (drives self-correction) when
/// the draft is unaffordable, has no buildable cell, only part of the
/// requested cells are placeable, or the sim refused a queued command —
/// the retry loop then re-enters with an adjusted payload
/// (<see cref="TryAdjust"/>).
/// </summary>
public sealed class PipelineExecutor : IActionExecutor
{
    /// <summary>Rejection code when the draft has no buildable cell at all.</summary>
    public const string NoBuildableCells = "no-buildable-cells";

    /// <summary>Rejection code prefix when only part of the requested cells are placeable.</summary>
    public const string CellsRejectedPrefix = "cells-rejected";

    /// <summary>Rejection code prefix when the stock cannot cover the buildable draft.</summary>
    public const string InsufficientResourcesPrefix = "insufficient-resources";

    /// <summary>Rejection code prefix when the sim refused one of the queued placements.</summary>
    public const string SimRejectedPrefix = "sim-rejected";

    private readonly ActionRegistry _registry;
    private readonly Simulation _simulation;
    private readonly IActionWorldView _view;
    private readonly Func<CellCoord>? _heroAdvanceCell;

    /// <param name="registry">The C-03 registry (sole lawful action door).</param>
    /// <param name="simulation">The live simulation placements enter through SimCommand.</param>
    /// <param name="view">Read-only world view (self-correction re-reads state).</param>
    /// <param name="heroAdvanceCell">
    /// Engine-side hero advance cell (wave entry) the move order aims at;
    /// null keeps a move command without coordinates — the sim refuses it
    /// honestly (G.2: the K-3 schema itself carries none).
    /// </param>
    public PipelineExecutor(
        ActionRegistry registry,
        Simulation simulation,
        IActionWorldView view,
        Func<CellCoord>? heroAdvanceCell = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(view);
        _registry = registry;
        _simulation = simulation;
        _view = view;
        _heroAdvanceCell = heroAdvanceCell;
    }

    /// <summary>
    /// Runs one step's action JSON: registry validation first, then the
    /// verb-specific execution. Returns the registry outcome enriched by the
    /// placement phase (BuildPlanDraft payload on success).
    /// </summary>
    public ActionOutcome InvokeStep(SystemActionKind verb, string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);

        // Verbs whose mutations ride the SimCommand lane: registry gates
        // (schema + era) validate first, the sim's own verb applies the
        // FULL bookkeeping (weapon rebuild / group swap / ruin occupancy /
        // carrier trip) — the agent layer never mutates sim internals.
        return verb switch
        {
            SystemActionKind.UpgradeTower => RunSimLaneCommand(
                verb, actionJson, json => JsonSerializer.Serialize(
                    ReadUpgradeArguments(json), PipelinePlannerJsonContext.Default.UpgradeTowerCommandJson)),
            SystemActionKind.SellTower => RunSimLaneCommand(
                verb, actionJson, json => JsonSerializer.Serialize(
                    ReadSellArguments(json), PipelinePlannerJsonContext.Default.SellTowerCommandJson)),
            SystemActionKind.HeroOrder => RunSimLaneCommand(
                verb, actionJson, json => JsonSerializer.Serialize(
                    ReadHeroOrderArguments(json), PipelinePlannerJsonContext.Default.HeroOrderCommandJson)),
            SystemActionKind.RerouteLogistics => RunReroute(actionJson),
            _ => RunRegistryStep(verb, actionJson),
        };
    }

    private ActionOutcome RunRegistryStep(SystemActionKind verb, string actionJson)
    {
        ActionOutcome outcome = _registry.Invoke(actionJson);
        if (outcome.Result.Status != ActionResultStatus.Ok || verb != SystemActionKind.BuildPlan)
        {
            return outcome;
        }

        if (outcome.Payload is not BuildPlanDraft draft)
        {
            return new ActionOutcome(
                new ActionResult(ActionResultStatus.Err, "build-plan-draft-missing"), null);
        }

        int buildable = draft.Steps.Count(step => step.Buildable);
        if (buildable == 0)
        {
            return Failed(NoBuildableCells, draft);
        }

        if (!draft.Affordable)
        {
            return Failed($"{InsufficientResourcesPrefix}:need-{draft.TotalCost:0.#}-have-{draft.AvailableCurrency:0.#}", draft);
        }

        if (buildable < draft.Steps.Count)
        {
            string rejected = string.Join(
                ",", draft.Steps.Where(step => !step.Buildable).Select(step => $"{step.Cell.X},{step.Cell.Y}:{step.Rejection}"));
            return Failed($"{CellsRejectedPrefix}:{rejected}", draft);
        }

        return PlaceBuildableCells(draft);
    }

    /// <summary>Frozen K-5 stage-5 port surface.</summary>
    public ActionResult Execute(SystemActionKind kind, string actionJson) => InvokeStep(kind, actionJson).Result;

    /// <summary>
    /// Unified self-correction adjuster (错误重入, T-068 全动作统一): builds
    /// the next payload for a failed step from the CURRENT world state, or
    /// returns null when no adjustment can help — the retry loop then gives
    /// up and the reporter tells the truth (自纠 ≤2 次后如实汇报). Per-verb
    /// adjustments:
    /// <list type="bullet">
    /// <item>BuildPlan — drop rejected cells, cap by affordability (T-053);</item>
    /// <item>SetTowerMode — drop tower ids that are no longer placed
    /// (dissolved towers leave the roster mid-wave);</item>
    /// <item>FocusFire — retarget the first living enemy (the planned
    /// target died between plan and execution).</item>
    /// </list>
    /// Every other verb has no meaningful payload fix — an unfixable
    /// failure reports honestly on the first attempt.
    /// </summary>
    /// <param name="verb">The failed step's verb.</param>
    /// <param name="actionJson">The failed step's action JSON.</param>
    /// <param name="error">The failure cause of the previous attempt.</param>
    public string? TryAdjust(SystemActionKind verb, string actionJson, string error)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        ArgumentNullException.ThrowIfNull(error);
        return verb switch
        {
            SystemActionKind.BuildPlan => AdjustBuildPlan(actionJson, error),
            SystemActionKind.SetTowerMode => AdjustSetTowerMode(actionJson, error),
            SystemActionKind.FocusFire => AdjustFocusFire(actionJson, error),
            _ => null,
        };
    }

    /// <summary>
    /// BuildPlan adjuster: builds the next payload from the CURRENT world
    /// state — rejected cells (occupied/terrain) drop out, and when the
    /// stock cannot cover the rest the affordable prefix (plan order) is
    /// taken. Returns null when no adjustment can help (nothing buildable /
    /// nothing affordable) — the retry loop then gives up and the reporter
    /// tells the truth.
    /// </summary>
    /// <param name="actionJson">The failed step's action JSON.</param>
    /// <param name="error">The failure cause of the previous attempt.</param>
    public string? AdjustBuildPlan(string actionJson, string error)
    {
        if (error.StartsWith(NoBuildableCells, StringComparison.Ordinal))
        {
            return null; // nothing left to place
        }

        BuildPlanActionJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(actionJson, PipelinePlannerJsonContext.Default.BuildPlanActionJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.Cells.Count == 0)
        {
            return null;
        }

        // Re-read live state: drop cells that are no longer placeable, keep
        // the rest in plan order; cap by affordability when required.
        bool capByAffordability = error.StartsWith(InsufficientResourcesPrefix, StringComparison.Ordinal)
            || error.StartsWith(SimRejectedPrefix, StringComparison.Ordinal);
        _view.TryGetTowerCost(parsed.TowerId, out double unitCost);

        var kept = new List<CellJson>();
        double total = 0;
        double available = _view.ResourceAmount(_view.CostCurrency);
        foreach (CellJson cell in parsed.Cells)
        {
            var coord = new CellCoord(cell.X, cell.Y);
            if (!_view.IsBuildable(coord, out string? rejection))
            {
                continue; // rejected cell drops out (带错误重入的调整)
            }

            if (capByAffordability && total + unitCost > available)
            {
                continue;
            }

            kept.Add(cell);
            total += unitCost;
        }

        if (kept.Count == 0 || kept.Count == parsed.Cells.Count && !capByAffordability)
        {
            // Identical payload would fail identically — no useful retry.
            return null;
        }

        var adjusted = new BuildPlanActionJson(nameof(SystemActionKind.BuildPlan), parsed.TowerId, kept);
        return JsonSerializer.Serialize(adjusted, PipelinePlannerJsonContext.Default.BuildPlanActionJson);
    }

    /// <summary>SetTowerMode adjuster: drop ids that are no longer placed towers.</summary>
    private string? AdjustSetTowerMode(string actionJson, string error)
    {
        if (!error.Contains("target-invalid", StringComparison.Ordinal)
            || _view is not IActionWorldState state)
        {
            return null;
        }

        SetTowerModeActionJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(actionJson, PipelinePlannerJsonContext.Default.SetTowerModeActionJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.TargetIds.Count == 0)
        {
            return null;
        }

        var kept = parsed.TargetIds.Where(id => state.Towers.Any(tower => tower.Id == id)).ToArray();
        if (kept.Length == 0 || kept.Length == parsed.TargetIds.Count)
        {
            return null; // nothing to drop / nothing dropped — retry cannot help
        }

        var adjusted = new SetTowerModeActionJson(nameof(SystemActionKind.SetTowerMode), kept, parsed.Mode);
        return JsonSerializer.Serialize(adjusted, PipelinePlannerJsonContext.Default.SetTowerModeActionJson);
    }

    /// <summary>FocusFire adjuster: the planned target died — retarget the first living enemy.</summary>
    private string? AdjustFocusFire(string actionJson, string error)
    {
        if (!error.Contains("target-invalid", StringComparison.Ordinal)
            || _view is not IActionWorldState state
            || state.LivingEnemies.Count == 0)
        {
            return null;
        }

        long first = state.LivingEnemies[0].Id;
        try
        {
            FocusFireActionJson? parsed = JsonSerializer.Deserialize(
                actionJson, PipelinePlannerJsonContext.Default.FocusFireActionJson);
            if (parsed is not null && parsed.TargetId == first)
            {
                return null; // already aiming there — retry cannot help
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var adjusted = new FocusFireActionJson(nameof(SystemActionKind.FocusFire), first);
        return JsonSerializer.Serialize(adjusted, PipelinePlannerJsonContext.Default.FocusFireActionJson);
    }

    private static ActionOutcome Failed(string reason, BuildPlanDraft draft) =>
        new(new ActionResult(ActionResultStatus.Err, reason), draft);

    // ------------------------------------------------ SimCommand lane --

    /// <summary>
    /// Runs one sim-lane verb: registry gates first (schema + era,
    /// <see cref="ActionRegistry.ValidateOnly"/>), then the sim's own
    /// command verb applies the full bookkeeping at the next tick boundary.
    /// A refused command mutates nothing and fails the step honestly.
    /// </summary>
    private ActionOutcome RunSimLaneCommand(SystemActionKind verb, string actionJson, Func<string, string> payloadOf)
    {
        ActionResult gate = _registry.ValidateOnly(actionJson);
        if (gate.Status != ActionResultStatus.Ok)
        {
            return new ActionOutcome(gate, null);
        }

        string payload = payloadOf(actionJson);
        return EnqueueAndApply(verb, payload, ack: null);
    }

    /// <summary>
    /// RerouteLogistics: the registry validates the endpoints and records
    /// the flow policy (AgentActionState), then the carrier trip enters the
    /// sim through the assign-carrier command (the sim re-validates the
    /// supplier/receiver lane — an honest double check, not a bypass).
    /// </summary>
    private ActionOutcome RunReroute(string actionJson)
    {
        ActionOutcome outcome = _registry.Invoke(actionJson);
        if (outcome.Result.Status != ActionResultStatus.Ok || outcome.Payload is not RerouteLogisticsPlan plan)
        {
            return outcome;
        }

        var command = new AssignCarrierCommandJson("assign-carrier", plan.From, plan.To);
        return EnqueueAndApply(
            SystemActionKind.RerouteLogistics,
            JsonSerializer.Serialize(command, PipelinePlannerJsonContext.Default.AssignCarrierCommandJson),
            plan);
    }

    private ActionOutcome EnqueueAndApply(SystemActionKind verb, string payload, object? ack)
    {
        long applyTick = _simulation.CurrentTick + 1;
        long seq = _simulation.Enqueue(new SimCommand(applyTick, SimCommandKind.PlayerBuild, payload));
        int firstNewResult = _simulation.CommandResults.Count;
        _simulation.Step();

        for (int i = firstNewResult; i < _simulation.CommandResults.Count; i++)
        {
            SimCommandResult result = _simulation.CommandResults[i];
            if (result.Seq == seq && !result.Accepted)
            {
                return new ActionOutcome(
                    new ActionResult(ActionResultStatus.Err, $"{SimRejectedPrefix}:{result.RejectionReason}"), ack);
            }
        }

        return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), ack);
    }

    private ActionOutcome PlaceBuildableCells(BuildPlanDraft draft)
    {
        // Enqueue one build-tower command per buildable cell for the next
        // tick boundary, then advance exactly one tick — the deterministic
        // flush of the agent's placements.
        long applyTick = _simulation.CurrentTick + 1;
        var seqs = new List<long>();
        foreach (BuildPlanStep step in draft.Steps)
        {
            if (!step.Buildable)
            {
                continue;
            }

            string payload = BuildTowerCommandPayload(draft.TowerId, step.Cell.X, step.Cell.Y);
            seqs.Add(_simulation.Enqueue(new SimCommand(applyTick, SimCommandKind.PlayerBuild, payload)));
        }

        int firstNewResult = _simulation.CommandResults.Count;
        _simulation.Step();

        var rejected = new List<string>();
        for (int i = firstNewResult; i < _simulation.CommandResults.Count; i++)
        {
            SimCommandResult result = _simulation.CommandResults[i];
            if (!seqs.Contains(result.Seq))
            {
                continue;
            }

            if (!result.Accepted)
            {
                rejected.Add($"seq{result.Seq}:{result.RejectionReason}");
            }
        }

        if (rejected.Count > 0)
        {
            return new ActionOutcome(
                new ActionResult(
                    ActionResultStatus.Err,
                    $"{SimRejectedPrefix}:{string.Join(";", rejected)}"),
                draft);
        }

        return new ActionOutcome(new ActionResult(ActionResultStatus.Ok), draft);
    }

    private static UpgradeTowerCommandJson ReadUpgradeArguments(string actionJson)
    {
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        return new UpgradeTowerCommandJson(
            "upgrade-tower",
            root.GetProperty("target_id").GetInt64(),
            root.GetProperty("to_tier").GetInt32());
    }

    private static SellTowerCommandJson ReadSellArguments(string actionJson)
    {
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        return new SellTowerCommandJson(
            "sell-tower",
            root.GetProperty("target_id").GetInt64(),
            root.GetProperty("confirm_sell").GetBoolean());
    }

    private HeroOrderCommandJson ReadHeroOrderArguments(string actionJson)
    {
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        string order = root.GetProperty("order").GetString()!;
        // The K-3 schema carries no coordinates (G.2: order alone — engine
        // resolves parameters). The sim's move verb needs a concrete cell:
        // the engine-side default advance cell wired at composition (wave
        // entry). Without one the command refuses honestly (cell-missing).
        CellJson? cell = order == "move" && _heroAdvanceCell is { } advance
            ? new CellJson(advance().X, advance().Y)
            : null;
        return new HeroOrderCommandJson("hero-order", order, cell);
    }

    private static string BuildTowerCommandPayload(string towerId, int x, int y)
    {
        var command = new BuildTowerCommandJson(
            "build-tower",
            towerId,
            new CellJson(x, y));
        return JsonSerializer.Serialize(command, PipelinePlannerJsonContext.Default.BuildTowerCommandJson);
    }
}

/// <summary>Wire shape of a K-6 build-tower command payload (mirrors the Sim lane's strict JSON).</summary>
public sealed record BuildTowerCommandJson(
    [property: System.Text.Json.Serialization.JsonPropertyName("verb")] string Verb,
    [property: System.Text.Json.Serialization.JsonPropertyName("tower")] string Tower,
    [property: System.Text.Json.Serialization.JsonPropertyName("cell")] CellJson Cell);

/// <summary>Wire shape of a K-6 upgrade-tower command payload (T-068 sim lane).</summary>
public sealed record UpgradeTowerCommandJson(
    [property: System.Text.Json.Serialization.JsonPropertyName("verb")] string Verb,
    [property: System.Text.Json.Serialization.JsonPropertyName("target")] long Target,
    [property: System.Text.Json.Serialization.JsonPropertyName("to_tier")] int ToTier);

/// <summary>Wire shape of a K-6 sell-tower command payload (T-068 sim lane).</summary>
public sealed record SellTowerCommandJson(
    [property: System.Text.Json.Serialization.JsonPropertyName("verb")] string Verb,
    [property: System.Text.Json.Serialization.JsonPropertyName("target")] long Target,
    [property: System.Text.Json.Serialization.JsonPropertyName("confirm_sell")] bool ConfirmSell);

/// <summary>Wire shape of a K-6 hero-order command payload (T-068 sim lane).</summary>
/// <param name="Verb">Sim verb discriminator.</param>
/// <param name="Order">move|skill|retreat.</param>
/// <param name="Cell">Move destination cell (the K-3 schema carries no coordinates — the executor injects the engine-side advance cell; null on skill/retreat).</param>
public sealed record HeroOrderCommandJson(
    [property: System.Text.Json.Serialization.JsonPropertyName("verb")] string Verb,
    [property: System.Text.Json.Serialization.JsonPropertyName("order")] string Order,
    [property: System.Text.Json.Serialization.JsonPropertyName("cell")] CellJson? Cell);

/// <summary>Wire shape of a K-6 assign-carrier command payload (T-068 sim lane).</summary>
public sealed record AssignCarrierCommandJson(
    [property: System.Text.Json.Serialization.JsonPropertyName("verb")] string Verb,
    [property: System.Text.Json.Serialization.JsonPropertyName("from")] long From,
    [property: System.Text.Json.Serialization.JsonPropertyName("to")] long To);
