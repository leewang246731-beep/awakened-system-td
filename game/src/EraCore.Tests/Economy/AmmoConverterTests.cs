using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-020][F-021] AmmoConverter unit layer (T-034): 1 件弹药=10 发
// 换算、tick 制 craftTime、效率=min(供给率, 能源 satisfaction) 的实机
// 表现（能源 0.5 ⇒ 产出减半）、满仓停产（效率 0、在制进度与输入不销毁）
// 与 ICargoReceiver/ICargoSupplier 双侧口。craft 计时按 0.5/1.0 等
// 二进制精确步长推进，断言用精确相等（容差来源：dyadic 步长无舍入）。
// [C-06][F-020][F-021]
public class AmmoConverterTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly ResourceKey Stone = new("era1:res-stone");
    private static readonly ResourceKey Arrow = new("era1:ammo-arrow");

    /// <summary>Converter on its own ledger + clock; drive(n) settles n ticks end-to-end.</summary>
    private sealed record Harness(AmmoConverter Converter, EnergyLedger Ledger, SteppedClock Clock)
    {
        public static Harness Make(bool starved = false, AmmoConverter.AmmoRecipe? recipe = null)
        {
            var clock = new SteppedClock();
            var ledger = new EnergyLedger(clock);
            ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10/s
            if (starved)
            {
                // converter demand 2 + extra 18 = 20 vs 10/s ⇒ satisfaction 0.5
                ledger.RegisterConsumer(new EnergyNodeId(9), 18.0);
            }

            var converter = new AmmoConverter(clock, ledger, new EnergyNodeId(2), recipe ?? AmmoConverter.E1ArrowRecipe);
            return new Harness(converter, ledger, clock);
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

    // ---- 公式组 3/6：弹药换算（1 件 = 10 发） ----------------------------

    [Fact] // legal — the frozen conversion anchor: one item stands for exactly 10 shots (v3.2 §5 L75)
    public void One_ammo_item_is_exactly_ten_shots()
    {
        Assert.Equal(10, AmmoConverter.ShotsPerItem);
        Assert.Equal(AmmoConverter.ShotsPerItem, Carrier.ShotsPerItem); // 与 Logistics 档常量同锚不漂移
    }

    [Fact] // legal — the E1 anchor recipe shape (basket, ammo key, craft time)
    public void E1_anchor_recipe_pins_the_thin_era_values()
    {
        AmmoConverter.AmmoRecipe recipe = AmmoConverter.E1ArrowRecipe;

        Assert.Single(recipe.Inputs);
        Assert.Equal(Wood, recipe.Inputs[0].Resource);
        Assert.Equal(2, recipe.Inputs[0].Items); // [NEW-T-034] 2 件木材
        Assert.Equal(Arrow, recipe.AmmoResource); // [NEW-T-034] era1:ammo-arrow
        Assert.Equal(60, recipe.CraftTicks); // [NEW-T-034] 2 s @ 30 Hz
        Assert.Equal(2.0, AmmoConverter.E1EnergyDemandPerSecond); // [NEW-T-034]
    }

    [Fact] // legal — a completed craft logs a structured event worth 10 shots
    public void Craft_completion_logs_a_structured_ten_shot_event()
    {
        var h = Harness.Make();
        Assert.Equal(2, h.Converter.TryAccept(Wood, 2)); // 恰一篮

        h.Drive(60);

        Assert.Equal(1, h.Converter.CumulativeCrafts);
        AmmoConverter.CraftCompleted evt = Assert.Single(h.Converter.CraftLog);
        Assert.Equal(60, evt.Tick); // 60 tick @ efficiency 1 ⇒ completes on tick 60
        Assert.Equal(1, evt.Seq);
        Assert.Equal(Arrow, evt.AmmoResource);
        Assert.Equal(1, evt.Items);
        Assert.Equal(10, evt.Shots);
        Assert.Equal(0, h.Converter.HopperStockOf(Wood)); // basket fully consumed
    }

    [Fact] // legal — craft pacing at full efficiency: one item per CraftTicks window
    public void Craft_pacing_at_full_efficiency()
    {
        var h = Harness.Make();
        h.Converter.TryAccept(Wood, 20); // 10 crafts' worth

        h.Drive(600);

        Assert.Equal(10, h.Converter.CumulativeCrafts);
        Assert.Equal(10, h.Converter.OutputBuffer);
        Assert.Equal(0, h.Converter.HopperStockOf(Wood));
        Assert.Equal(60, h.Converter.CraftLog[0].Tick);
        Assert.Equal(120, h.Converter.CraftLog[1].Tick);
        Assert.Equal(600, h.Converter.CraftLog[^1].Tick);
        Assert.Equal(100, h.Converter.CraftLog.Sum(static e => e.Shots)); // 10 件 × 10 发
    }

    // ---- 公式组 4/6（实机侧）：效率 min ⇒ 能源 0.5 → 产出减半 ------------

    [Fact] // legal — energy satisfaction 0.5 halves the ammo output over the same window
    public void Energy_half_satisfaction_halves_ammo_output()
    {
        int Run(bool starved)
        {
            var h = Harness.Make(starved: starved);
            h.Converter.TryAccept(Wood, 20);
            h.Drive(1);
            Assert.Equal(starved ? 0.5 : 1.0, h.Converter.EnergySatisfaction, 9); // 前置：账本维度
            Assert.Equal(starved ? 0.5 : 1.0, h.Converter.Efficiency, 9); // min 链通（供给 1）
            h.Drive(599);
            return (int)h.Converter.CumulativeCrafts;
        }

        int full = Run(false);
        int halved = Run(true);

        Assert.Equal(10, full);
        Assert.Equal(5, halved); // 严格减半：600 tick 内 10 件 → 5 件
    }

    [Fact] // legal — a starved hopper drops the supply dimension to 0 (断供降速的 E1 二值形态)
    public void Starved_hopper_stops_crafting_without_consuming_anything()
    {
        var h = Harness.Make();

        h.Drive(600);

        Assert.Equal(0.0, h.Converter.SupplyRate);
        Assert.Equal(0.0, h.Converter.Efficiency); // min(0, 1)
        Assert.Equal(0, h.Converter.CumulativeCrafts);

        h.Converter.TryAccept(Wood, 2);
        Assert.Equal(1.0, h.Converter.SupplyRate);

        h.Drive(60);
        Assert.Equal(1, h.Converter.CumulativeCrafts);
    }

    [Fact] // legal — an energy blackout mid-craft freezes the running timer, inputs intact
    public void Energy_blackout_mid_craft_freezes_progress_and_recovers()
    {
        var h = Harness.Make();
        h.Converter.TryAccept(Wood, 2);
        h.Drive(30); // craft running: 30 of 60 ticks done
        Assert.Equal(30.0, h.Converter.RemainingCraftTicks!.Value, 9);

        h.Ledger.Unregister(new EnergyNodeId(1)); // drop the labor camp ⇒ output 0 ⇒ satisfaction 0
        h.Drive(300);

        Assert.Equal(0.0, h.Converter.Efficiency, 9);
        Assert.Equal(30.0, h.Converter.RemainingCraftTicks!.Value, 9); // 冻结：不推进也不回退
        Assert.Equal(0, h.Converter.CumulativeCrafts); // 不产出
        Assert.Equal(0, h.Converter.HopperStockOf(Wood)); // 已入工作位的输入不销毁也不返还

        h.Ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        h.Drive(30);

        Assert.Equal(1, h.Converter.CumulativeCrafts); // 恢复后 30 tick 补完剩余进度
        Assert.Equal(10, h.Converter.CraftLog[0].Shots);
    }

    // ---- 满仓停产（输出满→停产，不停摆） ----------------------------------

    [Fact] // legal — a full output buffer pauses the machine: efficiency 0, inputs never destroyed
    public void Full_output_buffer_pauses_crafting_and_keeps_inputs()
    {
        var h = Harness.Make();
        h.Converter.TryAccept(Wood, 40); // 20 crafts' worth

        h.Drive(1000); // crafts complete at 60..600 ⇒ 10 items = buffer cap at tick 600

        Assert.Equal(10, h.Converter.CumulativeCrafts);
        Assert.True(h.Converter.IsBackpressured);
        Assert.Equal(0.0, h.Converter.Efficiency); // 停产：效率 0（§5.3 一致性——缺的是输出空间）
        Assert.Equal(20, h.Converter.HopperStockOf(Wood)); // 第 11 篮从未被消耗（不销毁输入）
        Assert.Equal(10, h.Converter.CraftLog.Count);
        Assert.Null(h.Converter.RemainingCraftTicks); // 无在制 craft：满仓时不开新篮

        int drained = h.Converter.TryTake(Arrow, 4);

        Assert.Equal(4, drained);
        Assert.False(h.Converter.IsBackpressured);
        h.Drive(60); // craft 11: 60 tick @ eff 1

        Assert.Equal(11, h.Converter.CumulativeCrafts);
        Assert.Equal(7, h.Converter.OutputBuffer); // 10 − 4 + 1
        Assert.Equal(11, h.Converter.CraftLog[^1].Seq); // seq 严格递增
    }

    // ---- 双侧口（ICargoReceiver / ICargoSupplier） ------------------------

    [Fact] // legal — the receiver side accepts recipe inputs only, up to hopper room
    public void TryAccept_takes_recipe_inputs_up_to_hopper_room()
    {
        var h = Harness.Make();

        Assert.Equal(0, h.Converter.TryAccept(Stone, 5)); // 非配方资源拒收（待卸队列重试）
        Assert.Equal(0, h.Converter.HopperStockOf(Stone));

        Assert.Equal(30, h.Converter.TryAccept(Wood, 30));
        Assert.Equal(10, h.Converter.TryAccept(Wood, 20)); // 只剩 10 容量 ⇒ 部分收下
        Assert.Equal(40, h.Converter.HopperStockOf(Wood));

        Assert.Equal(0, h.Converter.TryAccept(Wood, 5)); // 满了 ⇒ 全拒
        Assert.Equal(40, h.Converter.HopperStockOf(Wood));
    }

    [Fact] // legal — the supplier side hands over ammo items only
    public void TryTake_hands_over_ammo_items_only()
    {
        var h = Harness.Make();
        h.Converter.TryAccept(Wood, 2);
        h.Drive(60);

        Assert.Equal(0, h.Converter.TryTake(Wood, 5)); // 弹药口不交木材
        Assert.Equal(1, h.Converter.TryTake(Arrow, 5));
        Assert.Equal(0, h.Converter.OutputBuffer);
    }

    [Theory] // illegal — negative amounts rejected on both seams
    [InlineData(-1)]
    [InlineData(-100)]
    public void Cargo_seams_reject_negative_amounts(int amount)
    {
        var h = Harness.Make();

        Assert.Throws<ArgumentOutOfRangeException>(() => h.Converter.TryAccept(Wood, amount));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Converter.TryTake(Arrow, amount));
    }

    [Fact] // illegal — null resources rejected on both seams
    public void Cargo_seams_reject_null_resources()
    {
        var h = Harness.Make();

        Assert.Throws<ArgumentNullException>(() => h.Converter.TryAccept(null!, 1));
        Assert.Throws<ArgumentNullException>(() => h.Converter.TryTake(null!, 1));
        Assert.Throws<ArgumentNullException>(() => h.Converter.HopperStockOf(null!));
    }

    // ---- 构造非法 ---------------------------------------------------------

    [Fact] // illegal — malformed recipes rejected at construction
    public void Constructor_rejects_malformed_recipes()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2),
                new AmmoConverter.AmmoRecipe(Array.Empty<AmmoConverter.AmmoRecipe.Input>(), Arrow, 60)));
        Assert.Throws<ArgumentException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2),
                new AmmoConverter.AmmoRecipe(new[] { new AmmoConverter.AmmoRecipe.Input(Wood, 0) }, Arrow, 60)));
        Assert.Throws<ArgumentException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2),
                new AmmoConverter.AmmoRecipe(
                    new[] { new AmmoConverter.AmmoRecipe.Input(Wood, 1), new AmmoConverter.AmmoRecipe.Input(Wood, 2) },
                    Arrow, 60)));
        Assert.Throws<ArgumentException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2),
                new AmmoConverter.AmmoRecipe(new[] { new AmmoConverter.AmmoRecipe.Input(Wood, 2) }, Arrow, 0)));
        Assert.Throws<ArgumentNullException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2),
                new AmmoConverter.AmmoRecipe(new[] { new AmmoConverter.AmmoRecipe.Input(Wood, 2) }, null!, 60)));
    }

    [Fact] // illegal — null wiring rejected
    public void Constructor_rejects_null_arguments()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock);
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);

        Assert.Throws<ArgumentNullException>(
            () => new AmmoConverter(null!, ledger, new EnergyNodeId(2), AmmoConverter.E1ArrowRecipe));
        Assert.Throws<ArgumentNullException>(
            () => new AmmoConverter(clock, null!, new EnergyNodeId(2), AmmoConverter.E1ArrowRecipe));
        Assert.Throws<ArgumentNullException>(
            () => new AmmoConverter(clock, ledger, null!, AmmoConverter.E1ArrowRecipe));
        Assert.Throws<ArgumentNullException>(
            () => new AmmoConverter(clock, ledger, new EnergyNodeId(2), null!));
    }

    [Fact] // boundary — CraftTicks 1 is the minimal legal recipe and completes in one tick
    public void Minimal_craft_time_completes_in_one_tick()
    {
        var recipe = new AmmoConverter.AmmoRecipe(
            new[] { new AmmoConverter.AmmoRecipe.Input(Wood, 1) }, Arrow, CraftTicks: 1);
        var h = Harness.Make(recipe: recipe);
        h.Converter.TryAccept(Wood, 1);

        h.Drive(1);

        Assert.Equal(1, h.Converter.CumulativeCrafts);
        Assert.Equal(10, h.Converter.CraftLog[0].Shots);
    }
}
