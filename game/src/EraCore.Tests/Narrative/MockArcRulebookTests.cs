using System.Text.Json;
using EraCore.Narrative;
using Xunit;
using static EraCore.Tests.Narrative.NarrativeContent;

namespace EraCore.Tests.Narrative;

// [C-24] [F-100] [F-101] T-089 打脸/扮猪配置化判定：资源压制乘数（数据驱动、
// 非硬编码）、低配判定（合法/边界/非法三行）、隐藏奖励对 chests.json 档位与
// 物品面零幽灵引用（T-087 结算隐藏档的数据源）、嘲讽波锚。
public class MockArcRulebookTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — 扮猪压制：L2-2 初始 1600 铜 ×0.5=800（劣势开局全由数据行驱动）
    public void The_pig_arc_suppression_comes_from_the_configured_row()
    {
        MockArcRow arc = StoryE2().MockArcFor("campaign:level-e2-02")!;

        Assert.Equal(800d, MockArcRulebook.SuppressedStock(1600d, arc), 6);
        Assert.Equal(400d, MockArcRulebook.SuppressedStock(800d, arc), 6); // 与基数成比例
    }

    [Fact] // legal — 打脸判定：通关+3 塔（≤阈值）→ 隐藏评价命中，带档位/物品/演出台词
    public void A_low_config_clear_hits_the_hidden_evaluation()
    {
        MockArcRow arc = StoryE1().MockArcFor("campaign:level-e1-01")!;
        MockArcVerdict verdict = MockArcRulebook.Judge(arc, victory: true, towersStanding: 3);

        Assert.True(verdict.HiddenHit);
        Assert.Equal("gold_extra", verdict.HiddenChestTier); // 复用黄金+额外档
        Assert.Equal(2, verdict.HiddenItemIds.Count);
        Assert.Contains(verdict.Lines, line => line.Text.Contains("低配通关")); // 打脸演出台词
        Assert.Contains("打脸", verdict.ReasonZh);
    }

    [Fact] // legal — 扮猪判定：劣势开局翻盘（通关+低塔）→ 隐藏评价『扮猪吃虎』
    public void A_pig_arc_comeback_hits_the_hidden_evaluation()
    {
        MockArcRow arc = StoryE2().MockArcFor("campaign:level-e2-02")!;
        MockArcVerdict verdict = MockArcRulebook.Judge(arc, victory: true, towersStanding: 3);

        Assert.True(verdict.HiddenHit);
        Assert.Contains("扮猪吃虎", verdict.ReasonZh);
        Assert.Contains(verdict.Lines, line => line.Speaker == "零");
    }

    [Theory] // legal — 嘲讽波锚：波 1/3/5 各中一条、带外波不发
    [InlineData(1, "铜料减半")]
    [InlineData(3, "看笑话")]
    [InlineData(5, "铁邱")]
    [InlineData(2, "NONE")]
    [InlineData(9, "NONE")]
    public void Taunts_fire_only_on_their_anchored_waves(int wave, string textHint)
    {
        MockArcRow arc = StoryE2().MockArcFor("campaign:level-e2-02")!;
        IReadOnlyList<MockArcTaunt> due = MockArcRulebook.TauntsDue(arc, wave);

        if (textHint == "NONE")
        {
            Assert.Empty(due);
        }
        else
        {
            MockArcTaunt taunt = Assert.Single(due);
            Assert.Contains(textHint, taunt.Text);
        }
    }

    [Fact] // legal — 隐藏奖励零幽灵引用：三章全部 item_ids 与 chest_tier 都在 chests.json 实锚
    public void Every_hidden_reward_resolves_in_the_chest_table()
    {
        using JsonDocument chests = JsonDocument.Parse(ReadSibling("campaign", "chests.json"));
        IReadOnlyList<string> tiers = chests.RootElement.GetProperty("tiers")
            .EnumerateArray()
            .Select(tier => tier.GetProperty("tier").GetString()!)
            .ToArray();
        IReadOnlyList<string> items = chests.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        foreach (StoryChapter chapter in new[] { StoryE1(), StoryE2(), StoryE3() })
        {
            foreach (MockArcRow arc in chapter.MockArcs)
            {
                Assert.Contains(arc.HiddenReward.ChestTier, tiers);
                Assert.All(arc.HiddenReward.ItemIds, itemId => Assert.Contains(itemId, items));
            }
        }
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 压线判定：塔数==max_towers 命中（≤ 口径含等号）
    public void A_tower_count_exactly_at_the_threshold_hits()
    {
        MockArcRow arc = StoryE1().MockArcFor("campaign:level-e1-01")!;

        Assert.True(MockArcRulebook.Judge(arc, victory: true, towersStanding: arc.LowConfig.MaxTowers).HiddenHit);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 未通关：隐藏评价不判定（低配口径只在胜利面上）
    public void A_defeat_never_hits_the_hidden_evaluation()
    {
        MockArcVerdict verdict = MockArcRulebook.Judge(
            StoryE1().MockArcFor("campaign:level-e1-01")!, victory: false, towersStanding: 2);

        Assert.False(verdict.HiddenHit);
        Assert.Empty(verdict.Lines);
        Assert.Contains("未通关", verdict.ReasonZh);
    }

    [Fact] // illegal — 常规通关：塔数超阈值（5>3）→ 常规评价
    public void A_normal_clear_with_more_towers_misses()
    {
        MockArcVerdict verdict = MockArcRulebook.Judge(
            StoryE1().MockArcFor("campaign:level-e1-01")!, victory: true, towersStanding: 5);

        Assert.False(verdict.HiddenHit);
        Assert.Contains("超过低配阈值", verdict.ReasonZh);
    }

    [Fact] // illegal — 打脸行无压制参数：SuppressedStock 拒绝（压制仅扮猪行携带）
    public void Suppressed_stock_on_a_face_slap_row_is_rejected()
    {
        MockArcRow arc = StoryE1().MockArcFor("campaign:level-e1-01")!;

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => MockArcRulebook.SuppressedStock(100d, arc));
        Assert.Contains("无资源压制参数", ex.Message);
    }
}
