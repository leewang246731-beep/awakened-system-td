using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the conveyor belt (C-07 全量, T-064, [F-041]/[F-042]):
/// advance cadence (one cell per 2 ticks), head pull / tail accept, the
/// backpressure stall when the destination refuses (cargo only ever leaves
/// on accept), starved-head run-on, defensive slot snapshots, and the
/// 15 件/s steady-state throughput measured over a window (formula − fill
/// latency).
/// </summary>
// [F-041][F-042] 传送带档（格到格自动流 15 件/s，计数流薄版）
public class ConveyorBeltTests
{
    private static readonly ResourceKey Coal = new("core:coal");

    private static CellCoord[] StraightBelt(int cells = 5) =>
        Enumerable.Range(0, cells).Select(x => new CellCoord(x, 0)).ToArray();

    // ------------------------------------------------------------ construction

    [Fact]
    public void ConstructionFreezesTheChainAndTheBeltTier()
    {
        // [F-042] 合法：链条=有序相邻格；档位=传送带档（15 件/s，2 tick/格）
        var belt = new ConveyorBelt(StraightBelt(), Coal, new StockSupplier(0), new GateReceiver(0));

        Assert.Equal(5, belt.Cells.Count);
        Assert.Same(TransportTiers.Belt, belt.Tier);
        Assert.Equal(15.0, belt.ThroughputItemsPerSecond, 9);
        Assert.Equal(0, belt.ItemsOnBelt);
    }

    [Theory] // illegal
    [InlineData("single-cell")]
    [InlineData("gap")]
    [InlineData("wrong-tier")]
    public void ConstructionRejectsMalformedChains(string site)
    {
        // [F-042] 非法：单格/跳格链/非传送带档位 → 构造即拒
        var supplier = new StockSupplier(0);
        var receiver = new GateReceiver(0);
        switch (site)
        {
            case "single-cell":
                Assert.Throws<ArgumentException>(() => new ConveyorBelt(new[] { new CellCoord(0, 0) }, Coal, supplier, receiver));
                break;
            case "gap":
                Assert.Throws<ArgumentException>(
                    () => new ConveyorBelt(new[] { new CellCoord(0, 0), new CellCoord(2, 0) }, Coal, supplier, receiver));
                break;
            default:
                Assert.Throws<ArgumentException>(
                    () => new ConveyorBelt(StraightBelt(), Coal, supplier, receiver, TransportTiers.Porter));
                break;
        }
    }

    [Fact] // illegal
    public void ConstructionRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ConveyorBelt(null!, Coal, new StockSupplier(0), new GateReceiver(0)));
        Assert.Throws<ArgumentNullException>(
            () => new ConveyorBelt(StraightBelt(), Coal, null!, new GateReceiver(0)));
    }

    // ------------------------------------------------------------ flow + backpressure

    [Fact]
    public void BeltAdvancesOneCellEveryTwoTicksAndDeliversAfterTheFillLatency()
    {
        // [F-042] 计数流：2 tick 推进一格；5 格链装填延迟 = 5×2 = 10 tick 后
        // 尾部到达，t=12 首交；此后每 2 tick 交 1 件（0.5 件/tick）
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var belt = new ConveyorBelt(StraightBelt(), Coal, supplier, receiver);

        for (int tick = 1; tick <= 10; tick++)
        {
            belt.Tick();
            Assert.Equal(0L, belt.DeliveredItems); // 装填期未到尾
            if (tick % 2 == 0)
            {
                Assert.Equal(tick / 2, belt.ItemsOnBelt); // 每 2 tick 装满一格
            }
        }

        belt.Tick();
        belt.Tick(); // t=12：首件出带
        Assert.Equal(1L, belt.DeliveredItems);
        Assert.Equal(5, belt.ItemsOnBelt); // 链满后稳态在带量

        belt.Tick();
        belt.Tick(); // t=14
        Assert.Equal(2L, belt.DeliveredItems);
        Assert.Equal(5, belt.ItemsOnBelt);
    }

    [Fact]
    public void RefusingDestinationStallsTheChainUntilItAcceptsAgain()
    {
        // [F-042] 背压（负向）：目的端拒收 → 链停在尾部、余量原地、StalledTicks
        // 增长；放行后下一推进周期补交，货只经 accept 离带
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(0); // 满仓拒收
        var belt = new ConveyorBelt(StraightBelt(), Coal, supplier, receiver);

        for (int tick = 1; tick <= 12; tick++)
        {
            belt.Tick();
        }

        Assert.Equal(0L, belt.DeliveredItems);
        Assert.Equal(5, belt.ItemsOnBelt); // 全链满，尾部被拒
        Assert.True(belt.StalledTicks > 0);

        long stalledAt = belt.StalledTicks;
        for (int tick = 0; tick < 6; tick++)
        {
            belt.Tick();
        }

        Assert.Equal(0L, belt.DeliveredItems); // 持续拒收：零交付
        Assert.Equal(stalledAt + 3, belt.StalledTicks); // 每 2 tick 一次停带判定

        receiver.RemainingCapacity = 3;
        belt.Tick();
        belt.Tick(); // 放行后的首个推进周期：尾部交出
        Assert.Equal(1L, belt.DeliveredItems);
        Assert.Equal(5, belt.ItemsOnBelt); // 链恢复流动，首格又拉了新货
    }

    [Fact]
    public void StarvedHeadRunsTheChainOnEmpty()
    {
        // [F-042] 断供：源头空 → 头格拉 0、链照常推进（不停带），在带量逐格
        // 排空后稳态为 0
        var supplier = new StockSupplier(0);
        var receiver = new GateReceiver(int.MaxValue);
        var belt = new ConveyorBelt(StraightBelt(), Coal, supplier, receiver);

        for (int tick = 1; tick <= 30; tick++)
        {
            belt.Tick();
        }

        Assert.Equal(0L, belt.DeliveredItems);
        Assert.Equal(0, belt.ItemsOnBelt);
        Assert.Equal(0L, belt.StalledTicks); // 断供≠背压：不停带
        Assert.Equal(15, supplier.Requests.Count); // 每 2 tick 真拉了一次
    }

    [Fact]
    public void SlotSnapshotIsADefensiveCopy()
    {
        // [F-042] 防御性拷贝：外部改快照不影响带内槽位
        var belt = new ConveyorBelt(StraightBelt(), Coal, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue));
        for (int tick = 1; tick <= 4; tick++)
        {
            belt.Tick();
        }

        IReadOnlyList<int> snapshot = belt.SlotSnapshot;
        Assert.Equal(new[] { 1, 1, 0, 0, 0 }, snapshot);
        var mutable = (int[])snapshot;
        mutable[0] = 99;
        Assert.Equal(1, belt.SlotSnapshot[0]);
    }

    // ------------------------------------------------------------ throughput anchor

    [Fact]
    public void SteadyStateThroughputIsFifteenItemsPerSecond()
    {
        // [F-041] 15 件/s ±5% 实测：2012 tick 窗口（1006 个推进周期）；首件
        // t=2 入带、t=12 首交（5 格装填延迟 = 10 tick），此后每周期交 1 件 ⇒
        // 交付 = 1006 − 5 = 1001 件；1001 ÷ (2012/30 s) = 14.94 件/s ∈ [14.25, 15.75]
        var belt = new ConveyorBelt(
            StraightBelt(), Coal, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue));

        const int windowTicks = 2012;
        for (int tick = 1; tick <= windowTicks; tick++)
        {
            belt.Tick();
        }

        Assert.Equal(1001L, belt.DeliveredItems); // 精确计数（整数算术确定性）
        double measured = belt.DeliveredItems / (windowTicks / (double)Carrier.TicksPerSecond);
        Assert.InRange(
            measured,
            TransportTiers.Belt.ThroughputAnchorItemsPerSecond * (1 - TransportTiers.AnchorTolerance),
            TransportTiers.Belt.ThroughputAnchorItemsPerSecond * (1 + TransportTiers.AnchorTolerance));
    }
}
