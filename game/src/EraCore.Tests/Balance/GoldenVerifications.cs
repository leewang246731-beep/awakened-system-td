using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// Four-group verification of v3.2 appendix F.5（第 5/10/20/30 波）,
// T-035 first version. Expected numbers are frozen F.5 values (hardcoded,
// source cited per assertion); every ACTUAL number is computed through the
// T-036 formula library plus the DpsCalc/EhpCalc arithmetic — no expected
// value is ever fed back as an input (CS-4.4).
// Group 4 re-anchored on the v3.2.1 reconstructed accounting (±20% band
// around the recomputed 12.28s); the v2.1 57s/107s pair is legacy comment
// material only — see Q11 in docs/engineering/08-task-plan.md.
// Float precision 9 = the arithmetic chains carry at most a few ulps of
// rounding; F.5 prints integers or one decimal.
// [F-171] wave verification regression (C-29)
public class GoldenVerifications
{
    // E2 golden parameter set (T-035, [NEW-原型校准]): base 10 / begin 5 /
    // spacing 5 / scaling 2.0, calibrated so wave 5 lands on the F.5 anchor
    // 6 散兵 at the F.5 benchmark threat t=0.75 (wave 5: (10+0)/2×1.25=6).
    private const int Base = 10;
    private const int Begin = 5;
    private const int Spacing = 5;
    private const double Scaling = 2.0;
    private const double T = 0.75; // F.5 group-1 benchmark threat

    // F.5 wave-30 economy gate, v2.1 legacy values 57s/107s（v2.1 §14.3 断言，
    // 推导本身未随档；基线 8402414 已查无存）。v3.2.1 起 group 4 改按重建口径
    // 断言（下），57/107 不再作为门禁常量，追回挂 08-task-plan 开放问题 Q11。

    [Fact] // F.5 group 1: 第 5 波（t=0.75）6 散兵 360 EHP → 2 箭楼 6 s
    public void Group1_wave5_six_skirmishers_fall_to_two_arrow_towers_in_six_seconds()
    {
        ThreatMultipliers m = Threat.Multipliers(T);

        int count = WaveFormula.Count(5, Base, Begin, Spacing, Scaling, m.Count);
        Assert.Equal(6, count); // F.5 冻结: 6 散兵

        double ehp = EhpCalc.Total(count, E2EnemyBench.SkirmisherHp, m.Hp); // HP 乘子 1.0 at t=0.75
        Assert.Equal(360.0, ehp, precision: 9); // F.5 冻结: 360 EHP

        double dps = 2 * DpsCalc.EffectiveDps(
            E2TowerBench.ArrowT1Dps, E2TowerBench.ArrowT1Damage, E2EnemyBench.SkirmisherArmor);
        Assert.Equal(6.0, DpsCalc.ClearSeconds(ehp, dps), precision: 9); // F.5 冻结: 6 s
    }

    [Fact] // F.5 group 2: 第 10 波需 T2 上场（护甲吃 23%）
    public void Group2_wave10_warrior_armor_eats_23_percent_and_forces_T2()
    {
        ThreatMultipliers m = Threat.Multipliers(T);

        int count = WaveFormula.Count(10, Base, Begin, Spacing, Scaling, m.Count);
        Assert.Equal(6, count); // (10+1)/2×1.25 = 6.875 → 6 勇士

        // SP-3c derivation: 弩塔单发 17 vs 勇士甲 4 → 4/17 ≈ 23.5%, F.5 rounds to 23%
        double eaten = DpsCalc.ArmorEatenRatio(E2TowerBench.CrossbowT2Damage, E2EnemyBench.WarriorArmor);
        Assert.Equal(4.0 / 17.0, eaten, precision: 9);
        Assert.InRange(eaten, 0.23, 0.24); // F.5 冻结: 护甲吃 23%（容差=文档取整 0.5pp）

        double ehp = EhpCalc.Total(count, E2EnemyBench.WarriorHp, m.Hp); // 6×300×1.0
        Assert.Equal(1800.0, ehp, precision: 9);

        double t1Pair = 2 * DpsCalc.EffectiveDps(
            E2TowerBench.ArrowT1Dps, E2TowerBench.ArrowT1Damage, E2EnemyBench.WarriorArmor); // 2×30×11/15 = 44
        double t2Pair = 2 * DpsCalc.EffectiveDps(
            E2TowerBench.CrossbowT2Dps, E2TowerBench.CrossbowT2Damage, E2EnemyBench.WarriorArmor); // ≈2×45.9
        double t1Seconds = DpsCalc.ClearSeconds(ehp, t1Pair);
        double t2Seconds = DpsCalc.ClearSeconds(ehp, t2Pair);

        Assert.True(t2Seconds < t1Seconds, $"T2 pair ({t2Seconds:F2}s) must out-clear the T1 pair ({t1Seconds:F2}s) — 需 T2 上场");
        Assert.True(t2Seconds < Campaign.WaveIntervalSeconds(T), "T2 pair must clear inside the wave interval");
    }

    [Fact] // F.5 group 3: 第 20 波精英线 4 攻城锤 → 3 弩炮
    public void Group3_wave20_four_rams_fall_to_three_onagers_within_the_window()
    {
        int elites = Elite.Count(20);
        Assert.Equal(4, elites); // F.5/SP-3c 冻结: 1+⌊(20−8)/4⌋ = 4

        // F.5 验算基准: base HP without the per-level threat multiplier
        // (SP-3c L132: 威胁 HP 乘子按关卡 t 另乘, 验算基准同 F.5)
        double ehp = EhpCalc.Total(elites, E2EnemyBench.RamHp, 1.0);
        Assert.Equal(6000.0, ehp, precision: 9); // SP-3c: 4×1500 = 6000 点基础 HP

        double battery = 3 * DpsCalc.EffectiveDps(
            E2TowerBench.OnagerT3Dps, E2TowerBench.OnagerT3Damage, E2EnemyBench.RamArmor);
        Assert.Equal(222.0, battery, precision: 6); // SP-3c: 3×80×(160−12)/160 ≈ 222 有效 DPS

        double seconds = DpsCalc.ClearSeconds(ehp, battery);
        Assert.InRange(seconds, 27.0, 27.1); // SP-3c: ≈27 s 清场 (6000/222 = 27.03)
        Assert.True(seconds < Campaign.WaveIntervalSeconds(T), "clear time must fit the wave window");
    }

    [Fact] // F.5 group 4（v3.2.1 重建口径）: 第 30 波 4 火油 ≈12.28s 清场 ≤ 波间隔
    public void Group4_wave30_four_fire_oil_towers_clear_in_reconstructed_seconds()
    {
        ThreatMultipliers m = Threat.Multipliers(T);
        int count = WaveFormula.Count(30, Base, Begin, Spacing, Scaling, m.Count);
        int elites = Elite.Count(30);
        Assert.Equal(9, count); // (10+⌊25/5⌋)/2×1.25 = 9.375 → 9 散兵
        Assert.Equal(6, elites); // 1+⌊22/4⌋ = 6 攻城锤

        double regularEhp = EhpCalc.Total(count, E2EnemyBench.SkirmisherHp, m.Hp); // 9×60×1.0 = 540
        double eliteEhp = EhpCalc.Total(elites, E2EnemyBench.RamHp, 1.0); // F.5 验算基准（同 group 3）= 9000

        double regularDps = 4 * DpsCalc.EffectiveDps(
            E2TowerBench.FireOilT4Dps, E2TowerBench.FireOilT4Damage, E2EnemyBench.SkirmisherArmor); // armor 0 → 960
        double eliteDps = 4 * DpsCalc.EffectiveDps(
            E2TowerBench.FireOilT4Dps, E2TowerBench.FireOilT4Damage, E2EnemyBench.RamArmor); // (60−12)/60 → 768

        double seconds = DpsCalc.ClearSeconds(regularEhp, regularDps)
            + DpsCalc.ClearSeconds(eliteEhp, eliteDps); // 540/960 + 9000/768 = 12.28125 s

        // 重建值 ±20% 带：12.28125×0.8 = 9.825 → 下界 9.8；12.28125×1.2 = 14.7375
        // → 上界 14.8（带界向外取整留痕）。值本身仍由 T-036 公式库 + F.5/SP-3
        // 锚点常量真计算得出，带界只作回归哨兵、绝不回灌为输入（CS-4.4）。
        Assert.InRange(seconds, 9.8, 14.8);

        // 窗口用现行公式：120 s×间隔乘子 1.3(t=0.75) = 156 s（v3.2 §6）。
        // v2.1 遗留 57s/107s（§14.3 引用，推导未随档、基线 8402414 已查无存）
        // 降为注释——追回挂 08-task-plan 开放问题 Q11，不再作为断言门禁
        // （v3.2.1 口径修正，出处=code-review Standards 轴裁决）。
        Assert.True(
            seconds < Campaign.WaveIntervalSeconds(T),
            $"computed {seconds:F2}s exceeds the live wave interval "
            + $"{Campaign.WaveIntervalSeconds(T):F0}s (120s×间隔乘子@t={T})");
    }
}
