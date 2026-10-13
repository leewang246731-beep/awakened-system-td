using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06] Determinism contract of the E1 economy (T-034 验收: 同 seed+tick
// 序列 ⇒ 状态哈希相等，03-decomposition §1.2 回放前提)。E1 经济不消费
// 随机数（RNG 端口属于能源 lane 的粮仓事件），确定性是纯算术的——
// harness 仍以同 seed 构造，锁定"换 seed 不该改变行为"的口径。
// [C-06][F-019][F-020]
public class EconomyDeterminismTests
{
    private const long Seed = 20261014;

    private static readonly ResourceKey Wood = new("era1:res-wood");

    private sealed record ConverterHarness(AmmoConverter Converter, EnergyLedger Ledger, SteppedClock Clock)
    {
        public static ConverterHarness Create(int preloadWood)
        {
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
            var converter = new AmmoConverter(clock, ledger, new EnergyNodeId(2), AmmoConverter.E1ArrowRecipe);
            converter.TryAccept(Wood, preloadWood);
            return new ConverterHarness(converter, ledger, clock);
        }

        public void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Clock.Advance();
                Ledger.Settle();
                Converter.Tick();
            }
        }
    }

    private sealed record ProducerHarness(Producer Producer, EnergyLedger Ledger, SteppedClock Clock)
    {
        public static ProducerHarness Create()
        {
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
            var producer = new Producer(new EntityId(1), Wood, 1, 1, ledger, new EnergyNodeId(2));
            return new ProducerHarness(producer, ledger, clock);
        }

        public void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Clock.Advance();
                Ledger.Settle();
                Producer.Tick();
            }
        }
    }

    [Fact] // legal — same seed + same tick sequence ⇒ identical producer hashes
    public void Producer_hash_replays_identically()
    {
        var a = ProducerHarness.Create();
        var b = ProducerHarness.Create();

        a.Drive(137);
        b.Drive(137);

        Assert.Equal(a.Producer.ComputeStateHash(), b.Producer.ComputeStateHash());

        b.Drive(1); // one more tick ⇒ the fractional progress moved

        Assert.NotEqual(a.Producer.ComputeStateHash(), b.Producer.ComputeStateHash());
    }

    [Fact] // legal — same seed + same tick sequence ⇒ identical converter hashes
    public void Converter_hash_replays_identically()
    {
        var a = ConverterHarness.Create(preloadWood: 9);
        var b = ConverterHarness.Create(preloadWood: 9);

        a.Drive(137); // crafts mid-flight: timer bits and log both in play
        b.Drive(137);

        Assert.Equal(a.Converter.ComputeStateHash(), b.Converter.ComputeStateHash());

        var c = ConverterHarness.Create(preloadWood: 8); // one basket item less ⇒ divergence
        c.Drive(137);

        Assert.NotEqual(a.Converter.ComputeStateHash(), c.Converter.ComputeStateHash());
    }

    [Fact] // legal — the composed chain hash (stock ⊕ producer ⊕ converter) also replays
    public void Composite_hash_replays_identically()
    {
        long Run()
        {
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
            var producer = new Producer(new EntityId(1), Wood, 1, 1, ledger, new EnergyNodeId(2));
            var converter = new AmmoConverter(clock, ledger, new EnergyNodeId(3), AmmoConverter.E1ArrowRecipe);
            var stock = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
            converter.TryAccept(Wood, 5);

            for (int i = 0; i < 210; i++)
            {
                clock.Advance();
                ledger.Settle();
                producer.Tick();
                converter.Tick();
                if (i == 100)
                {
                    stock.Deposit(Wood, 2500.0); // 溢出一次，把溢出记录也织进哈希
                }
            }

            return stock.ComputeStateHash() ^ producer.ComputeStateHash() ^ converter.ComputeStateHash();
        }

        Assert.Equal(Run(), Run());
    }
}
