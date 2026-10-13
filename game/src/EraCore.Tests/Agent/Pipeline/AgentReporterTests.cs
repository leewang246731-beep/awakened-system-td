using EraCore.Agent.Pipeline;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-065] 第 5 段汇报单元面（T-053）：大白话汇总三要素（做了什么/
// 花了多少/下一步建议）+ K-3 边界（summary ≤200、findings ≤8）+ 自纠如实
// 措辞 + sink 回调。
public class AgentReporterTests
{
    private static Intent Intent(IntentKind kind, string text) => new(kind, text, new Dictionary<string, string>());

    private static StepDigest Digest(
        string verb = "LookAround",
        string status = "Ok",
        int points = 0,
        double resources = 0,
        string detail = "现存敌 0",
        bool selfCorrected = false,
        string? error = null) =>
        new(verb, status, error, points, resources, detail, selfCorrected);

    [Fact] // legal：全 Ok 且无自纠 → 已照办 + 三要素齐全
    public void Compose_reports_done_cost_and_advice()
    {
        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.Build, "在东门建两座木箭塔"),
            "completed",
            new[] { Digest("LookAround"), Digest("BuildPlan", points: 2, resources: 60, detail: "2 座已就位 (12,12),(11,11)") },
            reportText: string.Empty);

        Assert.StartsWith("已照办「在东门建两座木箭塔」", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.Text.Contains("BuildPlan 完成", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Text.Contains("系统点 2", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Text.Contains("下一步建议", StringComparison.Ordinal));
        Assert.All(report.Findings, f => Assert.Equal(FindingVerdict.Confirmed, f.Verdict));
        Assert.True(report.Summary.Length <= 200);
    }

    [Fact] // boundary：自纠过的步骤=没全办成（如实措辞，不谎报全胜）
    public void Compose_flags_self_corrected_runs_as_partial()
    {
        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.Build, "建两座箭塔"),
            "completed",
            new[] { Digest("BuildPlan", points: 2, resources: 30, detail: "1 座已就位 (12,12)", selfCorrected: true) },
            reportText: string.Empty);

        Assert.StartsWith("尽力了但没全办成", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.Text.Contains("（有自纠）", StringComparison.Ordinal));
    }

    [Fact] // boundary：失败步骤的错误原文进 findings
    public void Compose_carries_failure_causes()
    {
        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.SetAlarm, "三分钟后叫我"),
            "completed",
            new[] { Digest("SetAlarm", status: "Err", error: "not-implemented:SetAlarm") },
            reportText: string.Empty);

        Assert.Contains(report.Findings, f => f.Text.Contains("SetAlarm 失败：not-implemented:SetAlarm", StringComparison.Ordinal));
    }

    [Fact] // boundary：findings 超过 8 条时截到 K-3 上限（步数行+花费+建议排序）
    public void Compose_caps_findings_at_eight()
    {
        var many = Enumerable.Range(0, 20)
            .Select(i => Digest("Step" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.QueryStockpile, "还剩什么资源"), "completed", many, reportText: string.Empty);

        Assert.Equal(8, report.Findings.Count);
    }

    [Fact] // boundary：超长摘要截到 200 字符（K-3 硬界）——且截断=原文前 200 字符
           // 的前缀切片（W2 批 3 加固：仅断言长度放不过"截错位置/丢字/补字"）
    public void Compose_truncates_overlong_summary()
    {
        string longText = new('令', 300);
        string overlongReport = new('长', 260);
        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.Build, longText), "completed",
            new[] { Digest() }, reportText: overlongReport);

        Assert.Equal(200, report.Summary.Length);
        Assert.Equal(overlongReport[..200], report.Summary); // 内容=前 200 字符原样前缀（逐字符钉住）
    }

    [Fact] // legal：拒绝始终走 reportText 原文（决策由管线定，汇报层只转述）
    public void Compose_relays_refusal_copy_verbatim()
    {
        AgentReport report = new AgentReporter().Compose(
            Intent(IntentKind.Build, "建两座箭塔"), "denied", Array.Empty<StepDigest>(), "这道令现在办不了（预检未过），资源和点数都没有动。");

        Assert.Equal("这道令现在办不了（预检未过），资源和点数都没有动。", report.Summary);
    }

    [Fact] // legal：sink 收到完成的报告（宿主 UI 卡片通道）
    public void Report_dispatches_to_the_host_sink()
    {
        Intent intent = Intent(IntentKind.QueryThreat, "敌情汇报");
        AgentReport? received = null;
        var reporter = new AgentReporter((source, report) =>
        {
            received = report;
            Assert.Same(intent, source);
        });

        var composed = new AgentReport("summary", Array.Empty<Finding>());
        reporter.Report(intent, composed);

        Assert.Same(composed, received);
    }

    [Fact] // illegal：null 参数防御
    public void Compose_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AgentReporter().Compose(null!, "completed", Array.Empty<StepDigest>(), string.Empty));
        Assert.Throws<ArgumentNullException>(
            () => new AgentReporter().Compose(Intent(IntentKind.Build, "x"), "completed", null!, string.Empty));
    }
}
