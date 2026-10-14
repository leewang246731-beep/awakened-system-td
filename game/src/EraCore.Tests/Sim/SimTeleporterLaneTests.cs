using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Logistics;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-042][F-155] 传送阵能量钩单元（T-067）：账本满足度→整数能量银行的
// 折算、银行跨 tick 结转、扣能边界与非法输入。Transfer 的 ∞/限流语义已由
// TeleporterLinkTests 覆盖，此处钉 Sim 侧适配器语义。
public class SimTeleporterLaneTests
{
    private sealed class Supplier : ICargoSupplier
    {
        private int _stock;
        public Supplier(int stock) => _stock = stock;

        public int Stock => _stock;

        public int TryTake(ResourceKey resource, int amount)
        {
            int taken = Math.Min(_stock, amount);
            _stock -= taken;
            return taken;
        }
    }

    private sealed class Receiver : ICargoReceiver
    {
        public int Accepted { get; private set; }
        public int TryAccept(ResourceKey resource, int amount) { Accepted += amount; return amount; }
    }

    private static EnergyLedger LedgerWithCamps(int camps)
    {
        var clock = new SimClock();
        var ledger = new EnergyLedger(clock);
        for (int i = 0; i < camps; i++)
        {
            ledger.RegisterProducer(new EnergyNodeId(100 + i), EnergyNodeKind.LaborCamp);
        }

        return ledger;
    }

    private static SimTeleporterLane Lane(EnergyLedger ledger, double demand, Supplier source, Receiver destination) =>
        new(new EntityId(600_000), new EnergyNodeId(SimTeleporterLane.NodeIdBase), new ResourceKey("t:res"),
            source, destination, demand, ledger);

    [Fact] // legal — 满足度折算：floor(satisfaction×demand÷30) 整数银行入账
    public void SettleEnergy_credits_the_floored_tick_share()
    {
        // [F-042] 满足度折算
        EnergyLedger ledger = LedgerWithCamps(6); // 60/s 供给
        var source = new Supplier(0);
        var receiver = new Receiver();
        SimTeleporterLane lane = Lane(ledger, demand: 60.0, source, receiver); // 满供
        ledger.Settle(); // 消费节点已注册后结算（编排时序：命令相→结算相→能量折算相）

        lane.SettleEnergy(ledger, 30);
        Assert.Equal(2, lane.Port.Available); // floor(1.0×60/30)=2

        lane.SettleEnergy(ledger, 30);
        Assert.Equal(4, lane.Port.Available); // 银行结转
    }

    [Fact] // legal — 部分满足：3 营地（30/s）对 60 需求 → 0.5 → 每 tick +1
    public void Partial_satisfaction_accrues_at_half_rate()
    {
        // [F-042] 部分满足
        EnergyLedger ledger = LedgerWithCamps(3);
        SimTeleporterLane lane = Lane(ledger, demand: 60.0, new Supplier(0), new Receiver());
        ledger.Settle();

        lane.SettleEnergy(ledger, 30);
        Assert.Equal(1, lane.Port.Available); // floor(0.5×60/30)=1
    }

    [Fact] // legal — 供给断绝（满足度 0）：零入账，Transfer 零交付零计费
    public void Starved_lane_moves_nothing()
    {
        // [F-042] 断供零动
        EnergyLedger ledger = LedgerWithCamps(0);
        var source = new Supplier(100);
        var receiver = new Receiver();
        SimTeleporterLane lane = Lane(ledger, demand: 60.0, source, receiver);
        ledger.Settle();

        lane.SettleEnergy(ledger, 30);
        Assert.Equal(0, lane.Port.Available);
        Assert.Equal(0, lane.Transfer());
        Assert.Equal(0L, lane.Link.EnergySpent);
        Assert.Equal(100, source.Stock); // 库存未被拉动
        Assert.Equal(0, receiver.Accepted);
    }

    [Fact] // legal — 满银行传送：5 单位/件 ×N 件精确扣能（唯一限流器）
    public void Transfer_charges_exact_energy_per_item()
    {
        // [F-042] 精确扣能
        EnergyLedger ledger = LedgerWithCamps(6);
        var source = new Supplier(10);
        var receiver = new Receiver();
        SimTeleporterLane lane = Lane(ledger, demand: 60.0, source, receiver);
        ledger.Settle();

        lane.SettleEnergy(ledger, 30);
        lane.SettleEnergy(ledger, 30);
        lane.SettleEnergy(ledger, 30); // 银行 6 单位 → 1 件（5 单位）+1 余
        int moved = lane.Transfer();

        Assert.Equal(1, moved);
        Assert.Equal(1, lane.Port.Available); // 6−5
        Assert.Equal(5L, lane.Link.EnergySpent);
        Assert.Equal(1, receiver.Accepted);
    }

    [Fact] // boundary — 银行不足一件（<5）：拉动上限=0，库存不进缓冲
    public void Sub_item_bank_pulls_nothing()
    {
        // [F-042] 不足一件零拉动
        EnergyLedger ledger = LedgerWithCamps(3);
        var source = new Supplier(10);
        var receiver = new Receiver();
        SimTeleporterLane lane = Lane(ledger, demand: 60.0, source, receiver);
        ledger.Settle();

        lane.SettleEnergy(ledger, 30);
        lane.SettleEnergy(ledger, 30); // 银行 2 单位 < 5
        Assert.Equal(0, lane.Transfer());
        Assert.Equal(0, lane.Link.BufferedItems); // 拉动上限=可负担数——零库存滞留
        Assert.Equal(2, lane.Port.Available);
    }

    [Fact] // illegal — 参数纪律：null/负需求/非正 tick 率必须拒绝；越界扣能必须拒绝
    public void Invalid_arguments_are_rejected()
    {
        // [F-042] 参数纪律
        EnergyLedger ledger = LedgerWithCamps(1);
        Assert.Throws<ArgumentNullException>(() => new SimTeleporterLane(
            null!, new EnergyNodeId(1), new ResourceKey("t"), new Supplier(1), new Receiver(), 1.0, ledger));
        Assert.Throws<ArgumentException>(() => Lane(ledger, demand: -1.0, new Supplier(1), new Receiver()));
        Assert.Throws<ArgumentException>(() => Lane(ledger, double.NaN, new Supplier(1), new Receiver()));

        SimTeleporterLane lane = Lane(ledger, 60.0, new Supplier(1), new Receiver());
        Assert.Throws<ArgumentNullException>(() => lane.SettleEnergy(null!, 30));
        Assert.Throws<ArgumentException>(() => lane.SettleEnergy(ledger, 0));

        var port = new SimTeleporterEnergyPort();
        port.Produce(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Draw(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Draw(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Produce(-1));
    }
}
