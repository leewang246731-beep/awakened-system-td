using EraCore.Endless;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-080] SP-7b 每 10 波词缀表：迅捷 41 / 硬化 51 / 相位 61 / 自爆强化 71，
// 81 起每 +10 波叠层（强度 ×1.5/层）。量级默认=T-080 任务卡校准档；SP-7b 表体
// 口径（迅捷+25%/硬化 HP+50%/相位 0.5s）经 SpecTable 档同断言。
public class EndlessAffixesTests
{
    // ------------------------------------------------ 激活阈值 ---------

    [Theory] // legal：激活波门槛逐档点亮（40 空、41 迅捷、51 +硬化、61 +相位、71 +自爆）
    [InlineData(1, 0)]
    [InlineData(40, 0)]
    [InlineData(41, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(60, 2)]
    [InlineData(61, 3)]
    [InlineData(70, 3)]
    [InlineData(71, 4)]
    [InlineData(10000, 4)]
    public void Activation_thresholds_light_up_per_ten_waves(int wave, int expectedKinds)
    {
        Assert.Equal(expectedKinds, EndlessAffixes.ActiveAt(wave).Count);
        Assert.Equal(expectedKinds, EndlessAffixes.CountAt(wave));
    }

    [Fact] // legal：激活顺序固定（迅捷→硬化→相位→自爆强化）——记分/展示序稳定
    public void Kinds_activate_in_frozen_order()
    {
        Assert.Equal(
            new[]
            {
                EndlessAffixKind.Swift,
                EndlessAffixKind.Harden,
                EndlessAffixKind.Phase,
                EndlessAffixKind.SuicideEmpower,
            },
            EndlessAffixes.ActiveAt(71));
    }

    // ------------------------------------------------ 默认档量级 -------

    [Fact] // legal：任务卡默认档——迅捷 ×1.3 速 / 硬化 ×1.5 甲（HP 档默认关）/ 相位 6s 周期 1s 无敌帧
    public void Default_tuning_magnitudes()
    {
        EndlessEnemyModifiers m = EndlessAffixes.Modifiers(71, EndlessAffixTuning.Default);

        Assert.Equal(1.3, m.SpeedMultiplier);
        Assert.Equal(1.5, m.ArmorMultiplier);
        Assert.Equal(1.0, m.HpMultiplier);
        Assert.Equal(180, m.PhasePeriodTicks);        // 6 s × 30 Hz
        Assert.Equal(30, m.PhaseInvulnerableTicks);   // 1 s × 30 Hz
        Assert.Equal(1.5, m.SuicideRadiusMultiplier);
        Assert.Equal(2.0, m.SuicideDamageMultiplier);
        Assert.Equal(1.0, m.SuicideBaseRadiusCells);  // 非自爆敌获得基础自爆 1 格
        Assert.Equal(60.0, m.SuicideBaseDamage);      // ……60 点
        Assert.Equal(0, m.Stacks);
    }

    [Fact] // legal：SP-7b 表体档（SpecTable）——迅捷 +25% / 硬化 HP +50% / 相位 0.5s 无敌帧每 6s
    public void Spec_table_tuning_magnitudes()
    {
        EndlessEnemyModifiers m = EndlessAffixes.Modifiers(71, EndlessAffixTuning.SpecTable);

        Assert.Equal(1.25, m.SpeedMultiplier);
        Assert.Equal(1.0, m.ArmorMultiplier);
        Assert.Equal(1.5, m.HpMultiplier);
        Assert.Equal(180, m.PhasePeriodTicks);
        Assert.Equal(15, m.PhaseInvulnerableTicks);   // 0.5 s × 30 Hz
    }

    [Theory] // legal：词缀缺席=中性（乘子恰 1/基础 0）——41 前全中性，单档缺席同域
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(1)]
    public void Below_swift_start_everything_is_neutral(int wave)
    {
        EndlessEnemyModifiers m = EndlessAffixes.Modifiers(wave, EndlessAffixTuning.Default);

        Assert.Empty(m.ActiveKinds);
        Assert.Equal(1.0, m.SpeedMultiplier);
        Assert.Equal(1.0, m.ArmorMultiplier);
        Assert.Equal(1.0, m.HpMultiplier);
        Assert.Equal(0, m.PhasePeriodTicks);
        Assert.Equal(0, m.PhaseInvulnerableTicks);
        Assert.Equal(1.0, m.SuicideRadiusMultiplier);
        Assert.Equal(1.0, m.SuicideDamageMultiplier);
        Assert.Equal(0.0, m.SuicideBaseRadiusCells);
        Assert.Equal(0.0, m.SuicideBaseDamage);
    }

    [Fact] // legal：41~50 只有迅捷生效，硬化/相位/自爆域保持中性
    public void Partial_activation_keeps_other_domains_neutral()
    {
        EndlessEnemyModifiers m = EndlessAffixes.Modifiers(45, EndlessAffixTuning.Default);

        Assert.Equal(new[] { EndlessAffixKind.Swift }, m.ActiveKinds);
        Assert.Equal(1.3, m.SpeedMultiplier);
        Assert.Equal(1.0, m.ArmorMultiplier);   // 硬化 51 起才点亮
        Assert.Equal(1.0, m.HpMultiplier);
        Assert.Equal(0, m.PhasePeriodTicks);
        Assert.Equal(1.0, m.SuicideDamageMultiplier);
    }

    // ------------------------------------------------ 叠层 81 起 -------

    [Theory] // legal：叠层计数 81 起 +1/10 波（81→1、90→1、91→2、10000→992、极端波钳 1000）
    [InlineData(80, 0)]
    [InlineData(81, 1)]
    [InlineData(90, 1)]
    [InlineData(91, 2)]
    [InlineData(100, 2)]
    [InlineData(10000, 992)]
    [InlineData(10871, 1000)] // 1+⌊10790/10⌋=1080 → 溢出守卫钳 1000
    public void Stacks_climb_every_ten_waves_from_81(int wave, int expected)
    {
        Assert.Equal(expected, EndlessAffixes.Stacks(wave));
    }

    [Fact] // legal：强度 = 1.5^层数（SP-7b 叠层行）；0 层恰 1
    public void Intensity_is_compound_per_stack()
    {
        Assert.Equal(1.0, EndlessAffixes.Intensity(0, EndlessAffixTuning.Default));
        Assert.Equal(1.5, EndlessAffixes.Intensity(1, EndlessAffixTuning.Default));
        Assert.Equal(2.25, EndlessAffixes.Intensity(2, EndlessAffixTuning.Default));
    }

    [Fact] // legal：叠层作用在词缀增量上（1+(表值−1)×强度）——迅捷 1 层=×1.45，不是 ×1.95
    public void Stacks_scale_the_delta_not_the_whole_multiplier()
    {
        EndlessEnemyModifiers m = EndlessAffixes.Modifiers(81, EndlessAffixTuning.Default);

        Assert.Equal(1, m.Stacks);
        Assert.Equal(1.45, m.SpeedMultiplier, 10);              // 1+(1.3−1)×1.5
        Assert.Equal(1.75, m.ArmorMultiplier, 10);              // 1+(1.5−1)×1.5（硬化增量 ×1.5）
        Assert.Equal(45, m.PhaseInvulnerableTicks);             // 1.0s × 1.5 强度 → 1.5s → 45 tick
        Assert.Equal(2.25, m.SuicideRadiusMultiplier, 10);      // 1.5 × 1.5（配比乘子整体 ×强度）
        Assert.Equal(3.0, m.SuicideDamageMultiplier, 10);
        Assert.Equal(1.0, m.SuicideBaseRadiusCells);            // 基础授予对不随层放大（平价授予）
        Assert.Equal(60.0, m.SuicideBaseDamage);
    }

    // ------------------------------------------------ 守卫 -------------

    [Fact] // boundary：负波/负层数拒绝（不是定义过的游戏状态）
    public void Negative_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessAffixes.Modifiers(-1, EndlessAffixTuning.Default));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EndlessAffixes.Intensity(-1, EndlessAffixTuning.Default));
        Assert.Throws<ArgumentNullException>(
            () => EndlessAffixes.Modifiers(41, null!));
    }
}
