using EraCore.Balance;
using EraCore.Contracts.Foundation;

namespace EraCore.Endless;

/// <summary>
/// Score weights of the endless three boards (T-080, F-006): the SP-7c
/// frozen formula 分数=存活波数×100+精英击杀×20+Boss 击杀×200, plus the
/// T-080 task-card extension 词缀加权 [NEW-原型校准] — every wave survived
/// under an active affix kind earns <see cref="AffixKindPoints"/> per kind
/// (waves 41..50 → +1 kind/wave, 51..60 → +2, 61..70 → +3, 71+ → +4).
/// Supplied by the host like every other tuning record (RL7).
/// </summary>
/// <param name="WavePoints">Points per survived wave (100, SP-7c 冻结式).</param>
/// <param name="ElitePoints">Points per elite kill (20, SP-7c 冻结式).</param>
/// <param name="BossPoints">Points per boss kill (200, SP-7c 冻结式).</param>
/// <param name="AffixKindPoints">Points per ACTIVE AFFIX KIND per survived wave (10, T-080 词缀加权 [NEW-原型校准]).</param>
public sealed record EndlessScoreWeights(
    long WavePoints,
    long ElitePoints,
    long BossPoints,
    long AffixKindPoints)
{
    /// <summary>SP-7c 冻结式 + T-080 词缀加权默认档.</summary>
    public static EndlessScoreWeights Default { get; } = new(
        WavePoints: 100, // ci-allow: R7 T-080 11-spec-tables SP-7c「存活波数×100」（冻结表行）
        ElitePoints: 20, // ci-allow: R7 T-080 11-spec-tables SP-7c「精英击杀×20」（冻结表行）
        BossPoints: 200, // ci-allow: R7 T-080 11-spec-tables SP-7c「Boss 击杀×200」（冻结表行）
        AffixKindPoints: 10); // ci-allow: R7 T-080 词缀加权（[NEW-原型校准]，任务卡记分扩展）
}

/// <summary>
/// One wave of one endless run, fully resolved (T-080): the SP-7a
/// extrapolated counts, the mixed-era roster in spawn order (regulars,
/// then elites, then bosses), the wave interval and the SP-7b affix
/// modifiers the spawner applies at unit creation. Immutable value —
/// produced by <see cref="EndlessRules.PlanWave"/>, consumed by the
/// spawner and hashed by the run's determinism ledger.
/// </summary>
/// <param name="Wave">Wave number, 1-based.</param>
/// <param name="RegularCount">Regular-line count (SP-7a 外推数量).</param>
/// <param name="EliteCount">Elite-line count (1+⌊(w−8)/4⌋, 不封顶).</param>
/// <param name="BossCount">Bosses this wave (1 when the cadence hits, else 0).</param>
/// <param name="IntervalSeconds">Seconds until the next wave (120 s × 间隔乘子, 不放宽).</param>
/// <param name="HpMultiplier">Threat HP multiplier of the run (基础 HP 不随波增长——SP-7a).</param>
/// <param name="Modifiers">Affix modifier set at this wave (SP-7b).</param>
/// <param name="Roster">Spawn list in draw order: regulars, then elites, then bosses.</param>
public sealed record EndlessWavePlan(
    int Wave,
    int RegularCount,
    int EliteCount,
    int BossCount,
    double IntervalSeconds,
    double HpMultiplier,
    EndlessEnemyModifiers Modifiers,
    IReadOnlyList<EndlessSpawn> Roster)
{
    /// <summary>Total enemies of the wave (regular + elite + boss).</summary>
    public int TotalCount => RegularCount + EliteCount + BossCount;
}

/// <summary>
/// One resolved spawn of an endless wave (T-080 混编跨时代): the enemy
/// content id, its era and its role — the exact fact the determinism
/// ledger hashes per unit.
/// </summary>
/// <param name="EnemyId">Enemy content id (K-2 namespace).</param>
/// <param name="EraIndex">Era of the enemy (1..6) — 混编 proof per unit.</param>
/// <param name="Role">Spawn role the unit was drawn for.</param>
public sealed record EndlessSpawn(string EnemyId, int EraIndex, EndlessEnemyRole Role);

/// <summary>
/// Configuration of one endless run (T-080 入口规则, SP-7「任选时代、混编
/// 敌人、奇观带入」+ T-118 开局参与档): the starting era (chosen freely), its
/// wave shape for the ≤40 regular segment, the threat level driving the
/// multipliers, the cross-era enemy pool, the affix tuning, the boss cadence,
/// the carried-in wonder ids, the score weights and the LOCKED opening
/// participation mode (开局档——T-118 三榜分记的路由口径). Validated once at
/// construction; pure data, host-supplied (RL7); the caller seeds its rng from
/// <see cref="Seed"/> (determinism root).
/// </summary>
public sealed class EndlessRunConfig
{
    /// <summary>
    /// Validates the entry rules and freezes the configuration: era 1..6,
    /// non-negative threat, positive boss cadence, elite/boss sub-pools
    /// present (the uncapped elite line and the boss cadence are always
    /// reached in an endless run), unique non-blank wonder ids.
    /// </summary>
    /// <exception cref="ArgumentNullException">A member is null.</exception>
    /// <exception cref="ArgumentException">
    /// Era outside 1..6, negative threat, non-positive boss cadence, a
    /// missing Elite/Boss sub-pool, or blank/duplicate wonder ids.
    /// </exception>
    public EndlessRunConfig(
        long seed,
        int startEraIndex,
        double threatLevel,
        int waveBaseCount,
        int waveBegin,
        int waveSpacing,
        double waveScaling,
        int bossEveryWaves,
        EndlessEnemyPool pool,
        EndlessAffixTuning affixes,
        EndlessScoreWeights scores,
        IReadOnlyList<string> wonderIds,
        EndlessParticipationMode participation = EndlessParticipationMode.Manual)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(affixes);
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(wonderIds);
        if (startEraIndex is < EndlessEnemyPool.MinEraIndex or > EndlessEnemyPool.MaxEraIndex)
        {
            throw new ArgumentException(
                $"Start era must lie in {EndlessEnemyPool.MinEraIndex}..{EndlessEnemyPool.MaxEraIndex} (got {startEraIndex}).",
                nameof(startEraIndex));
        }

        if (!EndlessParticipation.IsDefined(participation))
        {
            throw new ArgumentException(
                $"Participation mode must be a defined three-board mode (got {participation}).", nameof(participation));
        }

        if (!(threatLevel >= 0))
        {
            throw new ArgumentException($"Threat level must be non-negative (got {threatLevel}).", nameof(threatLevel));
        }

        if (bossEveryWaves <= 0)
        {
            throw new ArgumentException(
                $"Boss cadence must be strictly positive (got {bossEveryWaves}).", nameof(bossEveryWaves));
        }

        if (!pool.HasRole(EndlessEnemyRole.Elite))
        {
            throw new ArgumentException(
                "The endless pool needs at least one Elite entry — the elite line 1+⌊(w−8)/4⌋ is uncapped (SP-7a).",
                nameof(pool));
        }

        if (!pool.HasRole(EndlessEnemyRole.Boss))
        {
            throw new ArgumentException(
                $"The endless pool needs at least one Boss entry — the boss cadence hits every {bossEveryWaves} waves.",
                nameof(pool));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string wonder in wonderIds)
        {
            if (string.IsNullOrWhiteSpace(wonder))
            {
                throw new ArgumentException("Carried-in wonder ids must not be blank.", nameof(wonderIds));
            }

            if (!seen.Add(wonder.Trim()))
            {
                throw new ArgumentException($"Duplicate carried-in wonder id '{wonder}'.", nameof(wonderIds));
            }
        }

        Seed = seed;
        StartEraIndex = startEraIndex;
        ThreatLevel = threatLevel;
        WaveBaseCount = waveBaseCount;
        WaveBegin = waveBegin;
        WaveSpacing = waveSpacing;
        WaveScaling = waveScaling;
        BossEveryWaves = bossEveryWaves;
        Pool = pool;
        Affixes = affixes;
        Scores = scores;
        WonderIds = wonderIds.ToList();
        Participation = participation;
    }

    /// <summary>Run seed — the caller's <see cref="IRng"/> is seeded with it (determinism root).</summary>
    public long Seed { get; }

    /// <summary>任选时代 (1..6): theming + the ≤40 segment's wave shape owner.</summary>
    public int StartEraIndex { get; }

    /// <summary>Threat level t (≥0; feed <see cref="Threat.ClampThreat"/> output in practice).</summary>
    public double ThreatLevel { get; }

    /// <summary>基数 of the starting era's wave table (≤40 segment).</summary>
    public int WaveBaseCount { get; }

    /// <summary>起 of the ramp (≤40 segment).</summary>
    public int WaveBegin { get; }

    /// <summary>间隔 of the ramp (≤40 segment); ≤ 0 disables the ramp.</summary>
    public int WaveSpacing { get; }

    /// <summary>成长 divisor (≤40 segment), floored at 0.1 by the formula.</summary>
    public double WaveScaling { get; }

    /// <summary>Boss cadence: one boss every this many waves (10, [NEW-原型校准]).</summary>
    public int BossEveryWaves { get; }

    /// <summary>The cross-era weighted enemy pool (混编敌人).</summary>
    public EndlessEnemyPool Pool { get; }

    /// <summary>Affix magnitude table (SP-7b + T-080 任务卡校准).</summary>
    public EndlessAffixTuning Affixes { get; }

    /// <summary>Three-board score weights (SP-7c + 词缀加权).</summary>
    public EndlessScoreWeights Scores { get; }

    /// <summary>奇观带入: wonder ids carried in from the campaign (v2.1 入口规则行).</summary>
    public IReadOnlyList<string> WonderIds { get; }

    /// <summary>
    /// 开局参与档（T-118 局面参与状态机）: 纯手动/军师辅助/全托管 — frozen
    /// here with the config (开局档锁定) and NEVER re-judged later: it is the
    /// run's ONLY board-routing input (记档整局). Mid-run mode switches live
    /// on the <see cref="EndlessRun"/> as current-mode audit, not here.
    /// </summary>
    public EndlessParticipationMode Participation { get; }

    /// <summary>Threat multiplier triple of the run (v3.2 appendix F.2).</summary>
    public ThreatMultipliers Threat => Balance.Threat.Multipliers(ThreatLevel);
}

/// <summary>
/// The endless wave-planning rules (T-080, F-006; SP-7): compose the SP-7a
/// extrapolation, the SP-7b affix table and the cross-era pool into one
/// fully resolved wave plan. The wave loop is PERPETUAL — every wave
/// number yields a finite plan (count clamp
/// <see cref="EndlessWaveFormula.MaxCountClamp"/> guards the int domain),
/// the run only ends by death (终局=死亡, EndlessRun.Defeat). Pure static
/// composition, stateless; all randomness flows through the injected
/// <see cref="IRng"/> (CS-1.4) with a FIXED draw order — regulars first,
/// then elites, then bosses — which is what makes same-seed replays hash
/// identical.
/// </summary>
public static class EndlessRules
{
    /// <summary>
    /// Resolves one endless wave (永续生成: any wave ≥ 1 works, there is no
    /// victory wave). Draw order is fixed: regular line
    /// (<see cref="EndlessWaveFormula.Count"/> units), elite line
    /// (<see cref="EndlessWaveFormula.EliteCount"/> units), boss (one when
    /// <c>wave % BossEveryWaves == 0</c>). The plan carries the SP-7b
    /// modifier set and the SP-7a interval (120 s × 间隔乘子, reused from
    /// <see cref="Campaign.WaveIntervalSeconds"/> — 不放宽).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    /// <exception cref="ArgumentNullException">config or rng is null.</exception>
    public static EndlessWavePlan PlanWave(int wave, EndlessRunConfig config, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);
        if (wave < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Waves are never negative.");
        }

        ThreatMultipliers threat = config.Threat;
        int regular = EndlessWaveFormula.Count(
            wave,
            config.WaveBaseCount,
            config.WaveBegin,
            config.WaveSpacing,
            config.WaveScaling,
            threat.Count);
        int elite = EndlessWaveFormula.EliteCount(wave);
        int boss = BossCount(wave, config.BossEveryWaves);

        var roster = new List<EndlessSpawn>(regular + elite + boss);
        for (int i = 0; i < regular; i++)
        {
            EndlessEnemyPoolEntry entry = config.Pool.Pick(rng, EndlessEnemyRole.Regular);
            roster.Add(new EndlessSpawn(entry.EnemyId, entry.EraIndex, EndlessEnemyRole.Regular));
        }

        for (int i = 0; i < elite; i++)
        {
            EndlessEnemyPoolEntry entry = config.Pool.Pick(rng, EndlessEnemyRole.Elite);
            roster.Add(new EndlessSpawn(entry.EnemyId, entry.EraIndex, EndlessEnemyRole.Elite));
        }

        for (int i = 0; i < boss; i++)
        {
            EndlessEnemyPoolEntry entry = config.Pool.Pick(rng, EndlessEnemyRole.Boss);
            roster.Add(new EndlessSpawn(entry.EnemyId, entry.EraIndex, EndlessEnemyRole.Boss));
        }

        return new EndlessWavePlan(
            Wave: wave,
            RegularCount: regular,
            EliteCount: elite,
            BossCount: boss,
            IntervalSeconds: Campaign.WaveIntervalSeconds(config.ThreatLevel),
            HpMultiplier: threat.Hp,
            Modifiers: EndlessAffixes.Modifiers(wave, config.Affixes),
            Roster: roster);
    }

    /// <summary>
    /// Boss cadence [NEW-原型校准]: one boss when the wave is a multiple of
    /// <paramref name="everyWaves"/>, else none. Wave 0 (pre-run) spawns
    /// none.
    /// </summary>
    public static int BossCount(int wave, int everyWaves) =>
        wave > 0 && wave % everyWaves == 0 ? 1 : 0;

    /// <summary>
    /// Affix bonus points of a run that survived <paramref name="waves"/>
    /// completed waves (T-080 词缀加权 [NEW-原型校准]): Σ over waves 1..W of
    /// active affix KINDS × <see cref="EndlessScoreWeights.AffixKindPoints"/>
    /// — surviving harder (more-affixed) waves earns more. Waves ≤ 40
    /// contribute nothing (no affix before 41).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">waves is negative.</exception>
    public static long AffixBonusPoints(int waves, EndlessScoreWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (waves < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waves), waves, "Survived waves are never negative.");
        }

        long total = 0;
        for (int wave = EndlessAffixes.SwiftStartWave; wave <= waves; wave++)
        {
            total += EndlessAffixes.CountAt(wave) * weights.AffixKindPoints;
        }

        return total;
    }

    /// <summary>
    /// Three-board score of one settled run (SP-7c 冻结式 + T-080 词缀加权):
    /// <c>存活波数×WavePoints + 精英击杀×ElitePoints + Boss 击杀×BossPoints
    /// + 词缀加权分</c>. Negative kills are rejected (no negative tallies).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A kill tally is negative.</exception>
    public static long Score(int wavesSurvived, long eliteKills, long bossKills, EndlessScoreWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (eliteKills < 0 || bossKills < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eliteKills), "Kill tallies are never negative.");
        }

        if (wavesSurvived < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wavesSurvived), wavesSurvived, "Survived waves are never negative.");
        }

        return (wavesSurvived * weights.WavePoints)
            + (eliteKills * weights.ElitePoints)
            + (bossKills * weights.BossPoints)
            + AffixBonusPoints(wavesSurvived, weights);
    }
}
