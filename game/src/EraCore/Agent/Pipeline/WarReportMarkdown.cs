using System.Text;

namespace EraCore.Agent.Pipeline;

// T-111 (C-30, F-174) 战报回放摘要导出（纯函数）：一局 JSONL 回放结果 →
// md 报告（汇总一局的军师行为）。输入=JournalReplayer 的决策轨迹 +
// ReplayTimeline 的时间轴投影（同一局的两面）；输出确定性——同输入逐字节
// 相同（无时钟/无随机，CS-1.4）。RL5：管线载荷本就不含 Key，这里亦不引入。

/// <summary>
/// Deterministic markdown war-report builder (T-111, F-174): summarizes one
/// replayed run — headline aggregates (军令数/结果分布/审批/执行/风险) plus
/// one section per handled order with its four-lane timeline. The Godot
/// replay UI writes the returned text next to the journal (reports/ 目录).
/// </summary>
public static class WarReportMarkdown
{
    /// <summary>Outcome copy of one trace (zh, 单源)。</summary>
    public static string OutcomeZh(string outcome) => outcome switch
    {
        "completed" => "完成",
        "denied" => "拒绝执行",
        "approval_refused" => "玩家否决",
        "clarification" => "澄清卡",
        "awaiting_approval" => "待确认",
        "in_progress" => "进行中（断档战报）",
        _ => outcome,
    };

    /// <summary>Builds the full markdown report for one run.</summary>
    /// <param name="runLabel">Run label (usually the journal file name).</param>
    /// <param name="traces">Decision traces (<see cref="JournalReplayer.Replay"/> of the same run).</param>
    /// <param name="timeline">Timeline index (<see cref="ReplayTimeline.Build"/> of the same run).</param>
    public static string Build(
        string runLabel,
        IReadOnlyList<DecisionTrace> traces,
        ReplayTimelineIndex timeline)
    {
        ArgumentNullException.ThrowIfNull(runLabel);
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(timeline);

        var body = new StringBuilder();
        body.AppendLine($"# 战报回放摘要：{runLabel}");
        body.AppendLine();
        AppendAggregates(body, traces, timeline);

        foreach (DecisionTrace trace in traces)
        {
            AppendOrderSection(body, trace, timeline);
        }

        return body.ToString();
    }

    private static void AppendAggregates(
        StringBuilder body, IReadOnlyList<DecisionTrace> traces, ReplayTimelineIndex timeline)
    {
        int completed = traces.Count(t => t.Outcome == "completed");
        int denied = traces.Count(t => t.Outcome == "denied");
        int refused = traces.Count(t => t.Outcome == "approval_refused");
        int clarified = traces.Count(t => t.Outcome == "clarification");
        int other = traces.Count - completed - denied - refused - clarified;

        int confirms = traces.Count(t => t.ApprovalDecision == "confirmed");
        int playerRefused = traces.Count(t => t.ApprovalDecision == "player-refused");

        int steps = traces.Sum(t => t.Steps.Count);
        int attempts = traces.Sum(t => t.Steps.Sum(s => s.Attempts.Count));
        int pointsSpent = traces.Sum(t => t.Steps.Sum(s => s.Attempts.Sum(a => a.PointsSpent)));
        int risks = traces.Sum(t => t.Risks.Count);

        body.AppendLine("## 一局汇总");
        body.AppendLine();
        body.AppendLine($"- 军令：{traces.Count} 条（完成 {completed} / 拒绝执行 {denied} / 玩家否决 {refused} / 澄清 {clarified} / 其他 {other}）");
        body.AppendLine($"- 审批：玩家确认 {confirms} / 玩家拒绝 {playerRefused}");
        body.AppendLine($"- 执行：{steps} 步 · {attempts} 次尝试 · 系统点实耗 {pointsSpent}pt");
        body.AppendLine($"- 风险红标：{risks} 条");
        body.AppendLine($"- 时间轴：tick {timeline.MinTick}–{timeline.MaxTick} · 事件 {timeline.Entries.Count} 条（30 Hz 域）");
        body.AppendLine();
    }

    private static void AppendOrderSection(
        StringBuilder body, DecisionTrace trace, ReplayTimelineIndex timeline)
    {
        body.AppendLine($"## 军令 #{trace.Order}「{trace.OrderText}」");
        body.AppendLine();
        body.AppendLine($"- 结果：{OutcomeZh(trace.Outcome)}（{trace.Outcome}）");
        if (trace.IntentKind is { Length: > 0 } intent)
        {
            body.AppendLine($"- 意图 {intent} · 路由 {trace.RouteKind} · 预估 {trace.EstimatedPoints}pt/{trace.EstimatedResources:0.#}资源");
        }

        body.AppendLine($"- 审批：{ApprovalZh(trace.ApprovalDecision)}");
        if (trace.ClarifyingKey is { Length: > 0 } key)
        {
            body.AppendLine($"- 澄清键：{key}");
        }

        if (trace.Risks.Count > 0)
        {
            body.AppendLine($"- 风险红标：{string.Join("；", trace.Risks)}");
        }

        foreach (ReplayStep step in trace.Steps)
        {
            body.AppendLine(step.Attempts.Count == 0
                ? $"- 步骤 {step.Verb}：{step.StatusZh()}"
                : $"- 步骤 {step.Verb}：{step.StatusZh()}（尝试 {step.Attempts.Count} 次，实耗 {step.Attempts.Sum(a => a.PointsSpent)}pt）");
        }

        if (trace.ReportSummary.Length > 0)
        {
            body.AppendLine($"- 汇报：{trace.ReportSummary}");
            foreach (string finding in trace.ReportFindings)
            {
                body.AppendLine($"  - {finding}");
            }
        }

        body.AppendLine();
        body.AppendLine("### 时间轴（军令/审批/执行/汇报）");
        body.AppendLine();
        foreach (ReplayTimelineEntry entry in timeline.Entries.Where(e => e.Order == trace.Order))
        {
            body.AppendLine($"- t{entry.Tick} {ReplayTimeline.StageLabelZh(entry.Stage)}：{entry.Summary}");
        }

        body.AppendLine();
    }

    private static string ApprovalZh(string decision) => decision switch
    {
        "confirmed" => "玩家确认，已执行",
        "player-refused" => "玩家拒绝，未执行",
        "ask" => "方案卡待确认",
        "deny" => "门裁决拒绝",
        "-" => "未进入审批",
        _ => decision,
    };
}

/// <summary>Zh copy of one replay step's status (单源 helper for the report).</summary>
file static class ReplayStepCopy
{
    /// <summary>Status strings are <c>ActionResultStatus.ToString()</c> (pipeline emission).</summary>
    public static string StatusZh(this ReplayStep step) => step.Status switch
    {
        "planned" => "已规划",
        "Ok" => "已执行",
        "Err" => "失败",
        "ErrBudgetDenied" => "波预算拒绝",
        "ErrCapabilityLocked" => "时代未解锁",
        "ErrSchemaDenied" => "Schema 拒绝",
        "ErrPermissionDenied" => "权限拒绝",
        _ => step.Status,
    };
}
