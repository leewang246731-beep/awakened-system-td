using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Tests.Pathfinding;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests for the porter-tier carrier (C-07 precursor, T-047,
/// F-041/F-042): load window discipline, movement along a real A* route,
/// unload-only-on-accept (待卸队列: refused → queued, partial → remainder),
/// supply starvation, mid-trip reroute validation, and the tier anchor —
/// 0.3 件/s throughput on the reference route, verified by full simulation
/// against the hand-computed 200-tick round trip (往返周期手算对照模拟).
/// </summary>
// [F-041][F-042] 民夫档：容量 2 件/趟、accept 才卸、0.3 件/s 吞吐断言
public class CarrierTests
{
    private static readonly ResourceKey Arrows = new("core:arrows");

    /// <summary>Real A* route (0,0)→(5,0) on a 1×6 corridor: 5 straight segments = 160 px.</summary>
    private static IReadOnlyList<CellCoord> ReferenceRoute()
    {
        var pathfinder = new AStarPathfinder(new MapGrid(6, 1));
        PathQueryResult result = pathfinder.FindPath(new CellCoord(0, 0), new CellCoord(5, 0));
        Assert.Equal(PathQueryStatus.Found, result.Status);
        return result.Cells;
    }

    private static Carrier NewPorter(StockSupplier supplier, GateReceiver receiver, IReadOnlyList<CellCoord>? route = null) =>
        new(new EntityId(1), Arrows, supplier, receiver, route ?? ReferenceRoute());

    private static void TickMany(Carrier carrier, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            carrier.Tick();
        }
    }

    // ------------------------------------------------------------ tier anchors

    [Fact]
    public void TierConstantsAndRoundTripHandCalcAgree()
    {
        // [F-041] 档位锚点：容量 2 件（1 件=10 发，v3.2 §5）；参考路线单程
        // 5 格 = 160 px；往返 = 2×160÷2.0 px/tick + 20 装 + 20 卸 = 200 tick；
        // 吞吐 = 2 件 ÷ (200/30 s) = 0.3 件/s ——物流阶梯第 1 档
        Assert.Equal(2, Carrier.CapacityItems);
        Assert.Equal(10, Carrier.ShotsPerItem);
        Assert.Equal(160.0, Carrier.RouteLengthPx(ReferenceRoute()), precision: 9);
        Assert.Equal(200.0, Carrier.RoundTripTicks(Carrier.ReferenceRouteOneWayPx), precision: 9);

        double throughput = Carrier.CapacityItems / (Carrier.RoundTripTicks(Carrier.ReferenceRouteOneWayPx) / Carrier.TicksPerSecond);
        Assert.Equal(Carrier.PorterThroughputItemsPerSecond, throughput, precision: 9);
    }

    // ------------------------------------------------------------ loading / travel

    [Fact]
    public void PorterLoadsTwoItemsAfterTheLoadWindow()
    {
        // [F-042] 装货窗口：前 19 tick 仍在装（0 件），第 20 tick 完成装 2 件并出发
        var supplier = new StockSupplier(int.MaxValue);
        var porter = NewPorter(supplier, new GateReceiver(int.MaxValue));

        TickMany(porter, 19);
        Assert.Equal(CarrierPhase.Loading, porter.Phase);
        Assert.Equal(0, porter.CargoItems);

        TickMany(porter, 1);
        Assert.Equal(CarrierPhase.Outbound, porter.Phase);
        Assert.Equal(2, porter.CargoItems);
        Assert.Equal(new CellCoord(0, 0), porter.CurrentCell);
        Assert.Single(supplier.Requests);
        Assert.Equal((Arrows, Carrier.CapacityItems), supplier.Requests[0]);
    }

    [Fact]
    public void ArrivalAndUnloadHappenOnTheHandComputedTicks()
    {
        // [F-042] 手算对照：出发 t=20，行程 160 px ÷ 2 px/tick = 80 tick →
        // t=100 到达入卸货态；卸货窗口 20 tick → t=120 accept 2 件并返程
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porter = NewPorter(supplier, receiver);

        TickMany(porter, 100);
        Assert.Equal(CarrierPhase.Unloading, porter.Phase);
        Assert.True(porter.IsUnloadQueued);
        Assert.Equal(new CellCoord(5, 0), porter.CurrentCell);
        Assert.Equal(2, porter.CargoItems);
        Assert.Equal(0L, porter.DeliveredItems);
        Assert.Empty(receiver.AcceptCalls);

        TickMany(porter, 20);
        Assert.Equal(CarrierPhase.Inbound, porter.Phase);
        Assert.Equal(2L, porter.DeliveredItems);
        Assert.Equal(0, porter.CargoItems);
        Assert.Single(receiver.AcceptCalls);
        Assert.Equal((Arrows, 2), receiver.AcceptCalls[0]);
    }

    [Fact]
    public void ReturnTripReachesHomeAndTheCycleRepeats()
    {
        // [F-042] 返程走反向路：t=120 返程 80 tick → t=200 到家装货，
        // t=220 再次出发（周期 200 tick）
        var porter = NewPorter(new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue));

        TickMany(porter, 200);
        Assert.Equal(CarrierPhase.Loading, porter.Phase);
        Assert.Equal(new CellCoord(0, 0), porter.CurrentCell);
        Assert.Equal(porter.Home, porter.CurrentCell);

        TickMany(porter, 20);
        Assert.Equal(CarrierPhase.Outbound, porter.Phase);
        Assert.Equal(2, porter.CargoItems);
    }

    // ------------------------------------------------------------ unload queue

    [Fact]
    public void RefusedUnloadKeepsThePorterQueuedUntilAccepted()
    {
        // [F-042] 待卸队列（负向）：目的建筑 accept=false → t=120 不卸、仍排队；
        // 之后放行 → 下一窗口 t=140 才卸
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(0); // 满仓拒收
        var porter = NewPorter(supplier, receiver);

        TickMany(porter, 120);
        Assert.Equal(CarrierPhase.Unloading, porter.Phase);
        Assert.True(porter.IsUnloadQueued);
        Assert.Equal(2, porter.CargoItems);
        Assert.Equal(0L, porter.DeliveredItems);
        Assert.Single(receiver.AcceptCalls); // 拒收也真调用了 accept（可观测）

        receiver.RemainingCapacity = 2; // 目的建筑腾出容量
        TickMany(porter, 20);
        Assert.Equal(CarrierPhase.Inbound, porter.Phase);
        Assert.Equal(2L, porter.DeliveredItems);
        Assert.Equal(2, receiver.AcceptCalls.Count);
    }

    [Fact]
    public void PartialAcceptServesTheRemainderNextWindow()
    {
        // [F-042] 部分接收：只收 1 件 → t=120 交 1 件余 1 件仍在队列，
        // t=140 交清返程
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(1);
        var porter = NewPorter(supplier, receiver);

        TickMany(porter, 120);
        Assert.Equal(CarrierPhase.Unloading, porter.Phase);
        Assert.Equal(1L, porter.DeliveredItems);
        Assert.Equal(1, porter.CargoItems);

        receiver.RemainingCapacity = 1;
        TickMany(porter, 20);
        Assert.Equal(CarrierPhase.Inbound, porter.Phase);
        Assert.Equal(2L, porter.DeliveredItems);
        Assert.Equal(0, porter.CargoItems);
    }

    // ------------------------------------------------------------ supply starvation

    [Fact]
    public void StarvedSupplierDelaysDeparture()
    {
        // [F-042] 断供等待（T-048 前向钩子）：无货 → 每窗口重试，留在原地
        var supplier = new StockSupplier(0);
        var porter = NewPorter(supplier, new GateReceiver(int.MaxValue));

        TickMany(porter, 59);
        Assert.Equal(CarrierPhase.Loading, porter.Phase);
        Assert.Equal(new CellCoord(0, 0), porter.CurrentCell);
        Assert.Equal(2, supplier.Requests.Count); // 两个窗口各真取了一次（都空）

        supplier.Stock = 2;
        TickMany(porter, 1);
        Assert.Equal(CarrierPhase.Outbound, porter.Phase);
        Assert.Equal(2, porter.CargoItems);
    }

    // ------------------------------------------------------------ throughput

    [Fact]
    public void ReferenceRouteThroughputIsThreeTenthsItemsPerSecond()
    {
        // [F-041] 0.3 件/s ±5% 模拟断言：参考路线（单程 160 px）满载往返，
        // 周期 = 200 tick（t=120/320/520 交付 2 件）→ 600 tick 共 6 件 =
        // 6 ÷ 20 s = 0.300 件/s ∈ [0.285, 0.315]
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porter = NewPorter(supplier, receiver);

        var deliveryTicks = new List<int>();
        for (int tick = 1; tick <= 600; tick++)
        {
            porter.Tick();
            if (porter.DeliveredItems > deliveryTicks.Count * (long)Carrier.CapacityItems)
            {
                deliveryTicks.Add(tick);
            }
        }

        Assert.Equal(new[] { 120, 320, 520 }, deliveryTicks); // 周期恰 200 tick
        Assert.Equal(6L, porter.DeliveredItems);

        double measured = porter.DeliveredItems / (600.0 / Carrier.TicksPerSecond);
        Assert.InRange(
            measured,
            Carrier.PorterThroughputItemsPerSecond * 0.95,
            Carrier.PorterThroughputItemsPerSecond * 1.05); // ±5%（任务卡口径）
        Assert.Equal(200, deliveryTicks[1] - deliveryTicks[0]); // 与 RoundTripTicks(160) 手算一致
        Assert.Equal(Carrier.RoundTripTicks(Carrier.RouteLengthPx(ReferenceRoute())), deliveryTicks[1] - deliveryTicks[0], precision: 6);
    }

    // ------------------------------------------------------------ reroute

    [Fact]
    public void RerouteCarriesSegmentProgressAndDelaysArrivalExactly()
    {
        // [F-040][F-042] 改道：5×5 平地路线 (0,2)→(4,2)（128 px）；t=60 已走
        // 40 个移动 tick ×2 px = 80 px → CurrentCell=(2,2)、段内 16 px。
        // 改道绕上路 4 段直行（128 px）：剩余 = 128−16 = 112 px = 56 tick →
        // t=116 到达、t=136 卸毕 ——比原直路（t=84 到达：余 48 px=24 tick）
        // 恰晚 32 tick（多绕 64 px）
        var grid = new MapGrid(5, 5);
        var pathfinder = new AStarPathfinder(grid);
        PathQueryResult direct = pathfinder.FindPath(new CellCoord(0, 2), new CellCoord(4, 2));
        Assert.Equal(PathQueryStatus.Found, direct.Status);
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porter = new Carrier(new EntityId(7), Arrows, supplier, receiver, direct.Cells);

        TickMany(porter, 60);
        Assert.Equal(CarrierPhase.Outbound, porter.Phase);
        Assert.Equal(new CellCoord(2, 2), porter.CurrentCell);

        var detour = new List<CellCoord>
        {
            new(2, 2), new(2, 1), new(3, 1), new(3, 2), new(4, 2),
        };
        porter.Reroute(detour);

        TickMany(porter, 56);
        Assert.Equal(CarrierPhase.Unloading, porter.Phase); // t=116 到达
        Assert.Equal(new CellCoord(4, 2), porter.CurrentCell);

        TickMany(porter, 20);
        Assert.Equal(CarrierPhase.Inbound, porter.Phase); // t=136 卸毕
        Assert.Equal(2L, porter.DeliveredItems);
    }

    [Theory]
    [InlineData("wrong-start")]   // illegal: 不从 CurrentCell 出发
    [InlineData("wrong-target")]  // illegal: 不止于当前阶段目标
    [InlineData("single-cell")]   // illegal: 单格路线
    [InlineData("not-moving")]    // illegal: 装货阶段改道
    public void RerouteRejectsMalformedRoutes(string site)
    {
        // [F-040] 改道守卫：错误起点/错误终点/单格/非移动阶段 → 确实拒绝。
        // t=60 时民夫已走 80 px → CurrentCell=(2,0)
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porter = NewPorter(supplier, receiver);

        switch (site)
        {
            case "not-moving":
                TickMany(porter, 5); // 仍在装货
                Assert.Throws<InvalidOperationException>(() => porter.Reroute(new[] { new CellCoord(0, 0), new CellCoord(1, 0) }));
                break;
            case "wrong-start":
                TickMany(porter, 60);
                Assert.Throws<InvalidOperationException>(
                    () => porter.Reroute(new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(2, 0), new CellCoord(3, 0), new CellCoord(4, 0), new CellCoord(5, 0) }));
                break;
            case "wrong-target":
                TickMany(porter, 60);
                Assert.Throws<InvalidOperationException>(
                    () => porter.Reroute(new[] { new CellCoord(2, 0), new CellCoord(3, 0), new CellCoord(3, 1) }));
                break;
            default:
                TickMany(porter, 60);
                Assert.Throws<ArgumentException>(() => porter.Reroute(new[] { new CellCoord(2, 0) }));
                break;
        }
    }

    [Fact]
    public void MalformedConstructorRoutesAreRejected()
    {
        // [F-042] 非法：单格路线/跳格路线/null 端口 → 构造即拒
        var supplier = new StockSupplier(1);
        var receiver = new GateReceiver(1);
        Assert.Throws<ArgumentException>(() => NewPorter(supplier, receiver, new[] { new CellCoord(0, 0) }));
        Assert.Throws<ArgumentException>(
            () => NewPorter(supplier, receiver, new[] { new CellCoord(0, 0), new CellCoord(2, 0) }));
        Assert.Throws<ArgumentNullException>(
            () => new Carrier(new EntityId(1), Arrows, null!, receiver, ReferenceRoute()));
    }
}
