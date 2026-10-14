using EraCore.Balance;
using EraCore.Endless;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-080] SP-7a 外推公式表驱动：w≤40 逐字委托常规冻结式（min(40) 封顶在
// 位）；w>40 走（40+2×(w−40)）×数量乘子，floor 最后，int 溢出守卫 clamp 10000。
// 任务卡表驱动锚点：41/50/100/10000 × 三档威胁度（t=0.3/0.9/1.2 → 乘子 0.5/1.5/2.0）。
public class EndlessWaveFormulaTests
{
    private static (int Base, int Begin, int Spacing, double Scaling) Shape => EndlessFixture.Era1Shape;

    // ------------------------------------------------ 表驱动：外推段 ----

    public static TheoryData<int, double, int> ExtrapolationRows => new()
    {
        // (wave, threat, expected) —— expected = floor((40+2×(w−40))×(5/3)t)
        // 任务卡锚点波次 41/50/100/10000 全档：
        { 41, 0.3, 21 },      // 42 × 0.5
        { 41, 0.9, 63 },      // 42 × 1.5
        { 41, 1.2, 84 },      // 42 × 2.0
        { 50, 0.3, 30 },      // 60 × 0.5
        { 50, 0.9, 90 },      // 60 × 1.5
        { 50, 1.2, 120 },     // 60 × 2.0
        { 100, 0.3, 80 },     // 160 × 0.5
        { 100, 0.9, 240 },    // 160 × 1.5
        { 100, 1.2, 320 },    // 160 × 2.0
        { 10000, 0.3, 9980 },   // 19960 × 0.5 —— 未触钳
        { 10000, 0.9, 10000 },  // 29940 → clamp 10000（任务卡「clamp 10000」咬合）
        { 10000, 1.2, 10000 },  // 39920 → clamp 10000
    };

    [Theory] // legal：SP-7a 外推式（40+2×(w−40)）×数量乘子，任务卡表驱动锚点
    [MemberData(nameof(ExtrapolationRows))]
    public void Extrapolated_count_follows_spec_formula(int wave, double threat, int expected)
    {
        double multiplier = Threat.Multipliers(threat).Count;

        int count = EndlessWaveFormula.Count(
            wave, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, multiplier);

        Assert.Equal(expected, count);
    }

    [Fact] // legal：外推斜率 = 每波 +2 只（SP-7a「每 +1 波 +2 只」）——41 与 42 差恰 2×乘子
    public void Extrapolation_gains_two_per_wave()
    {
        double multiplier = Threat.Multipliers(0.9).Count;

        int w41 = EndlessWaveFormula.Count(41, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, multiplier);
        int w42 = EndlessWaveFormula.Count(42, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, multiplier);
        int w43 = EndlessWaveFormula.Count(43, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, multiplier);

        Assert.Equal(63, w41);
        Assert.Equal(66, w42); // +3 = 2×1.5（乘子作用在 +2 增量上）
        Assert.Equal(69, w43);
    }

    [Fact] // legal：w≤40 逐字委托常规冻结式（min(40) 封顶仍在位——外推不回写常规段）
    public void Regular_segment_delegates_verbatim()
    {
        for (int wave = 0; wave <= EndlessWaveFormula.RegularCapWaves; wave++)
        {
            int regular = WaveFormula.Count(wave, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 1.5);
            int endless = EndlessWaveFormula.Count(wave, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 1.5);
            Assert.Equal(regular, endless);
        }
    }

    [Fact] // legal：边界 40/41 两段相接——常规段封顶值与外推段首波同一乘子口径
    public void Segments_join_at_the_cap()
    {
        // era1 形状在波 40 的斜坡值 (6+⌊39/3⌋)/1=19 < 40 —— 封顶未咬合，两段各自按冻结式取值
        Assert.Equal(28, EndlessWaveFormula.Count(40, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 1.5));
        Assert.Equal(63, EndlessWaveFormula.Count(41, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 1.5));
    }

    [Fact] // legal：极端 int 波不越界（守卫在 double 域先钳再转 int）
    public void Extreme_wave_stays_finite()
    {
        int count = EndlessWaveFormula.Count(int.MaxValue, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 2.0);
        Assert.Equal(EndlessWaveFormula.MaxCountClamp, count);
    }

    // ------------------------------------------------ 守卫 -------------

    [Fact] // boundary：负波/负乘子/NaN 乘子一律 0（永不生成负敌数）
    public void Guards_return_zero()
    {
        Assert.Equal(0, EndlessWaveFormula.Count(-1, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, 1.5));
        Assert.Equal(0, EndlessWaveFormula.Count(41, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, -0.1));
        Assert.Equal(0, EndlessWaveFormula.Count(41, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, double.NaN));
        Assert.Equal(0, EndlessWaveFormula.Count(10000, Shape.Base, Shape.Begin, Shape.Spacing, Shape.Scaling, -1.0));
    }

    // ------------------------------------------------ 精英线 -----------

    [Theory] // legal：精英线 1+⌊(w−8)/4⌋ 不封顶（SP-7a——常规式本就不封顶，直接沿用）
    [InlineData(7, 0)]
    [InlineData(8, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    [InlineData(100, 24)]
    [InlineData(10000, 2499)]
    [InlineData(-3, 0)]
    public void Elite_line_is_uncapped(int wave, int expected)
    {
        Assert.Equal(expected, EndlessWaveFormula.EliteCount(wave));
        Assert.Equal(Elite.Count(wave), EndlessWaveFormula.EliteCount(wave));
    }
}
