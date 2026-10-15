using EraCore.Agent.Pipeline;
using EraCore.Contracts.Journal;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-30][F-174] T-111 战报回放摘要导出：真局（多军令+确认+拒绝+澄清）的
// 决策轨迹+时间轴 → md 报告（汇总段+逐军令节+四色时间轴行）+ 确定性
// （同输入逐字节相同）+ 空战报边界 + 非法入参。夹具=PipelineHarness。
public class WarReportMarkdownTests
{
    private static void RunScriptedWar(PipelineHarness harness)
    {
        OrderOutcome build = harness.Pipeline.Handle("在东门建两座木箭塔");
        harness.Pipeline.Confirm(build.PendingId);
        harness.Simulation.Run(30);
        harness.Pipeline.Handle("东门有什么威胁");
        harness.Simulation.Run(30);
        // 波预算口径（同 JournalReplayIntegrationTests 脚本）：era-2 单波上限
        // 2pt 只够一次计价建造——先开波 2 回满，守住东门才有方案卡可拒。
        Assert.True(harness.Pipeline.Ledger.BeginWave(2, isBossWave: false));
        OrderOutcome hold = harness.Pipeline.Handle("守住东门");
        harness.Pipeline.Refuse(hold.PendingId);
        harness.Simulation.Run(30);
        harness.Pipeline.Handle("我们还剩多少木材");
        harness.Simulation.Run(30);
        harness.Pipeline.Handle("呃呃呃呃");
    }

    private static (IReadOnlyList<DecisionTrace> Traces, ReplayTimelineIndex Timeline) ReplayRun(
        PipelineHarness harness)
    {
        IReadOnlyList<JournalEvent> events = JsonlJournalWriter.ReadAll(harness.JournalPath);
        return (JournalReplayer.Replay(harness.JournalPath), ReplayTimeline.Build(events));
    }

    [Fact] // legal — 汇总段：军令计数/结果分布/审批口径/时间轴范围
    public void Report_summarizes_the_run_aggregates()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);
        (IReadOnlyList<DecisionTrace> traces, ReplayTimelineIndex timeline) = ReplayRun(harness);

        string md = WarReportMarkdown.Build("war-report.jsonl", traces, timeline);

        Assert.StartsWith("# 战报回放摘要：war-report.jsonl", md, StringComparison.Ordinal);
        Assert.Contains("## 一局汇总", md);
        Assert.Contains("军令：5 条（完成 3 / 拒绝执行 0 / 玩家否决 1 / 澄清 1 / 其他 0）", md);
        Assert.Contains("审批：玩家确认 1 / 玩家拒绝 1", md);
        Assert.Contains($"时间轴：tick {timeline.MinTick}–{timeline.MaxTick} · 事件 {timeline.Entries.Count} 条", md);
    }

    [Fact] // legal — 逐军令节：原文标题+结果+步骤+汇报+四色时间轴行
    public void Report_carries_one_section_per_order_with_the_lane_lines()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);
        (IReadOnlyList<DecisionTrace> traces, ReplayTimelineIndex timeline) = ReplayRun(harness);

        string md = WarReportMarkdown.Build("war-report.jsonl", traces, timeline);

        // 每条军令一节（含澄清与玩家否决两条非完成路径）
        foreach (string orderText in new[] { "在东门建两座木箭塔", "守住东门", "呃呃呃呃" })
        {
            Assert.Contains($"「{orderText}」", md);
        }

        // 完成路径：确认审批+已执行步骤+终报+时间轴四 lane 行
        Assert.Contains("- 审批：玩家确认，已执行", md);
        Assert.Contains("- 步骤 BuildPlan：已执行", md);
        Assert.Contains("- 汇报：", md);
        Assert.Contains("### 时间轴（军令/审批/执行/汇报）", md);
        Assert.Contains("- t", md);
        Assert.Contains("军令：在东门建两座木箭塔", md);
        Assert.Contains("审批：方案", md);
        Assert.Contains("执行：#1 ", md);
        Assert.Contains("澄清卡（", md);

        // 玩家否决与澄清的中文结果文案
        Assert.Contains("- 结果：玩家否决（approval_refused）", md);
        Assert.Contains("- 结果：澄清卡（clarification）", md);
    }

    [Fact] // legal — 确定性：同输入两次构建逐字节相同（无时钟/随机混入）
    public void Report_is_deterministic_for_the_same_inputs()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);
        (IReadOnlyList<DecisionTrace> traces, ReplayTimelineIndex timeline) = ReplayRun(harness);

        string first = WarReportMarkdown.Build("run-a", traces, timeline);
        string second = WarReportMarkdown.Build("run-a", traces, timeline);

        Assert.Equal(first, second);
    }

    [Fact] // boundary — 空战报：标题+零计数汇总仍成形（不抛、不伪造事件）
    public void Empty_run_builds_a_zero_aggregate_report()
    {
        var timeline = ReplayTimeline.Build(Array.Empty<JournalEvent>());

        string md = WarReportMarkdown.Build("empty.jsonl", [], timeline);

        Assert.Contains("军令：0 条（完成 0 / 拒绝执行 0 / 玩家否决 0 / 澄清 0 / 其他 0）", md);
        Assert.Contains("时间轴：tick 0–0 · 事件 0 条", md);
        Assert.DoesNotContain("## 军令 #", md, StringComparison.Ordinal);
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_arguments_rejected()
    {
        var timeline = ReplayTimeline.Build(Array.Empty<JournalEvent>());

        Assert.Throws<ArgumentNullException>(
            () => WarReportMarkdown.Build(null!, [], timeline));
        Assert.Throws<ArgumentNullException>(
            () => WarReportMarkdown.Build("x", null!, timeline));
        Assert.Throws<ArgumentNullException>(
            () => WarReportMarkdown.Build("x", [], null!));
    }
}
