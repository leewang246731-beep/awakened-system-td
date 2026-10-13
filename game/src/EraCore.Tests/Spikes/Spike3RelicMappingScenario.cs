using System.Text;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Tests.World;
using EraCore.World;

namespace EraCore.Tests.Spikes;

/// <summary>
/// One snapshotted building of the old-era relic city (T-058, appendix F.7
/// step 1: 类型/等级/位置/朝向). Position travels as coordinates RELATIVE to
/// the core anchor so the replay step can re-home the whole city onto the
/// new era's anchor. Value type with field-wise equality via
/// <see cref="ValueEquals"/> (records over lists compare by reference).
/// </summary>
internal sealed record Spike3BuildingSnapshot(
    long BuildingId,
    string TypeId,
    int Tier,
    int Orientation,
    int RelX,
    int RelY)
{
    public bool ValueEquals(Spike3BuildingSnapshot? other) =>
        other is not null
        && BuildingId == other.BuildingId
        && string.Equals(TypeId, other.TypeId, StringComparison.Ordinal)
        && Tier == other.Tier
        && Orientation == other.Orientation
        && RelX == other.RelX
        && RelY == other.RelY;
}

/// <summary>
/// 跃迁快照 (T-058, F.7 step 1 / A5): the old-era relic city as one
/// persistable fact — core anchor, the core-zone cell set (Chebyshev radius
/// 10 ∩ built-area +2 margin, computed exactly ONCE at snapshot time and
/// persisted EXPLICITLY per 07 A5: the replay side must consume this set and
/// is forbidden from recomputing it geometrically), and the buildings in
/// relative coordinates. This is the spike-local content of an
/// <see cref="ILevelLayoutSnapshot.LayoutPayload"/>; production compression
/// and encoding stay owned by C-04/C-20.
/// </summary>
internal sealed record Spike3RelicCitySnapshot(
    CellCoord Anchor,
    IReadOnlyList<CellCoord> CoreZoneCells,
    IReadOnlyList<Spike3BuildingSnapshot> Buildings)
{
    /// <summary>Field-wise equality (anchor + element-wise zone/buildings).</summary>
    public bool ValueEquals(Spike3RelicCitySnapshot? other)
    {
        if (other is null
            || Anchor.X != other.Anchor.X
            || Anchor.Y != other.Anchor.Y
            || CoreZoneCells.Count != other.CoreZoneCells.Count
            || Buildings.Count != other.Buildings.Count)
        {
            return false;
        }

        for (int i = 0; i < CoreZoneCells.Count; i++)
        {
            if (CoreZoneCells[i].X != other.CoreZoneCells[i].X
                || CoreZoneCells[i].Y != other.CoreZoneCells[i].Y)
            {
                return false;
            }
        }

        for (int i = 0; i < Buildings.Count; i++)
        {
            if (!Buildings[i].ValueEquals(other.Buildings[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Spike-local binary codec for <see cref="Spike3RelicCitySnapshot"/> payloads
/// (T-058): little-endian fields with a magic + version header. Encoding is a
/// spike concern — the product payload format (compression, chunking, SQLite
/// blob layout) is owned by C-04/C-20; only the "opaque byte[] travels through
/// <see cref="ILevelLayoutSnapshot"/> and parses back losslessly" contract is
/// under test here. Damaged payloads throw <see cref="SaveCorruptException"/>
/// (the K-7 corruption semantics); structurally invalid snapshots throw
/// <see cref="ArgumentException"/> at encode time.
/// </summary>
internal static class Spike3SnapshotCodec
{
    private static readonly byte[] Magic = { (byte)'S', (byte)'3', (byte)'R' };

    private const byte FormatVersion = 1;

    /// <summary>Valid building facings (v3.2 §7 orientation set, quarter turns).</summary>
    public static readonly int[] ValidOrientations = { 0, 90, 180, 270 };

    /// <summary>Relative-coordinate bound (mirrors GridWorld.MaxSide so any same-map offset fits).</summary>
    public const int MaxRelativeCoord = GridWorld.MaxSide;

    private const int MaxTypeIdBytes = 63;

    public static byte[] Encode(Spike3RelicCitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAnchor(snapshot.Anchor, "Anchor");
        ValidateCells(snapshot.CoreZoneCells, "CoreZoneCells");
        ValidateBuildings(snapshot.Buildings);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(snapshot.Anchor.X);
        writer.Write(snapshot.Anchor.Y);
        writer.Write(snapshot.CoreZoneCells.Count);
        foreach (CellCoord cell in snapshot.CoreZoneCells)
        {
            writer.Write(cell.X);
            writer.Write(cell.Y);
        }

        writer.Write(snapshot.Buildings.Count);
        foreach (Spike3BuildingSnapshot building in snapshot.Buildings)
        {
            writer.Write(building.BuildingId);
            writer.Write((byte)building.Tier);
            writer.Write((byte)Array.IndexOf(ValidOrientations, building.Orientation));
            writer.Write((short)building.RelX);
            writer.Write((short)building.RelY);
            byte[] typeId = Encoding.UTF8.GetBytes(building.TypeId);
            writer.Write((byte)typeId.Length);
            writer.Write(typeId);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static Spike3RelicCitySnapshot Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            byte version = ReadHeader(reader);
            var anchor = new CellCoord(reader.ReadInt32(), reader.ReadInt32());
            if (anchor.X < 0 || anchor.X > MaxRelativeCoord || anchor.Y < 0 || anchor.Y > MaxRelativeCoord)
            {
                throw new SaveCorruptException($"Anchor ({anchor.X},{anchor.Y}) outside [0,{MaxRelativeCoord}].", version);
            }

            var zone = ReadCells(reader, version);
            int buildingCount = ReadCount(reader, version, perItemBytes: 6);
            var buildings = new List<Spike3BuildingSnapshot>(buildingCount);
            var seenIds = new HashSet<long>();
            for (int i = 0; i < buildingCount; i++)
            {
                long id = reader.ReadInt64();
                byte tier = reader.ReadByte();
                byte orientationIndex = reader.ReadByte();
                short relX = reader.ReadInt16();
                short relY = reader.ReadInt16();
                int typeIdLength = reader.ReadByte();
                if (typeIdLength == 0)
                {
                    throw new SaveCorruptException("Building type id is empty.", version);
                }

                byte[] typeIdBytes = reader.ReadBytes(typeIdLength);
                if (typeIdBytes.Length != typeIdLength)
                {
                    throw new SaveCorruptException($"Building {id} type id truncated.", version);
                }

                string typeId = Encoding.UTF8.GetString(typeIdBytes);
                if (tier is < 1 or > 4)
                {
                    throw new SaveCorruptException($"Building {id} has tier {tier} outside 1..4.", version);
                }

                if (orientationIndex >= ValidOrientations.Length)
                {
                    throw new SaveCorruptException($"Building {id} has orientation index {orientationIndex}.", version);
                }

                if (Math.Abs(relX) > MaxRelativeCoord || Math.Abs(relY) > MaxRelativeCoord)
                {
                    throw new SaveCorruptException($"Building {id} relative coordinate ({relX},{relY}) exceeds ±{MaxRelativeCoord}.", version);
                }

                if (!seenIds.Add(id))
                {
                    throw new SaveCorruptException($"Building id {id} appears twice in the snapshot.", version);
                }

                buildings.Add(new Spike3BuildingSnapshot(
                    id, typeId, tier, ValidOrientations[orientationIndex], relX, relY));
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new SaveCorruptException("Trailing bytes after the last building.", version);
            }

            return new Spike3RelicCitySnapshot(anchor, zone, buildings);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ObjectDisposedException or DecoderFallbackException)
        {
            throw new SaveCorruptException($"Payload truncated at {payload.Length} bytes.", storedVersion: 0, ex);
        }
    }

    private static byte ReadHeader(BinaryReader reader)
    {
        byte[] magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new SaveCorruptException("Bad magic: not a spike3 relic-city snapshot.", storedVersion: 0);
        }

        byte version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new SaveCorruptException($"Format version {version} is not {FormatVersion}.", version);
        }

        return version;
    }

    private static IReadOnlyList<CellCoord> ReadCells(BinaryReader reader, byte version)
    {
        int count = ReadCount(reader, version, perItemBytes: 8);
        var cells = new List<CellCoord>(count);
        for (int i = 0; i < count; i++)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            if (x < 0 || x > MaxRelativeCoord || y < 0 || y > MaxRelativeCoord)
            {
                throw new SaveCorruptException($"Zone cell ({x},{y}) outside [0,{MaxRelativeCoord}].", version);
            }

            cells.Add(new CellCoord(x, y));
        }

        return cells;
    }

    private static int ReadCount(BinaryReader reader, byte version, int perItemBytes)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / perItemBytes)
        {
            throw new SaveCorruptException($"Count {count} exceeds the remaining payload.", version);
        }

        return count;
    }

    private static void ValidateAnchor(CellCoord anchor, string name)
    {
        if (anchor.X < 0 || anchor.X > MaxRelativeCoord || anchor.Y < 0 || anchor.Y > MaxRelativeCoord)
        {
            throw new ArgumentException($"Anchor ({anchor.X},{anchor.Y}) outside [0,{MaxRelativeCoord}].", name);
        }
    }

    private static void ValidateCells(IReadOnlyList<CellCoord> cells, string name)
    {
        ArgumentNullException.ThrowIfNull(cells, name);
        foreach (CellCoord cell in cells)
        {
            if (cell.X < 0 || cell.X > MaxRelativeCoord || cell.Y < 0 || cell.Y > MaxRelativeCoord)
            {
                throw new ArgumentException($"Zone cell ({cell.X},{cell.Y}) outside [0,{MaxRelativeCoord}].", name);
            }
        }
    }

    private static void ValidateBuildings(IReadOnlyList<Spike3BuildingSnapshot> buildings)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        var seen = new HashSet<long>();
        foreach (Spike3BuildingSnapshot building in buildings)
        {
            if (building.Tier is < 1 or > 4)
            {
                throw new ArgumentException($"Building {building.BuildingId} tier {building.Tier} outside 1..4.");
            }

            if (Array.IndexOf(ValidOrientations, building.Orientation) < 0)
            {
                throw new ArgumentException($"Building {building.BuildingId} orientation {building.Orientation} is not a quarter turn.");
            }

            if (Math.Abs(building.RelX) > MaxRelativeCoord || Math.Abs(building.RelY) > MaxRelativeCoord)
            {
                throw new ArgumentException($"Building {building.BuildingId} relative coordinate exceeds ±{MaxRelativeCoord}.");
            }

            if (string.IsNullOrEmpty(building.TypeId) || Encoding.UTF8.GetByteCount(building.TypeId) > MaxTypeIdBytes)
            {
                throw new ArgumentException($"Building {building.BuildingId} type id must be 1..{MaxTypeIdBytes} bytes.");
            }

            if (!seen.Add(building.BuildingId))
            {
                throw new ArgumentException($"Building id {building.BuildingId} appears twice.");
            }
        }
    }
}

/// <summary>
/// Terminal disposition of one snapshotted building after the F.7 replay
/// ladder (T-058). 回放 = IntactHome/IntactShifted (a standing relic on the
/// new map, in place or displaced by the 移位 rung); 降级 = RuinDowngraded
/// (废墟物品: abstract-list archaeology entry, no map presence); 封顶跳过 =
/// CapSkipped (density cap 单格 ≤1 rejected it → abstract-list entry).
/// </summary>
internal enum Spike3RelayOutcomeKind
{
    /// <summary>回放（原位）: placed exactly at anchor + relative coordinate.</summary>
    IntactHome = 0,

    /// <summary>回放（移位）: F.7 移位 rung found the nearest free ring cell.</summary>
    IntactShifted = 1,

    /// <summary>降级（废墟物品）: every candidate cell conflicts with terrain/resource/bounds.</summary>
    RuinDowngraded = 2,

    /// <summary>封顶跳过: candidate cells existed but the 单格 ≤1 density cap holds them.</summary>
    CapSkipped = 3,
}

/// <summary>One building's replay disposition (T-058).</summary>
/// <param name="Cell">New-map cell for standing relics; null for abstract-list outcomes.</param>
/// <param name="Cause">Conflict cause of the failed home cell (oob/terrain/resource/cap) for downgrades; "density-cap" for cap skips.</param>
/// <param name="ShiftDistance">Chebyshev ring the 移位 rung placed it on (0 = home).</param>
/// <param name="InCoreZone">Membership in the PERSISTED core-zone set (A5: answered only from the snapshot payload).</param>
internal sealed record Spike3RelayOutcome(
    long BuildingId,
    string TypeId,
    int Tier,
    Spike3RelayOutcomeKind Kind,
    CellCoord? Cell,
    string Cause,
    int ShiftDistance,
    bool InCoreZone);

/// <summary>
/// Result of one F.7 replay pass (T-058): every snapshotted building lands in
/// exactly one of the two ledgers — <see cref="RelicRegistry"/> (standing
/// relics) or <see cref="AbstractList"/> (降级/封顶跳过 archaeology entries)
/// — which is the 往返无丢失 definition: 回放 + 降级 + 封顶跳过 = 总数.
/// </summary>
internal sealed class Spike3RelicMappingResult
{
    public required IReadOnlyList<Spike3RelayOutcome> Outcomes { get; init; }

    /// <summary>Abstract-list entries (F.7 统一兜底: 废墟物品 + 封顶跳过, "某处曾有一座 T3 弩塔" style).</summary>
    public required IReadOnlyList<string> AbstractList { get; init; }

    /// <summary>Standing-relic registry entries (feeds ICampaignMeta.RelicRegistry).</summary>
    public required IReadOnlyList<string> RelicRegistry { get; init; }

    /// <summary>Distinct new-map cells holding exactly one relic each (单格 ≤1 invariant).</summary>
    public required IReadOnlyList<CellCoord> RelicCells { get; internal set; }

    public required int Total { get; init; }

    public int IntactHomeCount => Count(Spike3RelayOutcomeKind.IntactHome);

    public int IntactShiftedCount => Count(Spike3RelayOutcomeKind.IntactShifted);

    public int RuinCount => Count(Spike3RelayOutcomeKind.RuinDowngraded);

    public int CapSkipCount => Count(Spike3RelayOutcomeKind.CapSkipped);

    /// <summary>回放 total (原位 + 移位).</summary>
    public int IntactCount => IntactHomeCount + IntactShiftedCount;

    public double IntactRate => Total == 0 ? 1.0 : (double)IntactCount / Total;

    public int CoreZoneTotal { get; internal set; }

    public int CoreZoneIntact { get; internal set; }

    public int OutsideTotal { get; internal set; }

    public int OutsideIntact { get; internal set; }

    /// <summary>Core-zone intact rate (A5 口径: inside radius-10 ∩ built+2 only).</summary>
    public double CoreZoneIntactRate => CoreZoneTotal == 0 ? 1.0 : (double)CoreZoneIntact / CoreZoneTotal;

    /// <summary>Outside-core intact rate (comparison baseline for the A5 validation).</summary>
    public double OutsideIntactRate => OutsideTotal == 0 ? 1.0 : (double)OutsideIntact / OutsideTotal;

    /// <summary>
    /// 往返无丢失 (roundtrip no-loss): outcome count = building count, the
    /// three dispositions sum to it, ids are distinct, the two ledgers
    /// partition every building, and no cell holds two relics.
    /// </summary>
    public bool IsNoLoss =>
        Total == Outcomes.Count
        && Outcomes.Count == IntactHomeCount + IntactShiftedCount + RuinCount + CapSkipCount
        && Outcomes.Select(o => o.BuildingId).Distinct().Count() == Total
        && Outcomes.Count == AbstractList.Count + RelicRegistry.Count
        && RelicCells.Count == IntactCount;

    private int Count(Spike3RelayOutcomeKind kind)
    {
        int n = 0;
        foreach (Spike3RelayOutcome outcome in Outcomes)
        {
            if (outcome.Kind == kind)
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>
/// F.7 replay engine (T-058): 锚点回放 → 冲突降级 → 密度封顶 over a parsed
/// snapshot. The pass is VALUE-ORDERED (tier desc, then type id, then building
/// id) so the density cap keeps the most valuable relic when cells contend
/// (F.7 step 4 按价值排序保留). A cell is eligible iff it is in bounds, its
/// terrain is passable (water/rock/wall = 新地形冲突), it is not occupied by a
/// new-era resource node (占用冲突, the world occupancy layer), and no relic
/// holds it yet (单格 ≤1). The conflict ladder is spike-simplified to the F.7
/// 移位 rung (deterministic Chebyshev ring scan) with 剔除→废墟物品 as the
/// terminal form; 旋转/合并 rungs are product follow-ups and the reported
/// conflict rates are therefore a FLOOR for intact recovery. Core-zone
/// membership is answered ONLY from the persisted zone set in the payload —
/// this engine never sees the old map (07 A5).
/// </summary>
internal sealed class Spike3RelicReplayEngine
{
    /// <summary>Default 移位 rung radius (F.7 移位 rung, spike-local calibration).</summary>
    public const int DefaultShiftRadius = 2;

    public int ShiftRadius { get; }

    public Spike3RelicReplayEngine(int shiftRadius = DefaultShiftRadius)
    {
        if (shiftRadius is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(shiftRadius), "Spike3 移位半径 must be within 0..4.");
        }

        ShiftRadius = shiftRadius;
    }

    public Spike3RelicMappingResult Replay(GridWorld newWorld, CellCoord newAnchor, Spike3RelicCitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(newWorld);
        ArgumentNullException.ThrowIfNull(snapshot);

        var zone = new HashSet<CellCoord>(snapshot.CoreZoneCells);
        var relics = new HashSet<CellCoord>();
        var outcomes = new List<Spike3RelayOutcome>(snapshot.Buildings.Count);
        var abstractList = new List<string>();
        var registry = new List<string>();
        var result = new Spike3RelicMappingResult
        {
            Outcomes = outcomes,
            AbstractList = abstractList,
            RelicRegistry = registry,
            RelicCells = relics.ToList(),
            Total = snapshot.Buildings.Count,
        };

        foreach (Spike3BuildingSnapshot building in snapshot.Buildings
                     .OrderByDescending(b => b.Tier)
                     .ThenBy(b => b.TypeId, StringComparer.Ordinal)
                     .ThenBy(b => b.BuildingId))
        {
            var home = new CellCoord(newAnchor.X + building.RelX, newAnchor.Y + building.RelY);
            bool inCoreZone = zone.Contains(new CellCoord(
                snapshot.Anchor.X + building.RelX,
                snapshot.Anchor.Y + building.RelY));

            if (IsEligible(newWorld, relics, home))
            {
                Place(building, home, Spike3RelayOutcomeKind.IntactHome, "none", shiftDistance: 0, inCoreZone);
                continue;
            }

            string cause = HomeConflictCause(newWorld, relics, home);
            bool placed = false;
            bool capEligible = cause == "cap"; // home itself lost to the density cap
            for (int ring = 1; ring <= ShiftRadius && !placed; ring++)
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
                        if (IsEligible(newWorld, relics, cell))
                        {
                            Place(building, cell, Spike3RelayOutcomeKind.IntactShifted, "shift", ring, inCoreZone);
                            placed = true;
                        }
                        else if (newWorld.Contains(cell)
                            && TerrainInfo.IsPassable(newWorld.TerrainAt(cell))
                            && !newWorld.IsOccupied(cell)
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
                outcomes.Add(new Spike3RelayOutcome(
                    building.BuildingId, building.TypeId, building.Tier,
                    Spike3RelayOutcomeKind.CapSkipped, Cell: null, Cause: "density-cap",
                    ShiftDistance: 0, InCoreZone: inCoreZone));
                abstractList.Add($"abstract|id={building.BuildingId}|type={building.TypeId}|tier=T{building.Tier}|reason=density-cap");
            }
            else
            {
                // 冲突降级: 新地形/资源点/出界 → 废墟物品（可考古条目），不静默丢弃
                outcomes.Add(new Spike3RelayOutcome(
                    building.BuildingId, building.TypeId, building.Tier,
                    Spike3RelayOutcomeKind.RuinDowngraded, Cell: null, Cause: cause,
                    ShiftDistance: 0, InCoreZone: inCoreZone));
                abstractList.Add($"ruin|id={building.BuildingId}|type={building.TypeId}|tier=T{building.Tier}|cause={cause}|near=({home.X},{home.Y})");
            }

            TrackZone(result, inCoreZone, intact: false);
        }

        result.RelicCells = relics.ToList();
        return result;

        void Place(Spike3BuildingSnapshot b, CellCoord cell, Spike3RelayOutcomeKind kind, string cause, int shiftDistance, bool coreZone)
        {
            relics.Add(cell);
            outcomes.Add(new Spike3RelayOutcome(
                b.BuildingId, b.TypeId, b.Tier, kind, cell, cause, shiftDistance, coreZone));
            registry.Add($"relic|id={b.BuildingId}|type={b.TypeId}|tier=T{b.Tier}|at=({cell.X},{cell.Y})|mode={(kind == Spike3RelayOutcomeKind.IntactHome ? "home" : "shifted")}|shift={shiftDistance}");
            TrackZone(result, coreZone, intact: true);
        }

        void TrackZone(Spike3RelicMappingResult r, bool coreZone, bool intact)
        {
            if (coreZone)
            {
                r.CoreZoneTotal++;
                if (intact)
                {
                    r.CoreZoneIntact++;
                }
            }
            else
            {
                r.OutsideTotal++;
                if (intact)
                {
                    r.OutsideIntact++;
                }
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

/// <summary>
/// T-058 spike③ relic-mapping scenario (F-057/F-168): one full era-jump leg —
/// E1 continent (procedural <see cref="GridWorld"/>) with a random 60~120
/// building relic city around the core anchor → 跃迁快照 (explicit A5 core-zone
/// set + relative-coordinate buildings, encoded to an opaque
/// <see cref="ILevelLayoutSnapshot"/> payload) → E2 continent from a different
/// seed (optionally with the 地形让位 constraint: hard blockers inside the
/// core radius are converted to plain) → 锚点回放 via
/// <see cref="Spike3RelicReplayEngine"/>. The E2 map also carries 12 resource
/// nodes registered as real world occupancy (the 占用 conflict source). All
/// randomness flows through seeded <see cref="IRng"/> doubles (CS-1.4); same
/// seeds ⇒ identical snapshot bytes and identical replay outcomes.
/// </summary>
internal sealed class Spike3RelicMappingScenario
{
    /// <summary>Map side (both eras; roomy around the 33×33 layout footprint).</summary>
    public const int MapSize = 96;

    /// <summary>Core-zone Chebyshev radius (A5 裁定: 切比雪夫半径 10 格).</summary>
    public const int CoreRadius = 10;

    /// <summary>Core-zone built-area margin (A5 裁定: 已建区外扩 2 格).</summary>
    public const int BuildMargin = 2;

    /// <summary>Building scatter radius (> CoreRadius so outside-core samples exist for the A5 comparison).</summary>
    public const int LayoutRadius = 16;

    /// <summary>New-era resource nodes around the E2 anchor (占用 conflict source).</summary>
    public const int ResourceNodeCount = 12;

    /// <summary>Terrain-yield radius on E2 = core radius (推荐参数 candidate: new continent reserves a buildable core).</summary>
    public const int YieldRadius = CoreRadius;

    /// <summary>Tier→type flavor ids (F.5 箭楼/弩塔/连弩塔/火油塔 family; spike-local ids, no SP table yet).</summary>
    public static readonly string[] TierTypeIds = { "e1:watchtower", "e1:arrow-tower", "e1:xbow-bastion", "e1:fire-oil-tower" };

    public GridWorld E1World { get; }

    public GridWorld E2World { get; }

    public CellCoord E1Anchor { get; }

    public CellCoord E2Anchor { get; }

    /// <summary>City building cells on E1 (occupancy-registered; the snapshot input).</summary>
    public IReadOnlyList<CellCoord> E1BuiltCells { get; }

    /// <summary>In-memory snapshot object (encode side).</summary>
    public Spike3RelicCitySnapshot Snapshot { get; }

    /// <summary>Encoded snapshot payload (the ILevelLayoutSnapshot byte leg).</summary>
    public byte[] SnapshotPayload { get; }

    /// <summary>Cells the E2 terrain yield converted water/rock → plain (0 when yielding disabled).</summary>
    public int YieldedCellCount { get; }

    /// <summary>E2 resource-node cells (registered occupancy).</summary>
    public IReadOnlyList<CellCoord> ResourceNodes { get; }

    /// <summary>
    /// Measured blocker rate of the E2 replay footprint (Chebyshev ≤
    /// <see cref="LayoutRadius"/> of the E2 anchor, post-yield): fraction of
    /// cells that are impassable terrain or resource-occupied at replay time.
    /// </summary>
    public double E2FootprintBlockerRate { get; }

    public Spike3RelicMappingScenario(
        long e1Seed,
        long e2Seed,
        int buildingCount,
        double roughnessMultiplier = 1.0,
        bool terrainYielding = true)
    {
        if (buildingCount is < 1 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(buildingCount), "Spike3 building count must be within 1..400.");
        }

        double forestRatio = GridWorld.DefaultForestRatio * roughnessMultiplier;
        double rockRatio = GridWorld.DefaultRockRatio * roughnessMultiplier;
        double waterRatio = GridWorld.DefaultWaterRatio * roughnessMultiplier;

        E1Anchor = new CellCoord(MapSize / 2, MapSize / 2);
        E2Anchor = new CellCoord(MapSize / 2, MapSize / 2);
        E1World = new GridWorld(MapSize, MapSize, new LcgRng(e1Seed), forestRatio, rockRatio, waterRatio);

        // ---- E1 relic city: random passable cells within LayoutRadius of the anchor
        var layoutRng = new LcgRng(e1Seed + 4_001);
        var candidates = new List<CellCoord>();
        for (int dy = -LayoutRadius; dy <= LayoutRadius; dy++)
        {
            for (int dx = -LayoutRadius; dx <= LayoutRadius; dx++)
            {
                var cell = new CellCoord(E1Anchor.X + dx, E1Anchor.Y + dy);
                if (TerrainInfo.IsPassable(E1World.TerrainAt(cell)))
                {
                    candidates.Add(cell);
                }
            }
        }

        if (candidates.Count < buildingCount)
        {
            throw new InvalidOperationException(
                $"E1 seed {e1Seed} at roughness {roughnessMultiplier} offers only {candidates.Count} buildable cells for {buildingCount} buildings.");
        }

        FisherYatesShuffle(candidates, layoutRng);
        var builtCells = candidates.Take(buildingCount).ToList();
        var buildings = new List<Spike3BuildingSnapshot>(buildingCount);
        for (int i = 0; i < buildingCount; i++)
        {
            // real occupancy: the old city stands on the old map
            E1World.RegisterOccupancy(new EntityId(100_000 + i), new[] { builtCells[i] });
            int tierIndex = DrawTier(layoutRng);
            int orientation = Spike3SnapshotCodec.ValidOrientations[layoutRng.Next(0, 4)];
            buildings.Add(new Spike3BuildingSnapshot(
                BuildingId: 100_000 + i,
                TypeId: TierTypeIds[tierIndex],
                Tier: tierIndex + 1,
                Orientation: orientation,
                RelX: builtCells[i].X - E1Anchor.X,
                RelY: builtCells[i].Y - E1Anchor.Y));
        }

        E1BuiltCells = builtCells;

        // ---- A5 core zone: radius 10 ∩ built-area +2, computed ONCE, persisted explicitly
        IReadOnlyList<CellCoord> zone = ComputeCoreZone(E1BuiltCells, E1Anchor);
        Snapshot = new Spike3RelicCitySnapshot(E1Anchor, zone, buildings);
        SnapshotPayload = Spike3SnapshotCodec.Encode(Snapshot);

        // ---- E2 continent (different seed) + optional terrain yield + resource nodes
        E2World = new GridWorld(MapSize, MapSize, new LcgRng(e2Seed), forestRatio, rockRatio, waterRatio);
        YieldedCellCount = terrainYielding ? ApplyTerrainYield(E2World, E2Anchor) : 0;
        ResourceNodes = PlaceResourceNodes(E2World, E2Anchor, new LcgRng(e2Seed + 999_983));
        E2FootprintBlockerRate = MeasureBlockerRate(E2World, ResourceNodes, E2Anchor);
    }

    /// <summary>
    /// 真·两级存档链路回放: decode the payload fresh (no in-memory snapshot
    /// reuse) and replay it onto E2. The engine receives ONLY the parsed
    /// payload — geometric core-zone recomputation is structurally impossible
    /// (07 A5).
    /// </summary>
    public Spike3RelicMappingResult ReplayFromPayload(int shiftRadius = Spike3RelicReplayEngine.DefaultShiftRadius)
    {
        Spike3RelicCitySnapshot parsed = Spike3SnapshotCodec.Decode(SnapshotPayload);
        return new Spike3RelicReplayEngine(shiftRadius).Replay(E2World, E2Anchor, parsed);
    }

    /// <summary>
    /// A5 core-zone computation: { c : Cheb(c, anchor) ≤ CoreRadius } ∩
    /// { c : ∃b ∈ built, Cheb(c, b) ≤ BuildMargin }, row-major sorted for
    /// byte-determinism. Runs exactly once per scenario at snapshot time.
    /// </summary>
    public static IReadOnlyList<CellCoord> ComputeCoreZone(IReadOnlyList<CellCoord> builtCells, CellCoord anchor)
    {
        var zone = new SortedSet<CellCoord>(Comparer<CellCoord>.Create((a, b) =>
            a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)));
        foreach (CellCoord built in builtCells)
        {
            for (int dy = -BuildMargin; dy <= BuildMargin; dy++)
            {
                for (int dx = -BuildMargin; dx <= BuildMargin; dx++)
                {
                    var cell = new CellCoord(built.X + dx, built.Y + dy);
                    if (Math.Max(Math.Abs(cell.X - anchor.X), Math.Abs(cell.Y - anchor.Y)) <= CoreRadius)
                    {
                        zone.Add(cell);
                    }
                }
            }
        }

        return zone.ToList();
    }

    /// <summary>相对坐标的切比雪夫距离（核心区判定用）。</summary>
    public static int Chebyshev(CellCoord a, CellCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static int DrawTier(LcgRng rng) => rng.NextDouble() switch
    {
        < 0.40 => 0, // T1
        < 0.70 => 1, // T2
        < 0.90 => 2, // T3
        _ => 3,      // T4
    };

    private static void FisherYatesShuffle(List<CellCoord> list, LcgRng rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int ApplyTerrainYield(GridWorld world, CellCoord anchor)
    {
        int yielded = 0;
        for (int dy = -YieldRadius; dy <= YieldRadius; dy++)
        {
            for (int dx = -YieldRadius; dx <= YieldRadius; dx++)
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

        return yielded;
    }

    private static IReadOnlyList<CellCoord> PlaceResourceNodes(GridWorld world, CellCoord anchor, LcgRng rng)
    {
        var nodes = new List<CellCoord>(ResourceNodeCount);
        var taken = new HashSet<CellCoord>();
        while (nodes.Count < ResourceNodeCount)
        {
            var cell = new CellCoord(
                anchor.X + rng.Next(-LayoutRadius, LayoutRadius + 1),
                anchor.Y + rng.Next(-LayoutRadius, LayoutRadius + 1));
            if (!taken.Contains(cell) && TerrainInfo.IsPassable(world.TerrainAt(cell)))
            {
                taken.Add(cell);
                nodes.Add(cell);
                world.RegisterOccupancy(new EntityId(900_000 + nodes.Count), new[] { cell });
            }
        }

        return nodes;
    }

    private static double MeasureBlockerRate(GridWorld world, IReadOnlyList<CellCoord> nodes, CellCoord anchor)
    {
        int blocked = 0;
        int total = 0;
        var nodeSet = new HashSet<CellCoord>(nodes);
        for (int dy = -LayoutRadius; dy <= LayoutRadius; dy++)
        {
            for (int dx = -LayoutRadius; dx <= LayoutRadius; dx++)
            {
                var cell = new CellCoord(anchor.X + dx, anchor.Y + dy);
                total++;
                if (!TerrainInfo.IsPassable(world.TerrainAt(cell)) || nodeSet.Contains(cell))
                {
                    blocked++;
                }
            }
        }

        return (double)blocked / total;
    }
}
