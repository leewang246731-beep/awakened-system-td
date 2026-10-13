using System.Globalization;
using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Intents;
using System.Collections.Generic;

namespace EraCore.Agent.Pipeline;

// T-053 (C-13) 第 5 段汇报（F-065）：AgentReport 大白话汇总——做了什么/
// 花了多少/下一步建议。汇报文本由管线自己排（不编数值：点数来自台账结果、
// 资源来自建造成本报价），并整体经注册表 ReportToPlayer 动作通道出营
// （Schema 边界+可疑链接过滤=注入防护，F-4/附录 F.4）。

/// <summary>Execution digest of one step the reporter summarizes (plain data in, prose out).</summary>
/// <param name="Verb">Step verb.</param>
/// <param name="Status">Final status name (Ok / Err*).</param>
/// <param name="Error">Failure cause on Err.</param>
/// <param name="PointsSpent">Points actually charged for this step.</param>
/// <param name="Resources">Cost-currency resources spent by this step.</param>
/// <param name="Detail">Step detail line (e.g., placed cells) or empty.</param>
/// <param name="SelfCorrected">True when this step needed a self-correction retry (the report must say so).</param>
public sealed record StepDigest(
    string Verb,
    string Status,
    string? Error,
    int PointsSpent,
    double Resources,
    string Detail,
    bool SelfCorrected);

/// <summary>
/// Stage-5 reporter (T-053, implements the frozen K-5
/// <see cref="IAgentReporter"/>): composes the plain-language AgentReport —
/// what was done / what it cost / what to do next — and hands the finished
/// report to the host sink (C-22 renders the card; the pipeline additionally
/// journals it through K-8 — RL10). Every number in the prose comes from the
/// run's own records (CS-4.4: no invented figures).
/// </summary>
public sealed class AgentReporter : IAgentReporter
{
    private readonly Action<Intent, AgentReport>? _sink;

    /// <param name="sink">Optional host callback receiving every finished report.</param>
    public AgentReporter(Action<Intent, AgentReport>? sink = null)
    {
        _sink = sink;
    }

    /// <summary>
    /// Composes the report of one finished order run. Summary ≤ 200 chars
    /// (hard truncation at the boundary — the schema layer re-checks);
    /// findings ≤ 8 lines: per-step outcome, cost totals, next-step advice.
    /// </summary>
    /// <param name="intent">Source intent (kind + raw text).</param>
    /// <param name="outcome">Terminal outcome class of the order.</param>
    /// <param name="steps">Step digests in plan order.</param>
    /// <param name="reportText">Outcome-specific lead line (refusal/denial copy or empty).</param>
    public AgentReport Compose(Intent intent, string outcome, IReadOnlyList<StepDigest> steps, string reportText)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(reportText);

        int points = steps.Sum(step => step.PointsSpent);
        double resources = steps.Sum(step => step.Resources);
        bool allOk = steps.Count > 0 && steps.All(step => step.Status == nameof(ActionResultStatus.Ok));
        bool fullyComplied = allOk && steps.All(step => !step.SelfCorrected);
        string done = fullyComplied
            ? $"已照办「{intent.RawText}」："
            : $"尽力了但没全办成「{intent.RawText}」：";

        var lines = new List<string>(Math.Min(steps.Count, ReportToPlayerAction.FindingsMaxCount) + 2);
        foreach (StepDigest step in steps.Take(ReportToPlayerAction.FindingsMaxCount - 1))
        {
            lines.Add(step.Status == nameof(ActionResultStatus.Ok)
                ? $"{step.Verb} 完成{(step.Detail.Length > 0 ? $"：{step.Detail}" : string.Empty)}"
                : $"{step.Verb} 失败：{step.Error ?? "未知原因"}");
        }

        lines.Add($"花费：系统点 {points.ToString(CultureInfo.InvariantCulture)}"
            + $"、{intent.Kind.ToString()} 资源 {resources.ToString("0.#", CultureInfo.InvariantCulture)}"
            + (steps.Any(step => step.SelfCorrected) ? "（有自纠）" : string.Empty));
        lines.Add(NextStepAdvice(intent, outcome, allOk));
        var findings = lines
            .Take(ReportToPlayerAction.FindingsMaxCount)
            .Select(line => new Finding(line, FindingVerdict.Confirmed))
            .ToArray();

        string summary = reportText.Length > 0
            ? reportText
            : done + string.Join("；", steps.Select(step => step.Verb)) + "。";
        if (summary.Length > ReportToPlayerAction.SummaryMaxLength)
        {
            summary = summary[..ReportToPlayerAction.SummaryMaxLength];
        }

        return new AgentReport(summary, findings);
    }

    /// <summary>
    /// Next-step advice line (下一步建议) — per intent kind, deterministic
    /// copy. Suggestions never mutate anything; they only point at the next
    /// sensible order.
    /// </summary>
    public static string NextStepAdvice(Intent intent, string outcome, bool allOk)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return (intent.Kind, outcome, allOk) switch
        {
            (_, "completed", true) => "下一步建议：看一眼下一波敌情再定增援",
            (IntentKind.Build, "completed", false) => "下一步建议：换个位置或补足资源再试",
            (IntentKind.HoldRegion, "completed", false) => "下一步建议：先看战局，再补建或升级",
            (IntentKind.QueryThreat, _, _) => "下一步建议：按敌情调整防线",
            (IntentKind.QueryStockpile, _, _) => "下一步建议：按储备安排建造",
            _ => "下一步建议：换个说法再下令（我可懂的令见快捷轮盘）",
        };
    }

    /// <inheritdoc />
    public void Report(Intent sourceIntent, AgentReport report)
    {
        ArgumentNullException.ThrowIfNull(sourceIntent);
        ArgumentNullException.ThrowIfNull(report);
        _sink?.Invoke(sourceIntent, report);
    }
}
