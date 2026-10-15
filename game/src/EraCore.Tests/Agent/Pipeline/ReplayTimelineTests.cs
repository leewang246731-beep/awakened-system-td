using EraCore.Agent.Pipeline;
using EraCore.Contracts.Journal;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-30][C-22][F-121][F-174] T-111 战报时间轴纯模型：真实 E1 管线产出的
// JSONL（多军令+审批+执行+汇报+澄清，tick 交错推进）→ ReplayTimeline.Build
// 四色事件流 + 任意 tick 跳转（EntriesUpTo 前缀切割）+ K-8 有序域/审计行/
// 非法入参三行。夹具=PipelineHarness（真模拟真管线，CS-3.4 禁 mock 被测系统）。
public class ReplayTimelineTests
{
    /// <summary>
    /// Canonical multi-order war with interleaved ticks: each order is issued
    /// 30 sim ticks apart so the timeline carries a real (non-flat) tick axis.
    /// </summary>
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

    [Fact] // legal — 真实战报离线重建：四色事件流齐备、tick 单调、军令数一致
    public void Build_projects_the_four_lanes_from_a_real_journal()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);

        IReadOnlyList<JournalEvent> events = JsonlJournalWriter.ReadAll(harness.JournalPath);
        ReplayTimelineIndex timeline = ReplayTimeline.Build(events);

        Assert.Equal(5, timeline.OrderCount);
        Assert.True(timeline.Entries.Count >= 20, $"事件过少：{timeline.Entries.Count}");

        // 四色齐备：军令/审批/执行/汇报四 lane 各有事件
        foreach (ReplayStage stage in Enum.GetValues<ReplayStage>())
        {
            Assert.Contains(timeline.Entries, entry => entry.Stage == stage);
        }

        // 首事件=第一条军令原文；审批含方案+确认；执行带步号；澄清在汇报 lane
        Assert.Equal(ReplayStage.Order, timeline.Entries[0].Stage);
        Assert.Equal("在东门建两座木箭塔", timeline.Entries[0].Summary);
        Assert.Contains(timeline.Entries, e =>
            e.Stage == ReplayStage.Approval && e.Summary.Contains("方案", StringComparison.Ordinal));
        Assert.Contains(timeline.Entries, e =>
            e.Stage == ReplayStage.Approval && e.Summary.Contains("confirmed", StringComparison.Ordinal));
        Assert.Contains(timeline.Entries, e =>
            e.Stage == ReplayStage.Execution && e.Summary.StartsWith("#1 ", StringComparison.Ordinal));
        Assert.Contains(timeline.Entries, e =>
            e.Stage == ReplayStage.Report && e.Summary.Contains("澄清卡", StringComparison.Ordinal));

        // journal 序=事件序：tick 非降；每行摘要非空；序号与军令计数一致
        long lastTick = long.MinValue;
        foreach (ReplayTimelineEntry entry in timeline.Entries)
        {
            Assert.True(entry.Tick >= lastTick, $"tick 回退：{lastTick}->{entry.Tick}");
            lastTick = entry.Tick;
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
            Assert.InRange(entry.Order, 1, 5);
        }

        // tick 轴覆盖交错推进（非平坦轴：Min<Max）
        Assert.Equal(timeline.Entries[0].Tick, timeline.MinTick);
        Assert.Equal(timeline.Entries[^1].Tick, timeline.MaxTick);
        Assert.True(timeline.MinTick < timeline.MaxTick, "交错推进的战报应有非平坦 tick 轴");

        // 图例单源：四 lane 中文名
        Assert.Equal("军令", ReplayTimeline.StageLabelZh(ReplayStage.Order));
        Assert.Equal("审批", ReplayTimeline.StageLabelZh(ReplayStage.Approval));
        Assert.Equal("执行", ReplayTimeline.StageLabelZh(ReplayStage.Execution));
        Assert.Equal("汇报", ReplayTimeline.StageLabelZh(ReplayStage.Report));
    }

    [Fact] // legal — 任意 tick 跳转：全 tick 轴上二分前缀 == 线性过滤（逐步对拍）
    public void Seek_prefix_matches_the_linear_filter_on_every_tick()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);

        ReplayTimelineIndex timeline = ReplayTimeline.Build(
            JsonlJournalWriter.ReadAll(harness.JournalPath));

        for (long tick = timeline.MinTick - 1; tick <= timeline.MaxTick + 1; tick++)
        {
            IReadOnlyList<ReplayTimelineEntry> prefix = ReplayTimeline.EntriesUpTo(timeline, tick);
            ReplayTimelineEntry[] expected = timeline.Entries
                .Where(entry => entry.Tick <= tick)
                .ToArray();
            Assert.Equal(expected.Length, prefix.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], prefix[i]);
            }
        }

        // 边界语义钉死：MinTick-1 空；MaxTick 起全量
        Assert.Empty(ReplayTimeline.EntriesUpTo(timeline, timeline.MinTick - 1));
        Assert.Equal(
            timeline.Entries.Count,
            ReplayTimeline.EntriesUpTo(timeline, timeline.MaxTick).Count);
    }

    [Fact] // legal — 跳到首个执行 tick：该军令的汇报尚未发生（前缀切割真实可见）
    public void Seek_to_the_first_execution_tick_hides_the_later_report()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);

        ReplayTimelineIndex timeline = ReplayTimeline.Build(
            JsonlJournalWriter.ReadAll(harness.JournalPath));

        long firstExecutionTick = timeline.Entries
            .First(entry => entry.Stage == ReplayStage.Execution)
            .Tick;
        IReadOnlyList<ReplayTimelineEntry> prefix =
            ReplayTimeline.EntriesUpTo(timeline, firstExecutionTick);

        Assert.Contains(prefix, entry => entry.Stage == ReplayStage.Execution);
        // 同一军令（#1）的终报在执行之后——前缀里不得出现
        Assert.DoesNotContain(prefix, entry =>
            entry.Order == 1 && entry.Stage == ReplayStage.Report);
        Assert.Contains(timeline.Entries, entry =>
            entry.Order == 1 && entry.Stage == ReplayStage.Report);
    }

    [Fact] // boundary — 审计行（C-14 台账）过完整性但不进四色事件流
    public void Audit_lines_pass_integrity_but_join_no_lane()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);

        var events = JsonlJournalWriter.ReadAll(harness.JournalPath).ToList();
        long nextSeq = events[^1].Seq + 1;
        events.Add(new JournalEvent(
            JournalEventKind.Validation, events[^1].Tick, nextSeq,
            "{\"Type\":\"SystemPointLedgerEvent\",\"Note\":\"audit-only\"}"));

        ReplayTimelineIndex timeline = ReplayTimeline.Build(events);

        ReplayTimelineIndex baseline = ReplayTimeline.Build(
            JsonlJournalWriter.ReadAll(harness.JournalPath));
        Assert.Equal(baseline.Entries.Count, timeline.Entries.Count);
        Assert.Equal(baseline.OrderCount, timeline.OrderCount);
    }

    [Fact] // illegal — K-8 有序域违规（Seq 回退/重复）抛出，不静默降级
    public void Sequence_regression_throws()
    {
        using var harness = new PipelineHarness();
        RunScriptedWar(harness);

        var events = JsonlJournalWriter.ReadAll(harness.JournalPath).ToList();
        var regressed = events.ToList();
        (regressed[0], regressed[1]) = (regressed[1], regressed[0]); // 前两行交换 ⇒ Seq 回退

        Assert.Throws<InvalidOperationException>(() => ReplayTimeline.Build(regressed));
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ReplayTimeline.Build(null!));
        Assert.Throws<ArgumentNullException>(
            () => ReplayTimeline.EntriesUpTo(null!, tick: 0));
    }
}
