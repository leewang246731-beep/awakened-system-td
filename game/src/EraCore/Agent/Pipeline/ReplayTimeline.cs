using EraCore.Contracts.Journal;

namespace EraCore.Agent.Pipeline;

// T-111 (C-30/C-22, F-121/F-174) 战报时间轴纯模型：从一局 K-8 JSONL 战报
// 重建「军令/审批/执行/汇报」四色事件流 + tick 轴（可跳转到任意 tick）。
// 决策全部纯逻辑（CS-1.6）——载荷解析复用 JournalReplayer.TryPayload 同一
// 类型化解析器（无第二套解析可漂移）；Godot 侧 ReplayUi 只镜像。

/// <summary>
/// The four replay lanes of the timeline (K-8 四类事件映射，T-111 验收口径
/// 「军令/审批/执行/汇报四色事件流」): input→军令; plan+approval→审批
/// （方案卡内容与审批结论同属 Kind=Plan，见 PipelineJournal 注释）;
/// execution→执行; report+clarify→汇报.
/// </summary>
public enum ReplayStage
{
    /// <summary>军令——input 载荷（玩家原话进管线）。</summary>
    Order,

    /// <summary>审批——plan 载荷（方案卡）+ approval 载荷（门裁决/玩家确认）。</summary>
    Approval,

    /// <summary>执行——execution 载荷（单步一次尝试：Schema/状态/扣点/重试）。</summary>
    Execution,

    /// <summary>汇报——report 载荷（终报）+ clarify 载荷（澄清卡）。</summary>
    Report,
}

/// <summary>One timeline row (T-111): one pipeline event projected onto the four-lane stream.</summary>
/// <param name="Tick">Simulation tick of the journal event (30 Hz domain).</param>
/// <param name="Order">1-based order counter the event belongs to.</param>
/// <param name="Stage">Which lane the event renders in (四色事件流).</param>
/// <param name="Summary">One-line zh digest of the typed payload (内容单源：载荷字段）。</param>
public sealed record ReplayTimelineEntry(long Tick, int Order, ReplayStage Stage, string Summary);

/// <summary>Immutable timeline index of one run (T-111): seek domain + full entry stream.</summary>
/// <param name="MinTick">First pipeline-event tick (0 for an empty report).</param>
/// <param name="MaxTick">Last pipeline-event tick (0 for an empty report) — the seek slider's upper bound.</param>
/// <param name="OrderCount">Distinct order counters seen (one per handled 军令).</param>
/// <param name="Entries">Every pipeline event in journal order (tick non-decreasing).</param>
public sealed record ReplayTimelineIndex(
    long MinTick,
    long MaxTick,
    int OrderCount,
    IReadOnlyList<ReplayTimelineEntry> Entries);

/// <summary>
/// Pure timeline builder over one run's journal (T-111, F-121): reuses the
/// <see cref="JournalReplayer"/> typed payload parser, keeps the K-8 ordered
/// domain check (strictly increasing <c>Seq</c>), classifies each pipeline
/// event into the four lanes and derives the tick seek domain. Audit-class
/// lines (the C-14 ledger budget events) pass integrity but join no lane —
/// the timeline is the advisor DECISION stream, not the ledger.
/// </summary>
public static class ReplayTimeline
{
    /// <summary>Builds the timeline index from raw journal events (offline: no sim needed).</summary>
    /// <exception cref="InvalidOperationException">Journal sequence regressed (K-8 violation).</exception>
    public static ReplayTimelineIndex Build(IReadOnlyList<JournalEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var entries = new List<ReplayTimelineEntry>();
        var orders = new HashSet<int>();
        long lastSeq = 0;
        foreach (JournalEvent evt in events)
        {
            if (evt.Seq <= lastSeq)
            {
                throw new InvalidOperationException(
                    $"Journal sequence regressed or repeated ({lastSeq} -> {evt.Seq}) — K-8 ordered-domain violation.");
            }

            lastSeq = evt.Seq;

            object? payload = JournalReplayer.TryPayload(evt, out int order);
            if (payload is not null
                && TryProject(evt.Tick, order, payload, out ReplayTimelineEntry? entry))
            {
                orders.Add(order);
                entries.Add(entry!);
            }
        }

        long minTick = entries.Count == 0 ? 0 : entries[0].Tick;
        long maxTick = entries.Count == 0 ? 0 : entries[^1].Tick;
        return new ReplayTimelineIndex(minTick, maxTick, orders.Count, entries);
    }

    /// <summary>
    /// Seek: every entry at or before one tick (the「跳转到任意 tick」half of
    /// the acceptance — the UI slider feeds this). Ticks are non-decreasing in
    /// journal order, so the prefix is a binary-search cut.
    /// </summary>
    public static IReadOnlyList<ReplayTimelineEntry> EntriesUpTo(ReplayTimelineIndex index, long tick)
    {
        ArgumentNullException.ThrowIfNull(index);

        var entries = index.Entries;
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            if (entries[mid].Tick <= tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return entries.Take(lo).ToArray();
    }

    /// <summary>Zh lane label of one stage (军令/审批/执行/汇报 — UI 四色图例单源).</summary>
    public static string StageLabelZh(ReplayStage stage) => stage switch
    {
        ReplayStage.Order => "军令",
        ReplayStage.Approval => "审批",
        ReplayStage.Execution => "执行",
        _ => "汇报",
    };

    /// <summary>
    /// Projects one typed payload onto a lane entry. Returns false for
    /// payload shapes that carry no timeline surface (none of the six today —
    /// the switch is exhaustive; a defensive false keeps future payload types
    /// honest instead of throwing mid-render).
    /// </summary>
    private static bool TryProject(long tick, int order, object payload, out ReplayTimelineEntry? entry)
    {
        switch (payload)
        {
            case PipelineInputPayload input:
                entry = new ReplayTimelineEntry(tick, order, ReplayStage.Order, input.OrderText);
                return true;
            case PipelinePlanPayload plan:
                entry = new ReplayTimelineEntry(
                    tick, order, ReplayStage.Approval,
                    $"方案 {plan.StepVerbs.Length} 步 · 预估 {plan.EstimatedPoints}pt/{plan.EstimatedResources:0.#}资源"
                    + (plan.Risks.Length > 0 ? $" · 风险 {plan.Risks.Length} 红标" : string.Empty));
                return true;
            case PipelineApprovalPayload approval:
                entry = new ReplayTimelineEntry(
                    tick, order, ReplayStage.Approval,
                    approval.Reason is { Length: > 0 } reason
                        ? $"审批 {approval.Decision}（{reason}）"
                        : $"审批 {approval.Decision}");
                return true;
            case PipelineExecutionPayload execution:
                entry = new ReplayTimelineEntry(
                    tick, order, ReplayStage.Execution,
                    $"#{execution.StepIndex + 1} {execution.Verb} → {execution.Status}"
                    + (execution.Error is { Length: > 0 } error ? $"：{error}" : string.Empty));
                return true;
            case PipelineReportPayload report:
                entry = new ReplayTimelineEntry(
                    tick, order, ReplayStage.Report, $"{report.Outcome}｜{report.Summary}");
                return true;
            case PipelineClarifyPayload clarify:
                entry = new ReplayTimelineEntry(
                    tick, order, ReplayStage.Report, $"澄清卡（{clarify.ClarifyingKey}）");
                return true;
            default:
                entry = null;
                return false;
        }
    }
}
