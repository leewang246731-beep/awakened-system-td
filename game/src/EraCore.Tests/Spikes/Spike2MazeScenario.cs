using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.World;

namespace EraCore.Tests.Spikes;

/// <summary>
/// Enemy-side dual-cost view of the spike② map (T-044, F-167): the shared
/// <see cref="BuildingRegistry"/> composed grid with the supply-corridor cells
/// repriced. <see cref="IFlowGrid"/> carries ONE cost column, so the v3.2 §6
/// traffic rule "补给走廊对民夫便宜、对敌流场昂贵" is realized as two views over
/// the same world state instead of a dual table: this view multiplies the
/// corridor to <see cref="EnemyCorridorCost"/> for the enemy
/// <see cref="FlowFieldManager"/>, while porters consume the registry directly
/// (corridor = <see cref="TerrainKind.Road"/>, cost 1, vs forest 2). The
/// manager's lane × costType buckets stay reserved for future per-class cost
/// profiles (costType 0 = standard enemy profile here).
/// </summary>
internal sealed class Spike2EnemyCorridorView : IFlowGrid
{
    /// <summary>
    /// Corridor entry price for the ENEMY flow field only ([NEW-原型校准],
    /// spike-local knob): the walled lane makes shadowing impossible and the
    /// only inside-hops left are region-to-region (11 cells ≈ 3850) vs the
    /// overland leg they would replace (~2120) — so 350 keeps enemy flow on
    /// the serpentine; the full west-mouth-to-east-mouth transit (49 × 350 ≈
    /// 17150) is far out of reach. The 2 underpass toll cells per leg are the
    /// enemy's legitimate priced contact with the shared corridor.
    /// </summary>
    public const int EnemyCorridorCost = 350;

    private readonly Spike2MazeScenario _scenario;

    public Spike2EnemyCorridorView(Spike2MazeScenario scenario)
    {
        _scenario = scenario;
    }

    public int Width => _scenario.Registry.Width;

    public int Height => _scenario.Registry.Height;

    public int Cost(CellCoord c) =>
        _scenario.IsCorridor(c.X, c.Y) ? EnemyCorridorCost : _scenario.Registry.Cost(c);

    public bool Passable(CellCoord c) => _scenario.Registry.Passable(c);

    public int BloodCost(CellCoord c) => _scenario.Registry.BloodCost(c);
}

/// <summary>
/// Infinite ammo source / accept-all destination for the spike porters: the
/// spike isolates the transport layer, so the source never starves and the
/// destination never refuses (throughput numbers therefore reflect pure
/// routing + movement, not queueing).
/// </summary>
internal sealed class InfiniteSupplier : ICargoSupplier
{
    public long Calls { get; private set; }

    public int TryTake(ResourceKey resource, int maxAmount)
    {
        Calls++;
        return maxAmount;
    }
}

/// <summary>Accept-everything unload port (待卸队列 never refuses in the spike).</summary>
internal sealed class AcceptAllReceiver : ICargoReceiver
{
    public long Accepted { get; private set; }

    public int TryAccept(ResourceKey resource, int amount)
    {
        Accepted += amount;
        return amount;
    }
}

/// <summary>
/// T-044 spike② maze world (F-167, design v3.2 §6 "物流交规（补给走廊/
/// 城门）"): a deterministic 64×64 composite map — forest background, an
/// untearable Wall-terrain serpentine (5 columns, alternating top/bottom
/// gaps), and a WALLED supply corridor (城墙夹廊): the 2-cell paved lane runs
/// inside untearable lining walls with two mouths (west x2..3, east x59..61)
/// and pierces every serpentine column through its 城门 gate cell pair. Each
/// middle region gets an underpass hole through the lining so the overland
/// serpentine stays connected; enemies crossing an underpass pay the corridor
/// toll on the 2 gate-row cells, and an inside-the-tube hop (11 cells × 350)
/// costs more than the overland leg it would replace (~2120) — the dual cost
/// and the lining TOGETHER keep enemy flow on the long maze while porters
/// enjoy the cost-1 road. BuildingRegistry HP walls mix in as the gatehouse
/// bulges outside the tube plus the mid-run event wall. Enemy spawn
/// (bottom-left) and base (top-right) force a serpentine route ≥3× the
/// straight line; porter depot (bottom-left) and destination (east tube end)
/// share the same map, crossing the enemy lanes at every serpentine leg.
/// All randomness enters through the injected <see cref="IRng"/> (CS-1.4).
/// </summary>
internal sealed class Spike2MazeScenario
{
    public const int Size = 64;

    /// <summary>Untearable serpentine wall columns (Wall terrain), left to right.</summary>
    public static readonly int[] SerpentineColumns = { 10, 21, 32, 43, 54 };

    /// <summary>Columns whose wall opens at the TOP (y 2..5); the others open at the bottom (y 58..61).</summary>
    public static readonly int[] TopGapColumns = { 10, 32, 54 };

    public const int CorridorY0 = 30;
    public const int CorridorY1 = 31;
    public const int CorridorXMin = 4;
    public const int CorridorXMax = 58;

    /// <summary>
    /// Underpass hole x-positions (2 wide each, one per middle region): the
    /// lining walls open here so the overland north-south legs of the enemy
    /// serpentine stay connected through plain forest.
    /// </summary>
    public static readonly int[] UnderpassX = { 15, 16, 26, 27, 37, 38, 48, 49 };

    /// <summary>HP-wall tear price of the gatehouse bulges flanking the tube exterior.</summary>
    public const int GatehouseHp = 300;

    /// <summary>HP-wall tear price of the mid-run event wall sealing the corridor gate.</summary>
    public const int EventWallHp = 500;

    /// <summary>Player base = enemy flow-field target (top-right region).</summary>
    public CellCoord Base { get; } = new(58, 5);

    /// <summary>Porter ammo destination at the east end of the supply corridor.</summary>
    public CellCoord Destination { get; } = new(58, CorridorY0);

    /// <summary>Gradient-descent probe cell for the maze path-length ratio (a spawn cell).</summary>
    public CellCoord PathProbe { get; } = new(7, 55);

    /// <summary>Enemy entry cells (bottom-left region, between ring and the first serpentine column).</summary>
    public IReadOnlyList<CellCoord> SpawnCells { get; }

    /// <summary>Porter home cells (depot cluster bottom-left, distinct per carrier).</summary>
    public IReadOnlyList<CellCoord> DepotCells { get; }

    /// <summary>
    /// Cells sealed by the mid-run event wall: the 城门 gate of the first
    /// serpentine column (x=10, 落闸). Porters sealed west of it must backtrack
    /// out of the west mouth and take the overland serpentine to the east
    /// mouth — the full "绕城走大路" detour; the enemy field barely changes
    /// because the overland serpentine never used the gate.
    /// </summary>
    public IReadOnlyList<CellCoord> EventWallCells { get; } = new[] { new CellCoord(SerpentineColumns[0], CorridorY0), new CellCoord(SerpentineColumns[0], CorridorY1) };

    public GridWorld World { get; }

    public BuildingRegistry Registry { get; }

    public IFlowGrid EnemyGrid { get; }

    /// <summary>Number of Wall-terrain cells placed (untearable structure).</summary>
    public int WallTerrainCells { get; private set; }

    /// <summary>Number of BuildingRegistry HP walls placed at construction (gatehouse).</summary>
    public int HpWallBuildings { get; private set; }

    /// <summary>Ammo resource id shared by all spike porters.</summary>
    public ResourceKey Ammo { get; } = new("ammo");

    public Spike2MazeScenario(long seed = 20261013)
    {
        // All-forest background (ratio 1.0 ⇒ every draw lands below the
        // threshold deterministically); walls/road overwrite via SetTerrain.
        World = new GridWorld(Size, Size, new EraCore.Tests.World.LcgRng(seed), forestRatio: 1.0, rockRatio: 0, waterRatio: 0);
        Registry = new BuildingRegistry(World);
        Registry.AttachToWorld();
        EnemyGrid = new Spike2EnemyCorridorView(this);

        BuildRing();
        BuildSerpentine();
        BuildCorridor();
        BuildLining();
        PlaceGatehouse();

        SpawnCells = BuildCellBlock(xMin: 6, xMax: 8, yMin: 52, yMax: 58);
        DepotCells = new[]
        {
            new CellCoord(3, 61), new CellCoord(4, 61), new CellCoord(5, 61),
            new CellCoord(3, 60), new CellCoord(4, 60), new CellCoord(5, 60),
            new CellCoord(3, 59), new CellCoord(4, 59), new CellCoord(5, 59),
            new CellCoord(4, 58),
        };
    }

    /// <summary>Whether the cell belongs to the paved supply corridor (y 30..31 band).</summary>
    public bool IsCorridor(int x, int y) =>
        (y == CorridorY0 || y == CorridorY1) && x >= CorridorXMin && x <= CorridorXMax;

    /// <summary>
    /// Places the mid-run event wall (two HP buildings sealing the corridor
    /// gate of the first serpentine column) — the coexistence stress event:
    /// enemy field rebuild + porter cache invalidation + mid-trip reroute,
    /// all from one placement.
    /// </summary>
    public void PlaceEventWall(long idBase)
    {
        int i = 0;
        foreach (CellCoord cell in EventWallCells)
        {
            Registry.Place(new EntityId(idBase + i), WallSpec(EventWallHp), cell);
            i++;
        }
    }

    private void BuildRing()
    {
        // Two-cell-thick outer ring reaching the map edge — the band outside a
        // one-thick ring would be walkable-but-sealed forest and flip the
        // whole field into WallTear mode (全封死检测).
        for (int x = 0; x < Size; x++)
        {
            SetWall(x, 0);
            SetWall(x, 1);
            SetWall(x, 62);
            SetWall(x, 63);
        }

        for (int y = 0; y < Size; y++)
        {
            SetWall(0, y);
            SetWall(1, y);
            SetWall(62, y);
            SetWall(63, y);
        }
    }

    private void BuildSerpentine()
    {
        foreach (int col in SerpentineColumns)
        {
            bool topGap = TopGapColumns.Contains(col);
            for (int y = 2; y <= 61; y++)
            {
                if (topGap && y <= 5)
                {
                    continue;
                }

                if (!topGap && y >= 58)
                {
                    continue;
                }

                if (y == CorridorY0 || y == CorridorY1)
                {
                    continue; // 城门: the corridor pierces every column
                }

                SetWall(col, y);
            }
        }
    }

    private void BuildCorridor()
    {
        for (int x = CorridorXMin; x <= CorridorXMax; x++)
        {
            World.SetTerrain(new CellCoord(x, CorridorY0), TerrainKind.Road);
            World.SetTerrain(new CellCoord(x, CorridorY1), TerrainKind.Road);
        }
    }

    private void BuildLining()
    {
        // 城墙夹廊: untearable lining walls two rows thick on both sides of
        // the paved lane, spanning x4..58 with underpass holes per middle
        // region. Enemies cannot shadow the lane from outside (the lining
        // blocks) and cannot hop between regions inside it (a hop costs
        // 11 cells × corridor price > the overland leg it would replace);
        // porters inside the tube are untouched by all of this.
        int[] liningRows = { CorridorY0 - 2, CorridorY0 - 1, CorridorY1 + 1, CorridorY1 + 2 };
        foreach (int y in liningRows)
        {
            for (int x = CorridorXMin; x <= CorridorXMax; x++)
            {
                if (UnderpassX.Contains(x))
                {
                    continue; // 敌方过街涵洞: the overland serpentine legs cross here
                }

                var cell = new CellCoord(x, y);
                if (World.TerrainAt(cell) == TerrainKind.Wall)
                {
                    continue; // serpentine column body already walls this cell
                }

                World.SetTerrain(cell, TerrainKind.Wall);
                WallTerrainCells++;
            }
        }
    }

    private void PlaceGatehouse()
    {
        // 城门 gatehouse: HP-wall bulges outside the tube lining, west of the
        // first serpentine column — blocking but tearable for enemies
        // (BloodCost = HP), plain obstacles for porters (A* never tears).
        CellCoord[] gatehouse =
        {
            new(SerpentineColumns[0] - 2, CorridorY0 - 3), new(SerpentineColumns[0] - 1, CorridorY0 - 3),
            new(SerpentineColumns[0] - 2, CorridorY1 + 3), new(SerpentineColumns[0] - 1, CorridorY1 + 3),
        };
        int i = 0;
        foreach (CellCoord cell in gatehouse)
        {
            Registry.Place(new EntityId(800_000 + i), WallSpec(GatehouseHp), cell);
            i++;
        }

        HpWallBuildings = gatehouse.Length;
    }

    private static IReadOnlyList<CellCoord> BuildCellBlock(int xMin, int xMax, int yMin, int yMax)
    {
        var cells = new List<CellCoord>();
        for (int y = yMin; y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                cells.Add(new CellCoord(x, y));
            }
        }

        return cells;
    }

    private void SetWall(int x, int y)
    {
        World.SetTerrain(new CellCoord(x, y), TerrainKind.Wall);
        WallTerrainCells++;
    }

    /// <summary>
    /// Spike-local HP-wall spec (Support class, no weapon cycle): the numbers
    /// exist only to price the tear path; product content arrives from
    /// data/towers.json (CS-2.3 does not bind test-side fixtures).
    /// </summary>
    internal static TowerSpec WallSpec(int hp) => new(
        Id: "spike2:hp-wall",
        Class: TowerClass.Support,
        Tier: 1,
        MaxTier: 1,
        CostTotal: 10,
        Dps: 0,
        DamagePerShot: 0,
        ReloadTicks: null,
        RangeCells: 0,
        Hp: hp,
        AmmoItem: null,
        AmmoPerShot: 0,
        TierGrowth: null,
        Traits: null);
}
