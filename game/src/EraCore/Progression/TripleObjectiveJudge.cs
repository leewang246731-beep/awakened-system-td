using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;

namespace EraCore.Progression;

/// <summary>
/// Judgment metric of one pooled objective (SP-8 全数据化, T-073): the
/// frozen vocabulary of <see cref="LevelRunFacts"/> fields the pools may
/// predicate on — S1..S8 of SP-8a and C1..C8 of SP-8b map one metric each.
/// JSON spellings are the member names (<c>StationsLost</c> …), bound by
/// the converter below.
/// </summary>
[JsonConverter(typeof(ObjectiveMetricJsonConverter))]
public enum ObjectiveMetric
{
    /// <summary>S1: harvester/station buildings lost over the whole run.</summary>
    StationsLost,

    /// <summary>S2: minimum defense integrity over the whole run (fraction).</summary>
    DefenseIntegrityMin,

    /// <summary>S3: SellTower executions, player+system merged.</summary>
    TowersSoldCount,

    /// <summary>S4: summed ammo-starvation seconds.</summary>
    AmmoStarvedSeconds,

    /// <summary>S5: minimum over waves of the wave-end average tower efficiency.</summary>
    WaveEndEfficiencyMin,

    /// <summary>S6: damage events on the keep or the warehouse.</summary>
    KeyBuildingsDamagedCount,

    /// <summary>S7: researches completed before the level ended.</summary>
    ResearchCompletedCount,

    /// <summary>S8: logistics deadlocks (pending-unload queue overflows).</summary>
    LogisticsDeadlockCount,

    /// <summary>C1: clear time in seconds — threshold is a multiplier over the level budget (SP-8b C1).</summary>
    ClearTimeSeconds,

    /// <summary>C2: tower builds outside the allowed classes (allowed_classes line).</summary>
    OffClassTowerBuilds,

    /// <summary>C3: net tower count on the field.</summary>
    NetTowerCount,

    /// <summary>C4: system BuildPlan executions (action audit).</summary>
    SystemBuildPlanCount,

    /// <summary>C5: scroll triggers, system + manual merged.</summary>
    ScrollTriggerCount,

    /// <summary>C6: hero rule — pass when the hero never deployed OR never died (SP-8b C6).</summary>
    HeroRule,

    /// <summary>C7: pause event count.</summary>
    PauseCount,

    /// <summary>C8: leaks inside the first-wave grace.</summary>
    FirstWaveLeaks,
}

/// <summary>
/// String converter binding <see cref="ObjectiveMetric"/> to its member-name
/// JSON spellings (objectives.json carries <c>StationsLost</c> etc.; reading
/// case-insensitive, integers rejected — AOT-safe form, 07 D-05).
/// </summary>
public sealed class ObjectiveMetricJsonConverter : JsonStringEnumConverter<ObjectiveMetric>
{
    /// <summary>Member-name policy: StationsLost … FirstWaveLeaks.</summary>
    public ObjectiveMetricJsonConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

/// <summary>Comparison direction of one pooled objective's threshold.</summary>
[JsonConverter(typeof(ObjectiveComparisonJsonConverter))]
public enum ObjectiveComparison
{
    /// <summary>Metric must be ≤ threshold.</summary>
    AtMost = 0,

    /// <summary>Metric must be ≥ threshold.</summary>
    AtLeast = 1,
}

/// <summary>
/// String converter binding <see cref="ObjectiveComparison"/> to the
/// snake_case JSON spellings <c>at_most</c>/<c>at_least</c>.
/// </summary>
public sealed class ObjectiveComparisonJsonConverter : JsonStringEnumConverter<ObjectiveComparison>
{
    /// <summary>Snake-case policy: at_most/at_least.</summary>
    public ObjectiveComparisonJsonConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// One pooled objective (SP-8a/SP-8b row, data-driven): metric + comparison
/// + threshold (+ allowed_classes for C2, budget-multiplier semantics for
/// C1). Produced by <see cref="ObjectivesData.Parse"/> — validated.
/// </summary>
/// <param name="Id">Content id (<c>campaign:obj-s1</c>).</param>
/// <param name="Hard">★ high-difficulty sub-pool membership (challenge pool only, SP-8b).</param>
/// <param name="Metric">Judgment metric.</param>
/// <param name="Comparison">Threshold direction.</param>
/// <param name="Threshold">Threshold value (C1: budget multiplier; C6: ignored).</param>
/// <param name="AllowedClasses">tower.class whitelist for OffClassTowerBuilds (C2); empty otherwise.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
public sealed record ObjectiveEntry(
    string Id,
    bool Hard,
    ObjectiveMetric Metric,
    ObjectiveComparison Comparison,
    double Threshold,
    IReadOnlyList<string> AllowedClasses,
    string? NameZh = null,
    string? NameEn = null);

/// <summary>Both pools of a level's draw (SP-8: strategy 8 + challenge 8).</summary>
/// <param name="Strategy">Strategy pool (SP-8a, 8 entries, non-hard).</param>
/// <param name="Challenge">Challenge pool (SP-8b, 8 entries: 4 ★ hard + 4 non-hard).</param>
public sealed record ObjectivePools(
    IReadOnlyList<ObjectiveEntry> Strategy,
    IReadOnlyList<ObjectiveEntry> Challenge);

/// <summary>JSON shape of one objectives.json pool row.</summary>
public sealed record ObjectiveEntryDto(
    string? Id,
    bool? Hard,
    ObjectiveMetric? Metric,
    [property: JsonPropertyName("op")] ObjectiveComparison? Comparison,
    double? Threshold,
    IReadOnlyList<string>? AllowedClasses,
    string? NameZh,
    string? NameEn);

/// <summary>Root shape of objectives.json.</summary>
/// <param name="ContentType">K-2 content kind declaration ("Level", thin mapping).</param>
/// <param name="StrategyPool">SP-8a rows.</param>
/// <param name="ChallengePool">SP-8b rows.</param>
public sealed record ObjectivesDocument(
    string? ContentType,
    IReadOnlyList<ObjectiveEntryDto>? StrategyPool,
    IReadOnlyList<ObjectiveEntryDto>? ChallengePool);

/// <summary>
/// Loader + validator of the objective-pool document (T-073): enforces the
/// SP-8 frozen shape — exactly 8 strategy rows (non-hard), exactly 8
/// challenge rows split 4 ★ hard + 4 non-hard, unique ids, no duplicate
/// metric within a pool — so the threat-conditioned sub-pool draw always
/// has the frozen cardinality (C1~C4 ★, C5~C8 非 ★).
/// </summary>
public static class ObjectivesData
{
    /// <summary>Strategy-pool cardinality frozen by SP-8a (8 条).</summary>
    public const int StrategyPoolSize = 8;

    /// <summary>Challenge-pool cardinality frozen by SP-8b (8 条).</summary>
    public const int ChallengePoolSize = 8;

    /// <summary>★ high-difficulty sub-pool size frozen by SP-8b (C1~C4).</summary>
    public const int HardSubPoolSize = 4;

    /// <summary>
    /// Parses and validates an objectives document. Damaged payloads throw
    /// <see cref="JsonException"/>; shape violations throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="JsonException">Payload is not valid JSON for the shape.</exception>
    /// <exception cref="ArgumentException">Pool shape violates SP-8 (message names the row).</exception>
    public static ObjectivePools Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ObjectivesDocument document = JsonSerializer.Deserialize(
            json, ObjectivesJsonContext.Default.ObjectivesDocument)
            ?? throw new JsonException("Objectives document deserialized to null.");

        var strategy = ParsePool(document.StrategyPool, "strategy", requireNonHard: true);
        var challenge = ParsePool(document.ChallengePool, "challenge", requireNonHard: false);
        if (strategy.Count != StrategyPoolSize)
        {
            throw new ArgumentException(
                $"Strategy pool must carry exactly {StrategyPoolSize} entries (SP-8a), got {strategy.Count}.");
        }

        if (challenge.Count != ChallengePoolSize)
        {
            throw new ArgumentException(
                $"Challenge pool must carry exactly {ChallengePoolSize} entries (SP-8b), got {challenge.Count}.");
        }

        if (challenge.Count(entry => entry.Hard) != HardSubPoolSize)
        {
            throw new ArgumentException(
                $"Challenge pool must carry exactly {HardSubPoolSize} hard entries (SP-8b C1~C4), got {challenge.Count(entry => entry.Hard)}.");
        }

        return new ObjectivePools(strategy, challenge);
    }

    private static IReadOnlyList<ObjectiveEntry> ParsePool(
        IReadOnlyList<ObjectiveEntryDto>? rows, string poolName, bool requireNonHard)
    {
        if (rows is null || rows.Count == 0)
        {
            throw new ArgumentException($"Objective {poolName} pool is missing.");
        }

        var entries = new List<ObjectiveEntry>();
        var seenIds = new HashSet<string>();
        var seenMetrics = new HashSet<ObjectiveMetric>();
        foreach (ObjectiveEntryDto? row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id))
            {
                throw new ArgumentException($"An objective of the {poolName} pool is missing its id.");
            }

            string id = row.Id.Trim();
            if (!seenIds.Add(id))
            {
                throw new ArgumentException($"Duplicate objective id {id}.");
            }

            if (row.Metric is null || row.Comparison is null || row.Threshold is null)
            {
                throw new ArgumentException($"Objective {id} is missing metric/op/threshold.");
            }

            if (!seenMetrics.Add(row.Metric.Value))
            {
                throw new ArgumentException($"Objective {id} repeats metric {row.Metric} inside one pool — one row per metric.");
            }

            if (requireNonHard && row.Hard == true)
            {
                throw new ArgumentException($"Strategy-pool objective {id} must not carry hard=true (★ belongs to the challenge pool).");
            }

            entries.Add(new ObjectiveEntry(
                id,
                row.Hard ?? false,
                row.Metric.Value,
                row.Comparison.Value,
                row.Threshold.Value,
                row.AllowedClasses ?? Array.Empty<string>(),
                row.NameZh,
                row.NameEn));
        }

        return entries;
    }
}

/// <summary>
/// The audited run counters one level's triple-objective judgment feeds on
/// (SP-8 判定列, T-073). Hosts assemble this at settlement time; the
/// integration seam <see cref="CampaignProgression"/> derives the fields
/// the <c>CampaignLoop</c> public API already exposes and merges the
/// host-supplied remainder (<see cref="HostRunCounters"/>) — nothing
/// defaults silently.
/// </summary>
/// <param name="Survived">Survival objective: the final wave was held (level won).</param>
/// <param name="StationsLost">S1: stations lost.</param>
/// <param name="DefenseIntegrityMin">S2: minimum defense integrity (fraction in [0,1]).</param>
/// <param name="TowersSoldCount">S3: towers sold.</param>
/// <param name="AmmoStarvedSeconds">S4: summed starvation seconds.</param>
/// <param name="WaveEndEfficiencyMin">S5: minimum wave-end average tower efficiency (fraction).</param>
/// <param name="KeyBuildingsDamagedCount">S6: keep/warehouse damage events.</param>
/// <param name="ResearchCompletedCount">S7: researches completed during the level.</param>
/// <param name="LogisticsDeadlockCount">S8: logistics deadlocks.</param>
/// <param name="ClearTimeSeconds">C1: clear time in seconds (K-1 30 Hz domain ÷ 30).</param>
/// <param name="LevelBudgetSeconds">C1: the level's design budget in seconds — 0 means no budget carried, and the timed objective then cannot pass (no silent default).</param>
/// <param name="TowerBuildsByClass">C2: builds per tower.class key (<c>pierce</c> …).</param>
/// <param name="NetTowerCount">C3: net towers on the field.</param>
/// <param name="SystemBuildPlanCount">C4: system BuildPlan executions.</param>
/// <param name="ScrollTriggerCount">C5: scroll triggers (system + manual).</param>
/// <param name="HeroDeployed">C6: whether the hero took the field.</param>
/// <param name="HeroDead">C6: whether the deployed hero died.</param>
/// <param name="PauseCount">C7: pause events.</param>
/// <param name="FirstWaveLeaks">C8: leaks within the first-wave grace.</param>
public sealed record LevelRunFacts(
    bool Survived = false,
    int StationsLost = 0,
    double DefenseIntegrityMin = 1.0,
    int TowersSoldCount = 0,
    double AmmoStarvedSeconds = 0.0,
    double WaveEndEfficiencyMin = 1.0,
    int KeyBuildingsDamagedCount = 0,
    int ResearchCompletedCount = 0,
    int LogisticsDeadlockCount = 0,
    double ClearTimeSeconds = 0.0,
    double LevelBudgetSeconds = 0.0,
    IReadOnlyDictionary<string, int>? TowerBuildsByClass = null,
    int NetTowerCount = 0,
    int SystemBuildPlanCount = 0,
    int ScrollTriggerCount = 0,
    bool HeroDeployed = false,
    bool HeroDead = false,
    int PauseCount = 0,
    int FirstWaveLeaks = 0);

/// <summary>
/// Chest tier from the completed-objective count (SP-8c frozen: 0/1/2/3 →
/// 青铜/白银/黄金/黄金+额外 — deterministic, no score lines). The numeric
/// value IS the completed count.
/// </summary>
public enum ObjectiveTier
{
    /// <summary>0 objectives completed → 青铜.</summary>
    Bronze = 0,

    /// <summary>1 objective completed → 白银.</summary>
    Silver = 1,

    /// <summary>2 objectives completed → 黄金.</summary>
    Gold = 2,

    /// <summary>3 objectives completed → 黄金+额外奖励.</summary>
    GoldExtra = 3,
}

/// <summary>The drawn pair of one level (survival is implicit and always first).</summary>
/// <param name="Strategy">The drawn SP-8a row.</param>
/// <param name="Challenge">The drawn SP-8b row (threat-conditioned sub-pool).</param>
public sealed record ObjectiveDraw(ObjectiveEntry Strategy, ObjectiveEntry Challenge);

/// <summary>
/// Judgment outcome of one level (SP-8c): per-objective pass flags, the
/// completed count and the tier it maps to.
/// </summary>
/// <param name="Draw">The level's drawn objectives.</param>
/// <param name="Survived">Survival objective result.</param>
/// <param name="StrategyPassed">Strategy objective result.</param>
/// <param name="ChallengePassed">Challenge objective result.</param>
/// <param name="CompletedCount">How many of the three objectives passed (0..3).</param>
/// <param name="Tier">The chest tier for that count (0/1/2/3 → bronze/silver/gold/gold_extra).</param>
public sealed record ObjectiveOutcome(
    ObjectiveDraw Draw,
    bool Survived,
    bool StrategyPassed,
    bool ChallengePassed,
    int CompletedCount,
    ObjectiveTier Tier);

/// <summary>
/// Triple-objective judge (T-073, F-054, SP-8): deterministic per-level
/// draw (seed = 关卡 id) and deterministic judgment of the audited facts.
/// Draw rule (SP-8c): strategy pool uniformly 1 of 8; challenge pool by
/// threat — t &lt; 0.9 draws from the non-★ rows (C5~C8), t ≥ 0.9 from the
/// ★ rows (C1~C4). Judgment = each objective's metric compared against its
/// threshold; tier = completed count via <see cref="TierFor"/>.
/// </summary>
public sealed class TripleObjectiveJudge
{
    /// <summary>
    /// Threat where the challenge draw switches to the ★ sub-pool (SP-8c:
    /// t&lt;0.9 → C5~C8, t≥0.9 → C1~C4).
    /// </summary>
    public const double HardSubPoolThreshold = 0.9;

    private readonly ObjectivePools _pools;
    private readonly IRng _rng;

    /// <param name="pools">Validated pools (<see cref="ObjectivesData.Parse"/> output).</param>
    /// <param name="rng">Seeded random port (CS-1.4) — reseeded per draw, never shared state.</param>
    /// <exception cref="ArgumentNullException">pools or rng is null.</exception>
    public TripleObjectiveJudge(ObjectivePools pools, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(rng);
        _pools = pools;
        _rng = rng;
    }

    /// <summary>The loaded pools (SP-8a + SP-8b).</summary>
    public ObjectivePools Pools => _pools;

    /// <summary>
    /// Deterministic draw for one level (seed = FNV-1a of the level id):
    /// same id ⇒ same pair forever. The strategy draw consumes the first
    /// rng number, the challenge draw the second (order frozen).
    /// </summary>
    /// <exception cref="ArgumentException">levelId blank or threat negative.</exception>
    public ObjectiveDraw Draw(string levelId, double threat)
    {
        if (string.IsNullOrWhiteSpace(levelId))
        {
            throw new ArgumentException("Level id must not be blank.", nameof(levelId));
        }

        if (threat < 0)
        {
            throw new ArgumentException($"Threat must be non-negative (got {threat}).", nameof(threat));
        }

        _rng.Reseed(CampaignHash.Fnv64(levelId));
        ObjectiveEntry strategy = _pools.Strategy[_rng.Next(0, _pools.Strategy.Count)];

        IReadOnlyList<ObjectiveEntry> subPool = threat >= HardSubPoolThreshold
            ? _pools.Challenge.Where(entry => entry.Hard).ToArray()
            : _pools.Challenge.Where(entry => !entry.Hard).ToArray();
        ObjectiveEntry challenge = subPool[_rng.Next(0, subPool.Count)];
        return new ObjectiveDraw(strategy, challenge);
    }

    /// <summary>
    /// Draws and judges in one call — the settlement entry point. Survival
    /// comes straight from <paramref name="facts"/>.
    /// <see cref="LevelRunFacts.Survived"/>; tier from the completed count.
    /// </summary>
    /// <exception cref="ArgumentNullException">facts is null.</exception>
    /// <exception cref="ArgumentException">via <see cref="Draw(string, double)"/>.</exception>
    public ObjectiveOutcome Judge(string levelId, double threat, LevelRunFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ObjectiveDraw draw = Draw(levelId, threat);
        bool strategyPassed = Evaluate(draw.Strategy, facts);
        bool challengePassed = Evaluate(draw.Challenge, facts);
        int completed = (facts.Survived ? 1 : 0) + (strategyPassed ? 1 : 0) + (challengePassed ? 1 : 0);
        return new ObjectiveOutcome(draw, facts.Survived, strategyPassed, challengePassed, completed, TierFor(completed));
    }

    /// <summary>
    /// Evaluates one pooled objective against the facts — public so UI
    /// previews and the matrix tests can reuse the exact predicate.
    /// </summary>
    /// <exception cref="ArgumentNullException">entry or facts is null.</exception>
    public static bool Evaluate(ObjectiveEntry entry, LevelRunFacts facts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(facts);
        return entry.Metric switch
        {
            ObjectiveMetric.HeroRule => !facts.HeroDeployed || !facts.HeroDead,
            ObjectiveMetric.ClearTimeSeconds => Compare(
                facts.ClearTimeSeconds, facts.LevelBudgetSeconds * entry.Threshold, entry.Comparison),
            ObjectiveMetric.OffClassTowerBuilds => Compare(
                OffClassBuilds(entry, facts), entry.Threshold, entry.Comparison),
            ObjectiveMetric.StationsLost => Compare(facts.StationsLost, entry.Threshold, entry.Comparison),
            ObjectiveMetric.DefenseIntegrityMin => Compare(facts.DefenseIntegrityMin, entry.Threshold, entry.Comparison),
            ObjectiveMetric.TowersSoldCount => Compare(facts.TowersSoldCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.AmmoStarvedSeconds => Compare(facts.AmmoStarvedSeconds, entry.Threshold, entry.Comparison),
            ObjectiveMetric.WaveEndEfficiencyMin => Compare(facts.WaveEndEfficiencyMin, entry.Threshold, entry.Comparison),
            ObjectiveMetric.KeyBuildingsDamagedCount => Compare(facts.KeyBuildingsDamagedCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.ResearchCompletedCount => Compare(facts.ResearchCompletedCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.LogisticsDeadlockCount => Compare(facts.LogisticsDeadlockCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.NetTowerCount => Compare(facts.NetTowerCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.SystemBuildPlanCount => Compare(facts.SystemBuildPlanCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.ScrollTriggerCount => Compare(facts.ScrollTriggerCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.PauseCount => Compare(facts.PauseCount, entry.Threshold, entry.Comparison),
            ObjectiveMetric.FirstWaveLeaks => Compare(facts.FirstWaveLeaks, entry.Threshold, entry.Comparison),
            _ => throw new InvalidOperationException($"Unhandled metric {entry.Metric}."),
        };
    }

    /// <summary>
    /// Tier for a completed count (SP-8c frozen): 0→青铜, 1→白银, 2→黄金,
    /// 3→黄金+额外. Anything outside 0..3 is a caller bug, not a tier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">completed outside 0..3.</exception>
    public static ObjectiveTier TierFor(int completed) => completed switch
    {
        0 => ObjectiveTier.Bronze,
        1 => ObjectiveTier.Silver,
        2 => ObjectiveTier.Gold,
        3 => ObjectiveTier.GoldExtra,
        _ => throw new ArgumentOutOfRangeException(nameof(completed), completed, "Completed-objective count must be 0..3 (three objectives max)."),
    };

    private static int OffClassBuilds(ObjectiveEntry entry, LevelRunFacts facts)
    {
        if (facts.TowerBuildsByClass is null || facts.TowerBuildsByClass.Count == 0)
        {
            return 0;
        }

        int offClass = 0;
        foreach (KeyValuePair<string, int> line in facts.TowerBuildsByClass)
        {
            if (!entry.AllowedClasses.Contains(line.Key))
            {
                offClass += line.Value;
            }
        }

        return offClass;
    }

    private static bool Compare(double value, double threshold, ObjectiveComparison comparison) =>
        comparison == ObjectiveComparison.AtMost ? value <= threshold : value >= threshold;
}

/// <summary>
/// System.Text.Json source-generation context for objective-pool content
/// (AOT red line, 07 D-05). Kept in this file as a secondary type so its
/// coverage rides on TripleObjectiveJudgeTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ObjectivesDocument))]
[JsonSerializable(typeof(ObjectiveEntryDto))]
[JsonSerializable(typeof(ObjectiveMetric))]
[JsonSerializable(typeof(ObjectiveComparison))]
public sealed partial class ObjectivesJsonContext : JsonSerializerContext;
