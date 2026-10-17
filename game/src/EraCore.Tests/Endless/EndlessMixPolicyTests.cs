using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143] 混编敌人（高波次跨时代混编敌池——权重参数驱动）：
// MixFromWave 以下=开局时代基准池（任选时代的池面），以上=双权重钮跨时代
// 混编（每线内归一）；开局时代缺该线行时整桶回退；守卫与确定性。
public class EndlessMixPolicyTests
{
    private const long Seed = 20261430;

    /// <summary>三时代三线小池：E1 常规 2 行 + E2 常规 1 行 + 各线精英/Boss。</summary>
    private static EndlessEnemyPool Pool() => new(new[]
    {
        new EndlessEnemyPoolEntry("era1:enemy-a", 1, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-b", 1, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-c", 2, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-elite", 1, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-elite", 2, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-boss", 1, EndlessEnemyRole.Boss, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-boss", 2, EndlessEnemyRole.Boss, 1.0),
    });

    private static EndlessRunConfig Config(EndlessMixPolicy mix, long seed = Seed) => new(
        seed,
        startEraIndex: 1,
        threatLevel: 0.9,
        waveBaseCount: 6,
        waveBegin: 1,
        waveSpacing: 3,
        waveScaling: 1.0,
        bossEveryWaves: 10,
        Pool(),
        EndlessAffixTuning.Default,
        EndlessScoreWeights.Default,
        Array.Empty<string>(),
        EndlessParticipationMode.Manual,
        mixPolicy: mix);

    // ------------------------------------------------ 策略守卫 -----

    [Theory] // illegal：非正波次/非正权重拒绝
    [InlineData(0, 1.0, 1.0)]
    [InlineData(-5, 1.0, 1.0)]
    [InlineData(41, 0.0, 1.0)]
    [InlineData(41, 1.0, -0.5)]
    public void Invalid_policies_are_rejected(int mixFromWave, double startWeight, double otherWeight)
    {
        Assert.Throws<ArgumentException>(
            () => new EndlessMixPolicy(mixFromWave, startWeight, otherWeight));
    }

    [Fact] // legal：生产档=T-143 Default（41 起混编，1:1 时代均匀）
    public void Default_policy_shape()
    {
        EndlessMixPolicy policy = EndlessMixPolicy.Default;

        Assert.Equal(41, policy.MixFromWave);
        Assert.Equal(1.0, policy.StartEraWeight);
        Assert.Equal(1.0, policy.OtherEraWeight);
    }

    // ------------------------------------------------ 基准段（开局时代独占）-----

    [Fact] // legal：MixFromWave 以下名册全为开局时代（敌池基准=开局时代）
    public void Below_the_mix_wave_the_roster_is_start_era_only()
    {
        var policy = new EndlessMixPolicy(mixFromWave: 41, startEraWeight: 1.0, otherEraWeight: 1.0);
        var run = new EndlessRun(Config(policy), new LcgRng(Seed));

        for (int wave = 1; wave <= 40; wave++)
        {
            EndlessWavePlan plan = run.BeginWave();
            Assert.All(plan.Roster, spawn =>
                Assert.Equal(1, spawn.EraIndex)); // 三线（常规/精英/Boss）都只出 E1
            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }
    }

    // ------------------------------------------------ 混编段（双权重钮）-----

    [Fact] // legal：混编段跨时代出现且权重份额跟随参数（2:1 钮 → E1 份额≈2/3）
    public void Mixed_segment_follows_the_weight_knobs()
    {
        var policy = new EndlessMixPolicy(mixFromWave: 41, startEraWeight: 2.0, otherEraWeight: 1.0);
        var run = new EndlessRun(Config(policy), new LcgRng(Seed));

        long startEraRegulars = 0, otherEraRegulars = 0;
        for (int wave = 41; wave <= 120; wave++)
        {
            EndlessWavePlan plan = run.BeginWave();
            foreach (EndlessSpawn spawn in plan.Roster)
            {
                if (spawn.Role == EndlessEnemyRole.Regular)
                {
                    if (spawn.EraIndex == 1)
                    {
                        startEraRegulars++;
                    }
                    else
                    {
                        otherEraRegulars++;
                    }
                }
            }

            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        Assert.True(otherEraRegulars > 0, "混编段必须带入其他时代行");
        // 池内 E1 常规 2 行×权重 2 = 4 vs E2 1 行×权重 1 = 1 → 份额 4/5；±12% 容差（固定种子确定性，容差=大数波动带）
        double share = (double)startEraRegulars / (startEraRegulars + otherEraRegulars);
        Assert.InRange(share, 0.8 - 0.12, 0.8 + 0.12);
    }

    [Fact] // legal：权重钮拨到极端——OtherEraWeight 大时开局时代份额显著缩小（参数可测证明）
    public void Weight_knobs_actually_steer_the_share()
    {
        long CountStartEra(EndlessMixPolicy policy)
        {
            var run = new EndlessRun(Config(policy, Seed + 7), new LcgRng(Seed + 7));
            long startEra = 0, total = 0;
            for (int wave = 41; wave <= 100; wave++)
            {
                EndlessWavePlan plan = run.BeginWave();
                foreach (EndlessSpawn spawn in plan.Roster)
                {
                    if (spawn.Role == EndlessEnemyRole.Regular)
                    {
                        total++;
                        startEra += spawn.EraIndex == 1 ? 1 : 0;
                    }
                }

                run.CompleteWave(plan.EliteCount, plan.BossCount);
            }

            Assert.True(total > 0);
            return startEra;
        }

        long biasedToStart = CountStartEra(new EndlessMixPolicy(41, startEraWeight: 9.0, otherEraWeight: 1.0));
        long biasedToOther = CountStartEra(new EndlessMixPolicy(41, startEraWeight: 1.0, otherEraWeight: 9.0));
        Assert.True(biasedToStart > biasedToOther, "权重钮必须真实改变时代份额（9:1 vs 1:9）");
    }

    // ------------------------------------------------ 回退与守卫 -----

    [Fact] // boundary：开局时代在该线无行（无 E1 Boss 行）→ 基准段整桶回退仍出 Boss
    public void Baseline_falls_back_to_the_whole_bucket_when_the_start_era_lacks_the_role()
    {
        EndlessEnemyPool noStartBoss = new(new[]
        {
            new EndlessEnemyPoolEntry("era1:enemy-a", 1, EndlessEnemyRole.Regular, 1.0),
            new EndlessEnemyPoolEntry("era2:enemy-c", 2, EndlessEnemyRole.Regular, 1.0),
            new EndlessEnemyPoolEntry("era1:enemy-elite", 1, EndlessEnemyRole.Elite, 1.0),
            new EndlessEnemyPoolEntry("era2:enemy-elite", 2, EndlessEnemyRole.Elite, 1.0),
            new EndlessEnemyPoolEntry("era2:enemy-boss", 2, EndlessEnemyRole.Boss, 1.0), // 仅 E2 有 Boss 行
        });
        var config = new EndlessRunConfig(
            Seed, 1, 0.9, 6, 1, 3, 1.0, 10, noStartBoss,
            EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
            EndlessParticipationMode.Manual,
            mixPolicy: new EndlessMixPolicy(41, 1.0, 1.0));
        var run = new EndlessRun(config, new LcgRng(Seed));

        for (int wave = 1; wave <= 9; wave++)
        {
            EndlessWavePlan warmup = run.BeginWave();
            Assert.DoesNotContain(warmup.Roster, spawn => spawn.Role == EndlessEnemyRole.Boss);
            run.CompleteWave();
        }

        EndlessWavePlan wave10 = run.BeginWave(); // Boss 节奏波在基准段内
        Assert.Equal(1, wave10.BossCount);
        Assert.Equal(2, wave10.Roster.Single(spawn => spawn.Role == EndlessEnemyRole.Boss).EraIndex); // 回退取 E2 Boss
        run.CompleteWave();
    }

    [Fact] // boundary：负波拒绝；空线拒绝；null 策略拒绝
    public void Mixed_pick_guards()
    {
        var pool = Pool();
        var policy = new EndlessMixPolicy(41, 1.0, 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => pool.Pick(new LcgRng(Seed), EndlessEnemyRole.Regular, -1, 1, policy));
        Assert.Throws<ArgumentException>(
            () => pool.Pick(new LcgRng(Seed), (EndlessEnemyRole)99, 10, 1, policy)); // 未定义线
        Assert.Throws<ArgumentNullException>(
            () => pool.Pick(new LcgRng(Seed), EndlessEnemyRole.Regular, 10, 1, null!));
        Assert.Throws<ArgumentNullException>(
            () => pool.Pick(null!, EndlessEnemyRole.Regular, 10, 1, policy));
    }

    // ------------------------------------------------ 确定性 -----

    [Fact] // legal：同 seed 同策略两局逐波哈希全等（混编可复现）
    public void Mixed_runs_are_deterministic_for_the_same_seed()
    {
        var policy = new EndlessMixPolicy(15, startEraWeight: 2.0, otherEraWeight: 1.0);
        var left = new EndlessRun(Config(policy), new LcgRng(Seed));
        var right = new EndlessRun(Config(policy), new LcgRng(Seed));

        for (int wave = 1; wave <= 50; wave++)
        {
            EndlessWavePlan leftPlan = left.BeginWave();
            EndlessWavePlan rightPlan = right.BeginWave();
            Assert.Equal(leftPlan.Roster, rightPlan.Roster);
            left.CompleteWave(leftPlan.EliteCount, leftPlan.BossCount);
            right.CompleteWave(rightPlan.EliteCount, rightPlan.BossCount);
            Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
        }
    }

    [Fact] // legal：策略入身份哈希——不同 MixFromWave/权重=不同运行身份
    public void Mix_policy_joins_the_run_identity()
    {
        var policyA = new EndlessMixPolicy(41, 1.0, 1.0);
        var policyB = new EndlessMixPolicy(21, 1.0, 1.0);
        var runA = new EndlessRun(Config(policyA), new LcgRng(Seed));
        var runB = new EndlessRun(Config(policyB), new LcgRng(Seed));
        EndlessFixture.PlayWaves(runA, 5);
        EndlessFixture.PlayWaves(runB, 5);

        Assert.NotEqual(runA.ComputeStateHash(), runB.ComputeStateHash());
    }

    [Fact] // legal：MixFromWave=1+等权=无策略 T-118 语义——逐波名册完全一致（回归锚）
    public void First_wave_equal_weight_mix_equals_the_no_policy_roster_exactly()
    {
        EndlessEnemyPool pool = Pool();
        var raw = new EndlessRun(
            new EndlessRunConfig(
                Seed, 1, 0.9, 6, 1, 3, 1.0, 10, pool,
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual),
            new LcgRng(Seed));
        var mixed = new EndlessRun(
            new EndlessRunConfig(
                Seed, 1, 0.9, 6, 1, 3, 1.0, 10, pool,
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                mixPolicy: new EndlessMixPolicy(1, startEraWeight: 1.0, otherEraWeight: 1.0)),
            new LcgRng(Seed));

        for (int wave = 1; wave <= 30; wave++)
        {
            EndlessWavePlan rawPlan = raw.BeginWave();
            EndlessWavePlan mixedPlan = mixed.BeginWave();
            Assert.Equal(rawPlan.Roster, mixedPlan.Roster); // 等权全池=原始语义，逐 spawn 一致
            raw.CompleteWave(rawPlan.EliteCount, rawPlan.BossCount);
            mixed.CompleteWave(mixedPlan.EliteCount, mixedPlan.BossCount);
        }
    }
}
