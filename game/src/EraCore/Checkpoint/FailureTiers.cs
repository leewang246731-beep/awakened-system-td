using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Combat;
using EraCore.Sim;

namespace EraCore.Checkpoint;

/// <summary>
/// Failure tier of a lost level (v3.2 §3 失败分档): normal levels restart
/// from the wave checkpoint with a wreckage repair bill; the FINAL level of
/// an era clears only its own save domain and grants one free exercise.
/// Campaign-global state (resource pool, tech) is never lost in any tier.
/// </summary>
public enum FailureScenarioKind
{
    /// <summary>普通关失败：检查点恢复 + 残骸 30% 折扣修复账目。</summary>
    NormalLevel = 0,

    /// <summary>终局关失败：只清本关存档+检查点 + 一次免费演习。</summary>
    FinalLevel = 1,
}

/// <summary>
/// One wrecked building after a normal-level failure (v3.2 §3): the
/// restored checkpoint state re-materializes every non-warehouse building
/// as wreckage — HP damaged by 30%, repairable at a discount of the
/// original build cost.
/// </summary>
/// <param name="BuildingId">Building entity id in the restored simulation.</param>
/// <param name="ContentId">Content id (cost lookup key).</param>
/// <param name="HpBefore">HP as restored from the checkpoint.</param>
/// <param name="WreckedHp">HP after the ×30% wreckage damage (floor on the damage).</param>
/// <param name="DamageTaken">HP removed by the wreckage marking.</param>
/// <param name="OriginalCostPoints">Original build cost in currency points.</param>
/// <param name="RepairCostPoints">
/// Repair cost = original × <see cref="FailureTiers.RepairCostFraction"/> ×
/// (first defeat ? <see cref="FailureTiers.FirstDefeatMultiplier"/> : 1) —
/// 30% normally, 24% on the first defeat (乘法叠加，甲方已签认).
/// </param>
public sealed record WreckEntry(
    EntityId BuildingId,
    string ContentId,
    int HpBefore,
    int WreckedHp,
    int DamageTaken,
    double OriginalCostPoints,
    double RepairCostPoints);

/// <summary>Outcome of a normal-level failure (tier executor result).</summary>
/// <param name="Tier">Always <see cref="FailureScenarioKind.NormalLevel"/>.</param>
/// <param name="RestoredFromWave">Checkpoint wave replayed to; 0 = fresh level start (no checkpoint yet).</param>
/// <param name="RestoredAtTick">Tick the restore replayed to.</param>
/// <param name="RunSeed">Seed of the restored run (replay key #1).</param>
/// <param name="RestoredSimulation">Live simulation standing at the restore point.</param>
/// <param name="Wreckage">Repair bill of the restored buildings (warehouse excluded).</param>
/// <param name="TotalRepairCostPoints">Sum of <paramref name="Wreckage"/> repair costs.</param>
/// <param name="FirstDefeat">Whether the ×0.8 first-defeat discount was applied.</param>
public sealed record NormalLevelFailureOutcome(
    FailureScenarioKind Tier,
    int RestoredFromWave,
    long RestoredAtTick,
    long RunSeed,
    Simulation RestoredSimulation,
    IReadOnlyList<WreckEntry> Wreckage,
    double TotalRepairCostPoints,
    bool FirstDefeat);

/// <summary>Outcome of a final-level failure (tier executor result).</summary>
/// <param name="Tier">Always <see cref="FailureScenarioKind.FinalLevel"/>.</param>
/// <param name="LevelDomainCleared">Every level slot of this level was tombstoned.</param>
/// <param name="CampaignDomainIntact">Campaign meta read back identical (pool/tech zero loss).</param>
/// <param name="FreeExerciseGranted">One free exercise granted (v3.2 §3).</param>
public sealed record FinalLevelFailureOutcome(
    FailureScenarioKind Tier,
    bool LevelDomainCleared,
    bool CampaignDomainIntact,
    bool FreeExerciseGranted);

/// <summary>
/// Failure-tier executor (C-04, T-057; F-004): runs the two failure
/// scenarios against a <see cref="WaveCheckpointStore"/>. Pure orchestration
/// over public seams — restore is a deterministic replay through
/// <see cref="RestoredWaveSnapshot.Replay"/>, wreckage is a repair-bill
/// ledger (the E1 thin simulation has no damageable-building path; entries
/// carry the accounting the assertions pin), and the final-level clear only
/// tombstones the level domain of the SAME port, verifying the campaign
/// domain survived byte-for-byte in value terms.
/// </summary>
public static class FailureTiers
{
    /// <summary>Wreckage damage fraction of building HP (v3.2 §3: 建筑 HP×30% 损毁).</summary>
    public const double WreckageDamageFraction = 0.30;

    /// <summary>Repair cost fraction of the original build cost (v3.2 §3: 原造价×30%).</summary>
    public const double RepairCostFraction = 0.30;

    /// <summary>First-defeat multiplier on the repair bill (v3.2 §3: 首败再×0.8 = 24%, 甲方已签认).</summary>
    public const double FirstDefeatMultiplier = 0.80;

    /// <summary>
    /// 普通关失败：restore from the wave checkpoint (deterministic replay),
    /// then mark every non-warehouse building as wreckage with its repair
    /// bill. Without a stored checkpoint the level restarts from tick 0
    /// (the player never crossed wave 10 — the checkpoint floor).
    /// </summary>
    /// <param name="store">Checkpoint store of the failed level.</param>
    /// <param name="config">Level config the replay rebuilds with.</param>
    /// <param name="rng">Fresh rng stream for the replay (reseeded with the run seed).</param>
    /// <param name="levelSeed">Seed of the level (fresh-start fallback).</param>
    /// <param name="firstDefeat">Whether this is the level's first defeat.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static NormalLevelFailureOutcome FailNormalLevel(
        WaveCheckpointStore store,
        SimulationConfig config,
        IRng rng,
        long levelSeed,
        bool firstDefeat)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);

        Simulation restored;
        int restoredFromWave;
        long restoredAtTick;
        long runSeed;
        if (store.TryRestoreCheckpoint(out RestoredWaveSnapshot snapshot))
        {
            restored = snapshot.Replay(config, rng);
            restoredFromWave = snapshot.Wave;
            restoredAtTick = snapshot.Tick;
            runSeed = snapshot.Seed;
        }
        else
        {
            restored = new Simulation(config, rng, levelSeed);
            restoredFromWave = 0;
            restoredAtTick = 0;
            runSeed = levelSeed;
        }

        double discount = firstDefeat ? FirstDefeatMultiplier : 1.0;
        var wreckage = new List<WreckEntry>();
        double total = 0.0;
        foreach (SimBuilding building in restored.Buildings)
        {
            if (building.Kind == SimBuildingKind.Warehouse)
            {
                // The pool stand is the recovery anchor, not a wreck.
                continue;
            }

            int damage = (int)Math.Floor(building.Hp * WreckageDamageFraction);
            double original = OriginalCostPoints(config, building);
            double repair = original * RepairCostFraction * discount;
            wreckage.Add(new WreckEntry(
                building.Id, building.ContentId, building.Hp, building.Hp - damage, damage, original, repair));
            total += repair;
        }

        return new NormalLevelFailureOutcome(
            FailureScenarioKind.NormalLevel,
            restoredFromWave,
            restoredAtTick,
            runSeed,
            restored,
            wreckage,
            total,
            firstDefeat);
    }

    /// <summary>
    /// 终局关失败：clear ONLY this level's save domain (checkpoint and
    /// rolling slots) and grant one free exercise. The campaign domain of
    /// the same port is read before and after the clear —
    /// <see cref="FinalLevelFailureOutcome.CampaignDomainIntact"/> pins the
    /// 资源池/科技零损失 guarantee as a value comparison, not a promise.
    /// </summary>
    /// <exception cref="ArgumentNullException">store is null.</exception>
    public static FinalLevelFailureOutcome FailFinalLevel(WaveCheckpointStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        ICampaignMeta? before = TryReadCampaign(store.Port);
        store.Clear();
        ICampaignMeta? after = TryReadCampaign(store.Port);

        return new FinalLevelFailureOutcome(
            FailureScenarioKind.FinalLevel,
            LevelDomainCleared: true,
            CampaignDomainIntact: CampaignValuesEqual(before, after),
            FreeExerciseGranted: true);
    }

    // ------------------------------------------------------------ internals

    /// <summary>Original build cost of one building from level content (RL7).</summary>
    private static double OriginalCostPoints(SimulationConfig config, SimBuilding building) => building.Kind switch
    {
        SimBuildingKind.Tower => TowerCost(config, building.ContentId),
        SimBuildingKind.LaborCamp => config.Costs?.LaborCampCostPoints ?? 0.0,
        SimBuildingKind.Harvester => config.Costs?.HarvesterCostPoints ?? 0.0,
        SimBuildingKind.Converter => config.Costs?.ConverterCostPoints ?? 0.0,
        _ => 0.0,
    };

    private static double TowerCost(SimulationConfig config, string contentId)
    {
        foreach (TowerSpec spec in config.TowerCatalog)
        {
            if (string.Equals(spec.Id, contentId, StringComparison.Ordinal))
            {
                return spec.CostTotal;
            }
        }

        return 0.0;
    }

    private static ICampaignMeta? TryReadCampaign(ISavePort port)
    {
        try
        {
            return port.ReadCampaign(SaveVersion.Current);
        }
        catch (InvalidOperationException)
        {
            return null; // no campaign saved in this store yet
        }
    }

    /// <summary>Value equality of the campaign meta (seed + pool + tech).</summary>
    private static bool CampaignValuesEqual(ICampaignMeta? left, ICampaignMeta? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.WorldSeed != right.WorldSeed
            || left.EraIndex != right.EraIndex
            || left.UnlockedTechs.Count != right.UnlockedTechs.Count
            || left.ResourcePool.Count != right.ResourcePool.Count)
        {
            return false;
        }

        for (int i = 0; i < left.UnlockedTechs.Count; i++)
        {
            if (!string.Equals(left.UnlockedTechs[i], right.UnlockedTechs[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (KeyValuePair<string, long> pool in left.ResourcePool)
        {
            if (!right.ResourcePool.TryGetValue(pool.Key, out long value) || value != pool.Value)
            {
                return false;
            }
        }

        return true;
    }
}
