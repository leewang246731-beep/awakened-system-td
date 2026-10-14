using System.Diagnostics;
using System.Globalization;
using EraCore.Contracts.Foundation;
using EraCore.Progression;
using EraCore.Tests.Sim;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-177] 宝箱保底原型验证（T-088，C-30）：10k×4 档蒙特卡洛分布报告
// （区间法+Pearson 卡方 [NEW-原型校准：Wald 正态近似 95% CI、df=2 α=0.05]）
// + RNG 抽取路径无偏探针（累积走+均匀 floor，无取模）+ 保底公式
// （同档位连续 17 件无传说→第 18 件必出；稳态传说 10%→11.77%≤12%，游程封顶 17）
// + 白名单合规（保底产物仍装饰类）+ 重放=同 seed+同计数器。三行全覆盖。
// 全部 10k 数字留档 docs/engineering/27-t088-chest-monte-carlo.md（确定性 seed，
// 本文件复跑恒同数——这是 CI 固化的依据）。
public class ChestPityMonteCarloTests
{
    private const int Boxes = 10_000; // 每档开箱数（任务卡 10k；实测 <5s，全量入 CI）
    private const double ChiSquareCriticalDf2Alpha05 = 5.991464547138589; // χ²(df=2, α=0.05) 表值
    private readonly ITestOutputHelper _output;

    public ChestPityMonteCarloTests(ITestOutputHelper output) => _output = output;

    private static string P(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    private static ChestRewards RealRoller() =>
        new(ChestRewardsData.Parse(ReadCampaignContent("chests.json")), new SimRng(Seed));

    /// <summary>最小保底表：阈值 3、青铜单抽（SequenceRng 探针用，结构旋钮）。</summary>
    private const string PityTable =
        """
        {
          "content_type": "DropTable",
          "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 },
          "pity": { "max_misses_without_legendary": 3 },
          "tiers": [
            { "tier": "bronze", "item_rolls": 1 },
            { "tier": "silver", "item_rolls": 1 },
            { "tier": "gold", "item_rolls": 1 },
            { "tier": "gold_extra", "item_rolls": 1, "bonus_rolls": 1 }
          ],
          "items": [
            { "id": "c:common-1", "rarity": "common", "reward_class": "decoration" },
            { "id": "c:common-2", "rarity": "common", "reward_class": "presentation" },
            { "id": "c:rare-1", "rarity": "rare", "reward_class": "title" },
            { "id": "c:legend-1", "rarity": "legendary", "reward_class": "voice" }
          ]
        }
        """;

    private static readonly string[] AllTiers = { "bronze", "silver", "gold", "gold_extra" };

    private sealed record SimStats(int Items, int Common, int Rare, int Legendary, int WorstRun, int Forced, bool AllWhitelisted);

    /// <summary>
    /// 单玩家单档位连开 <paramref name="boxes"/> 箱（计数器跨箱持续——同档位序列），
    /// 逐件统计稀有度、无传说最长游程与白名单合规；boxSeed=Seed+i×977（确定性）。
    /// </summary>
    private static SimStats Simulate(ChestRewards roller, string tier, int boxes, ChestPity? pity)
    {
        int common = 0, rare = 0, legendary = 0, run = 0, worst = 0;
        bool whitelisted = true;
        for (int i = 0; i < boxes; i++)
        {
            foreach (GrantedChestItem item in roller.Open(tier, unchecked(Seed + i * 977), pity))
            {
                switch (item.Rarity)
                {
                    case RewardRarity.Common: common++; run++; break;
                    case RewardRarity.Rare: rare++; run++; break;
                    default: legendary++; run = 0; break;
                }

                worst = Math.Max(worst, run);
                whitelisted &= RewardWhitelist.IsAllowed(item.RewardClass);
            }
        }

        return new SimStats(common + rare + legendary, common, rare, legendary, worst, pity?.ForcedLegendaryCount ?? 0, whitelisted);
    }

    /// <summary>稳态理论（27-t088 报告 §5 推导）：强制件占比 π=q^P·p/(1−q^(P+1))，传说占比 L=p+q·π。</summary>
    private static (double LegendaryShare, double ForcedFraction) SteadyState(ChestTable table, int misses)
    {
        double total = table.OrderedWeights.Sum(line => line.Weight);
        double p = table.OrderedWeights.Single(line => line.Rarity == RewardRarity.Legendary).Weight / total;
        double q = 1 - p;
        double forcedFraction = Math.Pow(q, misses) * p / (1 - Math.Pow(q, misses + 1));
        return (p + q * forcedFraction, forcedFraction);
    }

    // ------------------------------------------------------- legal

    [Fact] // legal — 10k×4 档基线分布：Pearson 卡方（df=2，α=0.05）+ 目标落入实测 95%
    //        Wald 区间（区间法 [NEW-原型校准]）+ 带宽 ±3σ；自然最坏游程实测留档
    [Trait("Feature", "F-177")]
    public void Baseline_10k_per_tier_matches_60_30_10_by_chi_square_and_interval()
    {
        var watch = Stopwatch.StartNew();
        ChestRewards roller = RealRoller();
        ChestTable table = roller.Table;
        double total = table.OrderedWeights.Sum(line => line.Weight);
        foreach (string tier in AllTiers)
        {
            SimStats stats = Simulate(roller, tier, Boxes, pity: null);
            double n = stats.Items;
            Assert.Equal(Boxes * (table.Tiers.Single(row => row.Tier == tier).ItemRolls
                + table.Tiers.Single(row => row.Tier == tier).BonusRolls), stats.Items);

            double chi2 = 0;
            foreach ((RewardRarity rarity, double weight) in table.OrderedWeights)
            {
                double target = weight / total;
                int observed = rarity switch
                {
                    RewardRarity.Common => stats.Common,
                    RewardRarity.Rare => stats.Rare,
                    _ => stats.Legendary,
                };
                double expected = n * target;
                chi2 += (observed - expected) * (observed - expected) / expected;

                // 区间法：实测 95% Wald CI 必须罩住 60/30/10 目标；带宽另加 ±3σ 防漂移
                double share = observed / n;
                double ci = 1.96 * Math.Sqrt(share * (1 - share) / n);
                bool inside = Math.Abs(share - target) <= ci;
                double band = 3 * Math.Sqrt(target * (1 - target) / n);
                Assert.True(inside, $"{tier}/{rarity}: 目标 {P(target)} 落在实测 95% CI 外（share={P(share)} ci=±{P(ci)}）");
                Assert.InRange(share, target - band, target + band);

                _output.WriteLine($"T088-BASELINE tier={tier} rarity={rarity} share={P(share)} ci95=[{P(share - ci)},{P(share + ci)}] target={P(target)} inside={inside}");
            }

            Assert.True(chi2 < ChiSquareCriticalDf2Alpha05, $"{tier}: χ²={P(chi2)} ≥ {P(ChiSquareCriticalDf2Alpha05)}（df=2 α=0.05）");
            Assert.True(stats.WorstRun > table.PityMaxMisses, $"{tier}: 自然最坏游程 {stats.WorstRun} 未超保底阈值（保底无意义化）");
            Assert.True(stats.AllWhitelisted);
            _output.WriteLine($"T088-BASELINE tier={tier} boxes={Boxes} items={stats.Items} common={stats.Common} rare={stats.Rare} legendary={stats.Legendary} chi2={P(chi2)} worstRun={stats.WorstRun}");
        }

        _output.WriteLine($"T088-TIMING baselineMs={watch.ElapsedMilliseconds}");
    }

    [Fact] // legal — RNG 抽取路径无偏探针：稀有度=累积走（u×Σweight 逐段比较），
    //        物品=均匀 floor（Next(0,count)=⌊u×count⌋）——路径无取模运算，边界不引入偏置
    [Trait("Feature", "F-177")]
    public void Rarity_walk_and_item_pick_map_the_unit_interval_without_modulo()
    {
        var table = ChestRewardsData.Parse(MinimalChests); // 池：common×2 / rare×1 / legendary×1，青铜单抽
        // 每次 Open 消耗一对 (稀有度 u, 物品 u)；u 全部取在各段内部（严格避开可数边界点）
        double[] draws =
        {
            0.0, 0.0,      // u→0.0        ⇒ common，池下标 ⌊0×2⌋=0
            0.599, 0.6,    // u×100=59.9   ⇒ common，池下标 ⌊1.2⌋=1
            0.601, 0.0,    // u×100=60.1   ⇒ rare
            0.899, 0.0,    // u×100=89.9   ⇒ rare
            0.901, 0.0,    // u×100=90.1   ⇒ legendary
            0.999, 0.0,    // u×100=99.9   ⇒ legendary
        };
        var roller = new ChestRewards(table, new SequenceRng(draws));
        RewardRarity[] rarities = Enumerable.Range(0, draws.Length / 2)
            .Select(open => roller.Open("bronze", open).Single().Rarity)
            .ToArray();
        Assert.Equal(
            new[] { RewardRarity.Common, RewardRarity.Common, RewardRarity.Rare, RewardRarity.Rare, RewardRarity.Legendary, RewardRarity.Legendary },
            rarities);

        // 物品均匀 floor 探针：同 rare 池两次同 u → 同件；common 池 u=0/0.6 → 两件各一
        var again = new ChestRewards(table, new SequenceRng(draws));
        string[] ids = Enumerable.Range(0, draws.Length / 2)
            .Select(open => again.Open("bronze", open).Single().ItemId)
            .ToArray();
        Assert.Equal(new[] { "c:common-1", "c:common-2", "c:rare-1", "c:rare-1", "c:legend-1", "c:legend-1" }, ids);
    }

    [Fact] // legal — 保底公式定案（SP-8c）：阈值取自内容表（RL7），稳态传说占比
    //        L(17)=11.77% ≤ 12%（不过度膨胀）且 L(16)>12%（17=满足约束的最小整数）
    [Trait("Feature", "F-177")]
    public void Pity_threshold_is_the_minimal_integer_under_the_12_percent_bound()
    {
        ChestTable table = RealRoller().Table;
        Assert.Equal(17, table.PityMaxMisses); // chests.json 定案值（27-t088 报告敏感性表）

        (double share, double forcedFraction) = SteadyState(table, table.PityMaxMisses);
        Assert.InRange(share, 0.10, 0.12); // 10%→11.77%：有提升且不过度膨胀（任务卡 ≤12% 约束）
        Assert.InRange(forcedFraction, 0.01, 0.03); // 稳态强制件 ≈1.96%

        (double oneLower, _) = SteadyState(table, table.PityMaxMisses - 1);
        Assert.True(oneLower > 0.12, $"阈值 {table.PityMaxMisses - 1} 的稳态传说 {P(oneLower)} 应已越过 12% 约束（最小性）");
        _output.WriteLine($"T088-THEORY threshold={table.PityMaxMisses} legendaryShare={P(share)} forcedFraction={P(forcedFraction)}");
    }

    [Fact] // legal — 保底后 10k×4 档：最坏游程封顶=阈值；传说占比带（理论 11.77%）；
    //        强制件占比带；保底产物仍白名单（R9）
    [Trait("Feature", "F-177")]
    public void Pity_10k_per_tier_caps_worst_run_and_keeps_legendary_under_bound()
    {
        var watch = Stopwatch.StartNew();
        ChestRewards roller = RealRoller();
        ChestTable table = roller.Table;
        (double legendaryShare, double forcedFraction) = SteadyState(table, table.PityMaxMisses);
        foreach (string tier in AllTiers)
        {
            var pity = new ChestPity(table.PityMaxMisses);
            SimStats stats = Simulate(roller, tier, Boxes, pity);

            int itemsPerBox = table.Tiers.Single(row => row.Tier == tier).ItemRolls
                + table.Tiers.Single(row => row.Tier == tier).BonusRolls;
            Assert.Equal(Boxes * itemsPerBox, stats.Items);
            Assert.True(stats.Forced > 0, $"{tier}: 10k 序列内保底从未触发（断言失去意义）");
            Assert.Equal(table.PityMaxMisses, stats.WorstRun); // 游程恰被封顶在阈值（触发→下一件必传说）
            Assert.InRange(stats.Legendary / (double)stats.Items, legendaryShare - 0.004, legendaryShare + 0.004); // 理论 ±0.4pp（≥4σ）
            Assert.InRange(stats.Forced / (double)stats.Items, forcedFraction - 0.004, forcedFraction + 0.004);
            Assert.True(stats.AllWhitelisted, $"{tier}: 保底产物越出装饰白名单");

            double n = stats.Items;
            _output.WriteLine(
                $"T088-PITY tier={tier} boxes={Boxes} items={stats.Items} common={stats.Common}({P(stats.Common / n)}) rare={stats.Rare}({P(stats.Rare / n)}) legendary={stats.Legendary}({P(stats.Legendary / n)}) forced={stats.Forced} worstRun={stats.WorstRun}");
        }

        _output.WriteLine($"T088-TIMING pityMs={watch.ElapsedMilliseconds}");
    }

    [Fact] // legal — 保底语义单元：连续 3 件无传说→第 4 件强制传说并复位；档位间计数隔离；
    //        强制件从传说池均匀抽取（白名单类）；遥测计数=1
    [Trait("Feature", "F-177")]
    public void Pity_forces_at_threshold_resets_and_isolates_tiers()
    {
        var table = ChestRewardsData.Parse(PityTable);
        var pity = new ChestPity(table.PityMaxMisses);
        var roller = new ChestRewards(table, new SequenceRng(new double[14])); // 5+1+1 次青铜/黄金单抽，每次 2 draw，u=0 ⇒ 恒普通
        RewardRarity[] sequence = Enumerable.Range(0, 5)
            .Select(open => roller.Open("bronze", open, pity).Single())
            .Select(item => item.Rarity)
            .ToArray();
        Assert.Equal(new[] { RewardRarity.Common, RewardRarity.Common, RewardRarity.Common, RewardRarity.Legendary, RewardRarity.Common }, sequence);
        Assert.Equal(1, pity.ForcedLegendaryCount); // 只有第 4 件是保底强制
        Assert.Equal(1, pity.MissesByTier["bronze"]); // 强制传说复位后第 5 件又计 1

        GrantedChestItem forcedItem = roller.Open("bronze", 99, new ChestPity(3, new Dictionary<string, int> { ["bronze"] = 3 })).Single();
        Assert.Equal(RewardRarity.Legendary, forcedItem.Rarity);
        Assert.Equal("c:legend-1", forcedItem.ItemId); // 保底仍走传说池
        Assert.Contains(forcedItem.RewardClass, RewardWhitelist.Classes);

        Assert.False(pity.ForcesLegendary("gold")); // 青铜计数不外溢到黄金
        GrantedChestItem goldItem = roller.Open("gold", 1, pity).Single();
        Assert.Equal(RewardRarity.Common, goldItem.Rarity); // 自然抽取（黄金计数从 0 起）
    }

    [Fact] // legal — 重放语义：同 seed+同计数器→同箱；计数器不同→保底边界处分叉
    //        （检查点必须持久化 MissesByTier——持久化语义的可检验形式）
    [Trait("Feature", "F-177")]
    public void Pity_replay_requires_seed_and_counters()
    {
        var table = ChestRewardsData.Parse(PityTable);
        var first = new ChestRewards(table, new SimRng(Seed));
        var second = new ChestRewards(table, new SimRng(Seed));
        Assert.Equal(first.Open("bronze", 42, new ChestPity(3)), second.Open("bronze", 42, new ChestPity(3))); // 同计数器=同箱

        // 同 seed、计数器不同（宿主丢了保底状态）→ 首件被强制，序列分叉
        var withState = new ChestRewards(table, new SequenceRng(new double[2])); // u=0 ⇒ 恒普通
        var lostState = new ChestRewards(table, new SequenceRng(new double[2]));
        RewardRarity restored = withState.Open("bronze", 7, new ChestPity(3, new Dictionary<string, int> { ["bronze"] = 3 })).Single().Rarity;
        RewardRarity fresh = lostState.Open("bronze", 7, new ChestPity(3)).Single().Rarity;
        Assert.Equal(RewardRarity.Legendary, restored);
        Assert.Equal(RewardRarity.Common, fresh);

        // 双参重载与三参 null 恒等（T-073 既有调用面零改动）
        var compat = new ChestRewards(ChestRewardsData.Parse(MinimalChests), new SimRng(Seed));
        Assert.Equal(compat.Open("gold_extra", 5), compat.Open("gold_extra", 5, pity: null));
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — 阈值=1（最小合法）装载且第 2 件即强制；无 pity 块=0（旧表禁用）；
    //        真实表=17；计数持久化恢复在阈值上即刻生效
    [Trait("Feature", "F-177")]
    public void Pity_threshold_boundaries()
    {
        ChestTable minTable = ChestRewardsData.Parse(
            PityTable.Replace("\"max_misses_without_legendary\": 3", "\"max_misses_without_legendary\": 1"));
        Assert.Equal(1, minTable.PityMaxMisses);
        var pity = new ChestPity(1);
        var roller = new ChestRewards(minTable, new SequenceRng(new double[4])); // 2 次青铜单抽，u=0 ⇒ 恒普通
        Assert.Equal(RewardRarity.Common, roller.Open("bronze", 1, pity).Single().Rarity); // 计数 0→自然
        Assert.Equal(RewardRarity.Legendary, roller.Open("bronze", 2, pity).Single().Rarity); // 连 1 件→强制

        Assert.Equal(0, ChestRewardsData.Parse(MinimalChests).PityMaxMisses); // 旧表无 pity 块 → 禁用
        Assert.Equal(17, RealRoller().Table.PityMaxMisses); // chests.json 定案值

        var restored = new ChestPity(17, new Dictionary<string, int> { ["gold"] = 17 });
        Assert.True(restored.ForcesLegendary("gold")); // 恢复的计数器即刻生效
        Assert.False(restored.ForcesLegendary("gold_extra"));
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — 违规 pity：阈值 0/负/缺失被装载期拒绝；ChestPity 非法阈值拒绝；null 档位拒绝
    [Trait("Feature", "F-177")]
    public void Pity_violations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            PityTable.Replace("\"max_misses_without_legendary\": 3", "\"max_misses_without_legendary\": 0")));
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            PityTable.Replace("\"max_misses_without_legendary\": 3", "\"max_misses_without_legendary\": -5")));
        Assert.Throws<ArgumentException>(() => ChestRewardsData.Parse(
            PityTable.Replace("\"max_misses_without_legendary\": 3", "\"other_key\": 3"))); // 块内缺失阈值

        Assert.Throws<ArgumentException>(() => new ChestPity(0));
        Assert.Throws<ArgumentException>(() => new ChestPity(-1));

        var pity = new ChestPity(3);
        Assert.Throws<ArgumentNullException>(() => pity.ForcesLegendary(null!));
        Assert.Throws<ArgumentNullException>(() => pity.Record(null!, RewardRarity.Common));
    }

    /// <summary>固定序列 IRng 端口 Fake（CS-3.4 白名单）：按序回放 double，耗尽即抛（禁静默回绕）。</summary>
    private sealed class SequenceRng(double[] values) : IRng
    {
        private readonly double[] _values = values;
        private int _next;

        public int Next(int minValue, int maxValue) => minValue + (int)(NextDouble() * (maxValue - (long)minValue));

        public double NextDouble()
        {
            if (_next >= _values.Length)
            {
                throw new InvalidOperationException("SequenceRng 耗尽（测试装置 draw 数预估不足）");
            }

            return _values[_next++];
        }

        public void Reseed(long seed)
        {
            // Open 每箱 Reseed：保底探针需要跨箱持续流，忽略重播种（K-1 端口 Fake 自由度）
        }
    }
}
