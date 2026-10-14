using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-24] [F-103] T-089 黄金三章执行器：序贯推进（幕单发、乱序谓词不跳幕）、
// 谓词匹配矩阵（wave 精确/演出 id 精确/victory）、15 分钟循环预算口径
// （第三幕−第一幕 ≤ budget，tick 口径 30Hz）。
public class GoldenActsTests
{
    private static GoldenActs Acts(int act1Wave = 2, int act3Wave = 5, int budget = 900) =>
        new(new GoldenActsConfig(
            budget,
            new[]
            {
                new StoryAct("a1", "受辱", new ActPredicate(ActPredicateKind.WaveStart, act1Wave, null), Array.Empty<DialogueLine>()),
                new StoryAct("a2", "觉醒", new ActPredicate(ActPredicateKind.CinematicFired, null, "cinematic:e1-aha"), Array.Empty<DialogueLine>()),
                new StoryAct("a3", "打脸", new ActPredicate(ActPredicateKind.WaveStart, act3Wave, null), Array.Empty<DialogueLine>()),
            }));

    [Fact] // legal — 序贯推进：受辱(波2)→绑定(演出)→打脸(波5)，全部触发后 Complete
    public void Acts_fire_strictly_in_order()
    {
        GoldenActs golden = Acts();

        Assert.Null(golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e1-aha", 100)); // 乱序：绑定先来不跳幕
        Assert.Null(golden.Offer(ActPredicateKind.WaveStart, 5, null, 110)); // 乱序：打脸先来不跳幕
        Assert.Equal("a1", golden.Offer(ActPredicateKind.WaveStart, 2, null, 200)!.Id);
        Assert.Null(golden.Offer(ActPredicateKind.WaveStart, 2, null, 210)); // 幕单发：受辱不重复
        Assert.Equal("a2", golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e1-aha", 300)!.Id);
        Assert.Null(golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e2-awaken", 310)); // id 不匹配
        Assert.Equal("a3", golden.Offer(ActPredicateKind.WaveStart, 5, null, 400)!.Id);
        Assert.True(golden.Complete);
        Assert.Null(golden.Offer(ActPredicateKind.Victory, 0, null, 500)); // 完成后再来不复发
    }

    [Theory] // legal — 谓词矩阵：wave 精确匹配（带外波不触发）、victory 谓词由胜利拍触发
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public void Wave_predicates_match_exactly(int wave, bool fires)
    {
        GoldenActs golden = new(new GoldenActsConfig(
            900,
            new[] { new StoryAct("only", "t", new ActPredicate(ActPredicateKind.WaveStart, 2, null), Array.Empty<DialogueLine>()) }));

        Assert.Equal(fires, golden.Offer(ActPredicateKind.WaveStart, wave, null, 10) is not null);
    }

    [Fact] // legal — 循环预算：第三幕−第一幕 换算秒（30Hz tick），带内 WithinBudget
    public void The_loop_span_is_measured_from_first_to_third_act()
    {
        GoldenActs golden = Acts(budget: 900);

        _ = golden.Offer(ActPredicateKind.WaveStart, 2, null, tick: 60_000); // 2000s
        _ = golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e1-aha", 60_300);
        _ = golden.Offer(ActPredicateKind.WaveStart, 5, null, 87_000); // 2900s

        Assert.Equal(900d, golden.LoopSeconds!.Value, 6); // (2900−2000)s
        Assert.True(golden.WithinBudget);
        Assert.Equal(60_000, golden.TickOf("a1"));
        Assert.Equal(87_000, golden.TickOf("a3"));
    }

    [Fact] // boundary — 循环未闭合：LoopSeconds/WithinBudget 保持 null/false（不算命中）
    public void An_open_loop_is_not_within_budget()
    {
        GoldenActs golden = Acts();

        _ = golden.Offer(ActPredicateKind.WaveStart, 2, null, 1_000);

        Assert.Null(golden.LoopSeconds);
        Assert.False(golden.WithinBudget);
        Assert.False(golden.Complete);
    }

    [Fact] // boundary — 恰好压线：span == budget 判带内（≤ 口径）
    public void A_span_exactly_at_the_budget_is_within()
    {
        GoldenActs golden = Acts(budget: 100);

        _ = golden.Offer(ActPredicateKind.WaveStart, 2, null, 0);
        _ = golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e1-aha", 1);
        _ = golden.Offer(ActPredicateKind.WaveStart, 5, null, 3_000); // 100s

        Assert.True(golden.WithinBudget);
    }

    [Fact] // illegal — 超预算：span > budget 判带外（预算是硬口径）
    public void A_span_over_the_budget_is_outside()
    {
        GoldenActs golden = Acts(budget: 100);

        _ = golden.Offer(ActPredicateKind.WaveStart, 2, null, 0);
        _ = golden.Offer(ActPredicateKind.CinematicFired, 0, "cinematic:e1-aha", 1);
        _ = golden.Offer(ActPredicateKind.WaveStart, 5, null, 3_001); // 100.03s > 100s

        Assert.False(golden.WithinBudget);
    }

    [Fact] // illegal — 未知幕 id 查询：拒绝（防拼写错位静默）
    public void An_unknown_act_id_is_rejected()
    {
        GoldenActs golden = Acts();

        Assert.Throws<ArgumentException>(() => golden.TickOf("a4"));
    }

    [Fact] // illegal — null 配置：拒绝
    public void A_null_config_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new GoldenActs(null!));
    }
}
