using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Sim;

namespace EraCore.Agent.Actions;

// T-056 (C-23 建造交互薄版, F-036/F-119)：玩家手动拖放建造的执行泳道。
//
// 口径裁定（任务卡验收 08 原文 + 设计 v3.2 §6 塔防战斗系统末条 L82「玩家
// 拥有与系统同款的拖放批量建造（同受 cells≤4+2×E 上限，与系统公平同规）」
// 的"同款"语义）：
//   1) 同一咽喉——玩家拖放构造 BuildPlan 动作 JSON 后调用
//      PipelineExecutor.InvokeStep，与军师管线（AgentPipeline.ExecuteSteps
//      → 同一个 InvokeStep）走【完全相同】的 ActionRegistry.Invoke 校验
//      （闭表 Schema+解锁时代门+BuildPlan 草案）与 SimCommand 落子执行；
//      本类不含第二条建造路径，也不旁路注册表（RL6）。
//   2) 审批与点数——军师专属层（W2 批 3 结构化裁定，正式登记于
//      REV-20261014-wave2-round2 §三-F6）：玩家手动建造不消耗系统点、不出
//      方案卡（拖放松手即为玩家预授权 allow 直过）。裁定依据三段：
//      ① v3.2 §11.1 不变式③「预算限量：系统点数」是游戏内智能体（"系统"）
//         的三条不变式之一——点数预算的主体=军师；
//      ② v3.2 §11.4 计价表（建造 2/战斗 1/物流 1/巡逻 1/侦察 0）是军师
//         动作的计价，玩家手势不是计价对象；
//      ③ 结构事实：AgentPipeline.ExecuteSteps 把 TrySpend（点数扣减）与
//         ApprovalGate（方案卡审批）包在 InvokeStep 之外的包装段，而本
//         玩家道直调 InvokeStep——点数与审批位于军师管线包装层，不在共享
//         咽喉内，玩家道自然不经过。
//      但校验/执行/战报三段与军师同源。
//   3) 战报——每次调用追加一条 JournalEvent（Kind=Validation、载荷
//      Type="execution" 与五段管线执行段同词表），载荷 Actor="player"
//      区分军师（军师侧载荷无 Actor 字段=系统）——回放器按 Type 折叠时
//      玩家与军师的执行序列同流可审计。

/// <summary>Wire shape of a SellTower action payload (K-3 field table: target_id + confirm_sell danger bit).</summary>
public sealed record SellTowerActionJson(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_id")] long TargetId,
    [property: JsonPropertyName("confirm_sell")] bool ConfirmSell);

/// <summary>
/// Journal payload of one player-lane invocation (K-8 envelope body):
/// execution-stage vocabulary with <see cref="Actor"/>=<c>player</c> so the
/// war report distinguishes hand-built placements from advisor-built ones
/// (F-069 replay folds both through the same Type).
/// </summary>
/// <param name="Type">Stage discriminator — always "execution" (PipelineJournalTypes.Execution).</param>
/// <param name="Actor">Always "player" on this lane (the advisor pipeline emits no Actor field).</param>
/// <param name="Verb">Action verb (BuildPlan / SellTower).</param>
/// <param name="ActionJson">The exact action JSON the registry validated.</param>
/// <param name="Status">Registry outcome status name.</param>
/// <param name="Error">Error message on failure; null on success.</param>
/// <param name="CellsPlaced">Cells the executor actually placed (0 unless Ok).</param>
/// <param name="PointsSpent">Always 0 — 口径裁定（PlayerBuildLane.cs 文件头 2)：系统点预算主体=军师，玩家手动建造不扣点。</param>
public sealed record PlayerBuildJournalPayload(
    string Type,
    string Actor,
    string Verb,
    string ActionJson,
    string Status,
    string? Error,
    int CellsPlaced,
    int PointsSpent);

/// <summary>
/// One player-lane invocation outcome: the frozen <see cref="ActionResult"/>
/// plus the cells the executor placed (draft payload distilled).
/// </summary>
public sealed record PlayerBuildOutcome(ActionResult Result, int CellsPlaced);

/// <summary>
/// System.Text.Json source-generation context for the player lane's wire and
/// journal types (D-05: no reflection serialization).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SellTowerActionJson))]
[JsonSerializable(typeof(PlayerBuildJournalPayload))]
public sealed partial class PlayerBuildJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The player's manual build lane (T-056): drag-drop placements and tower
/// demolition requests dispatched through the SAME registry+SimCommand
/// throat the advisor pipeline uses — see the file remarks for the
/// approval/point ruling (v3.2 §6 L82 同款语义, structured in
/// REV-20261014-wave2-round2 §三-F6).
/// </summary>
public sealed class PlayerBuildLane
{
    /// <summary>Actor stamp of every journal payload this lane writes.</summary>
    public const string Actor = "player";

    private readonly PipelineExecutor _executor;
    private readonly IActionWorldView _view;
    private readonly IJournalWriter _journal;

    /// <param name="executor">The SAME PipelineExecutor instance the advisor pipeline runs on (one throat per run).</param>
    /// <param name="view">Read-only world view (journal tick source).</param>
    /// <param name="journal">K-8 sink — sequence-ordered per run (SequencingJournalWriter renumbers Seq).</param>
    public PlayerBuildLane(PipelineExecutor executor, IActionWorldView view, IJournalWriter journal)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(journal);
        _executor = executor;
        _view = view;
        _journal = journal;
    }

    /// <summary>The world view previews/drafts validate against.</summary>
    public IActionWorldView View => _view;

    /// <summary>
    /// Batch-build cell cap at one era ordinal — read straight off the K-3
    /// frozen schema table (cells ≤ 4+2×E; 玩家与系统同限, F-036), never a
    /// local copy of the numbers.
    /// </summary>
    public static int MaxBuildCells(int eraOrdinal) =>
        ActionSchemaTable.Schemas[nameof(SystemActionKind.BuildPlan)]
            .Fields.First(field => field.Name == "cells")
            .EffectiveMaxCount(eraOrdinal);

    /// <summary>
    /// Composition root over one live simulation: registry (schema+unlock
    /// gate+handlers), stage-5 executor (registry→SimCommand placement) and
    /// this lane sharing them — the same shape as
    /// <see cref="Pipeline.AgentPipeline.OverSimulation"/> minus the
    /// advisor-only approval/point/report stages.
    /// </summary>
    /// <param name="simulation">The live simulation placements enter through SimCommand.</param>
    /// <param name="config">The config that simulation was built with (catalog + prices).</param>
    /// <param name="eraOrdinalProvider">Era ordinal the registry's unlock gate reads (BuildPlan/SellTower are E2 verbs, K-3 unlock column).</param>
    /// <param name="journal">K-8 journal sink for the player-lane execution events.</param>
    /// <param name="logSink">Registry diagnostics sink (also the host's invocation counter).</param>
    public static PlayerBuildLane OverSimulation(
        Simulation simulation,
        SimulationConfig config,
        Func<int> eraOrdinalProvider,
        IJournalWriter journal,
        ILogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(config);
        var view = new SimulationActionView(simulation, config);
        var registry = new ActionRegistry(logSink, view, eraOrdinalProvider: eraOrdinalProvider);
        return new PlayerBuildLane(new PipelineExecutor(registry, simulation, view), view, journal);
    }

    /// <summary>
    /// One drag-drop commit: BuildPlan action JSON (source-gen, never string
    /// concatenated) through <see cref="PipelineExecutor.InvokeStep"/> — the
    /// identical call the advisor's ExecuteSteps loop makes — then the
    /// actor=player journal record (success AND failure both journaled).
    /// </summary>
    public PlayerBuildOutcome Build(string towerId, IReadOnlyList<Contracts.Foundation.CellCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        ArgumentNullException.ThrowIfNull(cells);
        string actionJson = JsonSerializer.Serialize(
            new BuildPlanActionJson(
                nameof(SystemActionKind.BuildPlan),
                towerId,
                cells.Select(cell => new CellJson(cell.X, cell.Y)).ToArray()),
            PipelinePlannerJsonContext.Default.BuildPlanActionJson);
        return InvokeJournaled(nameof(SystemActionKind.BuildPlan), actionJson, SystemActionKind.BuildPlan);
    }

    /// <summary>
    /// One demolish request: SellTower through the same registry throat
    /// (草案形态, T-070/W3 lands the real handler). The explicit danger bit
    /// is MANDATORY (v3.2 §11.4/F.4: confirm_sell:true, 永不自动允许) — a
    /// call without it is refused HERE, before the registry, so a naked
    /// payload can never even reach the closed action list.
    /// </summary>
    public PlayerBuildOutcome Sell(long targetId, bool confirmSell)
    {
        if (!confirmSell)
        {
            // Danger bit refused before the registry — journaled as the
            // honest local refusal it is (a confirm_sell:false payload never
            // reaches the closed action list; the registry is not consulted
            // for a call that could never lawfully run).
            const string refusal = "confirm_sell-must-be-true";
            var refused = new ActionResult(ActionResultStatus.ErrSchemaDenied, refusal);
            var refusalPayload = new PlayerBuildJournalPayload(
                PipelineJournalTypes.Execution,
                Actor,
                nameof(SystemActionKind.SellTower),
                string.Empty,
                refused.Status.ToString(),
                refusal,
                CellsPlaced: 0,
                PointsSpent: 0);
            _journal.Append(new JournalEvent(
                JournalEventKind.Validation,
                _view.Tick,
                Seq: 0,
                PayloadJson: JsonSerializer.Serialize(
                    refusalPayload, PlayerBuildJsonContext.Default.PlayerBuildJournalPayload)));
            return new PlayerBuildOutcome(refused, CellsPlaced: 0);
        }

        string actionJson = JsonSerializer.Serialize(
            new SellTowerActionJson(nameof(SystemActionKind.SellTower), targetId, ConfirmSell: true),
            PlayerBuildJsonContext.Default.SellTowerActionJson);
        return InvokeJournaled(nameof(SystemActionKind.SellTower), actionJson, SystemActionKind.SellTower);
    }

    private PlayerBuildOutcome InvokeJournaled(string verbName, string actionJson, SystemActionKind kind)
    {
        ActionOutcome outcome = _executor.InvokeStep(kind, actionJson);
        int placed = outcome.Payload is BuildPlanDraft draft
            ? draft.Steps.Count(step => step.Buildable)
            : 0;
        var payload = new PlayerBuildJournalPayload(
            PipelineJournalTypes.Execution,
            Actor,
            verbName,
            actionJson,
            outcome.Result.Status.ToString(),
            outcome.Result.ErrorMessage,
            placed,
            PointsSpent: 0);
        _journal.Append(new JournalEvent(
            JournalEventKind.Validation,
            _view.Tick,
            Seq: 0,
            PayloadJson: JsonSerializer.Serialize(payload, PlayerBuildJsonContext.Default.PlayerBuildJournalPayload)));
        return new PlayerBuildOutcome(outcome.Result, placed);
    }
}
