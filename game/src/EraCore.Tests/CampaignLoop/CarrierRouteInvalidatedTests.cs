using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Tests.Logistics;
using Xunit;

namespace EraCore.Tests.CampaignLoop;

// 正式"路线失效→重算"事件（T-059 DEBT ②，spike2-report §5 pit#5）：
// RouteInvalidated 只在相位过渡进移动相（Loading→Outbound / Unloading→
// Inbound）触发，编排器（fake 订阅者=本测试）在事件内同步 Reroute 可用且
// 不破坏交付——三行 legal/boundary/illegal。[C-12][F-042]
public class CarrierRouteInvalidatedTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");

    /// <summary>Straight 3-cell route (0,0)→(2,0) and a detour via row 1 of the same endpoints.</summary>
    private static readonly CellCoord[] StraightRoute =
    {
        new(0, 0), new(1, 0), new(2, 0),
    };

    private static readonly CellCoord[] DetourRoute =
    {
        new(0, 0), new(0, 1), new(1, 1), new(2, 1), new(2, 0),
    };

    [Fact] // legal — 事件恰在两次相位过渡触发（Outbound/Inbound 各一），字段=出发格+相位目标
    public void Event_fires_exactly_on_movement_phase_transitions()
    {
        var supplier = new StockSupplier(10);
        var receiver = new GateReceiver(int.MaxValue);
        Carrier carrier = NewCarrier(supplier, receiver);
        var events = new List<RouteInvalidatedEvent>();
        carrier.RouteInvalidated += events.Add;

        // Loading 服务窗 20 tick 内：无事件（服务窗中的民夫保持队列语义）。
        carrier.Tick();
        for (int i = 0; i < Carrier.LoadTicks - 2; i++)
        {
            carrier.Tick();
        }

        Assert.Empty(events);
        Assert.Equal(CarrierPhase.Loading, carrier.Phase);

        carrier.Tick(); // 第 20 tick：装载完成 → Loading→Outbound 过渡
        Assert.Single(events);
        Assert.Equal(CarrierPhase.Outbound, events[0].Phase);
        Assert.Equal(carrier.Home, events[0].DepartureCell);
        Assert.Equal(carrier.Destination, events[0].TargetCell);
        Assert.Same(carrier, events[0].Carrier);
        Assert.Equal(CarrierPhase.Outbound, carrier.Phase);

        // 走完去程（160 px ÷ 2 px/tick = 80 tick）+ 卸载窗 20 tick →
        // Unloading→Inbound 过渡触发第二个事件。
        while (carrier.Phase != CarrierPhase.Inbound && carrier.TicksAlive < 1_000)
        {
            carrier.Tick();
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(CarrierPhase.Inbound, events[1].Phase);
        Assert.Equal(carrier.Destination, events[1].DepartureCell);
        Assert.Equal(carrier.Home, events[1].TargetCell);
        Assert.True(carrier.DeliveredItems >= 1, "去程交付应已入账");
    }

    [Fact] // legal — 编排器在事件内同步 Reroute：改道成功且交付/往返不受影响
    public void Orchestrator_can_reroute_synchronously_inside_the_event()
    {
        var supplier = new StockSupplier(10);
        var receiver = new GateReceiver(int.MaxValue);
        Carrier carrier = NewCarrier(supplier, receiver);
        int reroutes = 0;
        carrier.RouteInvalidated += routeEvent =>
        {
            if (routeEvent.Phase == CarrierPhase.Outbound && reroutes == 0)
            {
                // 首次出发时"墙事件"使直线旧路失效 → 编排器主动改道（pit#5 语义）。
                carrier.Reroute(DetourRoute);
                reroutes++;
            }
        };

        while (carrier.DeliveredItems < 2 && carrier.TicksAlive < 2_000)
        {
            carrier.Tick(); // 改道德（4 段 × 32 或 32√2 px）内完成两件交付
        }

        Assert.Equal(1, reroutes);
        Assert.Equal(2, carrier.DeliveredItems);
        Assert.Equal(8, supplier.Stock); // 10 − 一趟 2 件（cargo 只在 accept 时离手）
        Assert.Contains(receiver.AcceptCalls, call => call.Resource.Equals(Wood));
    }

    // ---------------------------------------------------------- boundary

    [Fact] // boundary — 断供的民夫停在 Loading 重试：不触发任何失效事件
    public void Starved_carrier_never_emits_while_waiting_in_loading()
    {
        var supplier = new StockSupplier(0); // 断供
        Carrier carrier = NewCarrier(supplier, new GateReceiver(int.MaxValue));
        var events = new List<RouteInvalidatedEvent>();
        carrier.RouteInvalidated += events.Add;

        for (int i = 0; i < Carrier.LoadTicks * 5; i++) // 五个完整装载窗
        {
            carrier.Tick();
        }

        Assert.Empty(events);
        Assert.Equal(CarrierPhase.Loading, carrier.Phase);
        Assert.Equal(0, carrier.CargoItems);
    }

    [Fact] // boundary — 无订阅者时事件零成本：正常往返不受影响（空委托调用链）
    public void Unsubscribed_carrier_runs_an_ordinary_round_trip()
    {
        Carrier carrier = NewCarrier(new StockSupplier(4), new GateReceiver(int.MaxValue));
        while (carrier.DeliveredItems < 2 && carrier.TicksAlive < 2_000)
        {
            carrier.Tick();
        }

        Assert.Equal(2, carrier.DeliveredItems);
        Assert.Equal(CarrierPhase.Inbound, carrier.Phase);
    }

    // ---------------------------------------------------------- illegal

    [Fact] // illegal — 服务窗中直接 Reroute 被拒（事件是唯一的相位锁改道时机）
    public void Reroute_outside_movement_phases_is_rejected()
    {
        Carrier carrier = NewCarrier(new StockSupplier(10), new GateReceiver(int.MaxValue));
        Assert.Equal(CarrierPhase.Loading, carrier.Phase);

        Assert.Throws<InvalidOperationException>(() => carrier.Reroute(DetourRoute));
    }

    [Fact] // illegal — 事件处理器内错起点/错终点的改道确实被拒（Reroute 既有守卫不放松）
    public void Handler_reroute_with_wrong_endpoints_is_rejected()
    {
        Carrier carrier = NewCarrier(new StockSupplier(10), new GateReceiver(int.MaxValue));
        var badOrigin = new CellCoord[] { new(1, 0), new(1, 1), new(2, 1), new(2, 0) };
        carrier.RouteInvalidated += _ =>
        {
            if (carrier.Phase == CarrierPhase.Outbound)
            {
                Assert.Throws<InvalidOperationException>(() => carrier.Reroute(badOrigin));
            }
        };

        for (int i = 0; i < Carrier.LoadTicks + 5 && carrier.TicksAlive < 100; i++)
        {
            carrier.Tick(); // 跨过装载→出发过渡，触发守卫断言
        }

        Assert.Equal(CarrierPhase.Outbound, carrier.Phase);
    }

    private static Carrier NewCarrier(StockSupplier supplier, GateReceiver receiver) => new(
        new EntityId(Simulation.CarrierEntityBase + 1),
        Wood,
        supplier,
        receiver,
        StraightRoute);
}
