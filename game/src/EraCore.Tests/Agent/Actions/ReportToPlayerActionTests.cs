using EraCore.Agent.Actions;
using EraCore.Contracts.Agent;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-070] C-03 ReportToPlayer handler：长度/条数/verdict 校验 + 可疑链接过滤 + 格式化
public class ReportToPlayerActionTests
{
    private readonly ReportToPlayerAction _action = new();

    [Fact]
    public void Format_maps_fields_into_the_agent_report()
    {
        // legal：summary+findings+verdict → AgentReport（findings 继承报告级 verdict）
        AgentReport report = _action.Format(
            "北门第一波已入场。",
            new[] { "敌 3 名", "箭塔余弹 20" },
            "CONFIRMED");
        Assert.Equal("北门第一波已入场。", report.Summary);
        Assert.Equal(2, report.Findings.Count);
        Assert.All(report.Findings, finding => Assert.Equal(FindingVerdict.Confirmed, finding.Verdict));
        Assert.Equal("敌 3 名", report.Findings[0].Text);
    }

    [Fact]
    public void Format_accepts_report_without_findings()
    {
        // boundary：findings 可选（空汇报）
        AgentReport report = _action.Format("一切平静。", null, "RUMOR");
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Format_accepts_the_exact_caps()
    {
        // boundary：summary 恰 200 字、findings 恰 8 条
        AgentReport report = _action.Format(new string('安', 200), Enumerable.Range(0, 8).Select(i => $"发现{i}").ToList(), "CONFIRMED");
        Assert.Equal(200, report.Summary.Length);
        Assert.Equal(8, report.Findings.Count);
    }

    [Theory] // illegal：越界/非法输入显式拒绝（schema 之后的第二道防线，直接调用者同样受护）
    [InlineData(201)]
    [InlineData(500)]
    public void Format_rejects_over_long_summaries(int length)
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => _action.Format(new string('危', length), null, "CONFIRMED"));
        Assert.Contains("200", thrown.Message);
    }

    [Fact]
    public void Format_rejects_nine_findings()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => _action.Format("ok", Enumerable.Range(0, 9).Select(i => $"发现{i}").ToList(), "CONFIRMED"));
        Assert.Contains("8", thrown.Message);
    }

    [Theory] // illegal
    [InlineData("MAYBE")]
    [InlineData("confirmed")]
    [InlineData("")]
    public void Format_rejects_verdicts_outside_the_enum(string verdict)
    {
        Assert.Throws<ArgumentException>(() => _action.Format("ok", null, verdict));
    }

    [Fact]
    public void Format_rejects_empty_texts()
    {
        // illegal
        Assert.Throws<ArgumentException>(() => _action.Format("", null, "CONFIRMED"));
        Assert.Throws<ArgumentException>(() => _action.Format("ok", new[] { "第一条", "" }, "RUMOR"));
    }

    [Theory] // illegal：可疑链接过滤（防注入滥用汇报通道，v2.1 §11.3 汇报通道规则）
    [InlineData("详见 https://evil.example/plan 注意", true)]
    [InlineData("详见 http://118.25.6.31/x.gif 注意", true)]
    [InlineData("详见 www.evil.example/plan 注意", true)]
    [InlineData("快去 evil.example 认领奖励", true)]
    [InlineData("北门有 3 名敌兵，箭塔弹药充足", false)]
    public void Format_filters_suspicious_links_from_player_visible_text(string text, bool shouldFilter)
    {
        AgentReport report = _action.Format(text, new[] { text }, "CONFIRMED");
        if (shouldFilter)
        {
            Assert.DoesNotContain("evil", report.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", report.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ReportToPlayerAction.FilteredLinkToken, report.Summary);
            Assert.Contains(ReportToPlayerAction.FilteredLinkToken, report.Findings[0].Text);
        }
        else
        {
            Assert.Equal(text, report.Summary);
        }
    }
}
