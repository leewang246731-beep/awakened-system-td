using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Upgraded re-verification of the spike② maze+supply coexistence scenario
/// with the PRODUCTIZED pieces (C-07 T-064, [F-045]/[F-042]; T-044 留档的
/// 升级复验)：a 40×24 forest maze (two serpentine wall columns, alternating
/// gaps) crossed by a supply corridor built entirely by <see
/// cref="CorridorRule"/> — lining walls, auto-derived enemy price (pit#4) and
/// the enemy lane's dual-cost view registered on the
/// <see cref="FlowFieldManager"/> bucket (pit#3). Enemy flow = flow-field
/// gradient path analysis (counter-level, no unit layer needed for the
/// routing semantics); porter flow = real <see cref="Carrier"/>s on cached
/// A* routes. The 落闸 event (terrain wall on the corridor gate) must trigger
/// BOTH flows' replanning independently: exactly one enemy-field rebuild +
/// porter cache invalidation + phase-transition reroutes, after which
/// deliveries keep growing (互不阻塞复验).
/// </summary>
// [F-045][F-042] spike② 场景升级版：产品化走廊交规+城门落闸共存复验
public class LogisticsCorridorIntegrationTests
{
    private const long Seed = 20261019;

    private const int Width = 40;

    private const int Height = 24;

    private static readonly int[] SerpentineColumns = { 13, 27 };

    private static readonly CellRegion Lane = new(2, 11, 35, 2);

    private static readonly CellCoord EnemySpawn = new(3, 21);

    private static readonly CellCoord Base = new(37, 2);

    private static readonly CellCoord PorterHome = new(1, 11);

    private static readonly CellCoord PorterDestination = new(37, 11);

    /// <summary>城门：第一道蛇廊柱上的走廊闸口（落闸事件封这里）。</summary>
    private static readonly CellCoord[] GateCells =
    {
        new(SerpentineColumns[0], Lane.Y),
        new(SerpentineColumns[0], Lane.Y + 1),
    };

    /// <summary>
    /// Builds the maze world: forest floor, two serpentine wall columns
    /// (x=13 top gap y1..4, x=27 bottom gap y19..22), then the productized
    /// corridor (Road lane punches 城门 gates through both columns; lining
    /// walls flank it with one underpass at x20..21) and the auto-derived
    /// enemy price.
    /// </summary>
    private static (GridWorld World, CorridorRule Rule) BuildMaze()
    {
        GridWorld world = LogisticsWorlds.ForestWorld(Width, Height, Seed);
        foreach (int col in SerpentineColumns)
        {
            bool topGap = col == SerpentineColumns[0];
            for (int y = 1; y <= Height - 2; y++)
            {
                if (topGap && y <= 4)
                {
                    continue;
                }

                if (!topGap && y >= Height - 5)
                {
                    continue;
                }

                world.SetTerrain(new CellCoord(col, y), TerrainKind.Wall);
            }
        }

        var rule = new CorridorRule(Lane, new[] { 20 });
        int liningWalls = rule.ApplyStructure(world); // Road 车道穿透蛇廊柱=城门
        Assert.True(liningWalls > 0, "corridor structure must write lining walls");
        rule.DerivePricing(world);
        return (world, rule);
    }

    private static FlowField BuildEnemyField(FlowFieldManager manager, FlowFieldKey key)
    {
        FlowField field = manager.Acquire(key, Base);
        while (!field.IsReady)
        {
            manager.Update(FlowFieldManager.DefaultFrameBudgetMs);
            field = manager.Acquire(key, Base);
        }

        return field;
    }

    [Fact]
    public void ProductizedCorridorKeepsEnemyAndPorterFlowsApartAcrossAGateDrop()
    {
        (GridWorld world, CorridorRule rule) = BuildMaze();

        // ---- 敌流：重价视图桶（pit#3 正式接口），蛇廊绕行、不借道走廊 ----
        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);
        rule.RegisterEnemyLane(manager, enemyKey, world);
        FlowField enemyField = BuildEnemyField(manager, enemyKey);
        Assert.Equal(FlowFieldMode.Normal, enemyField.Mode);

        IReadOnlyList<CellCoord> enemyPath = enemyField.BuildPath(EnemySpawn);
        int enemyLaneCells = enemyPath.Count(rule.IsLaneCell);
        double straightCells = Math.Sqrt(
            (double)((Base.X - EnemySpawn.X) * (Base.X - EnemySpawn.X))
            + (Base.Y - EnemySpawn.Y) * (Base.Y - EnemySpawn.Y));
        double ratio = enemyPath.Count / straightCells;

        Assert.True(enemyLaneCells <= 2, $"enemy path uses {enemyLaneCells} lane cells — the corridor repricing failed");
        Assert.True(ratio >= 1.2, $"enemy path ratio {ratio:F2} below the serpentine bar 1.2 ({enemyPath.Count} cells vs {straightCells:F1})");

        // ---- 民流：A* + 缓存 + 真民夫穿管送弹 ----
        var pathfinder = new AStarPathfinder(world);
        var cache = new PathCache(pathfinder);
        PathQueryResult porterRoute = cache.GetPath(PorterHome, PorterDestination);
        Assert.Equal(PathQueryStatus.Found, porterRoute.Status);
        int porterLaneCells = porterRoute.Cells.Count(rule.IsLaneCell);
        Assert.True(porterLaneCells >= 30, $"porter route rides only {porterLaneCells} lane cells — 民走 broken");

        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porters = new List<Carrier>
        {
            new(new EntityId(500_001), new ResourceKey("core:ammo"), supplier, receiver, porterRoute.Cells),
            new(new EntityId(500_002), new ResourceKey("core:ammo"), supplier, receiver, porterRoute.Cells),
        };

        int reroutesIssued = 0;
        int rerouteFallbacks = 0;

        void TryReroute(Carrier porter, CellCoord from, CarrierPhase phase)
        {
            var target = phase == CarrierPhase.Outbound ? porter.Destination : porter.Home;
            PathQueryResult fresh = cache.GetPath(from, target);
            if (fresh.Status == PathQueryStatus.Found && fresh.Cells.Count >= 2)
            {
                porter.Reroute(fresh.Cells); // 加墙→失效→重算→改道
                reroutesIssued++;
            }
            else
            {
                rerouteFallbacks++;
            }
        }

        foreach (Carrier porter in porters)
        {
            porter.RouteInvalidated += e =>
            {
                // 出发相位主动换新路面（spike2 pit#5 的产品化事件接线）
                TryReroute(porter, e.DepartureCell, e.Phase);
            };
        }

        // ---- 落闸事件：封第一道蛇廊柱的城门 ---------------------------------
        const int eventTick = 150;
        const int runTicks = 1800;
        int rebuildsBefore = manager.GetRebuildCount(enemyKey);
        long searchesBeforeEvent = pathfinder.SearchCount;
        Assert.Equal(1L, searchesBeforeEvent); // 事件前仅初始搜索（缓存命中不重算）
        int removedEntries = 0;

        for (int tick = 1; tick <= runTicks; tick++)
        {
            if (tick == eventTick)
            {
                foreach (CellCoord gate in GateCells)
                {
                    world.SetTerrain(gate, TerrainKind.Wall); // 落闸
                }

                removedEntries = cache.Invalidate(GateCells);
                manager.MarkDirty(new CellRegion(
                    SerpentineColumns[0] - 1, Lane.Y - 1, 3, Lane.Height + 2));
                while (manager.GetRebuildCount(enemyKey) < rebuildsBefore + 1)
                {
                    manager.Update(FlowFieldManager.DefaultFrameBudgetMs); // 恰一次重建
                }

                foreach (Carrier porter in porters)
                {
                    if (porter.Phase is CarrierPhase.Outbound or CarrierPhase.Inbound)
                    {
                        TryReroute(porter, porter.CurrentCell, porter.Phase); // 在途立即改道
                    }
                }
            }

            manager.Update(0); // 无预算泵：无 pending 时为幂等空步
            foreach (Carrier porter in porters)
            {
                porter.Tick();
            }
        }

        // ---- 互不阻塞与各自重规划（计数器级） ------------------------------
        Assert.Equal(rebuildsBefore + 1, manager.GetRebuildCount(enemyKey)); // 事件=恰一次敌场重建
        Assert.True(removedEntries >= 1, "the gate drop must invalidate the porter route through it");
        Assert.True(reroutesIssued >= 1, $"phase-transition reroutes must fire (got {reroutesIssued})");
        Assert.True(rerouteFallbacks <= 2, $"no-route departures must stay capped (got {rerouteFallbacks})");

        long searchesAfter = pathfinder.SearchCount;
        Assert.True(searchesAfter > searchesBeforeEvent, "reroutes must really recompute");

        long deliveriesFinal = porters.Sum(p => p.DeliveredItems);
        Assert.True(deliveriesFinal > 0, "porters must deliver across the event");
        Assert.NotEmpty(receiver.AcceptCalls); // 目的端真收到过 accept 调用

        // 纯交通窗口（事件后静置段）内敌场零重建、民夫零重算——两条流互不拖累
        long searchesQuietStart = pathfinder.SearchCount;
        for (int tick = 1801; tick <= 1900; tick++)
        {
            manager.Update(0);
            foreach (Carrier porter in porters)
            {
                porter.Tick();
            }
        }

        Assert.Equal(searchesQuietStart, pathfinder.SearchCount);
        Assert.Equal(rebuildsBefore + 1, manager.GetRebuildCount(enemyKey));

        // 落闸后敌路径仍不借道（结构+双代价在新格局下依旧成立）
        FlowField rebuilt = manager.GetField(enemyKey)!;
        Assert.True(rebuilt.IsReady);
        int laneCellsAfter = rebuilt.BuildPath(EnemySpawn).Count(rule.IsLaneCell);
        Assert.True(laneCellsAfter <= 2, $"enemy path after the gate drop uses {laneCellsAfter} lane cells");
    }
}
