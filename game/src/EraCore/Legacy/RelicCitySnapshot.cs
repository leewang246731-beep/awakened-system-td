using System.Text;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.World;

namespace EraCore.Legacy;

/// <summary>
/// One snapshotted building of the old-era relic city (T-074, appendix F.7
/// step 1: 类型/等级/位置/朝向). Position travels as coordinates RELATIVE to
/// the core anchor so the replay step can re-home the whole city onto the
/// new era's anchor. Product port of the spike③ record — the byte format
/// differs from the spike codec (new magic) but keeps the same field-wise
/// validation and <see cref="SaveCorruptException"/> corruption semantics.
/// </summary>
/// <param name="BuildingId">Stable id of the old building (EntityId value).</param>
/// <param name="TypeId">Content type id of the old building.</param>
/// <param name="Tier">Building tier (1..4).</param>
/// <param name="Orientation">Facing (quarter turns).</param>
/// <param name="RelX">Anchor-relative X in cells.</param>
/// <param name="RelY">Anchor-relative Y in cells.</param>
public sealed record RelicBuildingSnapshot(
    long BuildingId,
    string TypeId,
    int Tier,
    int Orientation,
    int RelX,
    int RelY)
{
    /// <summary>Field-wise equality (records over lists compare by reference; this does not).</summary>
    public bool ValueEquals(RelicBuildingSnapshot? other) =>
        other is not null
        && BuildingId == other.BuildingId
        && string.Equals(TypeId, other.TypeId, StringComparison.Ordinal)
        && Tier == other.Tier
        && Orientation == other.Orientation
        && RelX == other.RelX
        && RelY == other.RelY;
}

/// <summary>
/// 跃迁快照 (T-074, F.7 step 1 / 07 A5): the old-era relic city as one
/// persistable fact — core anchor, the core-zone cell set (Chebyshev radius
/// ∩ built-area + margin, computed exactly ONCE at snapshot time and
/// persisted EXPLICITLY per 07 A5: the replay side must consume this set and
/// is forbidden from recomputing it geometrically), and the buildings in
/// relative coordinates. The encoded payload travels through the K-7
/// campaign document as an <see cref="ILevelLayoutSnapshot.LayoutPayload"/>
/// (payload format owned by C-04/C-20 per the K-7 contract comment).
/// </summary>
/// <param name="Anchor">Old-era core anchor (absolute cells).</param>
/// <param name="CoreZoneCells">Explicit A5 core-zone set, row-major sorted (byte-determinism).</param>
/// <param name="Buildings">Snapshot buildings in anchor-relative coordinates.</param>
public sealed record RelicCitySnapshot(
    CellCoord Anchor,
    IReadOnlyList<CellCoord> CoreZoneCells,
    IReadOnlyList<RelicBuildingSnapshot> Buildings)
{
    /// <summary>Field-wise equality (anchor + element-wise zone/buildings).</summary>
    public bool ValueEquals(RelicCitySnapshot? other)
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
/// A5 core-zone geometry (T-074, v3.2 §7 / 07 修正表 #13): the core zone is
/// <c>{ Cheb(cell, anchor) ≤ radius } ∩ { ∃built: Cheb(cell, built) ≤ margin }</c>,
/// computed exactly once per snapshot and persisted explicitly — after load
/// the persisted set is the only authority (recomputation is forbidden).
/// Output is row-major sorted for byte determinism.
/// </summary>
public static class CoreZoneGeometry
{
    /// <summary>
    /// Computes the A5 core-zone cell set. Deterministic: same inputs ⇒ the
    /// same sorted list (SortedSet row-major walk).
    /// </summary>
    /// <exception cref="ArgumentNullException">builtCells is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">radius &lt; 0 or margin &lt; 0.</exception>
    public static IReadOnlyList<CellCoord> ComputeCoreZone(
        IReadOnlyList<CellCoord> builtCells, CellCoord anchor, int coreRadiusCells, int buildMarginCells)
    {
        ArgumentNullException.ThrowIfNull(builtCells);
        if (coreRadiusCells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coreRadiusCells));
        }

        if (buildMarginCells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buildMarginCells));
        }

        var zone = new SortedSet<CellCoord>(Comparer<CellCoord>.Create((a, b) =>
            a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)));
        foreach (CellCoord built in builtCells)
        {
            for (int dy = -buildMarginCells; dy <= buildMarginCells; dy++)
            {
                for (int dx = -buildMarginCells; dx <= buildMarginCells; dx++)
                {
                    var cell = new CellCoord(built.X + dx, built.Y + dy);
                    if (Chebyshev(cell, anchor) <= coreRadiusCells)
                    {
                        zone.Add(cell);
                    }
                }
            }
        }

        return zone.ToList();
    }

    /// <summary>Chebyshev distance in cells (A5 判定距).</summary>
    public static int Chebyshev(CellCoord a, CellCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>
/// Binary codec of <see cref="RelicCitySnapshot"/> payloads (T-074): the
/// C-20-owned K-7 layout-payload format. Little-endian fields with a magic +
/// version header; structurally invalid snapshots throw
/// <see cref="ArgumentException"/> at encode time, damaged payloads throw
/// <see cref="SaveCorruptException"/> (the K-7 corruption semantics). Same
/// payload ⇒ same bytes (determinism anchor of the transition tests).
/// </summary>
public static class RelicCityCodec
{
    private static readonly byte[] Magic = { (byte)'E', (byte)'R', (byte)'L', (byte)'C' };

    private const byte FormatVersion = 1;

    /// <summary>Relative-coordinate bound (mirrors <see cref="GridWorld.MaxSide"/>).</summary>
    public const int MaxRelativeCoord = GridWorld.MaxSide;

    private const int MaxTypeIdBytes = 63;

    /// <summary>Encodes a structurally valid snapshot.</summary>
    /// <exception cref="ArgumentNullException">snapshot is null.</exception>
    /// <exception cref="ArgumentException">A field violates the format bounds.</exception>
    public static byte[] Encode(RelicCitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCells(snapshot.CoreZoneCells, "CoreZoneCells");
        var seen = new HashSet<long>();
        foreach (RelicBuildingSnapshot building in snapshot.Buildings)
        {
            if (building.Tier is < 1 or > 4)
            {
                throw new ArgumentException($"Building {building.BuildingId} tier {building.Tier} outside 1..4.");
            }

            if (building.Orientation is < 0 || building.Orientation > 359)
            {
                throw new ArgumentException($"Building {building.BuildingId} facing {building.Orientation} outside 0..359.");
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
        foreach (RelicBuildingSnapshot building in snapshot.Buildings)
        {
            writer.Write(building.BuildingId);
            writer.Write((byte)building.Tier);
            writer.Write((short)building.Orientation);
            writer.Write((short)building.RelX);
            writer.Write((short)building.RelY);
            byte[] typeId = Encoding.UTF8.GetBytes(building.TypeId);
            writer.Write((byte)typeId.Length);
            writer.Write(typeId);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Decodes a payload back into the snapshot (strict: trailing bytes are corruption).</summary>
    /// <exception cref="ArgumentNullException">payload is null.</exception>
    /// <exception cref="SaveCorruptException">The payload is damaged.</exception>
    public static RelicCitySnapshot Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            byte version = ReadHeader(reader);
            var anchor = new CellCoord(reader.ReadInt32(), reader.ReadInt32());
            if (anchor.X is < 0 || anchor.X > MaxRelativeCoord || anchor.Y is < 0 || anchor.Y > MaxRelativeCoord)
            {
                throw new SaveCorruptException($"Anchor ({anchor.X},{anchor.Y}) outside [0,{MaxRelativeCoord}].", version);
            }

            var zone = ReadCells(reader, version);
            int buildingCount = ReadCount(reader, version, perItemBytes: 6);
            var buildings = new List<RelicBuildingSnapshot>(buildingCount);
            var seenIds = new HashSet<long>();
            for (int i = 0; i < buildingCount; i++)
            {
                long id = reader.ReadInt64();
                byte tier = reader.ReadByte();
                int facing = reader.ReadInt16();
                int relX = reader.ReadInt16();
                int relY = reader.ReadInt16();
                int typeIdLength = reader.ReadByte();
                if (typeIdLength == 0)
                {
                    throw new SaveCorruptException($"Building {id} type id is empty.", version);
                }

                byte[] typeIdBytes = reader.ReadBytes(typeIdLength);
                if (typeIdBytes.Length != typeIdLength)
                {
                    throw new SaveCorruptException($"Building {id} type id truncated.", version);
                }

                if (tier is < 1 or > 4)
                {
                    throw new SaveCorruptException($"Building {id} has tier {tier} outside 1..4.", version);
                }

                if (facing is < 0 || facing > 359)
                {
                    throw new SaveCorruptException($"Building {id} has facing {facing} outside 0..359.", version);
                }

                if (Math.Abs(relX) > MaxRelativeCoord || Math.Abs(relY) > MaxRelativeCoord)
                {
                    throw new SaveCorruptException($"Building {id} relative coordinate ({relX},{relY}) exceeds ±{MaxRelativeCoord}.", version);
                }

                if (!seenIds.Add(id))
                {
                    throw new SaveCorruptException($"Building id {id} appears twice in the snapshot.", version);
                }

                buildings.Add(new RelicBuildingSnapshot(
                    id, Encoding.UTF8.GetString(typeIdBytes), tier, facing, relX, relY));
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new SaveCorruptException("Trailing bytes after the last building.", version);
            }

            return new RelicCitySnapshot(anchor, zone, buildings);
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
            throw new SaveCorruptException("Bad magic: not an EraCore relic-city snapshot.", storedVersion: 0);
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
            if (x is < 0 || x > MaxRelativeCoord || y is < 0 || y > MaxRelativeCoord)
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

    private static void ValidateCells(IReadOnlyList<CellCoord> cells, string name)
    {
        ArgumentNullException.ThrowIfNull(cells, name);
        foreach (CellCoord cell in cells)
        {
            if (cell.X is < 0 || cell.X > MaxRelativeCoord || cell.Y is < 0 || cell.Y > MaxRelativeCoord)
            {
                throw new ArgumentException($"Zone cell ({cell.X},{cell.Y}) outside [0,{MaxRelativeCoord}].", name);
            }
        }
    }
}
