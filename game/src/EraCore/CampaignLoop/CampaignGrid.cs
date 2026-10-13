using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.CampaignLoop;

/// <summary>Kind of one continent-grid cell (选图薄版, F-005).</summary>
public enum CampaignCellKind
{
    /// <summary>Empty water/wilderness — nothing to select.</summary>
    Empty = 0,

    /// <summary>A level node the player may capture.</summary>
    Level = 1,

    /// <summary>An enemy camp — adjacency raises neighbouring level threat.</summary>
    EnemyCamp = 2,
}

/// <summary>Immutable view of one continent-grid cell (F-005).</summary>
/// <param name="X">Column (0-based).</param>
/// <param name="Y">Row (0-based).</param>
/// <param name="Kind">Cell kind.</param>
public sealed record CampaignCell(int X, int Y, CampaignCellKind Kind);

/// <summary>
/// One selectable level node of the continent grid (F-005): its threat is
/// derived LIVE from the generated camp layout through the frozen v3.2 §7
/// formula — <c>clamp(0.3 + 0.3×邻接敌营数, 0.3, 1.2)</c> via
/// <see cref="Threat.ClampThreat"/> — and its win-wave count through
/// <see cref="Campaign.WinWaves"/> (10+⌊t×10⌋). Because the layout is
/// seed-deterministic, so is every node's threat (seed 化).
/// </summary>
/// <param name="Cell">Grid coordinates of the node.</param>
/// <param name="AdjacentEnemyCamps">Count of 4-neighbour cells that are enemy camps.</param>
/// <param name="Threat">Clamped threat t of the node.</param>
/// <param name="WinWaves">胜波 = 10 + ⌊t×10⌋ (v3.2 §7).</param>
public sealed record LevelNodeInfo(CampaignCell Cell, int AdjacentEnemyCamps, double Threat, int WinWaves);

/// <summary>
/// One banking line of an occupy/victory settlement (T-059 thin): what was
/// granted, what the campaign pool accepted (入池只计上限内) and what was
/// refused above the headroom. Refused loot is NOT destroyed — it simply
/// stays unbanked; conservation asserts granted = accepted + refused.
/// </summary>
/// <param name="Resource">Resource granted.</param>
/// <param name="Granted">Amount the settlement offered the pool.</param>
/// <param name="Accepted">Amount the pool accepted (≤ Granted).</param>
/// <param name="Refused">Amount refused above the cap (= Granted − Accepted).</param>
public sealed record ResourceGrant(ResourceKey Resource, double Granted, double Accepted, double Refused);

/// <summary>
/// Thin-version continent-grid knobs (F-005): the SQUARE grid with
/// 4-neighbour adjacency is the thin E1 form — v3.2 §7's hex 6-neighbour
/// geometry is a later campaign-map task (registered as follow-up; changing
/// it swaps <see cref="CampaignGrid.NeighbourOffsets"/> and the adjacency
/// count only, the threat formula stays). No v3.2 number pins continent camp
/// density — these are structural generation knobs of the thin version
/// (like <c>GridWorld</c>'s terrain ratios), supplied/configured by the
/// host; the E2E passes them explicitly.
/// </summary>
/// <param name="EnemyCampRatio">Row-major draw share of enemy-camp cells.</param>
/// <param name="LevelRatio">Row-major draw share of level cells.</param>
/// <param name="HomeX">Main-city column; null = grid centre.</param>
/// <param name="HomeY">Main-city row; null = grid centre.</param>
/// <param name="RewardResource">Occupy reward resource; null = no reward grant.</param>
/// <param name="RewardPerWinWave">Reward units per win wave (grant = value × node WinWaves).</param>
public sealed record CampaignGridOptions(
    double EnemyCampRatio,
    double LevelRatio,
    int? HomeX = null,
    int? HomeY = null,
    ResourceKey? RewardResource = null,
    double RewardPerWinWave = 0.0);

/// <summary>
/// 选图薄版 continent grid (F-005, T-059): a seed-deterministic square grid
/// of <see cref="CampaignCellKind"/> cells with 4-neighbour adjacency (hex
/// geometry = later task). The player owns the home cell from the start and
/// may only select a LEVEL cell that is 4-adjacent to an already-occupied
/// one (大陆网格相邻格). Occupying a cell banks its reward into the shared
/// campaign <see cref="ResourceStock"/> under the pool cap (入池只计上限内).
/// All randomness flows through the injected <see cref="IRng"/>, reseeded
/// with the world seed (IRng replay contract): same seed ⇒ same layout ⇒
/// same node threats (seed 化, CS-1.4).
/// </summary>
public sealed class CampaignGrid
{
    /// <summary>Thin-version 4-neighbour offsets (hex 6-neighbour = later task).</summary>
    public static readonly (int Dx, int Dy)[] NeighbourOffsets =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0),
    };

    private readonly CampaignCellKind[,] _kinds;
    private readonly bool[,] _occupied;
    private readonly List<CampaignCell> _occupiedInOrder = new();
    private readonly ResourceStock _pool;
    private readonly CampaignGridOptions _options;

    /// <summary>
    /// Generates the continent layout: rng reseeded with
    /// <paramref name="worldSeed"/>, one draw per cell row-major (camp
    /// first, then level, remainder empty), home cell forced to a Level
    /// node and occupied from the start.
    /// </summary>
    /// <param name="width">Grid width, ≥ 3 (home plus selectable ring).</param>
    /// <param name="height">Grid height, ≥ 3.</param>
    /// <param name="rng">Random port (reseeded with the world seed here).</param>
    /// <param name="worldSeed">Deterministic layout key (IRng replay contract).</param>
    /// <param name="pool">Campaign resource pool rewards deposit into (cap enforced inside).</param>
    /// <param name="options">Generation knobs (ratios, home cell, reward policy).</param>
    /// <exception cref="ArgumentNullException">rng or pool is null.</exception>
    /// <exception cref="ArgumentException">
    /// Dimensions below 3×3, ratio sum outside (0, 1], or negative reward
    /// per win wave.
    /// </exception>
    public CampaignGrid(int width, int height, IRng rng, long worldSeed, ResourceStock pool, CampaignGridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(pool);
        options ??= DefaultOptions;
        if (width < 3 || height < 3)
        {
            throw new ArgumentException($"Grid needs at least 3×3 cells (got {width}×{height}).");
        }

        if (options.EnemyCampRatio <= 0 || options.LevelRatio <= 0
            || options.EnemyCampRatio + options.LevelRatio > 1.0)
        {
            throw new ArgumentException(
                $"Ratios must be positive with a sum ≤ 1 (camp {options.EnemyCampRatio}, level {options.LevelRatio}).");
        }

        if (options.RewardPerWinWave < 0)
        {
            throw new ArgumentException(
                $"RewardPerWinWave must be non-negative (got {options.RewardPerWinWave}).");
        }

        Width = width;
        Height = height;
        WorldSeed = worldSeed;
        _options = options;
        _pool = pool;
        _kinds = new CampaignCellKind[width, height];
        _occupied = new bool[width, height];

        rng.Reseed(worldSeed);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double draw = rng.NextDouble();
                _kinds[x, y] = draw < options.EnemyCampRatio
                    ? CampaignCellKind.EnemyCamp
                    : draw < options.EnemyCampRatio + options.LevelRatio
                        ? CampaignCellKind.Level
                        : CampaignCellKind.Empty;
            }
        }

        Home = new CampaignCell(options.HomeX ?? (width / 2), options.HomeY ?? (height / 2), CampaignCellKind.Level);
        _kinds[Home.X, Home.Y] = CampaignCellKind.Level;
        _occupied[Home.X, Home.Y] = true;
        _occupiedInOrder.Add(Home);
    }

    /// <summary>
    /// Default thin knobs: no reward resource (occupy banks nothing until a
    /// host configures one) and equal 20%/40% camp/level shares — the E1
    /// E2E overrides both explicitly.
    /// </summary>
    public static CampaignGridOptions DefaultOptions { get; } = new(0.20, 0.40);

    /// <summary>Grid width in cells.</summary>
    public int Width { get; }

    /// <summary>Grid height in cells.</summary>
    public int Height { get; }

    /// <summary>Layout seed (replay key of the whole grid).</summary>
    public long WorldSeed { get; }

    /// <summary>The main-city cell (Level, occupied from construction).</summary>
    public CampaignCell Home { get; }

    /// <summary>Occupied cells in occupation order (home first).</summary>
    public IReadOnlyList<CampaignCell> OccupiedCells => _occupiedInOrder;

    /// <summary>Cell view at (x, y); null outside the grid.</summary>
    public CampaignCell? TryGetCell(int x, int y) =>
        Contains(x, y) ? new CampaignCell(x, y, _kinds[x, y]) : null;

    /// <summary>Whether (x, y) lies inside the grid.</summary>
    public bool Contains(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    /// <summary>Whether the cell is occupied (owned by the player).</summary>
    public bool IsOccupied(int x, int y) => Contains(x, y) && _occupied[x, y];

    /// <summary>
    /// Level-node view of (x, y): adjacent-camp count (4-neighbour, live
    /// scan of the seeded layout), clamped threat and win waves.
    /// </summary>
    /// <exception cref="ArgumentException">Cell outside the grid.</exception>
    /// <exception cref="InvalidOperationException">Cell is not a level node.</exception>
    public LevelNodeInfo NodeInfo(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentException($"Cell ({x},{y}) is outside the {Width}×{Height} grid.");
        }

        if (_kinds[x, y] != CampaignCellKind.Level)
        {
            throw new InvalidOperationException($"Cell ({x},{y}) is {_kinds[x, y]}, not a level node.");
        }

        int camps = CountAdjacentCamps(x, y);
        double threat = Threat.ClampThreat(camps);
        return new LevelNodeInfo(new CampaignCell(x, y, CampaignCellKind.Level), camps, threat, Campaign.WinWaves(threat));
    }

    /// <summary>
    /// Selection legality (只能选相邻已占格): the cell is a level node, not
    /// yet occupied, and 4-adjacent to at least one occupied cell.
    /// </summary>
    public bool CanSelect(int x, int y)
    {
        if (!Contains(x, y) || _kinds[x, y] != CampaignCellKind.Level || _occupied[x, y])
        {
            return false;
        }

        foreach ((int dx, int dy) in NeighbourOffsets)
        {
            if (IsOccupied(x + dx, y + dy))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>All currently selectable cells, row-major (host menu order).</summary>
    public IReadOnlyList<CampaignCell> SelectableCells()
    {
        var selectable = new List<CampaignCell>();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (CanSelect(x, y))
                {
                    selectable.Add(new CampaignCell(x, y, CampaignCellKind.Level));
                }
            }
        }

        return selectable;
    }

    /// <summary>
    /// Captures a selectable cell (占领) and banks its reward into the
    /// campaign pool: grant = <see cref="CampaignGridOptions.RewardPerWinWave"/>
    /// × the node's win waves, deposited per resource with cap enforcement
    /// (入池只计上限内, refused part surfaced in the outcome).
    /// </summary>
    /// <returns>The node captured plus its banking lines.</returns>
    /// <exception cref="ArgumentException">Cell outside the grid.</exception>
    /// <exception cref="InvalidOperationException">Cell not selectable.</exception>
    public OccupyOutcome Occupy(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentException($"Cell ({x},{y}) is outside the {Width}×{Height} grid.");
        }

        if (!CanSelect(x, y))
        {
            throw new InvalidOperationException(
                $"Cell ({x},{y}) is not selectable (must be an unoccupied level node adjacent to occupied ground).");
        }

        LevelNodeInfo node = NodeInfo(x, y);
        _occupied[x, y] = true;
        _occupiedInOrder.Add(node.Cell);

        var rewards = new List<ResourceGrant>();
        if (_options.RewardResource is not null && _options.RewardPerWinWave > 0)
        {
            double granted = _options.RewardPerWinWave * node.WinWaves;
            double accepted = _pool.Deposit(_options.RewardResource, granted);
            rewards.Add(new ResourceGrant(_options.RewardResource, granted, accepted, granted - accepted));
        }

        return new OccupyOutcome(node, rewards);
    }

    private int CountAdjacentCamps(int x, int y)
    {
        int camps = 0;
        foreach ((int dx, int dy) in NeighbourOffsets)
        {
            int nx = x + dx;
            int ny = y + dy;
            if (Contains(nx, ny) && _kinds[nx, ny] == CampaignCellKind.EnemyCamp)
            {
                camps++;
            }
        }

        return camps;
    }
}

/// <summary>Result of one <see cref="CampaignGrid.Occupy"/> (F-005).</summary>
/// <param name="Node">The captured level node.</param>
/// <param name="Rewards">Reward banking lines (empty when no reward policy).</param>
public sealed record OccupyOutcome(LevelNodeInfo Node, IReadOnlyList<ResourceGrant> Rewards);
