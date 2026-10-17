using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143] 每 10 波滚词缀（现有四词缀基础上）：RollStartWave 起每
// RollEveryWaves 波一块，块内词缀恒定、块界滚换；重复规则（连续块不得同词缀）
// 与叠加规则（连续重摇强度 ×RepeatIntensityGain/层）参数化；同 seed 同序列
// （哈希派生零 rng 接触——名册随机流不动）；81+ 冻结叠层照常垫底。
public class EndlessAffixRollsTests
{
    private const long Seed = 20261431;

    /// <summary>生产档（10 波一块、41 起、单词缀、禁连续重复、重摇 ×1.5）.</summary>
    private static EndlessAffixRollPolicy Default() => EndlessAffixRollPolicy.Default;

    // ------------------------------------------------ 策略守卫 -----

    [Theory] // illegal：非正节奏/起点、越界词缀数、非法无重复组合、增益 <1 拒绝
    [InlineData(0, 41, 1, false, 1.5)]
    [InlineData(10, 0, 1, false, 1.5)]
    [InlineData(10, 41, 0, false, 1.5)]
    [InlineData(10, 41, 5, false, 1.5)]
    [InlineData(10, 41, 3, false, 1.5)] // 无重复时 k≤2（4−k≥k 可行性）
    [InlineData(10, 41, 1, false, 0.5)]
    public void Invalid_policies_are_rejected(
        int every, int start, int kinds, bool allowRepeat, double gain)
    {
        Assert.Throws<ArgumentException>(
            () => new EndlessAffixRollPolicy(every, start, kinds, allowRepeat, gain));
    }

    [Fact] // legal：生产档形状（SP-7b 节奏/首词缀波锚；镜像叠层 ×1.5）
    public void Default_policy_shape()
    {
        EndlessAffixRollPolicy policy = Default();

        Assert.Equal(10, policy.RollEveryWaves);
        Assert.Equal(41, policy.RollStartWave);
        Assert.Equal(1, policy.KindsPerRoll);
        Assert.False(policy.AllowConsecutiveRepeat);
        Assert.Equal(1.5, policy.RepeatIntensityGain);
    }

    // ------------------------------------------------ 节奏与确定性 -----

    [Fact] // legal：滚动起以下空；41 起每 10 波一块恒定、块界换词缀
    public void Blocks_roll_every_ten_waves_and_hold_inside_the_block()
    {
        EndlessAffixRollPolicy policy = Default();

        Assert.Empty(EndlessAffixRolls.KindsAt(Seed, 40, policy));
        IReadOnlyList<EndlessAffixKind> blockKinds = EndlessAffixRolls.KindsAt(Seed, 41, policy);
        Assert.Single(blockKinds);
        for (int wave = 41; wave <= 70; wave++)
        {
            // 块内恒定：同块每波同词缀
            Assert.Equal(EndlessAffixRolls.KindsAt(Seed, 41 + (((wave - 41) / 10) * 10), policy), EndlessAffixRolls.KindsAt(Seed, wave, policy));
        }

        // 块界滚换：至少一块与相邻块不同（禁重复下必然全不同）
        var blocks = new List<IReadOnlyList<EndlessAffixKind>>();
        for (int block = 0; block < 6; block++)
        {
            blocks.Add(EndlessAffixRolls.KindsAt(Seed, 41 + (block * 10), policy));
        }

        Assert.True(blocks.Select(kinds => kinds[0]).Distinct().Count() >= 2, "块界必须滚出新词缀序列");
        for (int block = 1; block < blocks.Count; block++)
        {
            Assert.Empty(blocks[block - 1].Intersect(blocks[block])); // 重复规则：连续块互斥
        }
    }

    [Fact] // legal：同 seed 同序列（两次独立求值逐波一致）；不同 seed 序列不同
    public void Same_seed_same_sequence_and_other_seeds_differ()
    {
        EndlessAffixRollPolicy policy = Default();
        for (int wave = 41; wave <= 140; wave++)
        {
            Assert.Equal(
                EndlessAffixRolls.KindsAt(Seed, wave, policy),
                EndlessAffixRolls.KindsAt(Seed, wave, policy)); // 纯函数重入一致
        }

        // 固定两 seed 的整段序列：确定性函数下断言不同是稳定的（非随机翻硬币）
        IEnumerable<EndlessAffixKind> Sequence(long seed) =>
            Enumerable.Range(41, 100).Select(wave => EndlessAffixRolls.KindsAt(seed, wave, policy)[0]);
        Assert.NotEqual(Sequence(Seed).ToList(), Sequence(Seed + 1).ToList());
    }

    [Fact] // legal：滚动与名册随机流正交——同 rng 种子下滚/不滚两局名册完全一致
    public void Rolling_never_touches_the_roster_rng_stream()
    {
        EndlessRunConfig Config(EndlessAffixRollPolicy? rolls) => new(
            Seed, 1, 0.9, 6, 1, 3, 1.0, 10, EndlessFixture.MixedPool(),
            EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
            EndlessParticipationMode.Manual,
            affixRolls: rolls);

        var plain = new EndlessRun(Config(null), new LcgRng(Seed));
        var rolled = new EndlessRun(Config(Default()), new LcgRng(Seed));
        for (int wave = 1; wave <= 60; wave++)
        {
            Assert.Equal(plain.BeginWave().Roster, rolled.BeginWave().Roster);
            plain.CompleteWave();
            rolled.CompleteWave();
        }
    }

    // ------------------------------------------------ 重复/叠加规则 -----

    [Fact] // legal：允许连续重复时重摇可续块，叠加规则按连续重摇层数放大
    public void Repeat_rule_allows_reruns_and_the_gain_compounds_per_consecutive_rerun()
    {
        // 允许重复+每块 2 词缀：长跑必出现连续重摇（固定 seed 确定性选取）
        var policy = new EndlessAffixRollPolicy(10, 41, kindsPerRoll: 2, allowConsecutiveRepeat: true, repeatIntensityGain: 2.0);
        var runLengths = new Dictionary<EndlessAffixKind, int>();
        int rerunFound = 0;
        int maxRun = 1;
        IReadOnlyList<EndlessAffixKind> previous = Array.Empty<EndlessAffixKind>();
        for (int block = 0; block < 60; block++)
        {
            IReadOnlyList<EndlessAffixKind> kinds = EndlessAffixRolls.KindsAt(Seed, 41 + (block * 10), policy);
            Assert.Equal(2, kinds.Count);

            // 期望连续层数：上一块含该词缀 → +1，否则重置 1
            foreach (EndlessAffixKind kind in kinds)
            {
                int expectedRun = previous.Contains(kind) ? runLengths[kind] + 1 : 1;
                if (previous.Contains(kind))
                {
                    rerunFound++;
                }

                runLengths[kind] = expectedRun;
                maxRun = Math.Max(maxRun, expectedRun);
                IReadOnlyDictionary<EndlessAffixKind, double> gains =
                    EndlessAffixRolls.RepeatIntensityAt(Seed, 41 + (block * 10), policy);
                Assert.Equal(Math.Pow(2.0, expectedRun - 1), gains[kind], 12); // 2^(r−1)
            }

            previous = kinds;
        }

        Assert.True(rerunFound > 0, "60 块双词缀允许重复下必现连续重摇（固定 seed 已核）");
        Assert.True(maxRun >= 2, "必现 ≥2 层连续重摇");
    }

    [Fact] // legal：禁重复下重摇强度恒 1（无连续块，叠加规则无层可叠）
    public void No_repeat_policy_never_gains_intensity()
    {
        EndlessAffixRollPolicy policy = Default();
        for (int block = 0; block < 30; block++)
        {
            foreach ((_, double gain) in EndlessAffixRolls.RepeatIntensityAt(Seed, 41 + (block * 10), policy))
            {
                Assert.Equal(1.0, gain);
            }
        }
    }

    // ------------------------------------------------ 调制与冻结机制复用 -----

    [Fact] // legal：滚动词缀走同一调制机制——缺席通道中性、到场通道 SP-7b 量级
    public void Rolled_modifiers_reuse_the_frozen_channel_composition()
    {
        EndlessAffixRollPolicy policy = Default();

        // 找一个块其词缀恰为 Harden（固定 seed 扫描前若干块——确定性选取）
        int hardenBlock = -1;
        for (int block = 0; block < 40 && hardenBlock < 0; block++)
        {
            if (EndlessAffixRolls.KindsAt(Seed, 41 + (block * 10), policy).Contains(EndlessAffixKind.Harden))
            {
                hardenBlock = block;
            }
        }

        Assert.True(hardenBlock >= 0, "固定 seed 下 40 块内必滚到硬化（已核）");
        int wave = 41 + (hardenBlock * 10) + 3; // 块内第 4 波（<81 无叠层）
        EndlessEnemyModifiers m = EndlessAffixRolls.Modifiers(Seed, wave, EndlessAffixTuning.Default, policy);

        Assert.Equal(new[] { EndlessAffixKind.Harden }, m.ActiveKinds);
        Assert.Equal(1.5, m.HpMultiplier, 12);    // SP-7b 硬化 HP +50%
        Assert.Equal(1.0, m.SpeedMultiplier);     // 迅捷未滚到=中性（哪怕 ≥41）
        Assert.Equal(0, m.PhasePeriodTicks);
        Assert.Equal(1.0, m.SuicideDamageMultiplier);
    }

    [Fact] // legal：81+ 冻结叠层在滚动档照常垫底——同词缀块 81 前后增量恰 ×1.5
    public void The_frozen_81plus_stack_rule_still_applies_under_rolling()
    {
        EndlessAffixRollPolicy policy = Default();
        int FindBlock(EndlessAffixKind kind, int fromBlock, int toBlock)
        {
            for (int block = fromBlock; block <= toBlock; block++)
            {
                if (EndlessAffixRolls.KindsAt(Seed, 41 + (block * 10), policy).Contains(kind))
                {
                    return block;
                }
            }

            return -1;
        }

        // 找一个 81 前（块 0..3）与 81+（块 4..12）都滚到的词缀（固定 seed 已核存在）
        EndlessAffixKind shared = EndlessAffixKind.None;
        foreach (EndlessAffixKind kind in new[] { EndlessAffixKind.Swift, EndlessAffixKind.Harden, EndlessAffixKind.SuicideEmpower })
        {
            if (FindBlock(kind, 0, 3) >= 0 && FindBlock(kind, 4, 12) >= 0)
            {
                shared = kind;
                break;
            }
        }

        Assert.NotEqual(EndlessAffixKind.None, shared);
        double Table(EndlessAffixKind kind) => kind switch
        {
            EndlessAffixKind.Swift => EndlessAffixTuning.Default.SwiftSpeedMultiplier,
            EndlessAffixKind.Harden => EndlessAffixTuning.Default.HardenHpMultiplier,
            _ => EndlessAffixTuning.Default.SuicideDamageMultiplier,
        };
        double Channel(EndlessAffixKind kind, EndlessEnemyModifiers m) => kind switch
        {
            EndlessAffixKind.Swift => m.SpeedMultiplier,
            EndlessAffixKind.Harden => m.HpMultiplier,
            _ => m.SuicideDamageMultiplier,
        };

        int before = FindBlock(shared, 0, 3);
        int after = FindBlock(shared, 4, 12);
        int waveBefore = 41 + (before * 10) + 2;
        int waveAfter = 41 + (after * 10) + 2;
        EndlessEnemyModifiers mBefore = EndlessAffixRolls.Modifiers(Seed, waveBefore, EndlessAffixTuning.Default, policy);
        EndlessEnemyModifiers mAfter = EndlessAffixRolls.Modifiers(Seed, waveAfter, EndlessAffixTuning.Default, policy);

        Assert.Equal(0, mBefore.Stacks); // 块 0..3 → 波 ≤75 → 无叠层
        int stacksAfter = EndlessAffixes.Stacks(waveAfter);
        Assert.Equal(stacksAfter, mAfter.Stacks); // 81+ 冻结叠层垫底（波决定层数）
        Assert.Equal(1.0 + ((Table(shared) - 1.0) * EndlessAffixes.Intensity(0, EndlessAffixTuning.Default)), Channel(shared, mBefore), 12); // 81 前：强度 1
        Assert.Equal(
            1.0 + ((Table(shared) - 1.0) * EndlessAffixes.Intensity(stacksAfter, EndlessAffixTuning.Default)),
            Channel(shared, mAfter), 12); // 81 后：强度 1.5^层
    }

    // ------------------------------------------------ 记分（词缀加权）-----

    [Fact] // legal：滚动词缀加权分=Σ 块内波数×词缀数×点值（块级口径，与逐波口径吻合）
    public void Rolled_affix_bonus_points_match_the_block_sum()
    {
        EndlessAffixRollPolicy policy = Default();
        long points = EndlessAffixRolls.KindsAt(Seed, 41, policy).Count * 10 * 10; // 块 0：10 波 ×1 词缀 ×10 分
        Assert.Equal(points, EndlessRules.AffixBonusPoints(50, Seed, EndlessScoreWeights.Default, policy));
        Assert.Equal(0, EndlessRules.AffixBonusPoints(40, Seed, EndlessScoreWeights.Default, policy)); // 起滚前零分

        // 逐波口径核对（O(waves) 暴力和=块级公式值）
        long brute = 0;
        for (int wave = 41; wave <= 137; wave++)
        {
            brute += EndlessAffixRolls.KindsAt(Seed, wave, policy).Count * 10;
        }

        Assert.Equal(brute, EndlessRules.AffixBonusPoints(137, Seed, EndlessScoreWeights.Default, policy));
    }

    // ------------------------------------------------ 守卫 -----

    [Fact] // boundary：负波拒绝、null 策略拒绝
    public void Roller_guards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessAffixRolls.KindsAt(Seed, -1, Default()));
        Assert.Throws<ArgumentNullException>(
            () => EndlessAffixRolls.KindsAt(Seed, 41, null!));
        Assert.Throws<ArgumentNullException>(
            () => EndlessAffixRolls.Modifiers(Seed, 41, EndlessAffixTuning.Default, null!));
    }

    [Fact] // boundary：显式词缀表守卫——None/重复词缀拒绝（组合入口）
    public void Explicit_kind_list_guards()
    {
        Assert.Throws<ArgumentException>(
            () => EndlessAffixes.Modifiers(
                41, EndlessAffixTuning.Default,
                new[] { EndlessAffixKind.None }, repeatIntensity: null));
        Assert.Throws<ArgumentException>(
            () => EndlessAffixes.Modifiers(
                41, EndlessAffixTuning.Default,
                new[] { EndlessAffixKind.Swift, EndlessAffixKind.Swift }, repeatIntensity: null));
    }

    [Fact] // boundary：块溢出守卫——极端波块号钳 1000（序列冻结不崩）
    public void Extreme_waves_clamp_the_block_walk()
    {
        EndlessAffixRollPolicy policy = Default();
        int extreme = int.MaxValue - 3;
        IReadOnlyList<EndlessAffixKind> clamped = EndlessAffixRolls.KindsAt(Seed, extreme, policy);
        Assert.Single(clamped); // 钳位后仍有定义（块 1000 的词缀）
        Assert.Equal(clamped, EndlessAffixRolls.KindsAt(Seed, extreme - 100, policy)); // 同钳位块
    }
}
