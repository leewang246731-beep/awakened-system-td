using EraCore.Progression;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-002] 三重目标档位宝箱（T-073）：箱内稀有度 60/30/10 分布（≥1000 次
// 掉落模拟 ±3% 容差，SP-8c）+ 抽取确定性（先定后演 CS-4.9）+ 档位件数 +
// R9 装饰白名单（装载期拒绝白名单外奖励类）。三行全覆盖。[C-19]
public class ChestRewardsTests
{
    private static ChestRewards NewRoller(long seed = Seed) =>
        new(ChestRewardsData.Parse(MinimalChests), new SimRng(seed));

    // ------------------------------------------------------- legal

    [Fact] // legal — 确定性：同档同 seed 恒同箱；同 seed 不同档不同流（tier 混入）
    [Trait("Feature", "F-002")]
    public void Same_tier_and_seed_roll_identically()
    {
        ChestRewards roller = NewRoller();
        for (int seed = 1; seed <= 8; seed++)
        {
            var first = roller.Open("gold_extra", seed * 977);
            var second = NewRoller().Open("gold_extra", seed * 977);
            Assert.Equal(first, second); // 记录逐项相等
            Assert.NotEqual(roller.Open("bronze", seed * 977), roller.Open("gold", seed * 977));
        }
    }

    [Fact] // legal — 档位件数：每档抽 item_rolls+bonus_rolls 件（数据驱动）
    [Trait("Feature", "F-002")]
    public void Tier_roll_counts_follow_the_table()
    {
        ChestRewards roller = NewRoller();
        Assert.Single(roller.Open("bronze", 1));
        int silver = roller.Open("silver", 1).Count;
        Assert.Equal(2, silver);
        int gold = roller.Open("gold", 1).Count;
        Assert.Equal(3, gold);
        int goldExtra = roller.Open("gold_extra", 1).Count; // 黄金+额外=4 件（3+1 加抽）
        Assert.Equal(4, goldExtra);

        // 真实内容表：2/3/4/5（chests.json）
        ChestRewards real = new(ChestRewardsData.Parse(ReadCampaignContent("chests.json")), new SimRng(Seed));
        Assert.Equal(2, Count(real, "bronze"));
        Assert.Equal(3, Count(real, "silver"));
        Assert.Equal(4, Count(real, "gold"));
        Assert.Equal(5, Count(real, "gold_extra"));
    }

    private static int Count(ChestRewards roller, string tier) => roller.Open(tier, 1).Count;

    [Fact] // legal — 箱内稀有度分布 60/30/10：3000 次抽取频率 ±3% 容差（SP-8c
    //        F-002 验收口径为 ≥1000 次 ±3%；本测 3000 件，σ≈0.9%，3%≈3.3σ）
    [Trait("Feature", "F-002")]
    public void Rarity_distribution_matches_60_30_10_within_tolerance()
    {
        ChestTable table = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
        var roller = new ChestRewards(table, new SimRng(Seed));
        const int rolls = 3000;
        int common = 0, rare = 0, legendary = 0;
        for (int i = 0; i < rolls; i++)
        {
            foreach (GrantedChestItem item in roller.Open("gold", i * 31 + 7))
            {
                switch (item.Rarity)
                {
                    case RewardRarity.Common: common++; break;
                    case RewardRarity.Rare: rare++; break;
                    case RewardRarity.Legendary: legendary++; break;
                }
            }
        }

        int total = common + rare + legendary;
        Assert.Equal(rolls * 4, total); // gold 档每箱 4 件（表驱动）
        Assert.InRange(common / (double)total, 0.57, 0.63); // 60% ±3%
        Assert.InRange(rare / (double)total, 0.27, 0.33); // 30% ±3%
        Assert.InRange(legendary / (double)total, 0.07, 0.13); // 10% ±3%
    }

    [Fact] // legal — R9 白名单：真实物品池全部六类之内；档位映射 bronze/silver/gold/gold_extra
    [Trait("Feature", "F-002")]
    public void Real_items_stay_inside_the_decorative_whitelist()
    {
        ChestTable table = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
        Assert.All(table.ItemsByRarity.Values.SelectMany(pool => pool),
            item => Assert.Contains(item.RewardClass, RewardWhitelist.Classes));
        Assert.Equal(6, RewardWhitelist.Classes.Count); // 六类冻结（19-r2 L165）

        Assert.Equal("bronze", CampaignProgression.TierToChestId(ObjectiveTier.Bronze));
        Assert.Equal("silver", CampaignProgression.TierToChestId(ObjectiveTier.Silver));
        Assert.Equal("gold", CampaignProgression.TierToChestId(ObjectiveTier.Gold));
        Assert.Equal("gold_extra", CampaignProgression.TierToChestId(ObjectiveTier.GoldExtra));
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — 零权重被拒；合法边界（含临界稀有度全为一件的池）可装载
    [Trait("Feature", "F-002")]
    public void Weight_and_pool_boundaries()
    {
        // 每档至少一件的表合法装载
        Assert.NotNull(ChestRewardsData.Parse(MinimalChests));

        // 单稀有度零权重 → 拒绝（权重必须为正）
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            """{ "rarity_weights": { "common": 60, "rare": 0, "legendary": 10 }, "tiers": [ { "tier": "bronze", "item_rolls": 1 } ], "items": [ { "id": "c:a", "rarity": "common", "reward_class": "decoration" } ] }"""));
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — 白名单外奖励类：装载期拒绝（数值平衡红线，R9/D21-7）
    [Trait("Feature", "F-002")]
    public void Non_decorative_reward_classes_are_rejected_at_parse_time()
    {
        ArgumentException rejection = Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            """
            {
              "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 },
              "tiers": [ { "tier": "bronze", "item_rolls": 1 }, { "tier": "silver", "item_rolls": 1 }, { "tier": "gold", "item_rolls": 1 }, { "tier": "gold_extra", "item_rolls": 1, "bonus_rolls": 1 } ],
              "items": [
                { "id": "c:ok", "rarity": "common", "reward_class": "decoration" },
                { "id": "c:ok2", "rarity": "rare", "reward_class": "title" },
                { "id": "c:ok3", "rarity": "legendary", "reward_class": "voice" },
                { "id": "c:evil", "rarity": "common", "reward_class": "dps_boost" }
              ]
            }
            """));
        Assert.Contains("whitelist", rejection.Message); // 确因白名单拒绝

        Assert.False(RewardWhitelist.IsAllowed("dps_boost"));
        Assert.False(RewardWhitelist.IsAllowed("resource"));
        Assert.Throws<ArgumentNullException>(() => RewardWhitelist.IsAllowed(null!));
    }

    [Fact] // illegal — 形状违规：缺档/缺稀有度池/重复 id/未知开箱档
    [Trait("Feature", "F-002")]
    public void Table_shape_violations_are_rejected()
    {
        // 缺 gold_extra 档
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            """{ "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 }, "tiers": [ { "tier": "bronze", "item_rolls": 1 } ], "items": [ { "id": "c:a", "rarity": "common", "reward_class": "decoration" }, { "id": "c:b", "rarity": "rare", "reward_class": "decoration" }, { "id": "c:c", "rarity": "legendary", "reward_class": "decoration" } ] }"""));

        // 传说池空 → 拒绝
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            """{ "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 }, "tiers": [ { "tier": "bronze", "item_rolls": 1 }, { "tier": "silver", "item_rolls": 1 }, { "tier": "gold", "item_rolls": 1 }, { "tier": "gold_extra", "item_rolls": 1 } ], "items": [ { "id": "c:a", "rarity": "common", "reward_class": "decoration" }, { "id": "c:b", "rarity": "rare", "reward_class": "decoration" } ] }"""));

        // 重复 id → 拒绝
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            """{ "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 }, "tiers": [ { "tier": "bronze", "item_rolls": 1 }, { "tier": "silver", "item_rolls": 1 }, { "tier": "gold", "item_rolls": 1 }, { "tier": "gold_extra", "item_rolls": 1 } ], "items": [ { "id": "c:a", "rarity": "common", "reward_class": "decoration" }, { "id": "c:a", "rarity": "rare", "reward_class": "decoration" }, { "id": "c:b", "rarity": "legendary", "reward_class": "decoration" } ] }"""));

        // 未知档开箱 → 拒绝
        Assert.Throws<ArgumentException>(() => NewRoller().Open("diamond", 1));
    }
}
