using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-080] SP-7 组合规则单测：波计划组装（计数/名册序/间隔/HP 乘子/词缀修饰）、
// Boss 节奏、SP-7c+词缀加权记分、入口配置校验（任选时代/混编池/奇观带入）。
public class EndlessRulesTests
{
    // ------------------------------------------------ 波计划组装 -------

    [Fact] // legal：PlanWave 组装——名册序固定（常规→精英→Boss）、计数=三线合计、字段同源
    public void PlanWave_composes_the_three_lines_in_fixed_order()
    {
        var config = EndlessFixture.Config();

        EndlessWavePlan plan = EndlessRules.PlanWave(20, config, new LcgRng(EndlessFixture.Seed));

        Assert.Equal(20, plan.Wave);
        Assert.Equal(
            EndlessWaveFormula.Count(20, config.WaveBaseCount, config.WaveBegin, config.WaveSpacing, config.WaveScaling, config.Threat.Count),
            plan.RegularCount);
        Assert.Equal(EndlessWaveFormula.EliteCount(20), plan.EliteCount);
        Assert.Equal(1, plan.BossCount); // 波 20=节奏波
        Assert.Equal(plan.TotalCount, plan.Roster.Count);
        Assert.Equal(Campaign.WaveIntervalSeconds(config.ThreatLevel), plan.IntervalSeconds, 9);
        Assert.Equal(config.Threat.Hp, plan.HpMultiplier, 12);
        EndlessEnemyModifiers expectedModifiers = EndlessAffixes.Modifiers(20, config.Affixes);
        Assert.Equal(expectedModifiers.ActiveKinds, plan.Modifiers.ActiveKinds);
        Assert.Equal(expectedModifiers.Stacks, plan.Modifiers.Stacks);
        Assert.Equal(expectedModifiers.SpeedMultiplier, plan.Modifiers.SpeedMultiplier);
        Assert.Equal(expectedModifiers.ArmorMultiplier, plan.Modifiers.ArmorMultiplier);
        Assert.Equal(expectedModifiers.HpMultiplier, plan.Modifiers.HpMultiplier);

        // 名册序：[Regular ×N][Elite ×M][Boss ×1]，与线的计数一一对应
        EndlessEnemyRole[] roles = plan.Roster.Select(spawn => spawn.Role).ToArray();
        int firstElite = IndexOf(roles, EndlessEnemyRole.Elite);
        int firstBoss = IndexOf(roles, EndlessEnemyRole.Boss);
        Assert.Equal(plan.RegularCount, firstElite < 0 ? roles.Length : firstElite);
        Assert.Equal(plan.RegularCount + plan.EliteCount, firstBoss);
        Assert.All(plan.Roster.Take(plan.RegularCount), spawn => Assert.Equal(EndlessEnemyRole.Regular, spawn.Role));
        Assert.All(plan.Roster.Skip(plan.RegularCount).Take(plan.EliteCount), spawn => Assert.Equal(EndlessEnemyRole.Elite, spawn.Role));
        Assert.Equal(EndlessEnemyRole.Boss, plan.Roster[^1].Role);
    }

    private static int IndexOf(EndlessEnemyRole[] roles, EndlessEnemyRole role)
    {
        for (int i = 0; i < roles.Length; i++)
        {
            if (roles[i] == role)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact] // legal：同 rng 种子→同计划（PlanWave 纯确定性：抽签序固定、无隐藏状态）
    public void PlanWave_is_deterministic_per_seed()
    {
        var config = EndlessFixture.Config();
        for (int wave = 1; wave <= 45; wave++)
        {
            EndlessWavePlan a = EndlessRules.PlanWave(wave, config, new LcgRng(EndlessFixture.Seed));
            EndlessWavePlan b = EndlessRules.PlanWave(wave, config, new LcgRng(EndlessFixture.Seed));
            Assert.Equal(a.Roster, b.Roster);
            Assert.Equal(a.Wave, b.Wave);
            Assert.Equal(a.TotalCount, b.TotalCount);
        }
    }

    [Theory] // legal：Boss 节奏 [NEW-原型校准]——节奏波 1 只、其余 0、波 0/负数 0
    [InlineData(0, 10, 0)]
    [InlineData(10, 10, 1)]
    [InlineData(15, 10, 0)]
    [InlineData(20, 10, 1)]
    [InlineData(7, 7, 1)]
    [InlineData(-3, 10, 0)]
    public void Boss_cadence_hits_every_n_waves(int wave, int every, int expected)
    {
        Assert.Equal(expected, EndlessRules.BossCount(wave, every));
    }

    [Fact] // boundary：负波拒绝；空 rng/配置拒绝
    public void PlanWave_guards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessRules.PlanWave(-1, EndlessFixture.Config(), new LcgRng(1)));
        Assert.Throws<ArgumentNullException>(
            () => EndlessRules.PlanWave(1, EndlessFixture.Config(), null!));
        Assert.Throws<ArgumentNullException>(
            () => EndlessRules.PlanWave(1, null!, new LcgRng(1)));
    }

    // ------------------------------------------------ 记分 -------------

    [Theory] // legal：SP-7c 冻结式+词缀加权——分数=波×100+精英×20+Boss×200+Σ(词缀类次×10)
    [InlineData(40, 0, 0, 0)]        // 40 波内零词缀加成
    [InlineData(50, 5, 1, 100)]      // 41..50=10 波×1 类
    [InlineData(60, 5, 1, 300)]      // +10×2
    [InlineData(100, 0, 0, 1800)]    // +10×1+10×2+10×3+30×4
    public void Score_formula_sp7c_plus_affix_weighting(int waves, int elites, int bosses, long expectedAffix)
    {
        Assert.Equal(expectedAffix, EndlessRules.AffixBonusPoints(waves, EndlessScoreWeights.Default));
        long score = EndlessRules.Score(waves, elites, bosses, EndlessScoreWeights.Default, 0);
        Assert.Equal((waves * 100L) + (elites * 20L) + (bosses * 200L) + expectedAffix, score);
    }

    [Fact] // legal：词缀加权逐波核对——41 波首启 +10/波、51 波起 +20/波（逐波增量=类数×10）
    public void Affix_bonus_climbs_as_kinds_activate()
    {
        long at40 = EndlessRules.AffixBonusPoints(40, EndlessScoreWeights.Default);
        long at41 = EndlessRules.AffixBonusPoints(41, EndlessScoreWeights.Default);
        long at50 = EndlessRules.AffixBonusPoints(50, EndlessScoreWeights.Default);
        long at51 = EndlessRules.AffixBonusPoints(51, EndlessScoreWeights.Default);

        Assert.Equal(0, at40);
        Assert.Equal(10, at41 - at40);    // 迅捷点亮
        Assert.Equal(100, at50);
        Assert.Equal(20, at51 - at50);    // 硬化点亮→每波两类
    }

    [Fact] // boundary：负波数/负战绩拒绝
    public void Score_rejects_negative_tallies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessRules.Score(10, -1, 0, EndlessScoreWeights.Default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessRules.Score(10, 0, -1, EndlessScoreWeights.Default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessRules.Score(-1, 0, 0, EndlessScoreWeights.Default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessRules.AffixBonusPoints(-1, EndlessScoreWeights.Default));
        Assert.Throws<ArgumentNullException>(
            () => EndlessRules.Score(10, 0, 0, null!, 0));
    }

    // ------------------------------------------------ 入口规则校验 -----

    [Fact] // boundary：入口规则校验——时代越界/负威胁/非正节奏/缺精英或 Boss 行/奇观 id 重复或空白
    public void Config_validates_entry_rules()
    {
        _ = EndlessFixture.Config(); // 基线构造通过
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 0, 0.9, 6, 1, 3, 1.0, 10,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            Array.Empty<string>())); // 时代 0
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 7, 0.9, 6, 1, 3, 1.0, 10,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            Array.Empty<string>())); // 时代 7
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 1, -0.1, 6, 1, 3, 1.0, 10,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            Array.Empty<string>())); // 负威胁
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 1, 0.9, 6, 1, 3, 1.0, 0,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            Array.Empty<string>())); // 非正 Boss 节奏
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 1, 0.9, 6, 1, 3, 1.0, 10,
            new EndlessEnemyPool(new[]
            {
                new EndlessEnemyPoolEntry("era1:enemy-skirmisher", 1, EndlessEnemyRole.Regular, 1.0),
            }),
            EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>())); // 缺精英/Boss 行
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 1, 0.9, 6, 1, 3, 1.0, 10,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            new[] { "wonder:a", "wonder:a" })); // 重复奇观
        Assert.Throws<ArgumentException>(() => new EndlessRunConfig(
            EndlessFixture.Seed, 1, 0.9, 6, 1, 3, 1.0, 10,
            EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default,
            new[] { " " })); // 空白奇观 id
    }

    [Fact] // legal：入口三件套——任选时代（1..6 均可构造）、混编池冻结、奇观带入冻结保序
    public void Entry_rules_freeze_their_data()
    {
        var wonders = new[] { "wonder:pyramid", "wonder:internet" };
        foreach (int era in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var eraConfig = new EndlessRunConfig(
                EndlessFixture.Seed, era, 0.9, 6, 1, 3, 1.0, 10,
                EndlessFixture.MixedPool(), EndlessAffixTuning.Default, EndlessScoreWeights.Default, wonders);
            Assert.Equal(era, eraConfig.StartEraIndex);
            Assert.Equal(new[] { 1, 2, 3 }, eraConfig.Pool.EraIndexes); // 混编跨时代冻结
            Assert.Equal(wonders, eraConfig.WonderIds);                 // 奇观带入保序
            Assert.Equal(EndlessFixture.Seed, eraConfig.Seed); // xUnit2000: 常量位=expected
        }
    }
}
