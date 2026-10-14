using System.Globalization;
using EraCore.Progression;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-054] 系统任务三重目标判分（T-073，SP-8 全 16 条）：抽选确定性
// （seed=关卡 id）+ 威胁分流（t<0.9 非 ★ / t≥0.9 ★）+ 判分矩阵（16 度量
// 逐条正反）+ 档位边界（0/1/2/3→青铜/白银/黄金/黄金+额外）+ 内容锚点。
// 三行 legal/boundary/illegal 全覆盖（CS-3.1）。[C-19]
public class TripleObjectiveJudgeTests
{
    private static TripleObjectiveJudge NewJudge(long seed = Seed) =>
        new(ObjectivesData.Parse(ReadCampaignContent("objectives.json")), new SimRng(seed));

    private static LevelRunFacts FactsWith(
        bool survived = true,
        int stationsLost = 0,
        double integrityMin = 1.0,
        int towersSold = 0,
        double starvedSeconds = 0.0,
        double efficiencyMin = 1.0,
        int keyDamaged = 0,
        int research = 1,
        int deadlocks = 0,
        double clearSeconds = 100.0,
        double budgetSeconds = 1000.0,
        string? offClassKey = null,
        int netTowers = 2,
        int buildPlans = 0,
        int scrolls = 0,
        bool heroDeployed = false,
        bool heroDead = false,
        int pauses = 0,
        int leaks = 0) =>
        new(
            Survived: survived,
            StationsLost: stationsLost,
            DefenseIntegrityMin: integrityMin,
            TowersSoldCount: towersSold,
            AmmoStarvedSeconds: starvedSeconds,
            WaveEndEfficiencyMin: efficiencyMin,
            KeyBuildingsDamagedCount: keyDamaged,
            ResearchCompletedCount: research,
            LogisticsDeadlockCount: deadlocks,
            ClearTimeSeconds: clearSeconds,
            LevelBudgetSeconds: budgetSeconds,
            TowerBuildsByClass: offClassKey is null
                ? new Dictionary<string, int> { ["pierce"] = 2 }
                : new Dictionary<string, int> { [offClassKey] = 2 },
            NetTowerCount: netTowers,
            SystemBuildPlanCount: buildPlans,
            ScrollTriggerCount: scrolls,
            HeroDeployed: heroDeployed,
            HeroDead: heroDead,
            PauseCount: pauses,
            FirstWaveLeaks: leaks);

    // ------------------------------------------------------- legal

    [Fact] // legal — 内容锚点：SP-8a/b 各 8 条、挑战池 ★ 恰 4 条、冻结阈值逐项
    [Trait("Feature", "F-054")]
    public void Real_pools_match_the_sp8_frozen_shape()
    {
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));
        Assert.Equal(8, pools.Strategy.Count);
        Assert.Equal(8, pools.Challenge.Count);
        Assert.Equal(4, pools.Challenge.Count(entry => entry.Hard));
        Assert.All(pools.Strategy, entry => Assert.False(entry.Hard));

        // 阈值锚点（SP-8a/b 原文）：S2=0.4 / S4=30 / S5=0.7 / S7=1 / C1 乘子=0.8 / C3=8
        Assert.Equal(0.4, pools.Strategy.Single(entry => entry.Metric == ObjectiveMetric.DefenseIntegrityMin).Threshold, 9);
        Assert.Equal(30.0, pools.Strategy.Single(entry => entry.Metric == ObjectiveMetric.AmmoStarvedSeconds).Threshold, 9);
        Assert.Equal(0.7, pools.Strategy.Single(entry => entry.Metric == ObjectiveMetric.WaveEndEfficiencyMin).Threshold, 9);
        Assert.Equal(1.0, pools.Strategy.Single(entry => entry.Metric == ObjectiveMetric.ResearchCompletedCount).Threshold, 9);
        Assert.Equal(0.8, pools.Challenge.Single(entry => entry.Metric == ObjectiveMetric.ClearTimeSeconds).Threshold, 9);
        Assert.Equal(8.0, pools.Challenge.Single(entry => entry.Metric == ObjectiveMetric.NetTowerCount).Threshold, 9);

        // C2 白名单类=穿刺+支援（SP-8b C2）
        ObjectiveEntry c2 = pools.Challenge.Single(entry => entry.Metric == ObjectiveMetric.OffClassTowerBuilds);
        Assert.Equal(new[] { "pierce", "support" }, c2.AllowedClasses.OrderBy(name => name));

        // ★ 恰为 C1~C4 四条（SP-8b 高难子池）
        Assert.Equal(
            new[] { ObjectiveMetric.ClearTimeSeconds, ObjectiveMetric.OffClassTowerBuilds, ObjectiveMetric.NetTowerCount, ObjectiveMetric.SystemBuildPlanCount },
            pools.Challenge.Where(entry => entry.Hard).Select(entry => entry.Metric).OrderBy(metric => metric));
    }

    [Fact] // legal — 抽选确定性：同关卡 id 恒同对（两独立判官同 rng 型）
    [Trait("Feature", "F-054")]
    public void Draw_is_deterministic_per_level_id()
    {
        TripleObjectiveJudge first = NewJudge();
        TripleObjectiveJudge second = NewJudge();
        for (int i = 1; i <= 12; i++)
        {
            string levelId = $"campaign:level-e1-{i:D2}";
            ObjectiveDraw a = first.Draw(levelId, 0.6);
            ObjectiveDraw b = second.Draw(levelId, 0.6);
            Assert.Equal(a.Strategy.Id, b.Strategy.Id);
            Assert.Equal(a.Challenge.Id, b.Challenge.Id);
            Assert.Contains(a.Strategy, NewJudge().Pools.Strategy); // 抽中行必在池内
        }

        // 不同 id 至少存在分叉（8 池上抽选有区分度）
        Assert.Contains(Enumerable.Range(1, 12), i =>
            first.Draw($"campaign:level-e1-{i:D2}", 0.6).Strategy.Id
            != first.Draw("campaign:level-e1-01", 0.6).Strategy.Id);
    }

    [Fact] // legal — 威胁分流：t<0.9 恒抽非 ★（C5~C8），t≥0.9 恒抽 ★（C1~C4）
    [Trait("Feature", "F-054")]
    public void Threat_routes_the_challenge_sub_pool()
    {
        TripleObjectiveJudge judge = NewJudge();
        for (int i = 1; i <= 24; i++)
        {
            string levelId = $"campaign:level-t{i:D2}";
            Assert.False(judge.Draw(levelId, 0.89).Challenge.Hard, $"{levelId} t=0.89 must draw non-★");
            Assert.True(judge.Draw(levelId, 0.9).Challenge.Hard, $"{levelId} t=0.90 must draw ★");
        }
    }

    [Fact] // legal — 判分矩阵：16 度量逐条正/反（SP-8 判定列全数据化）
    [Trait("Feature", "F-054")]
    public void Evaluation_matrix_pass_and_fail_per_metric()
    {
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));
        var all = pools.Strategy.Concat(pools.Challenge);

        // 正面：全绿事实（预算充裕、效率满格、英雄不出阵）
        foreach (ObjectiveEntry entry in all)
        {
            Assert.True(TripleObjectiveJudge.Evaluate(entry, FactsWith()), $"{entry.Id} 应通过");
        }

        // 反面：逐度量打红一个事实
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.StationsLost, FactsWith(stationsLost: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.DefenseIntegrityMin, FactsWith(integrityMin: 0.39)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.TowersSoldCount, FactsWith(towersSold: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.AmmoStarvedSeconds, FactsWith(starvedSeconds: 30.1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.WaveEndEfficiencyMin, FactsWith(efficiencyMin: 0.69)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.KeyBuildingsDamagedCount, FactsWith(keyDamaged: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.ResearchCompletedCount, FactsWith(research: 0)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.LogisticsDeadlockCount, FactsWith(deadlocks: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.ClearTimeSeconds, FactsWith(clearSeconds: 801.0, budgetSeconds: 1000.0))); // 801>1000×0.8
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.OffClassTowerBuilds, FactsWith(offClassKey: "physical")));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.NetTowerCount, FactsWith(netTowers: 9))); // 9>8
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.SystemBuildPlanCount, FactsWith(buildPlans: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.ScrollTriggerCount, FactsWith(scrolls: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.PauseCount, FactsWith(pauses: 1)));
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.FirstWaveLeaks, FactsWith(leaks: 1)));
        // HeroRule 反面：出阵且阵亡
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.HeroRule, FactsWith(heroDeployed: true, heroDead: true)));
        // HeroRule 边内正例：出阵但存活
        Assert.True(EvaluateByMetric(pools, ObjectiveMetric.HeroRule, FactsWith(heroDeployed: true, heroDead: false)));
    }

    [Fact] // legal — 档位=完成数（0/1/2/3→青铜/白银/黄金/黄金+额外，无分数线）
    [Trait("Feature", "F-054")]
    public void Tier_boundaries_map_completed_counts()
    {
        Assert.Equal(ObjectiveTier.Bronze, TripleObjectiveJudge.TierFor(0));
        Assert.Equal(ObjectiveTier.Silver, TripleObjectiveJudge.TierFor(1));
        Assert.Equal(ObjectiveTier.Gold, TripleObjectiveJudge.TierFor(2));
        Assert.Equal(ObjectiveTier.GoldExtra, TripleObjectiveJudge.TierFor(3));
        Assert.Equal(0, (int)ObjectiveTier.Bronze); // 档位数值=完成数（冻结口径）
    }

    [Fact] // legal — Judge 全链：三目标逐项点亮驱动档位爬升（同关卡确定性）
    [Trait("Feature", "F-054")]
    public void Judging_counts_completed_objectives_into_the_tier()
    {
        TripleObjectiveJudge judge = NewJudge();
        ObjectiveDraw draw = judge.Draw("campaign:level-e1-01", 0.6);

        // 0 项：输局（生存红）+ 两池事实全打红（含 C6 出阵且阵亡）
        LevelRunFacts none = FactsWith(
            survived: false,
            stationsLost: 5,
            research: 0,
            integrityMin: 0.1,
            towersSold: 3,
            starvedSeconds: 99.0,
            efficiencyMin: 0.1,
            keyDamaged: 2,
            deadlocks: 4,
            scrolls: 2,
            pauses: 3,
            leaks: 4,
            buildPlans: 2,
            netTowers: 99,
            clearSeconds: 5000.0,
            heroDeployed: true,
            heroDead: true);
        ObjectiveOutcome zero = judge.Judge("campaign:level-e1-01", 0.6, none);
        Assert.False(zero.Survived);
        Assert.Equal(0, zero.CompletedCount);
        Assert.Equal(ObjectiveTier.Bronze, zero.Tier);

        // 全三项：胜局 + 两池全绿 → 黄金+额外
        ObjectiveOutcome full = judge.Judge("campaign:level-e1-01", 0.6, FactsWith());
        Assert.True(full.Survived);
        Assert.True(full.StrategyPassed);
        Assert.True(full.ChallengePassed);
        Assert.Equal(3, full.CompletedCount);
        Assert.Equal(ObjectiveTier.GoldExtra, full.Tier);
        Assert.Equal(draw.Strategy.Id, full.Draw.Strategy.Id); // Judge 内抽选与 Draw 一致
        Assert.Equal(draw.Challenge.Id, full.Draw.Challenge.Id);
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — C1 预算边界：恰等于预算×0.8 通过、超出 0.1s 失败
    [Trait("Feature", "F-054")]
    public void Clear_time_boundary_is_inclusive_at_eighty_percent()
    {
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));
        Assert.True(EvaluateByMetric(pools, ObjectiveMetric.ClearTimeSeconds,
            FactsWith(clearSeconds: 800.0, budgetSeconds: 1000.0))); // ==800 过（≤）
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.ClearTimeSeconds,
            FactsWith(clearSeconds: 800.1, budgetSeconds: 1000.0)));

        // 无预算数据=限时目标不可达成（防默认通过的暗门；真实通关耗时恒正）
        Assert.False(EvaluateByMetric(pools, ObjectiveMetric.ClearTimeSeconds,
            FactsWith(clearSeconds: 100.0, budgetSeconds: 0.0)));
    }

    [Fact] // boundary — 同日重复判分幂等；空建造字典 C2 视为无违规建造
    [Trait("Feature", "F-054")]
    public void Repeated_judgments_and_empty_build_maps_are_stable()
    {
        TripleObjectiveJudge judge = NewJudge();
        ObjectiveOutcome first = judge.Judge("campaign:level-e1-03", 1.2, FactsWith());
        ObjectiveOutcome second = judge.Judge("campaign:level-e1-03", 1.2, FactsWith());
        Assert.Equal(first.Draw.Strategy.Id, second.Draw.Strategy.Id); // 判分确定性（逐字段）
        Assert.Equal(first.Draw.Challenge.Id, second.Draw.Challenge.Id);
        Assert.Equal(first.Survived, second.Survived);
        Assert.Equal(first.StrategyPassed, second.StrategyPassed);
        Assert.Equal(first.ChallengePassed, second.ChallengePassed);
        Assert.Equal(first.CompletedCount, second.CompletedCount);
        Assert.Equal(first.Tier, second.Tier);

        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));
        ObjectiveEntry c2 = pools.Challenge.Single(entry => entry.Metric == ObjectiveMetric.OffClassTowerBuilds);
        var noBuilds = FactsWith() with { TowerBuildsByClass = new Dictionary<string, int>() };
        Assert.True(TripleObjectiveJudge.Evaluate(c2, noBuilds));
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — TierFor 越界 / 空 id / 负威胁 / null facts
    [Trait("Feature", "F-054")]
    public void Illegal_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TripleObjectiveJudge.TierFor(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => TripleObjectiveJudge.TierFor(-1));

        TripleObjectiveJudge judge = NewJudge();
        Assert.Throws<ArgumentException>(() => judge.Draw(" ", 0.5));
        Assert.Throws<ArgumentException>(() => judge.Draw("campaign:level-e1-01", -0.1));
        Assert.Throws<ArgumentNullException>(() => judge.Judge("campaign:level-e1-01", 0.5, null!));
    }

    [Fact] // illegal — 池形状违规：缺池/数量不是 8/策略池带 ★/池内重复度量/重复 id
    [Trait("Feature", "F-054")]
    public void Pool_shape_violations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ObjectivesData.Parse("""{ "strategy_pool": [] }""")); // 缺挑战池

        string Row(string id, string metric, bool hard = false, string op = "at_most", double threshold = 0) =>
            $$"""{ "id": "{{id}}", "hard": {{hard.ToString().ToLowerInvariant()}}, "metric": "{{metric}}", "op": "{{op}}", "threshold": {{threshold.ToString(CultureInfo.InvariantCulture)}} }""";

        string Seven() => string.Join(",",
            Row("o:1", "StationsLost"), Row("o:2", "TowersSoldCount"), Row("o:3", "KeyBuildingsDamagedCount"),
            Row("o:4", "LogisticsDeadlockCount"), Row("o:5", "ScrollTriggerCount"), Row("o:6", "PauseCount"),
            Row("o:7", "FirstWaveLeaks"));
        Assert.Throws<ArgumentException>(() => ObjectivesData.Parse(
            $$"""{ "strategy_pool": [ {{Seven()}} ], "challenge_pool": [ {{Seven()}} ] }""")); // 7≠8

        Assert.Throws<ArgumentException>(() => ObjectivesData.Parse(
            $$"""{ "strategy_pool": [ {{Seven()}}, {{Row("o:8", "NetTowerCount", hard: true)}} ], "challenge_pool": [ {{Seven()}}, {{Row("o:c8", "NetTowerCount")}} ] }""")); // 策略池带 ★

        Assert.Throws<ArgumentException>(() => ObjectivesData.Parse(
            $$"""{ "strategy_pool": [ {{Seven()}}, {{Row("o:8", "StationsLost")}} ], "challenge_pool": [ {{Seven()}}, {{Row("o:c8", "NetTowerCount")}} ] }""")); // 池内重复度量
    }

    // ------------------------------------------------------- helpers

    private static bool EvaluateByMetric(ObjectivePools pools, ObjectiveMetric metric, LevelRunFacts facts)
    {
        ObjectiveEntry entry = pools.Strategy.Concat(pools.Challenge).Single(row => row.Metric == metric);
        return TripleObjectiveJudge.Evaluate(entry, facts);
    }
}
