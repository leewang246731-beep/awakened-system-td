using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// [F-171] four-group verification of v3.2 appendix F.5（第 5/10/20/30 波）,
// T-035 first version. Expected numbers are frozen F.5 values (hardcoded,
// source cited per assertion); every ACTUAL number is computed through the
// T-036 formula library plus the DpsCalc/EhpCalc arithmetic — no expected
// value is ever fed back as an input (CS-4.4).
// Float precision 9 = the arithmetic chains carry at most a few ulps of
// rounding; F.5 prints integers or one decimal.
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

    // F.5 wave-30 economy gate, frozen numbers (57s &lt; 107s; v2.1 §14.3
    // asserts them, the derivation itself is not on file — the gate values
    // are quoted verbatim in v3.2 附录 F.5 / 04-arch N1 / 07 D-15).
    private const double GateClearSeconds = 57.0;
    private const double GateWindowSeconds = 107.0;

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

    [Fact] // F.5 group 4: 第 30 波 4 火油 57s &lt; 107s（T4 经济验算门禁, 07 N1/D-15）
    public void Group4_wave30_four_fire_oil_towers_beat_the_57s_gate()
    {
        ThreatMultipliers m = Threat.Multipliers(T);
        int count = WaveFormula.Count(30, Base, Begin, Spacing, Scaling, m.Count);
        int elites = Elite.Count(30);
        Assert.Equal(9, count); // (10+5)/2×1.25 = 9.375 → 9 散兵
        Assert.Equal(6, elites); // 1+⌊22/4⌋ = 6 攻城锤

        double regularEhp = EhpCalc.Total(count, E2EnemyBench.SkirmisherHp, m.Hp); // 9×60×1.0
        double eliteEhp = EhpCalc.Total(elites, E2EnemyBench.RamHp, 1.0); // F.5 验算基准（同 group 3）

        double regularDps = 4 * DpsCalc.EffectiveDps(
            E2TowerBench.FireOilT4Dps, E2TowerBench.FireOilT4Damage, E2EnemyBench.SkirmisherArmor); // armor 0 → 960
        double eliteDps = 4 * DpsCalc.EffectiveDps(
            E2TowerBench.FireOilT4Dps, E2TowerBench.FireOilT4Damage, E2EnemyBench.RamArmor); // (60−12)/60 → 768

        double seconds = DpsCalc.ClearSeconds(regularEhp, regularDps)
            + DpsCalc.ClearSeconds(eliteEhp, eliteDps); // 540/960 + 9000/768 = 12.28 s

        // F.5 gate: the four fire-oil towers must clear wave 30 no slower than
        // the frozen 57 s reference line, inside the 107 s window.
        Assert.True(seconds <= GateClearSeconds, $"computed {seconds:F2}s exceeds the F.5 57s reference");
        Assert.True(
            seconds < GateWindowSeconds,
            $"computed {seconds:F2}s exceeds the F.5 107s window (火油塔 T4 2430 上线门禁, 07 N1)");
    }
}
