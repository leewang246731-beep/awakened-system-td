using EraCore.Contracts.Foundation;

namespace EraCore.Energy;

/// <summary>
/// Energy risk event taxonomy (C-08, T-113, F-010): the load-rate ladder
/// risks of SP-4 (11-spec-tables L140, 适用代=E3 起). Numeric member values
/// mirror the SP-4 row ids (E3/E4) for traceability only — the engine never
/// branches on them.
/// </summary>
public enum EnergyRiskEventKind
{
    /// <summary>
    /// E3 过热爆炸 (SP-4 L146): sustained ≥100% load fills a heat slot;
    /// at capacity the culprit generator explodes (AOE + downtime/derate).
    /// </summary>
    OverheatExplosion = 3,

    /// <summary>
    /// E4 熔毁 (SP-4 L147): ≥120% load sustained 20 s or an instantaneous
    /// ≥150% spike (or a coolant cut, task-card 断冷 path) starts a 45 s
    /// countdown; expiry permanently destroys the culprit generator and
    /// leaves a radiation zone (地面污染 debuff 地形).
    /// </summary>
    Meltdown = 4,

    /// <summary>
    /// E5 湮灭 (SP-4 L148): a fusion building's HP hitting zero, or ≥150%
    /// load sustained 10 s, detonates IMMEDIATELY (无倒计时) — everything
    /// inside the (force-field-halvable) radius takes the flat unit damage
    /// and other energy buildings inside chain-detonate (×0.6 decay per
    /// chain link). T-134.
    /// </summary>
    Annihilation = 5,
}

/// <summary>
/// E3 overheat parameter row (SP-4 L146; every value [NEW-原型校准], lives in
/// <c>data/eras/{era}.json</c> per SP-4 L150 配套口径③ — the engine carries
/// NO design numbers, CS-2.3/RL7). Semantics:
/// <c>OverloadLoadRatio</c>=电网负载率 (demand÷output) at/above which the
/// grid counts as overloaded (满载, ≥ inclusive); while overloaded the heat
/// slot fills at <c>BaseHeatPerSecond</c> plus
/// <c>HeatPerLoadStepPerSecond</c> per <c>HeatLoadStepRatio</c> of load above
/// the threshold (linear, SP-4「过热期 +2/s，负载率每 +10% 追加 +1/s」);
/// unloading (load below threshold) decays the slot at
/// <c>HeatCooldownPerSecond</c> (「卸载后 −5/s 回落」). The explosion needs
/// BOTH <c>OverloadSustainedSeconds</c> of unbroken overload AND a full heat
/// slot (<c>HeatCapacity</c>) — at exactly the threshold the slot alone takes
/// <c>HeatCapacity/BaseHeatPerSecond</c> = 50 s (SP-4「0→100 需 ≥50 s」
/// boundary case) while heavier loads fill faster but wait out the sustained
/// window, so both gates bite (防"必然爆炸"——卸载即中断任一门).
/// </summary>
/// <param name="OverloadLoadRatio">Load ratio at/above which the grid is overloaded (1.00 = 100%).</param>
/// <param name="OverloadSustainedSeconds">Uninterrupted overload seconds required before an explosion can fire (30 s).</param>
/// <param name="HeatCapacity">Heat slot full value (100).</param>
/// <param name="BaseHeatPerSecond">Heat gain per second while overloaded at the threshold (+2/s).</param>
/// <param name="HeatLoadStepRatio">Load-ratio step above the threshold that adds heat (0.10 = every +10%).</param>
/// <param name="HeatPerLoadStepPerSecond">Extra heat per second per step (+1/s).</param>
/// <param name="HeatCooldownPerSecond">Heat decay per second after unloading (−5/s).</param>
/// <param name="ExplosionRadiusCells">Explosion radius in cells (4).</param>
/// <param name="BuildingDamageMaxHpFraction">Damage to buildings inside the radius as a fraction of their max HP (50%).</param>
/// <param name="EnemyDamage">Flat damage to enemies inside the radius (300).</param>
/// <param name="GeneratorDowntimeSeconds">Culprit generator full stop after the explosion (60 s 停机).</param>
/// <param name="GeneratorDerateFraction">Culprit output fraction after the stop (50% 功率).</param>
/// <param name="GeneratorDerateSeconds">Duration of the derate window (30 s).</param>
public sealed record OverheatTuning(
    double OverloadLoadRatio,
    double OverloadSustainedSeconds,
    double HeatCapacity,
    double BaseHeatPerSecond,
    double HeatLoadStepRatio,
    double HeatPerLoadStepPerSecond,
    double HeatCooldownPerSecond,
    double ExplosionRadiusCells,
    double BuildingDamageMaxHpFraction,
    double EnemyDamage,
    double GeneratorDowntimeSeconds,
    double GeneratorDerateFraction,
    double GeneratorDerateSeconds)
{
    /// <summary>
    /// Rejects non-finite values, non-positive durations/capacity/step and
    /// fractions outside [0,1] with <see cref="ArgumentException"/> (the
    /// constructor guard of the T-113 parameter channel).
    /// </summary>
    public void Validate()
    {
        RiskEnsure.FinitePositive(OverloadLoadRatio, nameof(OverloadLoadRatio));
        RiskEnsure.FinitePositive(OverloadSustainedSeconds, nameof(OverloadSustainedSeconds));
        RiskEnsure.FinitePositive(HeatCapacity, nameof(HeatCapacity));
        RiskEnsure.NonNegative(BaseHeatPerSecond, nameof(BaseHeatPerSecond));
        RiskEnsure.FinitePositive(HeatLoadStepRatio, nameof(HeatLoadStepRatio));
        RiskEnsure.NonNegative(HeatPerLoadStepPerSecond, nameof(HeatPerLoadStepPerSecond));
        RiskEnsure.NonNegative(HeatCooldownPerSecond, nameof(HeatCooldownPerSecond));
        RiskEnsure.FinitePositive(ExplosionRadiusCells, nameof(ExplosionRadiusCells));
        RiskEnsure.Fraction(BuildingDamageMaxHpFraction, nameof(BuildingDamageMaxHpFraction));
        RiskEnsure.NonNegative(EnemyDamage, nameof(EnemyDamage));
        RiskEnsure.FinitePositive(GeneratorDowntimeSeconds, nameof(GeneratorDowntimeSeconds));
        RiskEnsure.Fraction(GeneratorDerateFraction, nameof(GeneratorDerateFraction));
        RiskEnsure.FinitePositive(GeneratorDerateSeconds, nameof(GeneratorDerateSeconds));
    }
}

/// <summary>
/// Validation helpers of the T-113 parameter channel (file-local: the shared
/// <c>Guard</c> offers int-domain range checks only; the ledger's inline
/// finite checks are the style precedent).
/// </summary>
internal static class RiskEnsure
{
    public static void FinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentException($"Must be finite and strictly positive, got {value}.", name);
        }
    }

    public static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"Must be finite and non-negative, got {value}.", name);
        }
    }

    public static void Fraction(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentException($"Must be a finite fraction in [0,1], got {value}.", name);
        }
    }
}

/// <summary>
/// E4 meltdown parameter row (SP-4 L147; all values [NEW-原型校准], data-side
/// = <c>data/eras/{era}.json</c>, RL7). The countdown starts on
/// <c>OverloadLoadRatio</c> sustained for <c>OverloadSustainedSeconds</c> OR
/// an instantaneous load ≥ <c>InstantLoadRatio</c> (「≥120% 连续 20 s，或瞬时
/// ≥150%」), plus the task-card 断冷 path: when <c>RequireCoolant</c> is set,
/// losing the coolant supply starts the countdown at ANY load (T-113 任务卡
/// 「E4 核电[断冷→熔毁→辐射区]」——SP-4 未列冷却液行，[NEW-T-113] 通道).
/// Interruption (SP-4「卸载 ≥30% 负载可中断」): the current load falling to
/// ≤ start-load × (1−<c>InterruptUnloadFraction</c>) — a ≥30% cut of the load
/// the countdown began at, [NEW-T-113] relative reading of 「卸载 ≥30% 负载」
/// — or, for a countdown started by the coolant cut, the coolant being
/// restored (对策：保持不满载/冷却液供给可避免，防"必然爆炸"). Expiry
/// destroys the culprit generator permanently (残骸，修复口径=
/// <c>WreckRecoveryCostFraction</c> of the original cost, 联动 §十①/F-004),
/// hits buildings in <c>ExplosionRadiusCells</c> for
/// <c>BuildingDamageMaxHpFraction</c> of max HP and leaves ground pollution
/// for <c>RadiationZoneSeconds</c> slowing crossing enemies by
/// <c>RadiationEnemySlowFraction</c>.
/// </summary>
/// <param name="OverloadLoadRatio">Sustained trigger load ratio (1.20 = 120%).</param>
/// <param name="OverloadSustainedSeconds">Sustained window before the countdown starts (20 s).</param>
/// <param name="InstantLoadRatio">Instantaneous spike that starts the countdown at once (1.50 = 150%).</param>
/// <param name="CountdownSeconds">Meltdown countdown length (45 s).</param>
/// <param name="InterruptUnloadFraction">Relative load cut that interrupts the countdown (0.30 = ≥30%).</param>
/// <param name="RequireCoolant">Whether this era's generators need a coolant supply (true = 断冷 starts the countdown).</param>
/// <param name="ExplosionRadiusCells">Meltdown radius in cells (6).</param>
/// <param name="BuildingDamageMaxHpFraction">Damage to buildings inside the radius as a fraction of max HP (80%).</param>
/// <param name="RadiationZoneSeconds">Ground pollution duration (60 s).</param>
/// <param name="RadiationEnemySlowFraction">Speed debuff of enemies crossing the zone (25%).</param>
/// <param name="WreckRecoveryCostFraction">Wreck repair-recovery fraction of the original build cost (30%).</param>
public sealed record MeltdownTuning(
    double OverloadLoadRatio,
    double OverloadSustainedSeconds,
    double InstantLoadRatio,
    double CountdownSeconds,
    double InterruptUnloadFraction,
    bool RequireCoolant,
    double ExplosionRadiusCells,
    double BuildingDamageMaxHpFraction,
    double RadiationZoneSeconds,
    double RadiationEnemySlowFraction,
    double WreckRecoveryCostFraction)
{
    /// <summary>
    /// Rejects non-finite values, non-positive durations/radii and fractions
    /// outside [0,1] with <see cref="ArgumentException"/>; the instant spike
    /// must sit at or above the sustained threshold (a lower spike would be
    /// dead data — SP-4 L147 rows are strictly ≥-ordered).
    /// </summary>
    public void Validate()
    {
        RiskEnsure.FinitePositive(OverloadLoadRatio, nameof(OverloadLoadRatio));
        RiskEnsure.FinitePositive(OverloadSustainedSeconds, nameof(OverloadSustainedSeconds));
        RiskEnsure.FinitePositive(InstantLoadRatio, nameof(InstantLoadRatio));
        if (InstantLoadRatio < OverloadLoadRatio)
        {
            throw new ArgumentException(
                $"Instant spike ({InstantLoadRatio}) must be ≥ the sustained trigger ({OverloadLoadRatio}).",
                nameof(InstantLoadRatio));
        }

        RiskEnsure.FinitePositive(CountdownSeconds, nameof(CountdownSeconds));
        RiskEnsure.Fraction(InterruptUnloadFraction, nameof(InterruptUnloadFraction));
        RiskEnsure.FinitePositive(ExplosionRadiusCells, nameof(ExplosionRadiusCells));
        RiskEnsure.Fraction(BuildingDamageMaxHpFraction, nameof(BuildingDamageMaxHpFraction));
        RiskEnsure.NonNegative(RadiationZoneSeconds, nameof(RadiationZoneSeconds));
        RiskEnsure.Fraction(RadiationEnemySlowFraction, nameof(RadiationEnemySlowFraction));
        RiskEnsure.Fraction(WreckRecoveryCostFraction, nameof(WreckRecoveryCostFraction));
    }
}

/// <summary>
/// E5 annihilation parameter row (SP-4 L148; all values [NEW-原型校准],
/// data-side = era5/era.json <c>energy.annihilation</c>, T-134/RL7).
/// Semantics: a fusion building's HP reaching zero (the world layer reports
/// the fact through the <c>fusionHpZero</c> input — the engine is
/// position-free, it only consumes the flag) OR <c>OverloadLoadRatio</c>
/// sustained for <c>OverloadSustainedSeconds</c> detonates IMMEDIATELY —
/// 「无倒计时（即时）」: unlike the E4 meltdown there is NO countdown and NO
/// interrupt (the counterplay lives on the building side: keep the load
/// under 150%, keep fusion HP above zero, or stand a force-field tower —
/// which halves the blast radius, SP-1b L54). The detonation hits
/// <b>everything</b> inside <c>ExplosionRadiusCells</c> for the flat
/// <c>UnitDamage</c> (SP-4「半径内一切单位受 2000 伤害」) and
/// chain-detonates other energy buildings inside the radius with
/// <c>ChainDamageDecayFraction</c> decay per link (world-layer payload — the
/// chain needs positions, the engine carries the numbers).
/// </summary>
/// <param name="OverloadLoadRatio">Sustained trigger load ratio (1.50 = 150%).</param>
/// <param name="OverloadSustainedSeconds">Sustained window before the immediate detonation (10 s).</param>
/// <param name="ExplosionRadiusCells">Detonation radius in cells (8; force-field presence halves it, <see cref="ForceFieldRadiusMultiplier"/>).</param>
/// <param name="UnitDamage">Flat damage to every unit inside the radius (2000).</param>
/// <param name="ChainDamageDecayFraction">Per-link damage decay of the chain reaction (0.6 = ×0.6 each).</param>
/// <param name="ForceFieldRadiusMultiplier">Radius multiplier in force-field presence (0.5 = halved, SP-1b L54).</param>
/// <param name="WreckRecoveryCostFraction">Wreck repair-recovery fraction of the destroyed fusion plant's original cost (0.3, §十①/F-004 残骸修复同族口径 [NEW-T-134]; SP-4 L148 未列，内容侧补位).</param>
public sealed record AnnihilationTuning(
    double OverloadLoadRatio,
    double OverloadSustainedSeconds,
    double ExplosionRadiusCells,
    double UnitDamage,
    double ChainDamageDecayFraction,
    double ForceFieldRadiusMultiplier,
    double WreckRecoveryCostFraction)
{
    /// <summary>
    /// Rejects non-finite values, non-positive durations/radii/damage and
    /// decay/multiplier fractions outside [0,1] with
    /// <see cref="ArgumentException"/>; the force-field multiplier must be
    /// strictly positive (0 would delete the blast, not dampen it).
    /// </summary>
    public void Validate()
    {
        RiskEnsure.FinitePositive(OverloadLoadRatio, nameof(OverloadLoadRatio));
        RiskEnsure.FinitePositive(OverloadSustainedSeconds, nameof(OverloadSustainedSeconds));
        RiskEnsure.FinitePositive(ExplosionRadiusCells, nameof(ExplosionRadiusCells));
        RiskEnsure.FinitePositive(UnitDamage, nameof(UnitDamage));
        RiskEnsure.Fraction(ChainDamageDecayFraction, nameof(ChainDamageDecayFraction));
        RiskEnsure.Fraction(WreckRecoveryCostFraction, nameof(WreckRecoveryCostFraction));
        if (!double.IsFinite(ForceFieldRadiusMultiplier) || ForceFieldRadiusMultiplier <= 0 || ForceFieldRadiusMultiplier > 1)
        {
            throw new ArgumentException(
                $"Must be a finite multiplier in (0,1] (a dampener, not a deleter), got {ForceFieldRadiusMultiplier}.",
                nameof(ForceFieldRadiusMultiplier));
        }
    }
}

/// <summary>
/// One era's energy-risk parameter table (T-113, F-010): the E3 overheat row
/// is mandatory (SP-4 适用代=E3 起), the E4 meltdown row is nullable — an
/// era without nuclear plants (E3 蒸汽) passes <c>null</c> and the engine
/// tracks only the overheat channel (E4 留接口: same framework, one more row
/// of data, 11-spec-tables SP-4 L150 配套口径③「阈值数值入
/// data/eras/{era}.json，禁止硬编码（RL7）」——the era content loader
/// constructs this record; the engine reads it, never the reverse).
/// T-134: the E5 annihilation row is a third nullable channel (E5 聚变;
/// E4 grids pass <c>null</c> — 熔毁 stays the E4 channel, 湮灭 the E5 one).
/// </summary>
/// <param name="Overheat">E3 过热爆炸 parameter row (non-null).</param>
/// <param name="Meltdown">E4 熔毁 parameter row; null = this era has no meltdown channel.</param>
/// <param name="Annihilation">E5 湮灭 parameter row (T-134); null = this era has no annihilation channel.</param>
public sealed record EnergyRiskTuning(OverheatTuning Overheat, MeltdownTuning? Meltdown, AnnihilationTuning? Annihilation = null)
{
    /// <summary>Validates all rows (see the row validators).</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Overheat);
        Overheat.Validate();
        Meltdown?.Validate();
        Annihilation?.Validate();
    }
}

/// <summary>
/// One fired energy risk event (immutable, journaled by the ledger). The
/// ledger applies the in-domain consequences itself (E3: the culprit producer
/// enters its downtime/derate window; E4: the culprit producer is
/// unregistered — 永久损毁); every other field is the world-layer payload
/// (AOE damage numbers, radiation zone debuff, wreck recovery) the host
/// applies to buildings/units/terrain — <see cref="EnergyLedger"/> is
/// position-free by design (C-08 owns the pool, not the map).
/// </summary>
/// <param name="Tick">Tick the event fired on (from <see cref="IClock.Tick"/>).</param>
/// <param name="Kind">E3 overheat explosion or E4 meltdown.</param>
/// <param name="Culprit">The肇事 generator node id (largest-output producer, ties by registration order).</param>
/// <param name="LoadRatioAtTrigger">Grid load ratio on the firing settle.</param>
/// <param name="RadiusCells">Explosion radius in cells (SP-4 后果列).</param>
/// <param name="BuildingDamageMaxHpFraction">Building damage as a fraction of max HP inside the radius.</param>
/// <param name="EnemyDamage">Flat enemy damage inside the radius (E3 only; E4 damages buildings, SP-4 L147).</param>
/// <param name="GeneratorDowntimeSeconds">Culprit full-stop duration (E3 only).</param>
/// <param name="GeneratorDerateFraction">Culprit output fraction during the derate window (E3 only).</param>
/// <param name="GeneratorDerateSeconds">Derate window duration (E3 only).</param>
/// <param name="RadiationZoneSeconds">Ground pollution duration (E4 only).</param>
/// <param name="RadiationEnemySlowFraction">Speed debuff of enemies crossing the zone (E4 only).</param>
/// <param name="WreckRecoveryCostFraction">Wreck repair-recovery fraction of the original cost (E4 only).</param>
/// <param name="ChainDamageDecayFraction">Per-link damage decay of the chain detonation of energy buildings inside the radius (E5 only, T-134; 0 = no chain channel).</param>
/// <param name="ForceFieldRadiusMultiplier">Blast radius multiplier when a force-field tower stands (E5 only, SP-1b L54; payload — the world layer applies it, the engine is position-free).</param>
public sealed record EnergyRiskOutcome(
    long Tick,
    EnergyRiskEventKind Kind,
    EnergyNodeId Culprit,
    double LoadRatioAtTrigger,
    double RadiusCells,
    double BuildingDamageMaxHpFraction,
    double EnemyDamage,
    double GeneratorDowntimeSeconds,
    double GeneratorDerateFraction,
    double GeneratorDerateSeconds,
    double RadiationZoneSeconds,
    double RadiationEnemySlowFraction,
    double WreckRecoveryCostFraction,
    double ChainDamageDecayFraction = 0.0,
    double ForceFieldRadiusMultiplier = 1.0);

/// <summary>
/// Parameter-table-driven energy risk engine (C-08, T-113, F-010; SP-4
/// L140~151 — E3 过热爆炸 + E4 熔毁, 通用框架): pure deterministic
/// per-settle arithmetic over the grid load ratio (需求功率÷发电功率,
/// SP-4 L142). No <see cref="IRng"/> draws anywhere — every trigger is a
/// threshold comparison, so same tuning + same load-ratio sequence ⇒ same
/// event sequence by construction (CS-1.4; the determinism contract of
/// 03-decomposition §1.2 still folds the state via
/// <see cref="ComputeStateHash"/>). The engine owns NO design numbers: every
/// threshold, rate and consequence magnitude arrives in the
/// <see cref="EnergyRiskTuning"/> record the content layer builds from
/// <c>data/eras/{era}.json</c> (RL7; era3.json wiring = T-112 lane — tests
/// inject fixture tables).
/// <para>
/// Channels: (E3) while load ≥ the overheat threshold the sustained window
/// and the heat slot advance together (heat = base + step rate × overload
/// margin, clamped at capacity); unload resets the window and decays the
/// slot. The explosion fires only when BOTH gates hold on the same settle —
/// the 30 s window bites at heavy load (slot fills in ~14 s at 150%), the
/// 100-point slot bites at the 100% threshold (exactly 50 s, SP-4「0→100 需
/// ≥50 s（连续满载最快）」). (E4) a sustained-120%/instant-150%/coolant-cut
/// trigger starts a 45 s countdown that only a ≥30% load cut (relative to
/// the start load) or a coolant restore (coolant-started countdowns only)
/// interrupts; expiry is the meltdown.
/// </para>
/// </summary>
public sealed class EnergyRiskEngine
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Float slack on the "heat slot full" and "load cut reached" boundary
    /// comparisons: per-tick accumulation of e.g. 3/30 per tick over 1000
    /// ticks lands within 1e-12 of the exact 100, and a start-load × 0.7
    /// threshold evaluates to 1.0499999999999999 — the epsilon keeps
    /// tick-boundary triggers and exact-percentage cuts (恰 30% 卸载)
    /// inclusive as the table intends (CS-3.3 容差来源：逐步 double 累加与
    /// 乘法舍入 vs 手算精确值的差).
    /// </summary>
    internal const double LoadCompareEpsilon = 1e-9;

    private readonly OverheatTuning _overheat;
    private readonly MeltdownTuning? _meltdown;
    private readonly AnnihilationTuning? _annihilation;
    private readonly double _ticksPerSecond;

    private long _overloadSustainedTicks;
    private double _heat;
    private long _overheatFires;

    private long _meltdownSustainedTicks;
    private long? _countdownTicksRemaining;
    private double _countdownStartLoad;
    private bool _countdownFromCoolantLoss;
    private long _meltdownFires;

    private long _annihilationSustainedTicks;
    private long _annihilationFires;

    /// <summary>
    /// Creates the engine for one era's parameter table. The tuning is
    /// validated here (bad rows fail at construction, not at the first
    /// settle); <paramref name="ticksPerSecond"/> must be strictly positive
    /// and defaults to the K-1 frozen 30 Hz domain
    /// (<see cref="EnergyLedger.TicksPerSecond"/>).
    /// </summary>
    public EnergyRiskEngine(EnergyRiskTuning tuning, double ticksPerSecond = EnergyLedger.TicksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        tuning.Validate();
        RiskEnsure.FinitePositive(ticksPerSecond, nameof(ticksPerSecond));

        _overheat = tuning.Overheat;
        _meltdown = tuning.Meltdown;
        _annihilation = tuning.Annihilation;
        _ticksPerSecond = ticksPerSecond;
        OverloadSustainTicks = SecondsToTicks(_overheat.OverloadSustainedSeconds);
        MeltdownSustainTicks = _meltdown is null
            ? long.MaxValue
            : SecondsToTicks(_meltdown.OverloadSustainedSeconds);
        AnnihilationSustainTicks = _annihilation is null
            ? long.MaxValue
            : SecondsToTicks(_annihilation.OverloadSustainedSeconds);
    }

    /// <summary>Sustained-overload gate of the E3 channel in ticks (30 s = 900).</summary>
    public long OverloadSustainTicks { get; }

    /// <summary>Sustained-overload gate of the E4 channel in ticks (20 s = 600); long.MaxValue when no meltdown row.</summary>
    public long MeltdownSustainTicks { get; }

    /// <summary>Sustained-overload gate of the E5 channel in ticks (10 s = 300); long.MaxValue when no annihilation row.</summary>
    public long AnnihilationSustainTicks { get; }

    /// <summary>Whether an E5 annihilation row is attached (hash-fold and ledger-seam gate, T-134).</summary>
    public bool HasAnnihilation => _annihilation is not null;

    /// <summary>Uninterrupted ≥ annihilation-threshold ticks; any below-threshold settle resets it to 0 (E5 channel state).</summary>
    public long AnnihilationSustainedTicks => _annihilationSustainedTicks;

    /// <summary>Number of E5 annihilations fired so far (observability, feeds the state hash).</summary>
    public long AnnihilationFireCount => _annihilationFires;

    /// <summary>Heat slot fill 0..<see cref="OverheatTuning.HeatCapacity"/> (E3 channel state).</summary>
    public double Heat => _heat;

    /// <summary>Uninterrupted overloaded ticks of the E3 channel; any below-threshold settle resets it to 0.</summary>
    public long OverloadSustainedTicks => _overloadSustainedTicks;

    /// <summary>Uninterrupted ≥ meltdown-threshold ticks; drives the E4 countdown start.</summary>
    public long MeltdownSustainedTicks => _meltdownSustainedTicks;

    /// <summary>Meltdown countdown ticks remaining; null when no countdown is armed.</summary>
    public long? MeltdownCountdownTicksRemaining => _countdownTicksRemaining;

    /// <summary>Grid load ratio the armed countdown started at (interrupt math anchor).</summary>
    public double MeltdownCountdownStartLoad => _countdownStartLoad;

    /// <summary>Number of E3 explosions fired so far (observability, feeds the state hash).</summary>
    public long OverheatFireCount => _overheatFires;

    /// <summary>Number of E4 meltdowns fired so far (observability, feeds the state hash).</summary>
    public long MeltdownFireCount => _meltdownFires;

    /// <summary>
    /// Heat gain per second at a given load ratio (the SP-4 formula as a pure
    /// function): base + step rate × (load − threshold)/step, floored at the
    /// base — e.g. threshold 1.00/base 2/step 0.10/+1 gives 2, 3, 4, 7 at
    /// 100%, 110%, 120%, 150%.
    /// </summary>
    public double HeatGainPerSecond(double loadRatio)
    {
        RiskEnsure.NonNegative(loadRatio, nameof(loadRatio));
        double margin = Math.Max(0, loadRatio - _overheat.OverloadLoadRatio);
        return _overheat.BaseHeatPerSecond
            + (margin / _overheat.HeatLoadStepRatio) * _overheat.HeatPerLoadStepPerSecond;
    }

    /// <summary>
    /// Relative load cut the armed countdown interrupts at:
    /// <c>start-load × (1 − InterruptUnloadFraction)</c> (卸载 ≥30% 负载).
    /// Meaningful only with a meltdown row and an armed countdown.
    /// </summary>
    public double MeltdownInterruptLoadThreshold()
    {
        if (_meltdown is null || _countdownTicksRemaining is null)
        {
            throw new InvalidOperationException("No meltdown countdown is armed; the interrupt threshold is undefined.");
        }

        return _countdownStartLoad * (1.0 - _meltdown.InterruptUnloadFraction);
    }

    /// <summary>
    /// Runs one settle of both channels. Advances the E3 sustained window /
    /// heat slot (or decays them after unloading), then the E4 sustained
    /// counter / countdown, and returns every outcome firing on THIS settle
    /// (empty almost always; at most one per channel). The caller (the
    /// ledger) passes the raw load ratio — <c>demand ÷ effective output</c>,
    /// 0 when the grid has no producers (an empty grid cannot overheat:
    /// there is no肇事 generator) — the coolant flag and the culprit
    /// candidate (largest-output producer or null). Firing conditions met
    /// with a null culprit are a wiring bug and throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="tick">Current simulation tick (from <see cref="IClock.Tick"/>).</param>
    /// <param name="dtTicks">Ticks elapsed since the previous settle (≥ 1).</param>
    /// <param name="loadRatio">Raw grid load ratio (demand ÷ output); finite, ≥ 0.</param>
    /// <param name="coolantSupplied">Coolant supply state of the era's generators (E4 断冷 channel).</param>
    /// <param name="culprit">Candidate culprit generator id; null when the grid has no producers.</param>
    public IReadOnlyList<EnergyRiskOutcome> OnSettle(
        long tick,
        long dtTicks,
        double loadRatio,
        bool coolantSupplied,
        EnergyNodeId? culprit) => OnSettle(tick, dtTicks, loadRatio, coolantSupplied, fusionHpZero: false, culprit);

    /// <summary>
    /// Full settle including the E5 annihilation channel (T-134, SP-4 L148):
    /// <paramref name="fusionHpZero"/> is the world layer's report that a
    /// fusion building's HP reached zero — an IMMEDIATE detonation input
    /// that bypasses every window (无倒计时). Grids without an annihilation
    /// row ignore the flag (E3/E4 ledgers call the 5-arg overload, which
    /// feeds false — their behaviour and hashes are unchanged).
    /// </summary>
    /// <param name="fusionHpZero">Whether a fusion building's HP hit zero on this settle (E5 聚变通道).</param>
    public IReadOnlyList<EnergyRiskOutcome> OnSettle(
        long tick,
        long dtTicks,
        double loadRatio,
        bool coolantSupplied,
        bool fusionHpZero,
        EnergyNodeId? culprit)
    {
        if (dtTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dtTicks), dtTicks, "At least one tick must elapse per settle.");
        }

        RiskEnsure.NonNegative(loadRatio, nameof(loadRatio));

        double seconds = dtTicks / _ticksPerSecond;
        var fired = default(List<EnergyRiskOutcome>);

        // ---- E3 channel: sustained window + heat slot (SP-4 L146) -------
        if (loadRatio >= _overheat.OverloadLoadRatio)
        {
            _overloadSustainedTicks += dtTicks;
            _heat = Math.Min(_overheat.HeatCapacity, _heat + HeatGainPerSecond(loadRatio) * seconds);
        }
        else
        {
            // 卸载即中断: the window resets and the slot falls back (−5/s).
            _overloadSustainedTicks = 0;
            _heat = Math.Max(0, _heat - _overheat.HeatCooldownPerSecond * seconds);
        }

        bool overheatArmed = _overloadSustainedTicks >= OverloadSustainTicks
            && _heat >= _overheat.HeatCapacity - LoadCompareEpsilon;

        // ---- E4 channel: sustained counter + countdown (SP-4 L147) ------
        bool meltdownArmed = false;
        if (_meltdown is not null)
        {
            if (loadRatio >= _meltdown.OverloadLoadRatio)
            {
                _meltdownSustainedTicks += dtTicks;
            }
            else
            {
                _meltdownSustainedTicks = 0;
            }

            if (_countdownTicksRemaining is null)
            {
                bool instant = loadRatio >= _meltdown.InstantLoadRatio;
                bool sustained = _meltdownSustainedTicks >= MeltdownSustainTicks;
                bool coolantLost = _meltdown.RequireCoolant && !coolantSupplied;
                if (instant || sustained || coolantLost)
                {
                    _countdownTicksRemaining = SecondsToTicks(_meltdown.CountdownSeconds);
                    _countdownStartLoad = loadRatio;
                    _countdownFromCoolantLoss = coolantLost && !instant && !sustained;
                }
            }
            else
            {
                _countdownTicksRemaining -= dtTicks;

                // Interrupts (checked before expiry — the relief wins ties,
                // 防"必然熔毁"): 卸载 ≥30% of the start load (inclusive at
                // exactly 30% — epsilon absorbs the ×0.7 rounding), or the
                // coolant restored when the countdown was started by its loss.
                bool unloadRelief =
                    loadRatio <= (_countdownStartLoad * (1.0 - _meltdown.InterruptUnloadFraction)) + LoadCompareEpsilon;
                bool coolantRelief = _countdownFromCoolantLoss && coolantSupplied;
                if (unloadRelief || coolantRelief)
                {
                    _countdownTicksRemaining = null;
                    _countdownFromCoolantLoss = false;
                }
                else if (_countdownTicksRemaining <= 0)
                {
                    meltdownArmed = true;
                }
            }
        }

        // ---- E5 channel: sustained counter + immediate detonation (SP-4 L148)
        bool annihilationArmed = false;
        if (_annihilation is not null)
        {
            if (loadRatio >= _annihilation.OverloadLoadRatio)
            {
                _annihilationSustainedTicks += dtTicks;
            }
            else
            {
                _annihilationSustainedTicks = 0;
            }

            // 无倒计时（即时）: the sustained gate OR a fusion building's HP
            // hitting zero detonates on THIS settle — no countdown to arm, no
            // interrupt to grant (counterplay lives on the building side:
            // load discipline, fusion HP, or a force-field tower halving the
            // radius at the world layer).
            annihilationArmed = fusionHpZero || _annihilationSustainedTicks >= AnnihilationSustainTicks;
        }

        if (!overheatArmed && !meltdownArmed && !annihilationArmed)
        {
            return Array.Empty<EnergyRiskOutcome>();
        }

        if (culprit is null)
        {
            throw new InvalidOperationException(
                $"Energy risk armed at tick {tick} (load {loadRatio:0.###}) but the grid has no producer to be the culprit.");
        }

        fired = new List<EnergyRiskOutcome>(capacity: 3);

        if (annihilationArmed && _annihilation is not null)
        {
            fired.Add(new EnergyRiskOutcome(
                Tick: tick,
                Kind: EnergyRiskEventKind.Annihilation,
                Culprit: culprit,
                LoadRatioAtTrigger: loadRatio,
                RadiusCells: _annihilation.ExplosionRadiusCells,
                BuildingDamageMaxHpFraction: 0.0, // SP-4 L148: buildings inside take the CHAIN channel, not a max-HP fraction
                EnemyDamage: _annihilation.UnitDamage, // 半径内一切单位受 2000 伤害（平伤位复用 EnemyDamage 载荷）
                GeneratorDowntimeSeconds: 0.0,
                GeneratorDerateFraction: 0.0,
                GeneratorDerateSeconds: 0.0,
                RadiationZoneSeconds: 0.0,
                RadiationEnemySlowFraction: 0.0,
                WreckRecoveryCostFraction: _annihilation.WreckRecoveryCostFraction, // 残骸修复口径 §十①/F-004 同族（聚变堆损毁成残骸可追回，内容侧行）
                ChainDamageDecayFraction: _annihilation.ChainDamageDecayFraction, // 连锁引爆 ×0.6 衰减（世界层链式消费）
                ForceFieldRadiusMultiplier: _annihilation.ForceFieldRadiusMultiplier)); // 力场塔在场半径减半（世界层应用）
            _annihilationFires++;
            _annihilationSustainedTicks = 0;
        }

        if (meltdownArmed && _meltdown is not null)
        {
            fired.Add(new EnergyRiskOutcome(
                Tick: tick,
                Kind: EnergyRiskEventKind.Meltdown,
                Culprit: culprit,
                LoadRatioAtTrigger: loadRatio,
                RadiusCells: _meltdown.ExplosionRadiusCells,
                BuildingDamageMaxHpFraction: _meltdown.BuildingDamageMaxHpFraction,
                EnemyDamage: 0.0,
                GeneratorDowntimeSeconds: 0.0,
                GeneratorDerateFraction: 0.0,
                GeneratorDerateSeconds: 0.0,
                RadiationZoneSeconds: _meltdown.RadiationZoneSeconds,
                RadiationEnemySlowFraction: _meltdown.RadiationEnemySlowFraction,
                WreckRecoveryCostFraction: _meltdown.WreckRecoveryCostFraction));
            _meltdownFires++;
            _countdownTicksRemaining = null;
            _countdownFromCoolantLoss = false;
            _meltdownSustainedTicks = 0;
        }

        if (overheatArmed)
        {
            fired.Add(new EnergyRiskOutcome(
                Tick: tick,
                Kind: EnergyRiskEventKind.OverheatExplosion,
                Culprit: culprit,
                LoadRatioAtTrigger: loadRatio,
                RadiusCells: _overheat.ExplosionRadiusCells,
                BuildingDamageMaxHpFraction: _overheat.BuildingDamageMaxHpFraction,
                EnemyDamage: _overheat.EnemyDamage,
                GeneratorDowntimeSeconds: _overheat.GeneratorDowntimeSeconds,
                GeneratorDerateFraction: _overheat.GeneratorDerateFraction,
                GeneratorDerateSeconds: _overheat.GeneratorDerateSeconds,
                RadiationZoneSeconds: 0.0,
                RadiationEnemySlowFraction: 0.0,
                WreckRecoveryCostFraction: 0.0));
            _overheatFires++;
            _heat = 0;
            _overloadSustainedTicks = 0;
        }

        return fired;
    }

    /// <summary>
    /// FNV-1a hash over the full observable engine state (the channels'
    /// windows, slot, countdown, counters). Same tuning + same settle
    /// sequence ⇒ equal hashes; folded into the ledger hash only when the
    /// engine is attached (absent engines contribute nothing, so E1/E2
    /// hashes stay bit-identical). The E5 annihilation state (T-134) folds
    /// in ONLY when the row exists — E3/E4 engines keep their pre-T-134
    /// hashes bit-for-bit (golden stability).
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_overloadSustainedTicks);
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_heat));
        hash = Mix(hash, (ulong)_overheatFires);
        hash = Mix(hash, (ulong)_meltdownSustainedTicks);
        hash = Mix(hash, _countdownTicksRemaining is null ? 0UL : 1UL + (ulong)_countdownTicksRemaining.Value);
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_countdownStartLoad));
        hash = Mix(hash, _countdownFromCoolantLoss ? 1UL : 0UL);
        hash = Mix(hash, (ulong)_meltdownFires);
        if (_annihilation is not null)
        {
            hash = Mix(hash, (ulong)_annihilationSustainedTicks);
            hash = Mix(hash, (ulong)_annihilationFires);
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    private long SecondsToTicks(double seconds) => (long)Math.Ceiling(seconds * _ticksPerSecond);
}
