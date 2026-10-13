using EraCore.Balance;

namespace EraCore.Tests.Balance;

// [F-171] benchmark constants + tiny DPS/EHP calculators for the F.5
// four-group verification (T-035). Every constant carries its source;
// the T-036 formulas do all wave math, these types only do plain
// arithmetic on the F.5/SP-3 anchor values.
internal static class E2TowerBench
{
    // F.5 E2 tower benchmark（成本合计|DPS|射程格, v3.2 附录 F.5 冻结值）.
    // Damage-per-shot splits are the SP-3c [NEW-原型校准] calibration
    // (d 与 发/s 仅用于甲交互推导, DPS 合计严格等于 F.5 冻结值).
    internal const double ArrowT1Dps = 30.0; // 箭楼 T1 90|30|6.5
    internal const double ArrowT1Damage = 15.0; // v2.1 §14.1 单发 15×2/s
    internal const double CrossbowT2Dps = 60.0; // 弩塔 T2 270|60|8
    internal const double CrossbowT2Damage = 17.0; // SP-3c L131: d≈17 @≈3.5/s
    internal const double OnagerT3Dps = 80.0; // 弩炮 T3 特化 700|80|11
    internal const double OnagerT3Damage = 160.0; // SP-3c L132: 单发 160
    internal const double FireOilT4Dps = 240.0; // 火油塔 T4 2430|240AOE|7.5
    internal const double FireOilT4Damage = 60.0; // v2.1 §14.1 单发 60×4/s
}

internal static class E2EnemyBench
{
    // SP-3b E2 敌表锚点（docs/engineering/11-spec-tables.md L114~L122;
    // 散兵 60 = 已在案, 勇士 300/甲 4 与 攻城锤 1500/甲 12 = F.5 反推锚点）.
    internal const double SkirmisherHp = 60.0;
    internal const double SkirmisherArmor = 0.0;
    internal const double WarriorHp = 300.0;
    internal const double WarriorArmor = 4.0;
    internal const double RamHp = 1500.0;
    internal const double RamArmor = 12.0;
}

/// <summary>
/// Effective-DPS arithmetic over the v3.2 §6 damage formula
/// max(d×衰减−甲, 10%×d×衰减) with 衰减 = 1 on the benchmark rows:
/// per-shot survival ratio = max((d−甲)/d, 0.1), applied to the frozen DPS.
/// </summary>
internal static class DpsCalc
{
    internal static double EffectiveDps(double dps, double damagePerShot, double armor) =>
        dps * Math.Max((damagePerShot - armor) / damagePerShot, 0.1);

    /// <summary>Fraction of a shot eaten by armor: 甲/d (SP-3c 推导口径).</summary>
    internal static double ArmorEatenRatio(double damagePerShot, double armor) =>
        armor / damagePerShot;

    /// <summary>Seconds a battery needs to burn the given effective HP pool.</summary>
    internal static double ClearSeconds(double totalEffectiveHp, double effectiveDps) =>
        totalEffectiveHp / effectiveDps;
}

/// <summary>Effective-HP arithmetic: count × hp × threat HP multiplier.</summary>
internal static class EhpCalc
{
    internal static double Total(int count, double hp, double hpMultiplier) =>
        count * hp * hpMultiplier;
}
