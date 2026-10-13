using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Economy;

/// <summary>
/// Joint integration of the C-06 economy with the reused C-08 ledger and
/// C-07 precursor stack (T-034): a REAL <see cref="Carrier"/> porter walks
/// the reference 5-cell route hauling wood from a live
/// <see cref="Producer"/> into an <see cref="AmmoConverter"/>'s hopper,
/// the converter crafts ammo under the §5.3 min-efficiency chain, and the
/// ammo enters a tower-side magazine through the
/// <see cref="ICargoReceiver"/> seam (Combat wiring is later work — the
/// tower here is the F-020 counting seam, asserting shots = items × 10).
/// Also covers the ledger shortage propagation end-to-end and the replay
/// determinism of the whole chain (same seed + tick sequence ⇒ equal
/// state hashes).
/// </summary>
// [C-06][F-019][F-020][F-021][F-042]
public class EconomyIntegrationTests
{
    private const long Seed = 20261014; // same seed family the Logistics joint tests replay

    private static readonly ResourceKey Wood = new("era1:res-wood");

    /// <summary>
    /// Tower-side magazine seam (弹药入塔): accepts only the ammo resource
    /// and counts shots = items × <see cref="AmmoConverter.ShotsPerItem"/>.
    /// Unlimited room — the acceptance policy is Combat-side work.
    /// </summary>
    // [C-06][F-020]
    private sealed class TowerMagazine : ICargoReceiver
    {
        internal TowerMagazine(ResourceKey ammo)
        {
            Ammo = ammo;
        }

        public ResourceKey Ammo { get; }

        internal long ItemsAccepted { get; private set; }

        internal long Shots => ItemsAccepted * AmmoConverter.ShotsPerItem;

        public int TryAccept(ResourceKey resource, int amount)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Accept amount must be non-negative.");
            }

            if (!resource.Equals(Ammo))
            {
                return 0;
            }

            ItemsAccepted += amount;
            return amount;
        }
    }

    /// <summary>The full chain: ledger → producer → porter → converter → tower.</summary>
    private sealed record ChainHarness(
        SteppedClock Clock,
        EnergyLedger Ledger,
        Producer Producer,
        AmmoConverter Converter,
        Carrier Porter,
        TowerMagazine Tower,
        SeededRng Rng)
    {
        public static ChainHarness Create(long seed, bool starveEnergy = false)
        {
            var rng = new SeededRng(seed); // E1 economy draws no randomness — seed parity only
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10/s
            if (starveEnergy)
            {
                // demand: producer 1 + converter 2 + extra 17 = 20 vs 10/s ⇒ satisfaction 0.5
                ledger.RegisterConsumer(new EnergyNodeId(9), 17.0);
            }

            var producer = new Producer(new EntityId(1), Wood, seats: 1, tier: 1, ledger, new EnergyNodeId(2));
            var converter = new AmmoConverter(clock, ledger, new EnergyNodeId(3), AmmoConverter.E1ArrowRecipe);

            // Carrier reference route: straight 5 cells = 160 px (the tier-1 anchor route)
            var route = new List<CellCoord>();
            for (int x = 0; x <= 4; x++)
            {
                route.Add(new CellCoord(x, 0));
            }

            var porter = new Carrier(new EntityId(2), Wood, producer, converter, route);
            var tower = new TowerMagazine(AmmoConverter.E1ArrowRecipe.AmmoResource);
            return new ChainHarness(clock, ledger, producer, converter, porter, tower, rng);
        }

        public void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Clock.Advance();
                Ledger.Settle();
                Producer.Tick();
                Porter.Tick();
                Converter.Tick();

                // 弹药入塔：当 tick 取走成品并交付塔侧弹匣（结构化事件见 CraftLog）
                int ammo = Converter.TryTake(AmmoConverter.E1ArrowRecipe.AmmoResource, int.MaxValue);
                int accepted = Tower.TryAccept(AmmoConverter.E1ArrowRecipe.AmmoResource, ammo);
                Assert.Equal(ammo, accepted); // magazine is unlimited in this seam — nothing may vanish
            }
        }
    }

    [Fact]
    public void FullChainHarvestsHaulsCraftsAndFeedsTheTower()
    {
        // [F-019→F-020→F-042] 采集→加工→Carrier 真实运输→弹药入塔：守恒律
        // 逐条精确断言（不是近似值），吞吐只给下界——porter 时序由
        // Logistics 自己的测试负责
        var h = ChainHarness.Create(Seed);

        h.Drive(3000);

        // 能源充足：1 劳工营 10/s ≥ 需求 3/s，全程 satisfaction 1
        Assert.Equal(1.0, h.Ledger.Satisfaction, 9);

        // 弹药入塔计数：shots = items × 10（F-020 换算的塔侧读数）
        Assert.True(h.Tower.ItemsAccepted >= 5, $"tower items={h.Tower.ItemsAccepted}");
        Assert.Equal(h.Tower.ItemsAccepted * 10, h.Tower.Shots);

        // 弹药守恒：每件成品要么在塔里，要么还在输出缓冲
        Assert.Equal(h.Converter.CumulativeCrafts, h.Tower.ItemsAccepted + h.Converter.OutputBuffer);

        // 事件流：结构化 CraftCompleted 严格 (tick, seq) 递增，shots 合计 = 件数 × 10
        Assert.Equal(h.Converter.CumulativeCrafts, h.Converter.CraftLog.Count);
        long lastTick = 0;
        long lastSeq = 0;
        foreach (AmmoConverter.CraftCompleted evt in h.Converter.CraftLog)
        {
            Assert.True(evt.Tick > lastTick, "craft ticks must be strictly increasing");
            Assert.Equal(evt.Seq, lastSeq + 1);
            Assert.Equal(10, evt.Shots);
            lastTick = evt.Tick;
            lastSeq = evt.Seq;
        }

        Assert.Equal(h.Converter.CumulativeCrafts * 10, h.Converter.CraftLog.Sum(static e => e.Shots));

        // 木材守恒：porter 交付量 = 已消耗（含在制）+ 料斗余量
        long consumed = (2 * h.Converter.CumulativeCrafts)
            + (h.Converter.RemainingCraftTicks.HasValue ? 2 : 0);
        Assert.Equal(
            h.Porter.DeliveredItems,
            h.Converter.HopperStockOf(Wood) + consumed);

        // 采集守恒：产出 = 场院余量 + porter 在途 + 已交付
        Assert.Equal(
            h.Producer.CumulativeHarvested,
            h.Producer.OutputBuffer + h.Porter.CargoItems + h.Porter.DeliveredItems);

        // 真实运输确实发生了多次
        Assert.True(h.Porter.DeliveredItems >= 8, $"delivered={h.Porter.DeliveredItems}");
    }

    [Fact]
    public void EnergyShortagePropagatesThroughTheWholeChain()
    {
        // [F-021] 短降速传导（全链版）：同一 seed、同一 tick 序列，只有
        // 账本多挂 17 点需求 ⇒ satisfaction 0.5 ⇒ 采集与加工双降速 ⇒ 塔
        // 侧弹药严格减少（精确减半在 AmmoConverterTests 的受控窗口断言）
        var full = ChainHarness.Create(Seed, starveEnergy: false);
        var starved = ChainHarness.Create(Seed, starveEnergy: true);

        full.Drive(1);
        starved.Drive(1);
        Assert.Equal(1.0, full.Ledger.Satisfaction, 9);
        Assert.Equal(0.5, starved.Ledger.Satisfaction, 9); // 前置：账本确实 0.5
        Assert.Equal(0.5, starved.Converter.EnergySatisfaction, 9); // 账本→加工的能源维度传导
        // （此刻料斗尚空：porter 未到，供给维度=0，效率 min=0——供给维由下方
        // 端到端产出对比与 AmmoConverterTests 的受控窗口覆盖）

        full.Drive(2999);
        starved.Drive(2999);

        Assert.True(
            starved.Tower.ItemsAccepted < full.Tower.ItemsAccepted,
            $"starved={starved.Tower.ItemsAccepted} must be < full={full.Tower.ItemsAccepted}");
        Assert.True(starved.Producer.CumulativeHarvested < full.Producer.CumulativeHarvested);
    }

    [Fact]
    public void FullChainReplaysIdenticallyForTheSameSeedAndTickSequence()
    {
        // [F-154 前置] 同 seed + 同 tick 序列 ⇒ 状态哈希相等；多走一 tick
        // ⇒ 生产者哈希变化（进度在推进）。E1 经济不抽随机数（RNG 属能源
        // lane 的粮仓事件）——seed 只进 harness，不进被测对象
        var a = ChainHarness.Create(Seed);
        var b = ChainHarness.Create(Seed);

        a.Drive(1500);
        b.Drive(1500);

        Assert.Equal(a.Producer.ComputeStateHash(), b.Producer.ComputeStateHash());
        Assert.Equal(a.Converter.ComputeStateHash(), b.Converter.ComputeStateHash());

        b.Drive(1);

        Assert.NotEqual(a.Producer.ComputeStateHash(), b.Producer.ComputeStateHash());
    }
}
