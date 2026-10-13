using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.World;

/// <summary>
/// C-05 grid world (T-032, F-117): a width×height grid of
/// <see cref="CellCoord"/> cells carrying terrain, occupancy registrations
/// (EntityId → cells) and named zones (language-neutral zone ids, same id
/// family as the action Schema <c>lane</c> parameter — G.2 SetRally/SpawnScout).
/// All randomness enters through the injected <see cref="IRng"/> (CS-1.4):
/// same seed + same ratios + same dimensions ⇒ byte-identical terrain, which
/// <see cref="ComputeMapHash"/> makes observable for determinism tests
/// (03-decomposition §1.2 replay requirement).
/// </summary>
public sealed class GridWorld : IFlowGrid
{
    /// <summary>
    /// Upper bound for either grid side: dimensions of 512 or more are
    /// rejected (<c>超大 512+ 抛 ArgumentException</c>, T-032 task card).
    /// [NEW-任务卡口径] (limit 511 ⇒ worst case 511² ≈ 261k cells).
    /// </summary>
    public const int MaxSide = 511;

    /// <summary>E1 default forest ratio. [NEW-原型校准] (no SP table row yet).</summary>
    public const double DefaultForestRatio = 0.15;

    /// <summary>E1 default rock ratio. [NEW-原型校准] (no SP table row yet).</summary>
    public const double DefaultRockRatio = 0.10;

    /// <summary>E1 default water ratio. [NEW-原型校准] (no SP table row yet).</summary>
    public const double DefaultWaterRatio = 0.08;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly TerrainKind[] _terrain;
    private readonly Dictionary<CellCoord, EntityId> _occupants = new();
    private readonly Dictionary<EntityId, List<CellCoord>> _occupancy = new();
    private readonly Dictionary<string, List<CellCoord>> _zones = new(StringComparer.Ordinal);

    /// <summary>
    /// Generates a procedural map: one <see cref="IRng.NextDouble"/> draw per
    /// cell in row-major order, compared against the cumulative thresholds
    /// water → rock → forest (remainder plain). Ratios are validated to be
    /// non-negative with sum ≤ 1 (+1e-9); dimensions via
    /// <see cref="Guard.EnsureInRange"/> against [1, <see cref="MaxSide"/>].
    /// </summary>
    public GridWorld(
        int width,
        int height,
        IRng rng,
        double forestRatio = DefaultForestRatio,
        double rockRatio = DefaultRockRatio,
        double waterRatio = DefaultWaterRatio)
    {
        Guard.EnsureInRange(width, 1, MaxSide, nameof(width));
        Guard.EnsureInRange(height, 1, MaxSide, nameof(height));
        ArgumentNullException.ThrowIfNull(rng);
        ValidateRatios(forestRatio, rockRatio, waterRatio);

        Width = width;
        Height = height;
        _terrain = new TerrainKind[width * height];

        double rockThreshold = waterRatio + rockRatio;
        double forestThreshold = rockThreshold + forestRatio;
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double draw = rng.NextDouble();
                _terrain[rowBase + x] = draw < waterRatio
                    ? TerrainKind.Water
                    : draw < rockThreshold
                        ? TerrainKind.Rock
                        : draw < forestThreshold
                            ? TerrainKind.Forest
                            : TerrainKind.Plain;
            }
        }
    }

    /// <summary>Grid width in cells (X axis, eastward).</summary>
    public int Width { get; }

    /// <summary>Grid height in cells (Y axis, southward).</summary>
    public int Height { get; }

    /// <summary>
    /// Whether <paramref name="cell"/> lies inside the grid. Bounds query —
    /// never throws; use <see cref="TerrainAt"/> for the throwing variant.
    /// </summary>
    public bool Contains(CellCoord cell) =>
        cell.X >= 0 && cell.X < Width && cell.Y >= 0 && cell.Y < Height;

    /// <summary>
    /// Reads the terrain of one cell. Out-of-bounds coordinates (negative or
    /// past the far edge) are rejected with
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public TerrainKind TerrainAt(CellCoord cell)
    {
        EnsureInBounds(cell);
        return _terrain[cell.Y * Width + cell.X];
    }

    /// <summary>
    /// Overwrites the terrain of one cell (player-built walls/roads, terraforming).
    /// Same bounds contract as <see cref="TerrainAt"/>; the kind must be a
    /// defined <see cref="TerrainKind"/> value.
    /// </summary>
    public void SetTerrain(CellCoord cell, TerrainKind kind)
    {
        EnsureInBounds(cell);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Undefined terrain value {(int)kind}.", nameof(kind));
        }

        _terrain[cell.Y * Width + cell.X] = kind;
    }

    /// <summary>
    /// Terrain-layer passability (<see cref="TerrainInfo.IsPassable"/>);
    /// occupancy is a separate layer — see <see cref="IsOccupied"/>.
    /// </summary>
    public bool IsPassable(CellCoord cell) => TerrainInfo.IsPassable(TerrainAt(cell));

    /// <summary>
    /// Whether any entity currently occupies the cell. Same bounds contract
    /// as <see cref="TerrainAt"/> — out-of-grid cells throw
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public bool IsOccupied(CellCoord cell)
    {
        EnsureInBounds(cell);
        return _occupants.ContainsKey(cell);
    }

    /// <summary>
    /// Looks up the entity occupying a cell. Returns false for free cells;
    /// out-of-bounds cells are rejected with
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public bool TryGetOccupant(CellCoord cell, out EntityId occupant)
    {
        EnsureInBounds(cell);
        return _occupants.TryGetValue(cell, out occupant!);
    }

    /// <summary>
    /// Registers <paramref name="id"/> as the occupant of <paramref name="cells"/>
    /// (multi-cell bodies allowed). Rejects malformed input with
    /// <see cref="ArgumentException"/> (null/empty list, out-of-bounds cell) and
    /// any overlap — including re-registering an already-placed entity — with
    /// <see cref="InvalidOperationException"/> naming the first conflicting
    /// cell. Clear first via <see cref="ClearOccupancy"/> to move an entity.
    /// </summary>
    public void RegisterOccupancy(EntityId id, IReadOnlyList<CellCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
        {
            throw new ArgumentException("Occupancy needs at least one cell.", nameof(cells));
        }

        foreach (CellCoord cell in cells)
        {
            if (!Contains(cell))
            {
                throw new ArgumentException(
                    $"Cell ({cell.X}, {cell.Y}) is outside the {Width}×{Height} grid.", nameof(cells));
            }
        }

        if (_occupancy.ContainsKey(id))
        {
            throw new InvalidOperationException(
                $"Entity {id.Value} is already registered; clear its occupancy before re-registering.");
        }

        foreach (CellCoord cell in cells)
        {
            if (_occupants.ContainsKey(cell))
            {
                throw new InvalidOperationException(
                    $"Cell ({cell.X}, {cell.Y}) is already occupied by entity {_occupants[cell].Value}.");
            }
        }

        var owned = new List<CellCoord>(cells);
        _occupancy.Add(id, owned);
        foreach (CellCoord cell in owned)
        {
            _occupants.Add(cell, id);
        }
    }

    /// <summary>
    /// Releases every cell held by <paramref name="id"/>. Returns false (and
    /// changes nothing) when the entity holds no cells.
    /// </summary>
    public bool ClearOccupancy(EntityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_occupancy.Remove(id, out List<CellCoord>? owned))
        {
            return false;
        }

        foreach (CellCoord cell in owned)
        {
            _occupants.Remove(cell);
        }

        return true;
    }

    /// <summary>
    /// Defines a named zone (F-117 partition naming layer). Zone ids are
    /// language-neutral stable identifiers — the same id family the action
    /// Schema uses for <c>lane</c> (G.2 SetRally/SpawnScout, K-3/T-021 will
    /// reuse <see cref="IsValidZoneId"/> as the single validation rule).
    /// Zones may overlap (they are labels, not occupancy). Rejects invalid
    /// ids, duplicate ids and out-of-bounds/empty cell lists with
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public void DefineZone(string zoneId, IReadOnlyList<CellCoord> cells)
    {
        if (!IsValidZoneId(zoneId))
        {
            throw new ArgumentException(
                $"Zone id '{zoneId}' is invalid: expected ^[a-z0-9-]+$ (language-neutral lane id).",
                nameof(zoneId));
        }

        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
        {
            throw new ArgumentException("A zone needs at least one cell.", nameof(cells));
        }

        if (_zones.ContainsKey(zoneId))
        {
            throw new ArgumentException($"Zone '{zoneId}' is already defined.", nameof(zoneId));
        }

        foreach (CellCoord cell in cells)
        {
            if (!Contains(cell))
            {
                throw new ArgumentException(
                    $"Cell ({cell.X}, {cell.Y}) is outside the {Width}×{Height} grid.", nameof(cells));
            }
        }

        _zones.Add(zoneId, new List<CellCoord>(cells));
    }

    /// <summary>Whether a zone with this id exists.</summary>
    public bool ZoneExists(string zoneId) =>
        IsValidZoneId(zoneId) && _zones.ContainsKey(zoneId);

    /// <summary>Looks up the cells of a zone by id.</summary>
    public bool TryGetZoneCells(string zoneId, out IReadOnlyList<CellCoord> cells)
    {
        if (IsValidZoneId(zoneId) && _zones.TryGetValue(zoneId, out List<CellCoord>? owned))
        {
            cells = owned;
            return true;
        }

        cells = Array.Empty<CellCoord>();
        return false;
    }

    /// <summary>
    /// Zone/lane id rule (T-032 task card): matches ^[a-z0-9-]+$ — one or
    /// more lowercase ASCII letters, digits or hyphens. Deliberately excludes
    /// underscore, uppercase, spaces and non-ASCII so ids stay stable across
    /// locales — the localizable display name lives in content data, never in
    /// the id (same id family the action Schema uses for <c>lane</c>,
    /// G.2 SetRally/SpawnScout).
    /// </summary>
    public static bool IsValidZoneId(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return false;
        }

        foreach (char c in zoneId)
        {
            bool ok = c is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// FNV-1a 64 over dimensions + terrain in row-major order. Zones and
    /// occupancy are excluded (labels/state layers, not map identity): two
    /// worlds with the same seed, ratios and dimensions produce equal hashes,
    /// and any terrain edit changes the hash.
    /// </summary>
    public long ComputeMapHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)Width);
        hash = Mix(hash, (ulong)Height);
        for (int i = 0; i < _terrain.Length; i++)
        {
            hash = Mix(hash, (ulong)_terrain[i]);
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// Terrain base cost of entering the cell (Pathfinding.IFlowGrid seam for
    /// C-11, T-037). Terrain layer only — same contract as
    /// <see cref="TerrainInfo.Cost(TerrainKind)"/>.
    /// </summary>
    public int Cost(CellCoord cell) => TerrainInfo.Cost(TerrainAt(cell));

    /// <summary>
    /// IFlowGrid passability: terrain layer only. Buildings registered via
    /// <see cref="RegisterOccupancy"/> do NOT block here — their block+tear
    /// semantics need building HP, which lands with C-09 (T-038); at that
    /// point the C-05/C-09/C-11 composition maps occupied cells to
    /// Passable=false + positive BloodCost per F-044 (tear-down mode).
    /// </summary>
    public bool Passable(CellCoord cell) => IsPassable(cell);

    /// <summary>
    /// IFlowGrid tear price: always 0 in C-05 — terrain knows no hit points.
    /// Building HP×5 pricing is owned by C-09/C-11 (see <see cref="Passable"/>).
    /// </summary>
    public int BloodCost(CellCoord cell)
    {
        _ = TerrainAt(cell); // bounds check only; blood cost is HP-based, not terrain-based
        return 0;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (value >> shift) & 0xFFUL;
                hash *= FnvPrime;
            }

            return hash;
        }
    }

    private void EnsureInBounds(CellCoord cell)
    {
        if (!Contains(cell))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cell),
                $"Cell ({cell.X}, {cell.Y}) is outside the {Width}×{Height} grid.");
        }
    }

    private static void ValidateRatios(double forestRatio, double rockRatio, double waterRatio)
    {
        if (forestRatio < 0 || rockRatio < 0 || waterRatio < 0)
        {
            throw new ArgumentException("Terrain ratios must be non-negative.");
        }

        if (forestRatio + rockRatio + waterRatio > 1.0 + 1e-9)
        {
            throw new ArgumentException(
                $"Terrain ratios must sum to at most 1 (got {forestRatio + rockRatio + waterRatio}).");
        }
    }
}
