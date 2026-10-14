using EraCore.Agent.Governance;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-111] T-075 信任三档徽记呈现（v3.2 §11.4：<40 将信将疑 / 40~69
// 可托付 / ≥70 言听计从，不露裸数字）：档位映射（复用 TrustModel 冻结阈值，
// 边界 39|40|69|70）+ 三态标签/角标 + 档变自嘲台词 6 条模板（三档×升降，
// L1 模板填充）+ 跟踪器（同档静默、跨档单步/两步同向同线）。
// [F-111] [C-22]
public class TrustBadgePresenterTests
{
    // ---- legal — tier mapping (徽记边界 39|40|69|70) -----------------------

    [Theory] // boundary — 表驱动：39→疑、40→托、69→托、70→从（与 TrustModel.Badge 同阈值）
    [InlineData(0, TrustBadge.Skeptical)]
    [InlineData(39, TrustBadge.Skeptical)]
    [InlineData(40, TrustBadge.Trustworthy)]
    [InlineData(69, TrustBadge.Trustworthy)]
    [InlineData(70, TrustBadge.Devoted)]
    [InlineData(100, TrustBadge.Devoted)]
    public void BadgeOf_matches_the_frozen_tier_boundaries(int score, TrustBadge expected)
    {
        Assert.Equal(expected, TrustTiers.BadgeOf(score));
    }

    [Fact] // boundary — 与 TrustModel 阈值常量同源：两侧紧邻值逐一验证不可漂移
    public void BadgeOf_agrees_with_the_model_threshold_constants()
    {
        Assert.Equal(TrustBadge.Skeptical, TrustTiers.BadgeOf(TrustModel.SkepticalBelow - 1));
        Assert.Equal(TrustBadge.Trustworthy, TrustTiers.BadgeOf(TrustModel.SkepticalBelow));
        Assert.Equal(TrustBadge.Trustworthy, TrustTiers.BadgeOf(TrustModel.DevotedAtLeast - 1));
        Assert.Equal(TrustBadge.Devoted, TrustTiers.BadgeOf(TrustModel.DevotedAtLeast));
        Assert.Equal(TrustModel.MinScore, 0);
        Assert.Equal(TrustModel.MaxScore, 100);
    }

    [Theory] // illegal — 分数出 [0,100] 拒绝
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1000)]
    public void BadgeOf_rejects_scores_outside_the_domain(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrustTiers.BadgeOf(score));
    }

    // ---- legal — display labels (三态标签/角标，不露裸数字) -----------------

    [Theory] // legal — 全档标签与角标逐一断言（§11.4 原文措辞）
    [InlineData(TrustBadge.Skeptical, "将信将疑", "疑")]
    [InlineData(TrustBadge.Trustworthy, "可托付", "托")]
    [InlineData(TrustBadge.Devoted, "言听计从", "从")]
    public void Labels_and_marks_cover_all_three_tiers(
        TrustBadge badge, string label, string mark)
    {
        Assert.Equal(label, TrustBadgeLabels.Label(badge));
        Assert.Equal(mark, TrustBadgeLabels.Mark(badge));
    }

    [Fact] // boundary — 标签/角标永不泄漏裸数字（§11.4 只呈现档位）
    public void Labels_never_leak_the_raw_score()
    {
        foreach (TrustBadge badge in Enum.GetValues<TrustBadge>())
        {
            Assert.False(
                TrustBadgeLabels.Label(badge).Any(char.IsDigit) || TrustBadgeLabels.Mark(badge).Any(char.IsDigit),
                $"tier {badge} leaked a digit");
        }
    }

    // ---- legal — tier-change lines (三档×升降 6 条模板) ---------------------

    [Theory] // legal — 表驱动 6 条：去档×方向 → 各自一条非空台词
    [InlineData(TrustBadge.Skeptical, TrustBadge.Trustworthy)]
    [InlineData(TrustBadge.Skeptical, TrustBadge.Devoted)]
    [InlineData(TrustBadge.Trustworthy, TrustBadge.Devoted)]
    [InlineData(TrustBadge.Trustworthy, TrustBadge.Skeptical)]
    [InlineData(TrustBadge.Devoted, TrustBadge.Trustworthy)]
    [InlineData(TrustBadge.Devoted, TrustBadge.Skeptical)]
    public void Line_covers_the_full_three_by_two_template_table(TrustBadge from, TrustBadge to)
    {
        string line = TrustBadgeLines.Line(from, to);

        Assert.False(string.IsNullOrWhiteSpace(line));
    }

    [Fact] // legal — 六条模板互不相同：离档×方向六键六句。真实跨档只能命中
    // 其中四键（无档高于言听计从/低于将信将疑），两个结构极端经越值枚举探针覆盖
    public void Line_templates_are_pairwise_distinct()
    {
        var reachable = new[]
        {
            TrustBadgeLines.Line(TrustBadge.Skeptical, TrustBadge.Trustworthy),   // 离疑上行
            TrustBadgeLines.Line(TrustBadge.Trustworthy, TrustBadge.Devoted),     // 离托上行
            TrustBadgeLines.Line(TrustBadge.Trustworthy, TrustBadge.Skeptical),   // 离托下行
            TrustBadgeLines.Line(TrustBadge.Devoted, TrustBadge.Trustworthy),     // 离从下行
        };

        // 结构极端：越值枚举使 to 落在域外，方向仍可判定（离从上行/离疑下行）
        string devotedUp = TrustBadgeLines.Line(TrustBadge.Devoted, (TrustBadge)3);
        string skepticalDown = TrustBadgeLines.Line(TrustBadge.Skeptical, (TrustBadge)(-1));

        var all = reachable.Append(devotedUp).Append(skepticalDown).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory] // boundary — 同向一步/两步同线：离档+方向决定台词（Skpt→Dev 与 Trust→Dev 不同，但 Skpt→Trust 与 Skpt→Dev 同为“离疑上行”）
    [InlineData(TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Skeptical, TrustBadge.Devoted)]
    [InlineData(TrustBadge.Devoted, TrustBadge.Trustworthy, TrustBadge.Devoted, TrustBadge.Skeptical)]
    public void Line_is_keyed_by_departed_tier_and_direction(
        TrustBadge fromA, TrustBadge toA, TrustBadge fromB, TrustBadge toB)
    {
        Assert.Equal(TrustBadgeLines.Line(fromA, toA), TrustBadgeLines.Line(fromB, toB));
    }

    [Fact] // illegal — 同档无台词（无档变就没有自嘲）
    public void Line_rejects_same_tier_transitions()
    {
        Assert.Throws<ArgumentException>(
            () => TrustBadgeLines.Line(TrustBadge.Trustworthy, TrustBadge.Trustworthy));
    }

    // ---- legal — tracker (同档静默、跨档发换档) ------------------------------

    [Fact] // legal — 同档分数变动静默（50→55→49 全在可托付内，无档变）
    public void Observe_is_silent_while_the_tier_holds()
    {
        var tracker = new TrustBadgeTracker(TrustBadge.Trustworthy);

        Assert.Null(tracker.Observe(55));
        Assert.Null(tracker.Observe(49));
        Assert.Equal(TrustBadge.Trustworthy, tracker.Current);
    }

    [Fact] // legal — 跨档下行：49→39 触发换档（托→疑，下行标记+台词填充）
    public void Observe_emits_the_change_when_crossing_down()
    {
        var tracker = new TrustBadgeTracker(TrustBadge.Trustworthy);

        TrustBadgeChange? change = tracker.Observe(39);

        Assert.NotNull(change);
        Assert.Equal(TrustBadge.Trustworthy, change!.From);
        Assert.Equal(TrustBadge.Skeptical, change.To);
        Assert.False(change.Upward);
        Assert.Equal(TrustBadgeLines.Line(TrustBadge.Trustworthy, TrustBadge.Skeptical), change.Line);
        Assert.Equal(TrustBadge.Skeptical, tracker.Current);
    }

    [Theory] // legal — 表驱动跨档往返：升穿 40/70、降穿 70/40 各自发一次
    [InlineData(TrustBadge.Skeptical, 40, TrustBadge.Trustworthy, true)]
    [InlineData(TrustBadge.Trustworthy, 70, TrustBadge.Devoted, true)]
    [InlineData(TrustBadge.Devoted, 69, TrustBadge.Trustworthy, false)]
    public void Observe_emits_crossings_in_both_directions(
        TrustBadge start, int score, TrustBadge expectedTier, bool upward)
    {
        var tracker = new TrustBadgeTracker(start);

        TrustBadgeChange? change = tracker.Observe(score);

        Assert.NotNull(change);
        Assert.Equal(expectedTier, change!.To);
        Assert.Equal(upward, change.Upward);
    }

    [Fact] // boundary — 换档只发一次：越档后同档继续变动静默（39→20→5）
    public void Observe_fires_once_per_crossing_then_stays_quiet()
    {
        var tracker = new TrustBadgeTracker(TrustBadge.Trustworthy);

        Assert.NotNull(tracker.Observe(39));
        Assert.Null(tracker.Observe(20));
        Assert.Null(tracker.Observe(5));
        Assert.Equal(TrustBadge.Skeptical, tracker.Current);
    }

    [Fact] // boundary — 两步跨档（疑→从）同向同线（离档+方向定台词）
    public void Observe_two_step_jump_uses_the_departed_tier_line()
    {
        var tracker = new TrustBadgeTracker(TrustBadge.Skeptical);

        TrustBadgeChange? change = tracker.Observe(70);

        Assert.NotNull(change);
        Assert.Equal(TrustBadge.Devoted, change!.To);
        Assert.True(change.Upward);
        Assert.Equal(TrustBadgeLines.Line(TrustBadge.Skeptical, TrustBadge.Devoted), change.Line);
    }

    [Theory] // illegal — 跟踪器同样拒绝域外分数
    [InlineData(-1)]
    [InlineData(101)]
    public void Observe_rejects_scores_outside_the_domain(int score)
    {
        var tracker = new TrustBadgeTracker(TrustBadge.Trustworthy);

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe(score));
    }
}
