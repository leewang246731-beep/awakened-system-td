namespace EraCore.Endless;

/// <summary>
/// The four endless affixes (T-080, F-006; 11-spec-tables SP-7b「每 10 波
/// 词缀表」): 迅捷/硬化/相位/自爆强化, activating at waves 41/51/61/71 and
/// stacking from wave 81 every +10 waves (已激活词缀强度 ×1.5/层).
/// </summary>
public enum EndlessAffixKind
{
    /// <summary>No affix (waves below 41).</summary>
    None = 0,

    /// <summary>迅捷 — enemy speed multiplier (SP-7b 生产默认 +25%；T-080 任务卡校准 ×1.3 经 TaskCard 测试校准档注入).</summary>
    Swift = 1,

    /// <summary>硬化 — enemy HP multiplier (SP-7b 生产默认 HP +50%；T-080 任务卡「甲+50%」口径经 TaskCard 测试校准档注入).</summary>
    Harden = 2,

    /// <summary>相位 — periodic invulnerability frames (0.5 s per 6 s cycle, SP-7b 生产默认；T-080 任务卡校准 1 s 经 TaskCard 档注入).</summary>
    Phase = 3,

    /// <summary>自爆强化 — death explosion scaling plus a base suicide granted to non-suicide enemies (SP-7b: 半径 ×1.5/伤害 ×2/基础 1 格 60 伤).</summary>
    SuicideEmpower = 4,
}

/// <summary>
/// Affix effect magnitudes (SP-7b + T-080): supplied by the host like
/// <c>TrampleProfile</c> (CS-2.3/RL7 — the engine itself carries no design
/// numbers). <see cref="Default"/> is the PRODUCTION tier and carries the
/// 11-spec-tables SP-7b rows verbatim (W3 exit ruling REV-20261015-wave3-exit
/// F2: 生产默认档=冻结规格表值); <see cref="TaskCard"/> freezes the T-080
/// task-card calibration and is kept as the TEST-CALIBRATION tier — the four
/// KINDS and the activation/stacking rules are the frozen part, magnitudes
/// are data.
/// </summary>
/// <param name="SwiftSpeedMultiplier">迅捷 speed multiplier (×1.25, SP-7b「敌移速 +25%」——生产默认档).</param>
/// <param name="HardenArmorMultiplier">硬化 armor multiplier (×1.0, SP-7b 表体走 HP 口径不走甲口径——生产默认档；T-080 任务卡「甲+50%」经 <see cref="TaskCard"/> 测试校准档注入).</param>
/// <param name="HardenHpMultiplier">
/// 硬化 HP multiplier (×1.5, SP-7b「敌 HP +50%」——生产默认档；SP-7a「HP
/// 成长由词缀供给」由此通道；T-080 任务卡甲口径默认 1.0 经 TaskCard 档注入).
/// </param>
/// <param name="PhasePeriodSeconds">相位 cycle length in seconds (6 s, SP-7b「每 6 s」).</param>
/// <param name="PhaseInvulnerableSeconds">相位 invulnerability duration per cycle in seconds (0.5 s, SP-7b「每 6 s 获得 0.5 s 无敌帧」——生产默认档；T-080 任务卡「周期无敌 1s」经 TaskCard 档注入).</param>
/// <param name="SuicideRadiusMultiplier">自爆强化 explosion-radius multiplier on enemies already carrying a suicide trait (×1.5, SP-7b).</param>
/// <param name="SuicideDamageMultiplier">自爆强化 explosion-damage multiplier on enemies already carrying a suicide trait (×2, SP-7b).</param>
/// <param name="SuicideBaseRadiusCells">自爆强化 granted base radius for enemies WITHOUT a suicide trait (1 cell, SP-7b).</param>
/// <param name="SuicideBaseDamage">自爆强化 granted base damage for enemies WITHOUT a suicide trait (60 points, SP-7b).</param>
/// <param name="StackIntensityMultiplier">叠层强度乘数 (×1.5/层, SP-7b「81 起每 +10 波叠层」).</param>
public sealed record EndlessAffixTuning(
    double SwiftSpeedMultiplier,
    double HardenArmorMultiplier,
    double HardenHpMultiplier,
    double PhasePeriodSeconds,
    double PhaseInvulnerableSeconds,
    double SuicideRadiusMultiplier,
    double SuicideDamageMultiplier,
    double SuicideBaseRadiusCells,
    double SuicideBaseDamage,
    double StackIntensityMultiplier)
{
    /// <summary>
    /// PRODUCTION default (W3 exit ruling REV-20261015-wave3-exit F2):
    /// the 11-spec-tables SP-7b magnitudes verbatim — 迅捷 +25% 速 / 硬化
    /// HP +50%（甲口径 1.0）/ 相位 0.5 s 无敌帧每 6 s / 自爆强化 半径 ×1.5
    /// 伤害 ×2 基础 1 格 60 点 / 叠层 ×1.5. 生产默认档必须跟随冻结规格表。
    /// </summary>
    public static EndlessAffixTuning Default { get; } = new(
        SwiftSpeedMultiplier: 1.25,
        HardenArmorMultiplier: 1.0,
        HardenHpMultiplier: 1.5,
        PhasePeriodSeconds: 6.0,
        PhaseInvulnerableSeconds: 0.5,
        SuicideRadiusMultiplier: 1.5,
        SuicideDamageMultiplier: 2.0,
        SuicideBaseRadiusCells: 1.0,
        SuicideBaseDamage: 60.0,
        StackIntensityMultiplier: 1.5);

    /// <summary>
    /// T-080 任务卡校准档 — **测试校准档，非生产默认**（生产档裁定=SP-7b
    /// 表值，见 <see cref="Default"/>；REV-20261015-wave3-exit F2 / 11-spec-tables
    /// 变更记录）：迅捷 ×1.3 速 / 硬化 ×1.5 甲（HP 档关）/ 相位 6 s 周期
    /// 1 s 无敌帧。保留供既有测试锚定与任务卡口径对照。
    /// </summary>
    public static EndlessAffixTuning TaskCard { get; } = new(
        SwiftSpeedMultiplier: 1.3,
        HardenArmorMultiplier: 1.5,
        HardenHpMultiplier: 1.0,
        PhasePeriodSeconds: 6.0,
        PhaseInvulnerableSeconds: 1.0,
        SuicideRadiusMultiplier: 1.5,
        SuicideDamageMultiplier: 2.0,
        SuicideBaseRadiusCells: 1.0,
        SuicideBaseDamage: 60.0,
        StackIntensityMultiplier: 1.5);
}

/// <summary>
/// The resolved affix state of one wave: multiplier-typed enemy modifiers
/// (the same shape the unit status-effect channels consume — speed percent,
/// armor percent, timed invulnerability, death-explosion profile —
/// SP-7b × T-080 乘数型效果). All members are 1/0-neutral when the wave has
/// no active affix; the spawner applies them at unit creation exactly like
/// the threat hp multiplier and the boss-state multipliers.
/// </summary>
/// <param name="Wave">Wave the modifiers were resolved for.</param>
/// <param name="ActiveKinds">Affix kinds active at the wave, in activation order.</param>
/// <param name="Stacks">Stack count at the wave (0 below wave 81; +1 per +10 waves from 81 on).</param>
/// <param name="SpeedMultiplier">Enemy speed multiplier (迅捷 absent → exactly 1).</param>
/// <param name="ArmorMultiplier">Enemy armor multiplier (硬化 absent → exactly 1).</param>
/// <param name="HpMultiplier">Enemy HP multiplier (硬化 HP 口径 absent → exactly 1; SP-7b 生产默认 1.5, T-080 任务卡甲口径经 TaskCard 档注入 1.0).</param>
/// <param name="PhasePeriodTicks">相位 cycle length in ticks (0 when absent).</param>
/// <param name="PhaseInvulnerableTicks">相位 invulnerability frames per cycle in ticks (0 when absent).</param>
/// <param name="SuicideRadiusMultiplier">Death-explosion radius multiplier (自爆强化 absent → exactly 1).</param>
/// <param name="SuicideDamageMultiplier">Death-explosion damage multiplier (自爆强化 absent → exactly 1).</param>
/// <param name="SuicideBaseRadiusCells">Granted base explosion radius for non-suicide enemies (0 when absent).</param>
/// <param name="SuicideBaseDamage">Granted base explosion damage for non-suicide enemies (0 when absent).</param>
public sealed record EndlessEnemyModifiers(
    int Wave,
    IReadOnlyList<EndlessAffixKind> ActiveKinds,
    int Stacks,
    double SpeedMultiplier,
    double ArmorMultiplier,
    double HpMultiplier,
    int PhasePeriodTicks,
    int PhaseInvulnerableTicks,
    double SuicideRadiusMultiplier,
    double SuicideDamageMultiplier,
    double SuicideBaseRadiusCells,
    double SuicideBaseDamage);

/// <summary>
/// The every-10-wave affix table (T-080, F-006; SP-7b 叠加制): activation
/// thresholds 迅捷 41 / 硬化 51 / 相位 61 / 自爆强化 71, one shared stack
/// counter per wave from 81 on (每 +10 波 +1 层, every ACTIVE affix's
/// intensity scales by <see cref="EndlessAffixTuning.StackIntensityMultiplier"/>
/// per stack). Pure static rule set, stateless; magnitudes live in the
/// caller-supplied <see cref="EndlessAffixTuning"/> (RL7).
/// </summary>
public static class EndlessAffixes
{
    /// <summary>迅捷 activates at wave 41 (SP-7b first row).</summary>
    public const int SwiftStartWave = 41; // ci-allow: R7 T-080 11-spec-tables SP-7b 词缀起始波 41（冻结表行）

    /// <summary>硬化 activates at wave 51 (SP-7b second row).</summary>
    public const int HardenStartWave = 51; // ci-allow: R7 T-080 11-spec-tables SP-7b 词缀起始波 51（冻结表行）

    /// <summary>相位 activates at wave 61 (SP-7b third row).</summary>
    public const int PhaseStartWave = 61; // ci-allow: R7 T-080 11-spec-tables SP-7b 词缀起始波 61（冻结表行）

    /// <summary>自爆强化 activates at wave 71 (SP-7b fourth row).</summary>
    public const int SuicideStartWave = 71; // ci-allow: R7 T-080 11-spec-tables SP-7b 词缀起始波 71（冻结表行）

    /// <summary>First stacking wave: intensity ×1.5 from 81 on, +1 stack per +10 waves (SP-7b fifth row).</summary>
    public const int StackStartWave = 81; // ci-allow: R7 T-080 11-spec-tables SP-7b 叠层起始波 81（冻结表行）

    /// <summary>Stack cadence: one stack per this many waves (SP-7b「每 +10 波」).</summary>
    public const int StackSpacingWaves = 10; // ci-allow: R7 T-080 11-spec-tables SP-7b「每 +10 波叠层」（冻结表行；10 亦在白名单）

    /// <summary>
    /// Stack-count overflow guard: <c>StackIntensityMultiplier^stacks</c>
    /// is computed in double space, so an absurd wave (int-domain ⇒ up to
    /// ~2×10⁹ stacks) would overflow the multiplier to Infinity; stacks are
    /// clamped here first (the count formula's own guard is
    /// <see cref="EndlessWaveFormula.MaxCountClamp"/> — same discipline).
    /// </summary>
    public const int MaxStacksClamp = 1000; // ci-allow: R7 T-080 词缀叠层溢出守卫（工程上限，非平衡数值）

    /// <summary>Tick domain of the affix profile (K-1 IClock: 30 Hz — seconds → ticks conversion base).</summary>
    public const int TicksPerSecond = 30;

    /// <summary>
    /// Affix kinds active at <paramref name="wave"/>, in activation order
    /// (迅捷 → 硬化 → 相位 → 自爆强化). Waves below 41 return an empty list.
    /// Negative waves return an empty list (guard — no affix before the
    /// first wave).
    /// </summary>
    public static IReadOnlyList<EndlessAffixKind> ActiveAt(int wave)
    {
        if (wave < SwiftStartWave)
        {
            return Array.Empty<EndlessAffixKind>();
        }

        var active = new List<EndlessAffixKind>(4);
        if (wave >= SwiftStartWave)
        {
            active.Add(EndlessAffixKind.Swift);
        }

        if (wave >= HardenStartWave)
        {
            active.Add(EndlessAffixKind.Harden);
        }

        if (wave >= PhaseStartWave)
        {
            active.Add(EndlessAffixKind.Phase);
        }

        if (wave >= SuicideStartWave)
        {
            active.Add(EndlessAffixKind.SuicideEmpower);
        }

        return active;
    }

    /// <summary>
    /// Number of affix KINDS active at the wave (0~4) — the 记分词缀数 of
    /// the T-080 score formula (分数=波数+词缀数加权).
    /// </summary>
    public static int CountAt(int wave) => ActiveAt(wave).Count;

    /// <summary>
    /// Shared stack counter at the wave: 0 below <see cref="StackStartWave"/>,
    /// then +1 per <see cref="StackSpacingWaves"/> waves
    /// (<c>⌊(wave−81)/10⌋+1</c> — wave 81 → 1 层, 91 → 2 层 …), clamped at
    /// <see cref="MaxStacksClamp"/>.
    /// </summary>
    public static int Stacks(int wave)
    {
        if (wave < StackStartWave)
        {
            return 0;
        }

        long stacks = 1 + FloorDiv(wave - StackStartWave, StackSpacingWaves);
        return (int)Math.Min(stacks, MaxStacksClamp);
    }

    /// <summary>
    /// Stack intensity scalar: <c>StackIntensityMultiplier^stacks</c> with
    /// 1.0 for zero stacks. Negative stacks are rejected (an intensity of a
    /// negative order is not a defined game state).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">stacks is negative.</exception>
    public static double Intensity(int stacks, EndlessAffixTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (stacks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stacks), stacks, "Stacks are never negative.");
        }

        return Math.Pow(tuning.StackIntensityMultiplier, Math.Min(stacks, MaxStacksClamp));
    }

    /// <summary>
    /// Resolves the full multiplier set of one wave (SP-7b × T-080): absent
    /// affixs stay neutral (multiplier exactly 1, granted base 0), active
    /// ones scale their DELTA (or duration/profile) by the stack intensity —
    /// <c>1 + (表值−1)×强度</c> for 迅捷/硬化, duration×强度 for 相位 frames,
    /// profile×强度 for 自爆强化 multipliers; the granted base suicide pair
    /// (radius/damage) stays unscaled (it is the flat grant, the SCALING is
    /// the multipliers). Second-domain conversion is
    /// <see cref="TicksPerSecond"/>, rounded AwayFromZero (deterministic).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    /// <exception cref="ArgumentNullException">tuning is null.</exception>
    public static EndlessEnemyModifiers Modifiers(int wave, EndlessAffixTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (wave < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Waves are never negative.");
        }

        IReadOnlyList<EndlessAffixKind> active = ActiveAt(wave);
        int stacks = Stacks(wave);
        double intensity = Intensity(stacks, tuning);

        bool swift = active.Contains(EndlessAffixKind.Swift);
        bool harden = active.Contains(EndlessAffixKind.Harden);
        bool phase = active.Contains(EndlessAffixKind.Phase);
        bool suicide = active.Contains(EndlessAffixKind.SuicideEmpower);

        return new EndlessEnemyModifiers(
            Wave: wave,
            ActiveKinds: active,
            Stacks: stacks,
            SpeedMultiplier: swift ? 1.0 + ((tuning.SwiftSpeedMultiplier - 1.0) * intensity) : 1.0,
            ArmorMultiplier: harden ? 1.0 + ((tuning.HardenArmorMultiplier - 1.0) * intensity) : 1.0,
            HpMultiplier: harden ? 1.0 + ((tuning.HardenHpMultiplier - 1.0) * intensity) : 1.0,
            PhasePeriodTicks: phase ? SecondsToTicks(tuning.PhasePeriodSeconds) : 0,
            PhaseInvulnerableTicks: phase
                ? SecondsToTicks(tuning.PhaseInvulnerableSeconds * intensity)
                : 0,
            SuicideRadiusMultiplier: suicide ? tuning.SuicideRadiusMultiplier * intensity : 1.0,
            SuicideDamageMultiplier: suicide ? tuning.SuicideDamageMultiplier * intensity : 1.0,
            SuicideBaseRadiusCells: suicide ? tuning.SuicideBaseRadiusCells : 0.0,
            SuicideBaseDamage: suicide ? tuning.SuicideBaseDamage : 0.0);
    }

    private static int SecondsToTicks(double seconds) =>
        (int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Floor(a/b) for b &gt; 0 (⌊⌋ of the frozen formulas).</summary>
    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
    }
}
