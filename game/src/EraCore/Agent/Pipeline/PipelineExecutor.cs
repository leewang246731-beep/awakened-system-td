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

/// <summary>
/// Stage-5 execution lane (T-053): dispatches one plan step through the C-03
/// registry (closed-schema gate, unlock-era gate, handlers) and, for the
/// BuildPlan verb, turns the validated draft into real placements through
/// the K-6 SimCommand queue — the only lawful mutation path. A step is
/// FAILED (drives self-correction) when the draft is unaffordable, has no
/// buildable cell, or only part of the requested cells are placeable — the
/// retry loop then re-enters with an adjusted payload
/// (<see cref="AdjustBuildPlan"/>).
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

    /// <param name="registry">The C-03 registry (sole lawful action door).</param>
    /// <param name="simulation">The live simulation placements enter through SimCommand.</param>
    /// <param name="view">Read-only world view (self-correction re-reads state).</param>
    public PipelineExecutor(ActionRegistry registry, Simulation simulation, IActionWorldView view)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(view);
        _registry = registry;
        _simulation = simulation;
        _view = view;
    }

    /// <summary>
    /// Runs one step's action JSON: registry validation first, then the
    /// verb-specific execution. Returns the registry outcome enriched by the
    /// placement phase (BuildPlanDraft payload on success).
    /// </summary>
    public ActionOutcome InvokeStep(SystemActionKind verb, string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
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
    /// Self-correction adjuster (错误重入): builds the next BuildPlan payload
    /// from the CURRENT world state — rejected cells (occupied/terrain) drop
    /// out, and when the stock cannot cover the rest the affordable prefix
    /// (plan order) is taken. Returns null when no adjustment can help
    /// (nothing buildable / nothing affordable) — the retry loop then gives
    /// up and the reporter tells the truth (自纠 ≤2 次后如实汇报).
    /// </summary>
    /// <param name="actionJson">The failed step's action JSON.</param>
    /// <param name="error">The failure cause of the previous attempt.</param>
    public string? AdjustBuildPlan(string actionJson, string error)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        ArgumentNullException.ThrowIfNull(error);
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

    private static ActionOutcome Failed(string reason, BuildPlanDraft draft) =>
        new(new ActionResult(ActionResultStatus.Err, reason), draft);

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
