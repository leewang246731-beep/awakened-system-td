using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the teleport link (C-07 全量, T-064, [F-041]/[F-042]):
/// single-call bulk transfer (the ∞ tier — no travel time, any amount in
/// one call), energy as the sole limiter (starved port ⇒ partial move,
/// exact accounting delivered × price), refused destinations buffering the
/// stock for a later call (tier-6 待卸), and argument discipline.
/// </summary>
// [F-041][F-042] 传送阵档（瞬时+能量成本钩子；吞吐 ∞ 的唯一限流器=能量）
public class TeleporterLinkTests
{
    private static readonly ResourceKey Cores = new("core:cores");

    /// <summary>Mutable in-memory energy port (test seam of ITeleportEnergyPort).</summary>
    private sealed class EnergyTank : ITeleportEnergyPort
    {
        public EnergyTank(int units) => Available = units;

        public int Available { get; set; }

        public long DrawnTotal { get; private set; }

        public int DrawCalls { get; private set; }

        public void Draw(int units)
        {
            Guard.EnsureInRange(units, 0, Available, nameof(units));
            Available -= units;
            DrawnTotal += units;
            DrawCalls++;
        }
    }

    // ------------------------------------------------------------ ∞ throughput

    [Fact]
    public void OneCallMovesAThousandItemsInstantlyAndPaysExactEnergy()
    {
        // [F-041] ∞ 档：单次调用瞬时搬 1000 件（零行程零服务窗），能量按
        // 5/件 精确扣 5000 —— 吞吐唯一限流器是能量，不是时间
        var tank = new EnergyTank(10_000);
        var link = new TeleporterLink(tank);
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);

        int moved = link.Transfer(supplier, receiver, Cores, 1000);

        Assert.Equal(1000, moved);
        Assert.Equal(1000L, link.DeliveredItems);
        Assert.Equal(0, link.BufferedItems);
        Assert.Equal(5000L, link.EnergySpent);
        Assert.Equal(5000, tank.DrawnTotal);
        Assert.Equal(1L, link.TransferCalls); // 一次调用=一整趟
    }

    [Fact]
    public void EnergyStarvedPortMovesOnlyTheAffordableAmount()
    {
        // [F-042] 能量限流（负向）：库存 1000 件、能量只够 300 件 → 本调用
        // 恰搬 300 件，其余留在缓冲（不丢不重复计费），下次补给后再搬
        var tank = new EnergyTank(1500); // 1500 ÷ 5 = 300 件
        var link = new TeleporterLink(tank);
        var supplier = new StockSupplier(1000);
        var receiver = new GateReceiver(int.MaxValue);

        int moved = link.Transfer(supplier, receiver, Cores, 1000);

        Assert.Equal(300, moved);
        Assert.Equal(300L, link.DeliveredItems);
        Assert.Equal(700, link.BufferedItems);
        Assert.Equal(1500L, link.EnergySpent);
        Assert.Equal(0, tank.Available); // 端口被抽干

        tank.Available = 3500; // 补给 700 件的能量
        int retried = link.Transfer(supplier, receiver, Cores, 0); // 不再取新货，只清缓冲
        Assert.Equal(700, retried);
        Assert.Equal(0, link.BufferedItems);
        Assert.Equal(1000L, link.DeliveredItems);
        Assert.Equal(5000L, link.EnergySpent);
    }

    [Fact]
    public void EmptyPortMovesNothingAndLosesNothing()
    {
        // [F-042] 边界：零能量 → 零交付零计费，缓冲原样
        var link = new TeleporterLink(new EnergyTank(0));
        var supplier = new StockSupplier(50);
        var receiver = new GateReceiver(int.MaxValue);

        Assert.Equal(0, link.Transfer(supplier, receiver, Cores, 50));
        Assert.Equal(0L, link.DeliveredItems);
        Assert.Equal(50, link.BufferedItems); // 货已取入缓冲，未丢
        Assert.Equal(0L, link.EnergySpent);
    }

    // ------------------------------------------------------------ destination backpressure

    [Fact]
    public void RefusingDestinationBuffersStockAndDeliversWhenReopened()
    {
        // [F-042] 第 6 档待卸语义：目的端只收 40 → 交 40 缓 960（能量只按
        // 已交件数扣），放行后下一调用清缓冲
        var link = new TeleporterLink(new EnergyTank(int.MaxValue / 8));
        var supplier = new StockSupplier(1000);
        var receiver = new GateReceiver(40);

        int first = link.Transfer(supplier, receiver, Cores, 1000);

        Assert.Equal(40, first);
        Assert.Equal(960, link.BufferedItems);
        Assert.Equal(40L * TeleporterLink.EnergyCostPerItem, link.EnergySpent);
        Assert.Single(receiver.AcceptCalls); // 拒收也真调用了 accept（可观测）

        receiver.RemainingCapacity = int.MaxValue;
        int second = link.Transfer(supplier, receiver, Cores, 0);
        Assert.Equal(960, second);
        Assert.Equal(1000L, link.DeliveredItems);
        Assert.Equal(0, link.BufferedItems);
    }

    [Fact]
    public void EnergyAccountingInvariantHoldsAcrossMixedCalls()
    {
        // [F-042] 记账不变式：EnergySpent == DeliveredItems × EnergyCostPerItem
        // 在任意调用序列下成立（部分拒收/能量短缺/空调用混合）
        var tank = new EnergyTank(1000);
        var link = new TeleporterLink(tank);
        var supplier = new StockSupplier(400);
        var receiver = new GateReceiver(7);

        for (int call = 1; call <= 12; call++)
        {
            link.Transfer(supplier, receiver, Cores, 25 * call);
            Assert.Equal(link.DeliveredItems * TeleporterLink.EnergyCostPerItem, link.EnergySpent);
            Assert.Equal(link.EnergySpent, tank.DrawnTotal);
        }

        Assert.True(link.DeliveredItems > 0);
        Assert.True(link.BufferedItems > 0);
    }

    // ------------------------------------------------------------ argument discipline

    [Theory] // legal / boundary / illegal
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void TransferValidatesMaxItems(int maxItems)
    {
        // [F-042] 合法 0/1；负数确实被拒（ArgumentOutOfRangeException）
        var link = new TeleporterLink(new EnergyTank(100));
        var supplier = new StockSupplier(10);
        var receiver = new GateReceiver(int.MaxValue);

        if (maxItems < 0)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => link.Transfer(supplier, receiver, Cores, maxItems));
        }
        else
        {
            int moved = link.Transfer(supplier, receiver, Cores, maxItems);
            Assert.Equal(maxItems, moved);
        }
    }

    [Fact] // illegal
    public void NullArgumentsAreRejected()
    {
        // [F-042] 负向：null 供应端/接收端/能量端口确实被拒
        var link = new TeleporterLink(new EnergyTank(100));
        var supplier = new StockSupplier(10);
        var receiver = new GateReceiver(int.MaxValue);

        Assert.Throws<ArgumentNullException>(() => link.Transfer(null!, receiver, Cores, 1));
        Assert.Throws<ArgumentNullException>(() => link.Transfer(supplier, null!, Cores, 1));
        Assert.Throws<ArgumentNullException>(() => new TeleporterLink(null!));
    }
}
