using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Joint integration of the C-05 grid world with the C-07 precursor stack
/// (T-047, [F-040]/[F-042]): a real seeded <see cref="GridWorld"/> drives
/// A* + path cache + porter through the <c>IFlowGrid</c> port — no MapGrid
/// double, no mocked gradient (RL2). Covers: cross-engine agreement of the
/// transport A* with the C-11 flow field on real terrain, wall placement →
/// affected-only invalidation → recompute detour (other routes stay cached),
/// a porter rerouting mid-trip after a wall lands on its road and still
/// delivering, and forest cost steering routes on a real world.
/// </summary>
// [F-040][F-042] GridWorld→A*+缓存+民夫 joint integration (C-05×C-07 前体)
public class LogisticsIntegrationTests
{
    private const long Seed = 20261014; // same seed the C-05×C-11 joint tests verified pocket-free

    [Fact]
    public void SeededWorldRouteIsOptimalAndAgreesWithTheFlowField()
    {
        // [F-040] 真地形 + 双引擎互证：64×40 种子世界对角路线，A* 找到且每步
        // 合法；代价与 C-11 流场的积分距离相等（同 10/14 度量、GridWorld
        // 血量恒 0 → 两个独立实现对同一最优值收敛）
        var world = LogisticsWorlds.SeededWorld(64, 40, Seed);
        CellCoord start = LogisticsWorlds.FirstPassable(world);
        CellCoord goal = LogisticsWorlds.LastPassable(world);

        var pathfinder = new AStarPathfinder(world);
        PathQueryResult route = pathfinder.FindPath(start, goal);

        Assert.Equal(PathQueryStatus.Found, route.Status);
        Assert.Equal(start, route.Cells[0]);
        Assert.Equal(goal, route.Cells[^1]);
        for (int i = 1; i < route.Cells.Count; i++)
        {
            int dx = Math.Abs(route.Cells[i].X - route.Cells[i - 1].X);
            int dy = Math.Abs(route.Cells[i].Y - route.Cells[i - 1].Y);
            Assert.True(dx <= 1 && dy <= 1 && (dx + dy) > 0, $"Step {i - 1}→{i} is not an 8-neighbour move.");
            Assert.True(world.Passable(route.Cells[i]), $"Step {i} enters impassable cell {route.Cells[i]}.");
        }

        FlowField field = FlowField.BuildComplete(world, goal);
        Assert.Equal(FlowFieldMode.Normal, field.Mode);
        Assert.Equal(field.Distance(start), route.Cost); // 双引擎同值
    }

    [Fact]
    public void WallPlacementInvalidatesOnlyTheRoutedPathAndRecomputeDetours()
    {
        // [F-040] 加墙→仅受影响失效→重算改道：长路线中段格砌墙后，含该格的
        // 缓存路径恰好删 1 条；重算绕开墙（代价不降、哈希变化）；另一条
        // 不相交的短路线仍命中不重算
        var world = LogisticsWorlds.SeededWorld(64, 40, Seed);
        var pathfinder = new AStarPathfinder(world);
        var cache = new PathCache(pathfinder);

        var longFrom = LogisticsWorlds.FirstPassable(world);
        var longTo = LogisticsWorlds.LastPassable(world);
        (CellCoord shortFrom, CellCoord shortTo) = LogisticsWorlds.AdjacentPassablePairFromEnd(world);

        PathQueryResult longRoute = cache.GetPath(longFrom, longTo);
        PathQueryResult shortRoute = cache.GetPath(shortFrom, shortTo);
        Assert.Equal(PathQueryStatus.Found, longRoute.Status);
        Assert.Equal(PathQueryStatus.Found, shortRoute.Status);
        Assert.Equal(2, cache.Count);

        CellCoord wallCell = longRoute.Cells[longRoute.Cells.Count / 2];
        Assert.DoesNotContain(wallCell, shortRoute.Cells); // 前置：两条路线互不共格
        world.SetTerrain(wallCell, TerrainKind.Wall);

        int removed = cache.Invalidate(wallCell);

        Assert.Equal(1, removed); // 只删了长路线
        Assert.False(cache.TryGetCachedPath(longFrom, longTo, out _));
        Assert.True(cache.TryGetCachedPath(shortFrom, shortTo, out _));

        long missesBefore = cache.Misses;
        PathQueryResult detour = cache.GetPath(longFrom, longTo);
        Assert.Equal(PathQueryStatus.Found, detour.Status); // 稀疏图上绕行仍可达
        Assert.Equal(missesBefore + 1, cache.Misses); // 重算恰一次
        Assert.DoesNotContain(wallCell, detour.Cells); // 真的绕开了墙
        Assert.True(detour.Cost >= longRoute.Cost); // 加墙不会让最优代价变小
        Assert.NotEqual(longRoute.Hash, detour.Hash);

        long missesAfterDetour = cache.Misses;
        PathQueryResult shortAgain = cache.GetPath(shortFrom, shortTo);
        Assert.Equal(missesAfterDetour, cache.Misses); // 短路线命中、零重算
        Assert.Equal(shortRoute.Hash, shortAgain.Hash);
    }

    [Fact]
    public void PorterReroutesAroundANewWallAndStillDelivers()
    {
        // [F-042] 民夫改道：15×7 平地直路 (0,3)→(14,3)（448 px）；t=60 民夫
        // 已走 39 个移动 tick = 78 px → CurrentCell=(2,3)；在 (7,3) 砌墙并
        // 失效缓存 → 从 CurrentCell 重算绕行路线（不含墙格）→ Reroute →
        // 民夫照常到达交付；重放两次改道 tick 全等（确定性）
        (int ArrivalTick, int DeliveryTick) RunScenario()
        {
            var world = LogisticsWorlds.PlainWorld(15, 7, Seed);
            var cache = new PathCache(new AStarPathfinder(world));
            PathQueryResult initial = cache.GetPath(new CellCoord(0, 3), new CellCoord(14, 3));
            Assert.Equal(PathQueryStatus.Found, initial.Status);

            var supplier = new StockSupplier(int.MaxValue);
            var receiver = new GateReceiver(int.MaxValue);
            var porter = new Carrier(new EntityId(9), new ResourceKey("core:arrows"), supplier, receiver, initial.Cells);

            int arrivalTick = -1;
            int deliveryTick = -1;
            for (int tick = 1; tick <= 500; tick++)
            {
                if (tick == 60)
                {
                    Assert.Equal(CarrierPhase.Outbound, porter.Phase);
                    Assert.Equal(new CellCoord(2, 3), porter.CurrentCell); // 78 px = 2 格 + 14 px

                    world.SetTerrain(new CellCoord(7, 3), TerrainKind.Wall);
                    int removed = cache.Invalidate(new CellCoord(7, 3));
                    Assert.Equal(1, removed); // 直路失效，恰删一条

                    PathQueryResult detour = cache.GetPath(porter.CurrentCell, new CellCoord(14, 3));
                    Assert.Equal(PathQueryStatus.Found, detour.Status);
                    Assert.DoesNotContain(new CellCoord(7, 3), detour.Cells); // 绕开墙
                    porter.Reroute(detour.Cells);
                }

                porter.Tick();
                if (arrivalTick < 0 && porter.Phase == CarrierPhase.Unloading)
                {
                    arrivalTick = tick;
                }

                if (deliveryTick < 0 && porter.DeliveredItems > 0)
                {
                    deliveryTick = tick;
                    return (arrivalTick, deliveryTick);
                }
            }

            return (arrivalTick, deliveryTick);
        }

        (int arrival, int delivery) = RunScenario();
        (int arrivalReplay, int deliveryReplay) = RunScenario();

        Assert.True(arrival > 0, "Porter never reached the destination within 500 ticks.");
        Assert.True(delivery > 0, "Porter never delivered within 500 ticks.");
        Assert.True(arrival > 244, "Detour must be longer than the straight road (arrival at t=244)."); // 直路手算到达 t=20+224=244
        Assert.Equal((arrival, delivery), (arrivalReplay, deliveryReplay)); // 改道重放全等
    }

    [Fact]
    public void ForestCellOnARealWorldSteersTheRouteAroundIt()
    {
        // [F-040] 真世界代价集成：9×3 平地中点 (4,1) 改森林（代价 2）→
        // 穿心 90（10×(1+1+1+2+1+1+1+1)）> 绕上 88（14+5×10+14+10）→ 绕行
        var world = LogisticsWorlds.PlainWorld(9, 3, Seed);
        world.SetTerrain(new CellCoord(4, 1), TerrainKind.Forest);

        var pathfinder = new AStarPathfinder(world);
        PathQueryResult route = pathfinder.FindPath(new CellCoord(0, 1), new CellCoord(8, 1));

        Assert.Equal(PathQueryStatus.Found, route.Status);
        Assert.Equal(88L, route.Cost);
        Assert.DoesNotContain(new CellCoord(4, 1), route.Cells);
    }
}
