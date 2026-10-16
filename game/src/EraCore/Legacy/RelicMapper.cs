using EraCore.Contracts.Foundation;
using EraCore.World;

namespace EraCore.Legacy;

/// <summary>
/// The generated new-continent bundle (T-074): the terrain world, its
/// anchor, the registered resource nodes, the 让位-threat nodes and the
/// reserved core-area cells. Everything is derived deterministically from
/// <c>(campaign seed, target era)</c> through the injected
/// <see cref="IRng"/> port (CS-1.4) — the factory reseeds it with
/// <see cref="NewContinentFactory.DeriveSeed"/> before drawing.
/// </summary>
/// <param name="World">Terrain+occupancy world of the new continent.</param>
/// <param name="Anchor">Core anchor (replay origin) in new-map cells.</param>
/// <param name="ResourceNodes">Resource-node cells (registered world occupancy — the replay 占用 conflict source).</param>
/// <param name="ThreatNodes">Threat-node cells — placed OUTSIDE the reserved core (让位, T-074).</param>
/// <param name="YieldedCellCount">Cells the terrain 让位 pass converted water/rock → plain.</param>
/// <param name="ReserveCells">Reserved core-area cells (Chebyshev ≤ core radius; threat 让位 zone).</param>
public sealed record NewContinent(
    GridWorld World,
    CellCoord Anchor,
    IReadOnlyList<CellCoord> ResourceNodes,
    IReadOnlyList<CellCoord> ThreatNodes,
    int YieldedCellCount,
    IReadOnlyList<CellCoord> ReserveCells)
{
    /// <summary>Terrain hash of the finished continent (determinism anchor).</summary>
    public long MapHash => World.ComputeMapHash();
}

/// <summary>
/// New-continent factory (T-074, F-056): procedural terrain from the
/// content ratios, the A5 terrain 让位 pass (water/rock inside the yield
/// radius become plain — spike3 §6 推荐参数 1), resource nodes that avoid
/// the yield zone (推荐参数 2), threat nodes that 让位 outside the reserved
/// core area, and the reserve-fraction cap check
/// (<c>reserve_area_cap_fraction</c>). All draws flow through the reseeded
/// <see cref="IRng"/>; same seed ⇒ byte-identical continent.
/// </summary>
public static class NewContinentFactory
{
    /// <summary>Entity-id base of resource nodes (occupancy registrations).</summary>
    public const long ResourceNodeEntityBase = 900_000;

    /// <summary>FNV-1a offset basis (same family as GridWorld/ResourceStock hashes; whitelisted domain constant).</summary>
    public const ulong FnvOffset = 14695981039346656037UL;

    /// <summary>FNV-1a prime (whitelisted domain constant).</summary>
    public const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Derives the new-continent seed: FNV-1a over
    /// <c>(campaign world seed, target era index)</c> — pure and
    /// deterministic; the E2 continent of one campaign is reproducible
    /// from the K-7 WorldSeed alone.
    /// </summary>
    public static long DeriveSeed(long worldSeed, int targetEraIndex)
    {
        unchecked
        {
            ulong hash = FnvOffset;
            foreach (byte b in BitConverter.GetBytes(worldSeed))
            {
                hash ^= b;
                hash *= FnvPrime;
            }

            foreach (byte b in BitConverter.GetBytes(targetEraIndex))
            {
                hash ^= b;
                hash *= FnvPrime;
            }

            return unchecked((long)hash);
        }
    }

    /// <summary>
    /// Generates one new continent. The <paramref name="rng"/> port is
    /// RESEEDED with <see cref="DeriveSeed"/> — callers may share one port
    /// across the campaign (CS-1.4: no hidden Random).
    /// </summary>
    /// <exception cref="ArgumentNullException">rng or parameters is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The reserve-fraction cap is exceeded, or a node budget cannot be
    /// placed on the drawn terrain.
    /// </exception>
    public static NewContinent Create(
        RelicMappingParams parameters, long worldSeed, int targetEraIndex, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(rng);
        int side = parameters.MapSizeCells;
        var anchor = new CellCoord(side / 2, side / 2);
        rng.Reseed(DeriveSeed(worldSeed, targetEraIndex));
        var world = new GridWorld(
            side, side, rng, parameters.ForestRatio, parameters.RockRatio, parameters.WaterRatio);

        int yielded = 0;
        for (int dy = -parameters.TerrainYieldRadiusCells; dy <= parameters.TerrainYieldRadiusCells; dy++)
        {
            for (int dx = -parameters.TerrainYieldRadiusCells; dx <= parameters.TerrainYieldRadiusCells; dx++)
            {
                var cell = new CellCoord(anchor.X + dx, anchor.Y + dy);
                TerrainKind kind = world.TerrainAt(cell);
                if (kind is TerrainKind.Water or TerrainKind.Rock)
                {
                    world.SetTerrain(cell, TerrainKind.Plain);
                    yielded++;
                }
            }
        }

        var reserve = new List<CellCoord>();
        for (int dy = -parameters.CoreRadiusCells; dy <= parameters.CoreRadiusCells; dy++)
        {
            for (int dx = -parameters.CoreRadiusCells; dx <= parameters.CoreRadiusCells; dx++)
            {
                reserve.Add(new CellCoord(anchor.X + dx, anchor.Y + dy));
            }
        }

        double reserveFraction = (double)reserve.Count / (side * (double)side);
        if (reserveFraction > parameters.ReserveAreaCapFraction + 1e-9)
        {
            throw new InvalidOperationException(
                $"Reserved core area fraction {reserveFraction:F4} exceeds the content cap {parameters.ReserveAreaCapFraction}.");
        }

        var resourceNodes = new List<CellCoord>(parameters.ResourceNodeCount);
        var taken = new HashSet<CellCoord>();
        while (resourceNodes.Count < parameters.ResourceNodeCount)
        {
            var cell = new CellCoord(rng.Next(0, side), rng.Next(0, side));
            if (taken.Contains(cell) || !TerrainInfo.IsPassable(world.TerrainAt(cell)))
            {
                continue;
            }

            if (parameters.ResourceAvoidYieldZone
                && CoreZoneGeometry.Chebyshev(cell, anchor) <= parameters.TerrainYieldRadiusCells)
            {
                continue;
            }

            taken.Add(cell);
            resourceNodes.Add(cell);
            world.RegisterOccupancy(new EntityId(ResourceNodeEntityBase + resourceNodes.Count), new[] { cell });
        }

        var threatNodes = new List<CellCoord>(parameters.ThreatNodeCount);
        var threatTaken = new HashSet<CellCoord>(taken);
        var reserveSet = new HashSet<CellCoord>(reserve);
        while (threatNodes.Count < parameters.ThreatNodeCount)
        {
            var cell = new CellCoord(rng.Next(0, side), rng.Next(0, side));
            if (reserveSet.Contains(cell) || threatTaken.Contains(cell))
            {
                continue;
            }

            threatTaken.Add(cell);
            threatNodes.Add(cell);
        }

        return new NewContinent(world, anchor, resourceNodes, threatNodes, yielded, reserve);
    }
}

/// <summary>
/// Terminal disposition of one snapshotted building after the F.7 replay
/// ladder (T-074). 回放 = <see cref="IntactHome"/>/<see cref="IntactShifted"/>
/// (a standing relic on the new map, in place or displaced by the 移位
/// rung); 降级 = <see cref="RuinDowngraded"/> (废墟: abstract-list
/// archaeology entry, no map presence); 封顶跳过 =
/// <see cref="CapSkipped"/> (density cap 单格 ≤1 rejected it → abstract
/// list). Product port of the spike③ outcome enum.
/// </summary>
public enum RelayOutcomeKind
{
    /// <summary>回放（原位）: placed exactly at anchor + relative coordinate.</summary>
    IntactHome = 0,

    /// <summary>回放（移位）: F.7 移位 rung found the nearest free ring cell.</summary>
    IntactShifted = 1,

    /// <summary>降级（废墟）: every candidate cell conflicts with terrain/resource/bounds.</summary>
    RuinDowngraded = 2,

    /// <summary>封顶跳过: candidate cells existed but the 单格 ≤1 density cap holds them.</summary>
    CapSkipped = 3,
}

/// <summary>One building's replay disposition (T-074).</summary>
/// <param name="BuildingId">Old building id.</param>
/// <param name="TypeId">Old building type id.</param>
/// <param name="Tier">Old building tier.</param>
/// <param name="Kind">Terminal disposition.</param>
/// <param name="Cell">New-map cell for standing relics; null for abstract-list outcomes.</param>
/// <param name="Cause">Conflict cause of the failed home cell (oob/terrain/resource/cap) or "density-cap".</param>
/// <param name="ShiftDistance">Chebyshev ring the 移位 rung placed it on (0 = home).</param>
/// <param name="InCoreZone">Membership in the PERSISTED core-zone set (A5: answered only from the snapshot payload).</param>
/// <param name="HomeCell">Absolute home cell on the NEW map (T-125): anchor + relative coordinate, where the building WOULD stand — the dig-site anchor of a degraded ruin. Always computed, so it may lie outside the map for oob rows.</param>
public sealed record RelicRelayOutcome(
    long BuildingId,
    string TypeId,
    int Tier,
    RelayOutcomeKind Kind,
    CellCoord? Cell,
    string Cause,
    int ShiftDistance,
    bool InCoreZone,
    CellCoord? HomeCell = null);

/// <summary>
/// Result of one F.7 replay pass (T-074): every snapshotted building lands
/// in exactly one of the two ledgers — <see cref="RelicRegistry"/> (standing
/// relics) or <see cref="AbstractList"/> (降级/封顶跳过 archaeology entries)
/// — which is the 往返无丢失 definition: 回放 + 降级 + 封顶跳过 = 总数.
/// </summary>
public sealed class RelicMappingResult
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Per-building dispositions (value-ordered pass output, deterministic).</summary>
    public required IReadOnlyList<RelicRelayOutcome> Outcomes { get; init; }

    /// <summary>Abstract-list entries (F.7 统一兜底: 废墟 + 封顶跳过, diggable archaeology sites).</summary>
    public required IReadOnlyList<string> AbstractList { get; init; }

    /// <summary>Standing-relic registry entries (feeds ICampaignMeta.RelicRegistry).</summary>
    public required IReadOnlyList<string> RelicRegistry { get; init; }

    /// <summary>Distinct new-map cells holding exactly one relic each (单格 ≤1 invariant).</summary>
    public required IReadOnlyList<CellCoord> RelicCells { get; init; }

    /// <summary>Snapshot building count (the no-loss denominator).</summary>
    public required int Total { get; init; }

    /// <summary>回放（原位）count.</summary>
    public int IntactHomeCount => Count(RelayOutcomeKind.IntactHome);

    /// <summary>回放（移位）count.</summary>
    public int IntactShiftedCount => Count(RelayOutcomeKind.IntactShifted);

    /// <summary>降级（废墟）count.</summary>
    public int RuinCount => Count(RelayOutcomeKind.RuinDowngraded);

    /// <summary>封顶跳过 count.</summary>
    public int CapSkipCount => Count(RelayOutcomeKind.CapSkipped);

    /// <summary>回放 total (原位 + 移位).</summary>
    public int IntactCount => IntactHomeCount + IntactShiftedCount;

    /// <summary>Intact fraction of all buildings.</summary>
    public double IntactRate => Total == 0 ? 1.0 : IntactCount / (double)Total;

    /// <summary>Core-zone building count (A5 口径).</summary>
    public int CoreZoneTotal { get; internal set; }

    /// <summary>Core-zone buildings that survived as standing relics.</summary>
    public int CoreZoneIntact { get; internal set; }

    /// <summary>Outside-core building count (comparison baseline).</summary>
    public int OutsideTotal { get; internal set; }

    /// <summary>Outside-core buildings that survived.</summary>
    public int OutsideIntact { get; internal set; }

    /// <summary>Core-zone intact rate (A5 口径: inside the persisted zone set only).</summary>
    public double CoreZoneIntactRate => CoreZoneTotal == 0 ? 1.0 : CoreZoneIntact / (double)CoreZoneTotal;

    /// <summary>Outside-core intact rate (comparison baseline).</summary>
    public double OutsideIntactRate => OutsideTotal == 0 ? 1.0 : OutsideIntact / (double)OutsideTotal;

    /// <summary>
    /// 往返无丢失 (roundtrip no-loss): outcome count = building count, the
    /// four dispositions sum to it, ids are distinct, the two ledgers
    /// partition every building, and no cell holds two relics.
    /// </summary>
    public bool IsNoLoss =>
        Total == Outcomes.Count
        && Outcomes.Count == IntactHomeCount + IntactShiftedCount + RuinCount + CapSkipCount
        && Outcomes.Select(o => o.BuildingId).Distinct().Count() == Total
        && Outcomes.Count == AbstractList.Count + RelicRegistry.Count
        && RelicCells.Count == IntactCount;

    /// <summary>
    /// FNV-1a hash over every outcome field (determinism anchor: same
    /// snapshot + same continent ⇒ equal hashes).
    /// </summary>
    public long ComputeOutcomeHash()
    {
        unchecked
        {
            ulong hash = FnvOffset;
            foreach (RelicRelayOutcome outcome in Outcomes)
            {
                hash = Mix(hash, (ulong)outcome.BuildingId);
                hash = Mix(hash, (ulong)outcome.Tier);
                hash = Mix(hash, (ulong)outcome.Kind);
                hash = Mix(hash, (ulong)(outcome.Cell?.X ?? -1));
                hash = Mix(hash, (ulong)(outcome.Cell?.Y ?? -1));
                hash = Mix(hash, (ulong)outcome.ShiftDistance);
                hash = Mix(hash, outcome.InCoreZone ? 1UL : 0UL);
                hash = Mix(hash, (ulong)(outcome.HomeCell?.X ?? -1));
                hash = Mix(hash, (ulong)(outcome.HomeCell?.Y ?? -1));
                foreach (char c in outcome.TypeId)
                {
                    hash = Mix(hash, c);
                }
            }

            return unchecked((long)hash);
        }
    }

    private int Count(RelayOutcomeKind kind)
    {
        int n = 0;
        foreach (RelicRelayOutcome outcome in Outcomes)
        {
            if (outcome.Kind == kind)
            {
                n++;
            }
        }

        return n;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }
}

/// <summary>
/// F.7 replay engine, product port (T-074, F-056/F-057): 锚点回放 → 冲突降级
/// → 密度封顶 over a parsed snapshot, parameterized by the content table
/// (spike③ recommended set: 移位半径 2 / 让位半径 10 / 资源避开 / A5 核心
/// 区). The pass is VALUE-ORDERED (tier desc, then type id, then building
/// id) so the density cap keeps the most valuable relic when cells contend
/// (F.7 step 4 按价值排序保留). A cell is eligible iff in bounds, terrain
/// passable, free of new-era resource occupancy, and held by no relic yet
/// (单格 ≤1). Standing relics register world occupancy so later gameplay
/// cannot build over them. Core-zone membership is answered ONLY from the
/// persisted zone set — this engine never sees the old map (07 A5).
/// </summary>
public sealed class RelicMapper
{
    /// <summary>Entity-id base of standing relics (occupancy registrations on the new world).</summary>
    public const long RelicEntityBase = 800_000;

    /// <param name="parameters">Content parameter table (shift radius etc.).</param>
    /// <exception cref="ArgumentNullException">parameters is null.</exception>
    public RelicMapper(RelicMappingParams parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <summary>Content parameter table of this mapper.</summary>
    public RelicMappingParams Parameters { get; }

    /// <summary>
    /// Replays one decoded snapshot onto a generated continent. Standing
    /// relics are registered as world occupancy; ruins/cap-skips land in
    /// the abstract list (the diggable archaeology sites).
    /// </summary>
    /// <exception cref="ArgumentNullException">continent or snapshot is null.</exception>
    /// <exception cref="InvalidOperationException">The pass violated the no-loss invariant (never expected; guards the ladder).</exception>
    public RelicMappingResult Map(NewContinent continent, RelicCitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(continent);
        ArgumentNullException.ThrowIfNull(snapshot);
        GridWorld world = continent.World;
        var zone = new HashSet<CellCoord>(snapshot.CoreZoneCells);
        var relics = new HashSet<CellCoord>();
        var outcomes = new List<RelicRelayOutcome>(snapshot.Buildings.Count);
        var abstractList = new List<string>();
        var registry = new List<string>();
        int coreZoneTotal = 0, coreZoneIntact = 0, outsideTotal = 0, outsideIntact = 0;

        foreach (RelicBuildingSnapshot building in snapshot.Buildings
                     .OrderByDescending(b => b.Tier)
                     .ThenBy(b => b.TypeId, StringComparer.Ordinal)
                     .ThenBy(b => b.BuildingId))
        {
            var home = new CellCoord(continent.Anchor.X + building.RelX, continent.Anchor.Y + building.RelY);
            bool inCoreZone = zone.Contains(new CellCoord(
                snapshot.Anchor.X + building.RelX,
                snapshot.Anchor.Y + building.RelY));

            if (IsEligible(world, relics, home))
            {
                Place(building, home, home, RelayOutcomeKind.IntactHome, "none", shiftDistance: 0, inCoreZone);
                continue;
            }

            string cause = HomeConflictCause(world, relics, home);
            bool placed = false;
            bool capEligible = cause == "cap"; // home itself lost to the density cap
            for (int ring = 1; ring <= Parameters.ShiftRadiusCells && !placed; ring++)
            {
                for (int dy = -ring; dy <= ring && !placed; dy++)
                {
                    for (int dx = -ring; dx <= ring && !placed; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }

                        var cell = new CellCoord(home.X + dx, home.Y + dy);
                        if (IsEligible(world, relics, cell))
                        {
                            Place(building, home, cell, RelayOutcomeKind.IntactShifted, "shift", ring, inCoreZone);
                            placed = true;
                        }
                        else if (world.Contains(cell)
                            && TerrainInfo.IsPassable(world.TerrainAt(cell))
                            && !world.IsOccupied(cell)
                            && relics.Contains(cell))
                        {
                            capEligible = true; // cell fine except the 单格 ≤1 cap
                        }
                    }
                }
            }

            if (placed)
            {
                continue;
            }

            if (capEligible)
            {
                // 密度封顶: 候选格被更早（更高价值）遗迹占满 → 抽象清单，不挤占
                outcomes.Add(new RelicRelayOutcome(
                    building.BuildingId, building.TypeId, building.Tier,
                    RelayOutcomeKind.CapSkipped, Cell: null, Cause: "density-cap",
                    ShiftDistance: 0, InCoreZone: inCoreZone, HomeCell: home));
                abstractList.Add($"abstract|id={building.BuildingId}|type={building.TypeId}|tier=T{building.Tier}|reason=density-cap");
            }
            else
            {
                // 冲突降级: 新地形/资源点/出界 → 废墟（可考古条目），不静默丢弃
                outcomes.Add(new RelicRelayOutcome(
                    building.BuildingId, building.TypeId, building.Tier,
                    RelayOutcomeKind.RuinDowngraded, Cell: null, Cause: cause,
                    ShiftDistance: 0, InCoreZone: inCoreZone, HomeCell: home));
                abstractList.Add($"ruin|id={building.BuildingId}|type={building.TypeId}|tier=T{building.Tier}|cause={cause}|near=({home.X},{home.Y})");
            }

            if (inCoreZone)
            {
                coreZoneTotal++;
            }
            else
            {
                outsideTotal++;
            }
        }

        var result = new RelicMappingResult
        {
            Outcomes = outcomes,
            AbstractList = abstractList,
            RelicRegistry = registry,
            RelicCells = relics.ToList(),
            Total = snapshot.Buildings.Count,
            CoreZoneTotal = coreZoneTotal,
            CoreZoneIntact = coreZoneIntact,
            OutsideTotal = outsideTotal,
            OutsideIntact = outsideIntact,
        };
        if (!result.IsNoLoss)
        {
            throw new InvalidOperationException(
                $"Relic replay violated the no-loss invariant (outcomes {outcomes.Count}, ledgers {abstractList.Count}+{registry.Count}, total {snapshot.Buildings.Count}).");
        }

        return result;

        void Place(RelicBuildingSnapshot b, CellCoord home, CellCoord cell, RelayOutcomeKind kind, string cause, int shiftDistance, bool coreZone)
        {
            relics.Add(cell);
            // 顺序序号（非 buildingId）：与资源节点 id 段（900_000+）保持不相交
            world.RegisterOccupancy(new EntityId(RelicEntityBase + relics.Count), new[] { cell });
            outcomes.Add(new RelicRelayOutcome(
                b.BuildingId, b.TypeId, b.Tier, kind, cell, cause, shiftDistance, coreZone, home));
            registry.Add($"relic|id={b.BuildingId}|type={b.TypeId}|tier=T{b.Tier}|at=({cell.X},{cell.Y})|mode={(kind == RelayOutcomeKind.IntactHome ? "home" : "shifted")}|shift={shiftDistance}");
            if (coreZone)
            {
                coreZoneTotal++;
                coreZoneIntact++;
            }
            else
            {
                outsideTotal++;
                outsideIntact++;
            }
        }
    }

    private static bool IsEligible(GridWorld world, HashSet<CellCoord> relics, CellCoord cell) =>
        world.Contains(cell)
        && TerrainInfo.IsPassable(world.TerrainAt(cell))
        && !world.IsOccupied(cell)
        && !relics.Contains(cell);

    private static string HomeConflictCause(GridWorld world, HashSet<CellCoord> relics, CellCoord home)
    {
        if (!world.Contains(home))
        {
            return "oob";
        }

        if (!TerrainInfo.IsPassable(world.TerrainAt(home)))
        {
            return "terrain";
        }

        if (world.IsOccupied(home))
        {
            return "resource";
        }

        return relics.Contains(home) ? "cap" : "unknown";
    }
}
