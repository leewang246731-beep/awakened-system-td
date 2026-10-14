using System.Text.Json;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-169][F-065][F-066][F-071] 完整闭环矩阵（T-053 验收 08）：
// 4~6 条典型军令各走全管线，断言终态（模拟世界/台账/汇报）+JournalEvent
// 序列（按管线载荷折叠的阶段序）；自纠路径、deny 路径、澄清路径、挂起态机。
public class AgentPipelineClosedLoopTests
{
    // ---- 阶段序辅助：按 order 过滤管线载荷，得 "input/plan/approval/..." 序列 ----

    internal static List<string> StageSequence(IReadOnlyList<JournalEvent> events, int order)
    {
        var stages = new List<string>();
        foreach (JournalEvent evt in events)
        {
            using JsonDocument doc = JsonDocument.Parse(evt.PayloadJson);
            if (!doc.RootElement.TryGetProperty("Type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || !doc.RootElement.TryGetProperty("Order", out JsonElement orderElement)
                || !orderElement.TryGetInt32(out int owner)
                || owner != order)
            {
                continue;
            }

            stages.Add(type.GetString()!);
        }

        return stages;
    }

    internal static PipelineApprovalPayload? ApprovalPayloadOf(IReadOnlyList<JournalEvent> events, int order)
    {
        foreach (JournalEvent evt in events)
        {
            // 先判 Type 再反序列化（异构载荷缺字段时 STJ 会以默认值绑参）
            using JsonDocument probe = JsonDocument.Parse(evt.PayloadJson);
            if (!probe.RootElement.TryGetProperty("Type", out JsonElement type)
                || type.GetString() != PipelineJournalTypes.Approval)
            {
                continue;
            }

            var payload = JsonSerializer.Deserialize<PipelineApprovalPayload>(
                evt.PayloadJson, PipelineJsonContext.Default.PipelineApprovalPayload);
            if (payload is { Order: int owner } && owner == order)
            {
                return payload;
            }
        }

        return null;
    }

    // ---- 矩阵①：守住东门（HoldRegion 目标级模板：军令→LookAround→BuildPlan→汇报） ----

    [Fact] // legal：ask 挂起→Confirm→建成 2 塔+扣点+汇报（F-169 闭环）
    public void HoldRegion_order_runs_the_full_loop()
    {
        using var harness = new PipelineHarness();

        OrderOutcome awaiting = harness.Pipeline.Handle("守住东门");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        Assert.NotNull(awaiting.PendingCard);
        Assert.Equal(new[] { "LookAround", "BuildPlan", "ReportToPlayer" },
            awaiting.PendingCard!.Steps.Select(step => step.Verb).ToArray());
        Assert.Equal(2, awaiting.PendingCard.TotalPoints);
        Assert.Equal(60.0, awaiting.PendingCard.TotalResources);

        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal(2, harness.Simulation.Towers.Count); // 模板在东门锚建 2 座默认塔
        Assert.Equal(200.0 - 60.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
        Assert.Equal(3, harness.Pipeline.Ledger.Points); // 5 − 建造 2 点
        Assert.StartsWith("已照办", done.Report!.Summary, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "input", "plan", "approval", "approval", "execution", "execution", "report" },
            StageSequence(JsonlJournalWriter.ReadAll(harness.JournalPath), done.Order));
        Assert.Equal("ask", ApprovalPayloadOf(JsonlJournalWriter.ReadAll(harness.JournalPath), done.Order)!.Decision);
    }

    // ---- 矩阵②：建两座木箭塔（Build 意图产建造步） ------------------------------

    [Fact] // legal：显式塔名/数量/分区三槽全填
    public void Build_order_places_the_requested_towers()
    {
        using var harness = new PipelineHarness();

        OrderOutcome awaiting = harness.Pipeline.Handle("在东门建两座木箭塔");
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal(2, harness.Simulation.Towers.Count);
        Assert.All(harness.Simulation.Towers, tower => Assert.Equal("era1:tower-wood-arrow", tower.Spec.Id));
        Assert.Contains(harness.Simulation.Towers, tower => tower.Cell.X == 12 && tower.Cell.Y == 12); // 锚点本格
        Assert.Equal(1, harness.Simulation.CurrentTick); // 每个建造批恰推进 1 tick
        Assert.Equal(2, harness.Simulation.AppliedCommandCount); // 2 条 build-tower 全应用
        Assert.Equal(140.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
        var buildStep = done.Trace.Steps.First(step => step.Verb == "BuildPlan");
        Assert.Equal("Ok", buildStep.Status);
        Assert.Equal(2, buildStep.Attempts[0].PointsSpent); // 成功步计费 2 点（台账扣款）
    }

    // ---- 矩阵③：看战局（QueryThreat 只读——全 allow 直跑不挂起） -----------------

    [Fact] // legal：0 点 0 资源，LookAround+汇报，不产方案卡
    public void Threat_query_answers_directly_without_a_card()
    {
        using var harness = new PipelineHarness();

        OrderOutcome done = harness.Pipeline.Handle("东门有什么威胁");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal(0, done.Trace.EstimatedPoints);
        Assert.Equal(0.0, done.Trace.EstimatedResources);
        Assert.Equal(5, harness.Pipeline.Ledger.Points); // 全免费动词
        Assert.Empty(harness.Simulation.Towers);
        Assert.NotNull(done.Report);
        Assert.Equal(
            new[] { "input", "plan", "approval", "execution", "report" },
            StageSequence(JsonlJournalWriter.ReadAll(harness.JournalPath), done.Order));
        Assert.Equal("allow", done.Trace.ApprovalDecision);
    }

    // ---- 矩阵④：问储备（QueryStockpile 只读） -----------------------------------

    [Fact] // legal：汇报里带当前资源行数
    public void Stockpile_query_reports_the_pool()
    {
        using var harness = new PipelineHarness();

        OrderOutcome done = harness.Pipeline.Handle("我们还剩多少木材");

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal("QueryStockpile", IntentQuery(done));
        Assert.Contains(done.Report!.Findings, f => f.Text.Contains("资源 1 类", StringComparison.Ordinal));
        Assert.Equal(
            new[] { "input", "plan", "approval", "execution", "execution", "report" },
            StageSequence(JsonlJournalWriter.ReadAll(harness.JournalPath), done.Order));
    }

    private static string IntentQuery(OrderOutcome outcome) => outcome.Trace.IntentKind ?? string.Empty;

    // ---- 矩阵⑤：deny 路径（方案卡原子预检：资源不足整案拒绝） --------------------

    [Fact] // illegal：库存盖不住整计划 → 整案拒绝，一分一木都不动
    public void Build_order_is_denied_whole_by_the_atomic_precheck()
    {
        using var harness = new PipelineHarness(initialWood: 40.0); // 2×30=60 > 40

        OrderOutcome denied = harness.Pipeline.Handle("在东门建两座木箭塔");

        Assert.Equal(PipelineOutcomeKind.Denied, denied.Kind);
        Assert.Null(denied.PendingCard);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(40.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood)); // 拒绝零扣费
        Assert.Equal(5, harness.Pipeline.Ledger.Points);
        var approval = ApprovalPayloadOf(JsonlJournalWriter.ReadAll(harness.JournalPath), denied.Order);
        Assert.NotNull(approval);
        Assert.Equal("deny", approval!.Decision);
        Assert.Contains("atomic-precheck:resources", approval.Reason, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "input", "plan", "approval", "report" },
            StageSequence(JsonlJournalWriter.ReadAll(harness.JournalPath), denied.Order));
    }

    // ---- 矩阵⑥：澄清路径（意图层失败=澄清卡+快捷轮盘，T-052 话术） ----------------

    [Fact] // illegal：学不会的军令 → 澄清卡 + 快捷轮盘，零执行零计费
    public void Unparseable_order_gets_a_clarification_card()
    {
        using var harness = new PipelineHarness();

        OrderOutcome clarified = harness.Pipeline.Handle("呃呃呃呃");

        Assert.Equal(PipelineOutcomeKind.Clarification, clarified.Kind);
        Assert.Equal(EraCore.Agent.Grammar.L1IntentParser.DefaultClarifyingKey, clarified.ClarifyingKey);
        // T-069：快捷轮盘随 SupportedIntents 扩为 SP-2 全量 12 意图（原 T-052 五意图子集）
        Assert.Equal(
            new[]
            {
                "HoldRegion", "Build", "Upgrade", "Sell", "FocusFire", "ChangeFocusMode",
                "RerouteLogistics", "QueryStockpile", "QueryThreat", "SetAlarm", "Patrol", "ScrollControl",
            },
            clarified.QuickWheel);
        Assert.Null(clarified.Report);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(5, harness.Pipeline.Ledger.Points);
        Assert.Equal(
            new[] { "input", "clarify" },
            StageSequence(JsonlJournalWriter.ReadAll(harness.JournalPath), clarified.Order));
    }

    // ---- 自纠路径：部分格被占 → 带错误重入 ≤2 → 如实汇报 ------------------------

    [Fact] // boundary：请求 2 座、1 格被占 → 自纠建 1 座，attempts=2、调整留痕
    public void Occupied_ring_cell_self_corrects_to_the_buildable_subset()
    {
        using var harness = new PipelineHarness();
        // 先占掉环扫第二格 (11,11)：锚点本格仍可建
        harness.Simulation.Enqueue(new SimCommand(
            1, SimCommandKind.PlayerBuild, BuildTowerJson("era1:tower-wood-arrow", 11, 11)));
        harness.Simulation.Run(1);
        Assert.Single(harness.Simulation.Towers);

        OrderOutcome awaiting = harness.Pipeline.Handle("在东门建两座木箭塔");
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        Assert.Equal(2, harness.Simulation.Towers.Count); // 预置 1 + 自纠后新建 1
        Assert.Equal(200.0 - 60.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
        var buildStep = done.Trace.Steps.First(step => step.Verb == "BuildPlan");
        Assert.Equal(2, buildStep.Attempts.Count);
        Assert.StartsWith("cells-rejected", buildStep.Attempts[0].Error, StringComparison.Ordinal);
        Assert.False(buildStep.Attempts[0].SelfCorrected);
        Assert.True(buildStep.Attempts[1].SelfCorrected);
        Assert.Equal("adjusted-cells:2->1", buildStep.Attempts[1].RetryAdjustment);
        Assert.StartsWith("尽力了但没全办成", done.Report!.Summary, StringComparison.Ordinal); // 如实汇报
        Assert.Contains(done.Report.Findings, f => f.Text.Contains("1 座已就位 (12,12)", StringComparison.Ordinal));
    }

    // ---- 自纠上限：不可纠失败（T-068 动作真实现后换用例）→ 不硬试，如实汇报 ----

    [Fact] // boundary：未装备卷轴触发 → 1 次失败即放弃（无可行调整），汇报原文错误，零计费
    public void Non_correctable_failure_reports_honestly_without_burned_retries()
    {
        using var harness = new PipelineHarness();

        OrderOutcome awaiting = harness.Pipeline.Handle("开火油卷轴");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        OrderOutcome done = harness.Pipeline.Confirm(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.Completed, done.Kind);
        var scrollStep = done.Trace.Steps.First(step => step.Verb == "TriggerScroll");
        Assert.Equal("Err", scrollStep.Status);
        Assert.Single(scrollStep.Attempts); // 无自纠通道——不烧重试（统一自纠循环：调整器返回 null）
        Assert.Contains("scroll-not-equipped", scrollStep.Attempts[0].Error, StringComparison.Ordinal);
        Assert.StartsWith("尽力了但没全办成", done.Report!.Summary, StringComparison.Ordinal); // 如实汇报
        Assert.Equal(5, harness.Pipeline.Ledger.Points); // 失败动作永不计费
    }

    // ---- 挂起态机：Refuse / 双 Confirm / 未知 id ---------------------------------

    [Fact] // illegal：玩家拒绝方案卡 → 零执行零扣费，pending 清空
    public void Refusing_the_card_executes_nothing()
    {
        using var harness = new PipelineHarness();

        OrderOutcome awaiting = harness.Pipeline.Handle("在东门建两座木箭塔");
        OrderOutcome refused = harness.Pipeline.Refuse(awaiting.PendingId);

        Assert.Equal(PipelineOutcomeKind.ApprovalRefused, refused.Kind);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(200.0, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));
        Assert.Equal(5, harness.Pipeline.Ledger.Points);
        Assert.StartsWith("方案卡被拒绝", refused.Report!.Summary, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => harness.Pipeline.Confirm(awaiting.PendingId)); // 二次操作=非法
    }

    [Fact] // illegal：未知 pending id / null 军令被拒
    public void Pending_and_order_arguments_are_validated()
    {
        using var harness = new PipelineHarness();

        Assert.Throws<ArgumentException>(() => harness.Pipeline.Confirm(999));
        Assert.Throws<ArgumentNullException>(() => harness.Pipeline.Handle(null!));
    }

    private static string BuildTowerJson(string towerId, int x, int y) =>
        JsonSerializer.Serialize(
            new BuildTowerCommandJson("build-tower", towerId, new CellJson(x, y)),
            PipelinePlannerJsonContext.Default.BuildTowerCommandJson);
}
