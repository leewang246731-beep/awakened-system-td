using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143][T-154] 混编敌人（高波次跨时代混编敌池——权重参数驱动 +
// T-154 波渐升）：MixFromWave 以下=开局时代基准池（任选时代的池面），以上=
// 双权重钮跨时代混编（每线内归一；T-154 起权重钮可随波线性渐升——41 波 9:1
// → 81 波 3:1 业主裁定锚）；开局时代缺该线行时整桶回退；守卫与确定性。
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

    // -------------------------------- T-154 波渐升（主场主导+波渐升）-----

    [Theory] // illegal：渐升守卫——部分参数/终值波早于起始波/终值权重非正拒绝
    [InlineData(41, 9.0, 1.0, 81, null, 1.0)]   // 缺 end_start
    [InlineData(41, 9.0, 1.0, 81, 3.0, null)]   // 缺 end_other
    [InlineData(41, 9.0, 1.0, null, 3.0, 1.0)]  // 缺 ramp_end
    [InlineData(41, 9.0, 1.0, 40, 3.0, 1.0)]    // 终值波 < 起始波
    [InlineData(41, 9.0, 1.0, 81, 0.0, 1.0)]    // 终值权重非正
    [InlineData(41, 9.0, 1.0, 81, 3.0, -1.0)]   // 终值权重负
    public void Invalid_ramp_parameters_are_rejected(
        int mixFromWave, double start, double other, int? rampEnd, double? endStart, double? endOther)
    {
        Assert.Throws<ArgumentException>(
            () => new EndlessMixPolicy(mixFromWave, start, other, rampEnd, endStart, endOther));
    }

    [Fact] // legal：渐升曲线插值——41 波 9:1、61 波 6:1、81 波 3:1 业主锚逐点核对（中间线性）
    public void Ramp_weights_interpolate_linearly_between_the_mix_and_end_waves()
    {
        // T-154 生产档形状：41 起 9:1（主场:跨代）→ 81 起 3:1 终值（endless-policies.json 同值）
        var policy = new EndlessMixPolicy(41, 9.0, 1.0, rampEndWave: 81, endStartEraWeight: 3.0, endOtherEraWeight: 1.0);

        (double Start, double Other) At(int wave) => policy.WeightsAt(wave);

        Assert.Equal((9.0, 1.0), At(40));  // 基准段=名义起始值（不消费）
        Assert.Equal((9.0, 1.0), At(41));  // 41 波=9:1（t=0）
        Assert.Equal((7.5, 1.0), At(51));  // t=0.25 线性中点前
        Assert.Equal((6.0, 1.0), At(61));  // 61 波=6:1（t=0.5 业主插值锚）
        Assert.Equal((4.5, 1.0), At(71));  // t=0.75
        Assert.Equal((3.0, 1.0), At(81));  // 81 波=3:1 终值（t=1，含锚）
        Assert.Equal((3.0, 1.0), At(121)); // 终值波后保持 3:1
        Assert.Equal((3.0, 1.0), At(9999));
    }

    [Fact] // legal：固定档 WeightsAt 恒定；渐升参数缺省=null（既有构造兼容面）
    public void Fixed_knob_schedules_return_constant_weights()
    {
        var policy = new EndlessMixPolicy(41, 2.0, 1.0);

        Assert.Null(policy.RampEndWave);
        Assert.Null(policy.EndStartEraWeight);
        Assert.Null(policy.EndOtherEraWeight);
        foreach (int wave in new[] { 1, 40, 41, 61, 81, 500 })
        {
            Assert.Equal((2.0, 1.0), policy.WeightsAt(wave));
        }
    }

    [Fact] // legal：rampEnd=mixFrom 的退化渐升=从混编起即终值（除零守卫）
    public void Degenerate_ramp_holds_the_end_weights_from_the_mix_wave()
    {
        var policy = new EndlessMixPolicy(41, 9.0, 1.0, rampEndWave: 41, endStartEraWeight: 3.0, endOtherEraWeight: 1.0);

        Assert.Equal((3.0, 1.0), policy.WeightsAt(41));
        Assert.Equal((3.0, 1.0), policy.WeightsAt(42));
        Assert.Equal((3.0, 1.0), policy.WeightsAt(200));
    }

    [Fact] // legal：渐升真实生效——早段（9:1）主场份额显著高于晚段（3:1）
    public void Ramped_mix_actually_eases_the_home_era_share_over_waves()
    {
        double HomeShare(int fromWave, int toWave)
        {
            var run = new EndlessRun(Config(RampPolicy(), Seed + 11), new LcgRng(Seed + 11));
            EndlessFixture.PlayWaves(run, fromWave - 1); // 预热到目标段（BeginWave 从波 1 递增）
            long home = 0, total = 0;
            for (int wave = fromWave; wave <= toWave; wave++)
            {
                EndlessWavePlan plan = run.BeginWave();
                Assert.Equal(wave, plan.Wave); // 预热后波号对齐
                foreach (EndlessSpawn spawn in plan.Roster)
                {
                    if (spawn.Role == EndlessEnemyRole.Regular)
                    {
                        total++;
                        home += spawn.EraIndex == 1 ? 1 : 0;
                    }
                }

                run.CompleteWave(plan.EliteCount, plan.BossCount);
            }

            Assert.True(total > 0);
            return (double)home / total;
        }

        // 池内 E1 常规 2 行 vs E2 1 行（各权重 1）：9:1 段 E1 份额=18/19≈0.947；3:1 段=6/7≈0.857
        double early = HomeShare(41, 70);   // 9:1→4.5:1 段（主场最重）
        double late = HomeShare(201, 230);  // 终值 3:1 段
        Assert.True(early > late, $"渐升必须真实放松主场份额（早段 {early:F3} 应 > 晚段 {late:F3}）");
        Assert.InRange(early, 0.90, 0.99);  // 9:1 起始带（18/19≈0.947±抽样带）
        Assert.InRange(late, 0.78, 0.92);   // 3:1 终值带（6/7≈0.857±抽样带）
    }

    private static EndlessMixPolicy RampPolicy() =>
        new(41, 9.0, 1.0, rampEndWave: 81, endStartEraWeight: 3.0, endOtherEraWeight: 1.0);

    [Fact] // legal：渐升档确定性——同 seed 同渐升策略两局逐波哈希全等（渐升=纯 (policy,wave) 函数）
    public void Ramped_runs_are_deterministic_for_the_same_seed()
    {
        var left = new EndlessRun(Config(RampPolicy()), new LcgRng(Seed));
        var right = new EndlessRun(Config(RampPolicy()), new LcgRng(Seed));

        for (int wave = 1; wave <= 90; wave++) // 跨渐升全程（41 起→81 终值→之后）
        {
            EndlessWavePlan leftPlan = left.BeginWave();
            EndlessWavePlan rightPlan = right.BeginWave();
            Assert.Equal(leftPlan.Roster, rightPlan.Roster);
            left.CompleteWave(leftPlan.EliteCount, leftPlan.BossCount);
            right.CompleteWave(rightPlan.EliteCount, rightPlan.BossCount);
            Assert.Equal(left.ComputeStateHash(), right.ComputeStateHash());
        }
    }

    [Fact] // legal：渐升参数入身份哈希——同起始权重、渐升 vs 固定=不同身份；不同终值=不同身份
    public void Ramp_parameters_join_the_run_identity()
    {
        var fixedKnobs = new EndlessMixPolicy(41, 9.0, 1.0);
        var rampedToEnd3 = RampPolicy();
        var rampedToEnd2 = new EndlessMixPolicy(41, 9.0, 1.0, rampEndWave: 81, endStartEraWeight: 2.0, endOtherEraWeight: 1.0);
        var a = new EndlessRun(Config(fixedKnobs), new LcgRng(Seed));
        var b = new EndlessRun(Config(rampedToEnd3), new LcgRng(Seed));
        var c = new EndlessRun(Config(rampedToEnd2), new LcgRng(Seed));
        EndlessFixture.PlayWaves(a, 5);
        EndlessFixture.PlayWaves(b, 5);
        EndlessFixture.PlayWaves(c, 5);

        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash()); // 渐升 vs 固定分账
        Assert.NotEqual(b.ComputeStateHash(), c.ComputeStateHash()); // 不同终值分账
    }
}
