using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Economy;
using EraCore.Persistence;
using EraCore.Progression;

namespace EraCore.Legacy;

/// <summary>
/// The ordered steps of the era-transition transaction (T-074, F-003):
/// each boundary is probe-able (the fault-injection seam of the rollback
/// tests fires between steps).
/// </summary>
public enum EraTransitionStep
{
    /// <summary>硬前置校验: 终局关 captured + era wonder built.</summary>
    Precheck = 0,

    /// <summary>30% levy: per-resource carried split + 3:1 useless fold into the fund.</summary>
    Levy = 1,

    /// <summary>跃迁快照: A5 core-zone computed once + buildings encoded (F.7 step 1).</summary>
    Snapshot = 2,

    /// <summary>新大陆: deterministic continent from (world seed, target era).</summary>
    NewContinent = 3,

    /// <summary>遗迹映射回放: F.7 replay ladder + no-loss assertion.</summary>
    RelicReplay = 4,

    /// <summary>科技时代根切换: tech tree era root +1.</summary>
    TechRootSwitch = 5,

    /// <summary>前三关排期元数据: the new era's first-three schedule rows.</summary>
    ScheduleBuild = 6,

    /// <summary>campaign 域一次写: the single K-7 campaign-document commit.</summary>
    CampaignWrite = 7,
}

/// <summary>
/// The old-era relic-city layout facts the transaction snapshots
/// (F.7 step 1 input): one row per standing building, in absolute old-map
/// cells (the A5 core zone is computed from these exactly once).
/// </summary>
/// <param name="BuildingId">Old building id.</param>
/// <param name="TypeId">Content type id.</param>
/// <param name="Tier">Building tier (1..4).</param>
/// <param name="Orientation">Facing (quarter turns).</param>
/// <param name="Cell">Absolute old-map cell.</param>
public sealed record RelicCityBuildingFact(
    long BuildingId,
    string TypeId,
    int Tier,
    int Orientation,
    CellCoord Cell)
{
    /// <summary>X relative to the old anchor (snapshot encoding).</summary>
    public int RelXTo(CellCoord anchor) => Cell.X - anchor.X;

    /// <summary>Y relative to the old anchor (snapshot encoding).</summary>
    public int RelYTo(CellCoord anchor) => Cell.Y - anchor.Y;
}

/// <summary>
/// One executed era transition request (T-074). The service touches ONLY
/// the objects handed in: the old pool (levy withdrawal), the tech tree
/// (era root), the legacy ledger (fund/abstract-list/transition record)
/// and the save port (the single campaign write). Everything mutated
/// carries a compensating entry in the transaction journal — any step
/// failure runs the journal backwards and rethrows, leaving every input
/// byte-identical (store untouched: the only write is the last step).
/// </summary>
/// <param name="WorldSeed">Campaign world seed (K-7 payload; drives the new continent).</param>
/// <param name="FromEraIndex">Era being left (1=E1 … 6=终章).</param>
/// <param name="OldPool">The old era's campaign pool — keeps exactly the retained side.</param>
/// <param name="Tech">The campaign tech tree — era root switches at commit.</param>
/// <param name="Ledger">The legacy ledger — fund deposit, abstract-list ingest, transition record.</param>
/// <param name="CityBuildings">Old-era relic-city building facts (snapshot input).</param>
/// <param name="OldAnchor">Old-era core anchor (absolute cells).</param>
/// <param name="NewEraResources">Resources still useful in the target era (everything else folds 3:1).</param>
/// <param name="Clock">Deterministic clock for the new pool.</param>
/// <param name="Rng">Random port (reseeded with the derived continent seed).</param>
/// <param name="Port">Save port — receives exactly ONE campaign write on success.</param>
/// <param name="PreviousMeta">The current campaign document (carried families merge into the new one).</param>
/// <param name="Mapping">Relic-mapping parameter table.</param>
/// <param name="Transition">Era-transition parameter table.</param>
/// <param name="Wonders">Wonder table (the era's hard-precondition wonder).</param>
/// <param name="StepProbe">Optional probe invoked at every step boundary AFTER the step's work — the rollback tests throw here.</param>
public sealed record EraTransitionRequest(
    long WorldSeed,
    int FromEraIndex,
    ResourceStock OldPool,
    TechTree Tech,
    LegacyLedger Ledger,
    IReadOnlyList<RelicCityBuildingFact> CityBuildings,
    CellCoord OldAnchor,
    IReadOnlySet<ResourceKey> NewEraResources,
    IClock Clock,
    IRng Rng,
    ISavePort Port,
    ICampaignMeta PreviousMeta,
    RelicMappingParams Mapping,
    EraTransitionSpec Transition,
    IReadOnlyList<WonderSpec> Wonders,
    Action<EraTransitionStep>? StepProbe = null);

/// <summary>One resource's levy line (T-074, F-011): the 30%/70% split of the jump.</summary>
/// <param name="Resource">Pooled resource.</param>
/// <param name="OldAmount">Pool amount before the jump.</param>
/// <param name="Carried">floor(carried_share × old) — travels to the new era.</param>
/// <param name="Retained">OldAmount − Carried — stays with the old continent (恰 70% 口径的留存侧).</param>
/// <param name="FundFolded">Fund units folded out of the carried part (use-less resources, 3:1).</param>
public sealed record EraLevyLine(
    ResourceKey Resource,
    double OldAmount,
    long Carried,
    double Retained,
    long FundFolded);

/// <summary>
/// The committed transition outcome (T-074): the new era index, the levy
/// ledger, the new-era pool (warehouse level +1), the new continent, the
/// relic mapping result, the first-three schedule and the campaign
/// document that was written exactly once.
/// </summary>
public sealed record EraTransitionResult(
    int FromEraIndex,
    int ToEraIndex,
    IReadOnlyList<EraLevyLine> Levy,
    long FundGained,
    ResourceStock NewPool,
    NewContinent Continent,
    RelicCitySnapshot Snapshot,
    RelicMappingResult Mapping,
    IReadOnlyList<EraFirstThreeSlot> Schedule,
    ICampaignMeta WrittenMeta);

/// <summary>
/// Hard-precondition rejection (T-074, F-003): the transition REFUSED
/// before any mutation — 终局关未占领 or 时代奇观未建 (附录 F.3 清单=跃迁硬条件).
/// </summary>
public sealed class EraTransitionRejectedException : InvalidOperationException
{
    /// <summary>The failing precondition, machine-readable.</summary>
    public string Reason { get; }

    internal EraTransitionRejectedException(string reason)
        : base($"Era transition refused: {reason}.")
    {
        Reason = reason;
    }
}

/// <summary>
/// The atomic E1→E2 era transition (T-074, C-20, F-003): one journal-guarded
/// transaction across [前置校验 → 30% levy → 跃迁快照 → 新大陆 → 遗迹映射回放
/// → 科技时代根切换 → 前三关排期 → campaign 域一次写]. The ONLY persistent
/// effect is the single <see cref="ISavePort.WriteCampaign"/> of the last
/// step — every earlier failure leaves the store byte-identical and the
/// journal restores every in-memory input (pool amounts, tech era root,
/// legacy ledger bytes). Determinism: same WorldSeed ⇒ same continent seed
/// ⇒ identical map, identical replay outcomes, identical ledger payload.
/// </summary>
public sealed class EraTransitionService
{
    /// <summary>
    /// Executes the transaction. Throws
    /// <see cref="EraTransitionRejectedException"/> for hard-precondition
    /// failures (nothing mutated), rethrows any other failure AFTER the
    /// journal has restored every input.
    /// </summary>
    /// <exception cref="ArgumentNullException">Request member null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">FromEraIndex not 1..5.</exception>
    /// <exception cref="EraTransitionRejectedException">Precondition failed.</exception>
    /// <exception cref="Exception">Any later step failure — inputs restored first (journal guarantee).</exception>
    public EraTransitionResult Execute(EraTransitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OldPool);
        ArgumentNullException.ThrowIfNull(request.Tech);
        ArgumentNullException.ThrowIfNull(request.Ledger);
        ArgumentNullException.ThrowIfNull(request.CityBuildings);
        ArgumentNullException.ThrowIfNull(request.NewEraResources);
        ArgumentNullException.ThrowIfNull(request.Clock);
        ArgumentNullException.ThrowIfNull(request.Rng);
        ArgumentNullException.ThrowIfNull(request.Port);
        ArgumentNullException.ThrowIfNull(request.PreviousMeta);
        ArgumentNullException.ThrowIfNull(request.Mapping);
        ArgumentNullException.ThrowIfNull(request.Transition);
        ArgumentNullException.ThrowIfNull(request.Wonders);
        if (request.FromEraIndex is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.FromEraIndex, "Only era jumps 1→2 … 5→6 exist.");
        }

        int toEra = request.FromEraIndex + 1;

        // ---- transaction journal: (undo) in commit order; reversed on any failure
        var undo = new List<Action>();
        try
        {
            // ================= 1 Precheck: 终局关 + 奇观 (hard, refusal not rollback)
            Precheck(request, toEra);
            Probe(request, EraTransitionStep.Precheck);

            // ================= 2 Levy: 30% carried / 70% retained; 3:1 useless fold
            var levy = ComputeLevy(request);
            foreach (EraLevyLine line in levy)
            {
                double taken = request.OldPool.TryWithdraw(line.Resource, line.Carried);
                if (taken != line.Carried)
                {
                    _ = request.OldPool.Deposit(line.Resource, taken);
                    throw new InvalidOperationException(
                        $"Levy withdrawal of {line.Carried} {line.Resource.Value} came up short ({taken}) — pool drifted mid-transaction.");
                }

                ResourceKey resource = line.Resource;
                double restored = line.Carried;
                undo.Add(() => _ = request.OldPool.Deposit(resource, restored));
            }

            long fundGained = levy.Sum(line => line.FundFolded);
            if (fundGained > 0)
            {
                request.Ledger.Fund.DepositFund(fundGained);
                undo.Add(() => request.Ledger.Fund.WithdrawFund(fundGained));
            }

            var newPool = new ResourceStock(
                request.Clock,
                request.OldPool.WarehouseLevel + request.Transition.WarehouseLevelGain,
                request.Transition.PyramidBonusCarriesOver && OldEraWonderBuilt(request));
            foreach (EraLevyLine line in levy)
            {
                if (line.FundFolded == 0 && line.Carried > 0)
                {
                    _ = newPool.Deposit(line.Resource, line.Carried);
                }
            }

            Probe(request, EraTransitionStep.Levy);

            // ================= 3 跃迁快照: A5 zone computed ONCE + encoded payload (F.7 step 1)
            var snapshot = BuildSnapshot(request);
            byte[] snapshotPayload = RelicCityCodec.Encode(snapshot);
            Probe(request, EraTransitionStep.Snapshot);

            // ================= 4 新大陆: deterministic from (seed, era)
            NewContinent continent = NewContinentFactory.Create(
                request.Mapping, request.WorldSeed, toEra, request.Rng);
            Probe(request, EraTransitionStep.NewContinent);

            // ================= 5 遗迹映射回放: F.7 ladder + no-loss guard
            RelicMappingResult mapping = new RelicMapper(request.Mapping).Map(continent, snapshot);
            Probe(request, EraTransitionStep.RelicReplay);

            // ================= 6 科技时代根切换
            int previousEraRoot = request.Tech.EraIndex;
            request.Tech.SetEraIndex(toEra);
            undo.Add(() => request.Tech.SetEraIndex(previousEraRoot));
            Probe(request, EraTransitionStep.TechRootSwitch);

            // ================= 7 前三关排期元数据 (v3.2 §4: 新能源/新物流/考古+英雄+系统)
            IReadOnlyList<EraFirstThreeSlot> schedule = request.Transition.FirstThreeSchedule
                .OrderBy(slot => slot.Slot)
                .ToList();
            Probe(request, EraTransitionStep.ScheduleBuild);

            // ================= 8 campaign 域一次写 (the ONLY persistent effect)
            byte[] ledgerBefore = request.Ledger.Encode(); // journal exact-restore bytes
            request.Ledger.InheritAbstractList(mapping.AbstractList);
            request.Ledger.RecordTransition(request.FromEraIndex, toEra, fundGained);
            undo.Add(() => request.Ledger.DecodeInto(ledgerBefore));
            var meta = BuildCampaignMeta(request, toEra, newPool, mapping, snapshotPayload, request.Ledger.Encode());
            request.Port.WriteCampaign(meta, SaveVersion.Current);
            Probe(request, EraTransitionStep.CampaignWrite);

            return new EraTransitionResult(
                request.FromEraIndex,
                toEra,
                levy,
                fundGained,
                newPool,
                continent,
                snapshot,
                mapping,
                schedule,
                meta);
        }
        catch
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                undo[i]();
            }

            throw;
        }
    }

    // ------------------------------------------------------------------ steps

    private static void Precheck(EraTransitionRequest request, int toEra)
    {
        if (request.Transition.RequiresFinalLevel)
        {
            if (!request.Transition.FinalLevels.TryGetValue(request.FromEraIndex, out string? finalLevel))
            {
                throw new EraTransitionRejectedException(
                    $"era_transition content declares no final level for era {request.FromEraIndex}");
            }

            if (!request.Tech.CapturedLevelIds.Contains(finalLevel))
            {
                throw new EraTransitionRejectedException(
                    $"终局关 {finalLevel} 未占领 (captured: {string.Join(",", request.Tech.CapturedLevelIds)})");
            }
        }

        if (request.Transition.RequiresWonder)
        {
            foreach (WonderSpec wonder in request.Wonders.Where(w => w.EraIndex == request.FromEraIndex))
            {
                if (!request.Ledger.IsWonderBuilt(wonder.Id))
                {
                    throw new EraTransitionRejectedException($"时代奇观未建: {wonder.Id} ({wonder.NameZh}) — 附录 F.3 清单=跃迁硬条件");
                }
            }
        }

        if (request.Tech.EraIndex != request.FromEraIndex)
        {
            throw new EraTransitionRejectedException(
                $"科技时代根={request.Tech.EraIndex} 与跳代 {request.FromEraIndex}→{toEra} 不一致");
        }
    }

    private static List<EraLevyLine> ComputeLevy(EraTransitionRequest request)
    {
        var levy = new List<EraLevyLine>();
        foreach (ResourceKey resource in request.OldPool.Resources)
        {
            double oldAmount = request.OldPool.AmountOf(resource);
            long carried = (long)Math.Floor(oldAmount * request.Transition.CarriedShare);
            bool useful = request.NewEraResources.Contains(resource);
            long fundFolded = useful ? 0 : carried / request.Transition.UselessFoldDivisor;
            levy.Add(new EraLevyLine(resource, oldAmount, carried, oldAmount - carried, fundFolded));
        }

        return levy;
    }

    private RelicCitySnapshot BuildSnapshot(EraTransitionRequest request)
    {
        var builtCells = new List<CellCoord>(request.CityBuildings.Count);
        var buildings = new List<RelicBuildingSnapshot>(request.CityBuildings.Count);
        foreach (RelicCityBuildingFact fact in request.CityBuildings)
        {
            builtCells.Add(fact.Cell);
            buildings.Add(new RelicBuildingSnapshot(
                fact.BuildingId,
                fact.TypeId,
                fact.Tier,
                fact.Orientation,
                fact.RelXTo(request.OldAnchor),
                fact.RelYTo(request.OldAnchor)));
        }

        IReadOnlyList<CellCoord> zone = CoreZoneGeometry.ComputeCoreZone(
            builtCells, request.OldAnchor, request.Mapping.CoreRadiusCells, request.Mapping.BuildMarginCells);
        return new RelicCitySnapshot(request.OldAnchor, zone, buildings);
    }

    private static bool OldEraWonderBuilt(EraTransitionRequest request) =>
        request.Wonders.Any(wonder =>
            wonder.EraIndex == request.FromEraIndex && request.Ledger.IsWonderBuilt(wonder.Id));

    private static CampaignMetaDto BuildCampaignMeta(
        EraTransitionRequest request,
        int toEra,
        ResourceStock newPool,
        RelicMappingResult mapping,
        byte[] snapshotPayload,
        byte[] ledgerPayload)
    {
        var pool = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ResourceKey resource in newPool.Resources)
        {
            double amount = newPool.AmountOf(resource);
            long whole = (long)Math.Floor(amount);
            if (whole != amount)
            {
                throw new InvalidOperationException(
                    $"New-era pool amount of {resource.Value} is {amount} — the levy only moves whole items (K-7 long).");
            }

            pool[resource.Value] = whole;
        }

        var registry = new List<string>(request.PreviousMeta.RelicRegistry);
        registry.AddRange(mapping.RelicRegistry);
        registry.AddRange(mapping.AbstractList);

        // 已解锁科技随代传递：previous ∪ 当前树（去重保序——树上解锁的为准）
        var unlocked = new List<string>(request.PreviousMeta.UnlockedTechs);
        unlocked.AddRange(request.Tech.UnlockedIds.Where(id => !unlocked.Contains(id)));

        string relicCitySlotId = RelicCitySlotId(request.FromEraIndex);
        var layouts = request.PreviousMeta.LevelLayoutSnapshots
            .Where(layout => layout.LevelId != LegacyLedger.LedgerSlotId && layout.LevelId != relicCitySlotId)
            .Select(layout => new LevelLayoutSnapshotDto(layout.LevelId, layout.LayoutPayload))
            .ToList();
        layouts.Add(new LevelLayoutSnapshotDto(LegacyLedger.LedgerSlotId, ledgerPayload));
        layouts.Add(new LevelLayoutSnapshotDto(relicCitySlotId, snapshotPayload));

        return new CampaignMetaDto(
            request.WorldSeed,
            toEra,
            pool,
            unlocked,
            registry,
            layouts);
    }

    /// <summary>Layout slot id of one era's relic-city snapshot payload.</summary>
    public static string RelicCitySlotId(int fromEraIndex) => $"legacy:relic-city-era{fromEraIndex}";

    private static void Probe(EraTransitionRequest request, EraTransitionStep step) =>
        request.StepProbe?.Invoke(step);
}
