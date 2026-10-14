using EraCore.CampaignLoop;
using EraCore.Economy;
using EraCore.Sim;
using EraCore.Units;

namespace EraCore.Progression;

/// <summary>
/// Host-audited run counters the CampaignLoop public API cannot derive
/// (T-073): the S1~S8/C4/C5/C8 instrumentation lands with the agent/logistics
/// audit seams, so the host carries these numbers into settlement — every
/// field is a required constructor argument, nothing defaults silently
/// (CS-4.10: no self-invented lenient standard).
/// <para>
/// 命名勘误（REV-20261014-wave3-round2 #1）：本域取名 <c>Progression</c> 而非
/// <c>Campaign</c>，是取语义清晰（战役进度/科技/结算宿主域）、避免与
/// <see cref="EraCore.Balance.Campaign"/>（平衡公式静态类）语义混淆的预防性
/// 选择。T-073 交付时曾把该选择表述为"CS0234 编译冲突避让"——审查探针实证
/// 不成立：Balance.Campaign 是静态类而非命名空间，同名命名空间与其可共存
/// 无编译冲突；原表述系事后合理化，特此改正，命名本身维持不变。
/// </para>
/// </summary>
/// <param name="StationsLost">S1: stations lost over the run.</param>
/// <param name="DefenseIntegrityMin">S2: minimum defense integrity (fraction).</param>
/// <param name="TowersSoldCount">S3: towers sold (player+system merged).</param>
/// <param name="AmmoStarvedSeconds">S4: summed ammo-starvation seconds.</param>
/// <param name="WaveEndEfficiencyMin">S5: minimum wave-end average tower efficiency.</param>
/// <param name="KeyBuildingsDamagedCount">S6: keep/warehouse damage events.</param>
/// <param name="LogisticsDeadlockCount">S8: logistics deadlocks.</param>
/// <param name="SystemBuildPlanCount">C4: system BuildPlan executions.</param>
/// <param name="ScrollTriggerCount">C5: scroll triggers (system + manual).</param>
/// <param name="FirstWaveLeaks">C8: leaks inside the first-wave grace.</param>
public sealed record HostRunCounters(
    int StationsLost,
    double DefenseIntegrityMin,
    int TowersSoldCount,
    double AmmoStarvedSeconds,
    double WaveEndEfficiencyMin,
    int KeyBuildingsDamagedCount,
    int LogisticsDeadlockCount,
    int SystemBuildPlanCount,
    int ScrollTriggerCount,
    int FirstWaveLeaks);

/// <summary>
/// The victory settlement of one level inside the campaign progression
/// (T-073 占领→结算全链): objective verdict + chest tier/items + the banked
/// loot echo of the LevelSession outcome.
/// </summary>
/// <param name="LevelId">Campaign level id the settlement belongs to.</param>
/// <param name="Objectives">Triple-objective verdict (tier inside).</param>
/// <param name="ChestTierId">Chest tier id the tier mapped to (bronze/silver/gold/gold_extra).</param>
/// <param name="ChestItems">Decorative items the chest granted (先定后演).</param>
/// <param name="Loot">The level-session loot echo (banked by LevelSession.OnVictory before this settlement).</param>
public sealed record CampaignSettlement(
    string LevelId,
    ObjectiveOutcome Objectives,
    string ChestTierId,
    IReadOnlyList<GrantedChestItem> ChestItems,
    IReadOnlyList<ResourceGrant> Loot);

/// <summary>
/// C-19 campaign-progression orchestrator (T-073): binds the campaign
/// pool, the tech tree, the triple-objective judge and the chest roller
/// into the occupy→settle→research chain. The victory hook is the
/// <see cref="LevelSession"/> public API — <see cref="SettleVictory"/>
/// reads <see cref="LevelSession.Victory"/> (never touches session
/// internals), judges the objectives, opens the chest deterministically
/// from the run seed, and registers the capture that feeds the tech
/// tree's 关卡绑定 gate. The pool moves ONLY through the already-existing
/// seams: LevelSession banks the loot, <see cref="TechTree"/> withdraws
/// materials/installments.
/// </summary>
public sealed class CampaignProgression
{
    private readonly ResourceStock _pool;
    private readonly TechTree _tech;
    private readonly TripleObjectiveJudge _judge;
    private readonly ChestRewards _chests;
    private readonly List<string> _capturedInOrder = new();
    private readonly HashSet<string> _captured = new();
    private readonly List<GrantedChestItem> _cosmetics = new();

    /// <param name="pool">Campaign resource pool (shared with the LevelSession and the TechTree).</param>
    /// <param name="tech">Tech tree fed with captures.</param>
    /// <param name="judge">Triple-objective judge.</param>
    /// <param name="chests">Chest roller.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public CampaignProgression(ResourceStock pool, TechTree tech, TripleObjectiveJudge judge, ChestRewards chests)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(chests);
        _pool = pool;
        _tech = tech;
        _judge = judge;
        _chests = chests;
    }

    /// <summary>The campaign pool this progression banks through.</summary>
    public ResourceStock Pool => _pool;

    /// <summary>The tech tree captures feed (关卡绑定 gate).</summary>
    public TechTree Tech => _tech;

    /// <summary>Captured level ids in capture order.</summary>
    public IReadOnlyList<string> CapturedLevelIds => _capturedInOrder;

    /// <summary>Every decorative item granted so far (chests + milestones landing spot).</summary>
    public IReadOnlyList<GrantedChestItem> Cosmetics => _cosmetics;

    /// <summary>
    /// Registers a captured (占领) level id — same contract as
    /// <see cref="TechTree.RegisterCapture"/>, mirrored here so the
    /// settlement chain has one entry point.
    /// </summary>
    /// <exception cref="ArgumentNullException">levelId is null.</exception>
    /// <exception cref="ArgumentException">levelId blank.</exception>
    /// <exception cref="InvalidOperationException">levelId already captured.</exception>
    public void RegisterCapture(string levelId)
    {
        ArgumentNullException.ThrowIfNull(levelId);
        if (string.IsNullOrWhiteSpace(levelId))
        {
            throw new ArgumentException("Level id must not be blank.", nameof(levelId));
        }

        string id = levelId.Trim();
        if (!_captured.Add(id))
        {
            throw new InvalidOperationException($"Level {id} is already captured.");
        }

        _capturedInOrder.Add(id);
        _tech.RegisterCapture(id);
    }

    /// <summary>
    /// Settles a won level (the victory hook): judges the triple
    /// objectives from the facts, opens the tier's chest seeded with the
    /// RUN seed (replaying the level reproduces the box), grants the
    /// decorative items and registers the capture. Call after the session
    /// machine reached <see cref="GameFlowState.Victory"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">session or facts is null.</exception>
    /// <exception cref="ArgumentException">levelId blank or threat negative.</exception>
    /// <exception cref="InvalidOperationException">The session has not won yet (Victory outcome missing).</exception>
    public CampaignSettlement SettleVictory(LevelSession session, string levelId, double threat, LevelRunFacts facts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(levelId))
        {
            throw new ArgumentException("Level id must not be blank.", nameof(levelId));
        }

        VictoryOutcome? victory = session.Victory
            ?? throw new InvalidOperationException(
                "SettleVictory requires a won session — LevelSession.Victory is null (the victory hook fires on the machine's Victory state).");

        ObjectiveOutcome objectives = _judge.Judge(levelId, threat, facts);
        string tierId = TierToChestId(objectives.Tier);
        IReadOnlyList<GrantedChestItem> items = _chests.Open(tierId, victory.Seed);
        _cosmetics.AddRange(items);
        RegisterCapture(levelId);
        return new CampaignSettlement(levelId.Trim(), objectives, tierId, items, victory.Loot);
    }

    /// <summary>
    /// Maps an objective tier to its chest tier id (SP-8c):
    /// bronze/silver/gold/gold_extra.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">tier outside the enum.</exception>
    public static string TierToChestId(ObjectiveTier tier) => tier switch
    {
        ObjectiveTier.Bronze => "bronze",
        ObjectiveTier.Silver => "silver",
        ObjectiveTier.Gold => "gold",
        ObjectiveTier.GoldExtra => "gold_extra",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown objective tier."),
    };

    /// <summary>
    /// Derives the derivable <see cref="LevelRunFacts"/> from a WON
    /// session's public API and merges the host-audited counters: survival
    /// (Victory outcome), pause count (machine history), net towers and
    /// per-class builds (sim groups), clear time (victory tick ÷ 30), hero
    /// deployment/death (sim hero). Researches completed during the level
    /// come from the tech-tree ledger (unlock count delta the host
    /// observed), the rest from <paramref name="host"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">session or host is null.</exception>
    /// <exception cref="InvalidOperationException">The session has not won yet.</exception>
    public static LevelRunFacts ObserveFacts(
        LevelSession session, double levelBudgetSeconds, int researchCompletedCount, HostRunCounters host)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);
        VictoryOutcome? victory = session.Victory
            ?? throw new InvalidOperationException("Facts observation requires a won session (Victory outcome).");

        var buildsByClass = new Dictionary<string, int>();
        foreach (SimTower tower in session.Sim.Towers)
        {
            string className = tower.Spec.Class.ToString().ToLowerInvariant();
            buildsByClass[className] = buildsByClass.TryGetValue(className, out int count) ? count + 1 : 1;
        }

        Hero? hero = session.Sim.Hero;
        return new LevelRunFacts(
            Survived: true,
            StationsLost: host.StationsLost,
            DefenseIntegrityMin: host.DefenseIntegrityMin,
            TowersSoldCount: host.TowersSoldCount,
            AmmoStarvedSeconds: host.AmmoStarvedSeconds,
            WaveEndEfficiencyMin: host.WaveEndEfficiencyMin,
            KeyBuildingsDamagedCount: host.KeyBuildingsDamagedCount,
            ResearchCompletedCount: researchCompletedCount,
            LogisticsDeadlockCount: host.LogisticsDeadlockCount,
            ClearTimeSeconds: victory.Tick / (double)Simulation.TicksPerSecond,
            LevelBudgetSeconds: levelBudgetSeconds,
            TowerBuildsByClass: buildsByClass,
            NetTowerCount: session.Sim.Towers.Count,
            SystemBuildPlanCount: host.SystemBuildPlanCount,
            ScrollTriggerCount: host.ScrollTriggerCount,
            HeroDeployed: hero is not null,
            HeroDead: hero is not null && !hero.Alive,
            PauseCount: session.Machine.History.Count(
                transition => transition.Kind == GameFlowTransitionKind.Pause),
            FirstWaveLeaks: host.FirstWaveLeaks);
    }
}
