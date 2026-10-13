using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-024] Global resource pool and cap formula (T-034):
// 容量=2000×仓库等级×(1+金字塔 0.2)，入池只计上限内，超上限拒收并留
// 溢出记录（v3.2 附录 F.3 / §5 L79：仓库等级 E1=1、跃迁自动+1、不可手
// 动升级）。表驱动覆盖 1~6 级 × 金字塔两态。
// [C-06][F-024]
public class ResourceStockTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly ResourceKey Stone = new("era1:res-stone");
    private static readonly ResourceKey Food = new("era1:res-food");

    // ---- 公式组 6/6：池上限公式表 ---------------------------------------

    [Theory] // legal — capacity = 2000 × level × (1 + 0.2 if pyramid), levels 1..6 (F.3)
    [InlineData(1, false, 2000.0)]
    [InlineData(1, true, 2400.0)]
    [InlineData(2, false, 4000.0)]
    [InlineData(2, true, 4800.0)]
    [InlineData(3, false, 6000.0)]
    [InlineData(4, false, 8000.0)]
    [InlineData(5, false, 10000.0)]
    [InlineData(6, true, 14400.0)]
    public void Capacity_follows_the_frozen_formula(int level, bool pyramid, double expected)
    {
        var stock = new ResourceStock(new SteppedClock(), level, pyramid);

        // tolerance 1e-9: 2000/0.2 are exact dyadics — guards drift only
        Assert.Equal(expected, stock.Capacity, 9);
    }

    [Fact] // legal — F-024 验收: 金字塔建成前后上限差恰 +20%
    public void Pyramid_bonus_is_exactly_twenty_percent()
    {
        var plain = new ResourceStock(new SteppedClock(), 1, pyramidBuilt: false);
        var pyramid = new ResourceStock(new SteppedClock(), 1, pyramidBuilt: true);

        Assert.Equal(plain.Capacity * ResourceStock.PyramidBonus, pyramid.Capacity - plain.Capacity, 9);
        Assert.Equal(0.2, ResourceStock.PyramidBonus);
        Assert.Equal(2000.0, ResourceStock.BaseCapacityPerWarehouseLevel);
    }

    [Fact] // legal — the E1 warehouse anchor equals era1.json warehouse_level (cannot drift)
    public void E1_warehouse_level_matches_era1_json()
    {
        // repo-relative walk (RL9: 禁绝对路径)，same convention as the Energy lane
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1", "era.json");
            if (File.Exists(probe))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(probe));
                int jsonLevel = doc.RootElement.GetProperty("items")[0].GetProperty("warehouse_level").GetInt32();

                Assert.Equal(ResourceStock.E1WarehouseLevel, jsonLevel);
                Assert.Equal(1, ResourceStock.E1WarehouseLevel);
                return;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/era1/era.json（测试须在仓库内运行）");
    }

    // ---- 入池/出池语义 ---------------------------------------------------

    [Fact] // legal — resources pool independently, in first-deposit order
    public void Deposits_pool_per_resource_independently()
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);

        double acceptedWood = stock.Deposit(Wood, 100.0);
        double acceptedStone = stock.Deposit(Stone, 50.0);

        Assert.Equal(100.0, acceptedWood, 9);
        Assert.Equal(50.0, acceptedStone, 9);
        Assert.Equal(100.0, stock.AmountOf(Wood), 9);
        Assert.Equal(50.0, stock.AmountOf(Stone), 9);
        Assert.Equal(0.0, stock.AmountOf(Food), 9);
        Assert.Equal(150.0, stock.TotalAmount, 9);
        Assert.Equal(new[] { Wood, Stone }, stock.Resources);
    }

    [Fact] // boundary — topping a pool off to exactly the cap accepts everything, no overflow
    public void Deposit_up_to_capacity_is_fully_accepted()
    {
        var clock = new SteppedClock();
        var stock = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);

        stock.Deposit(Wood, 1500.0);
        double accepted = stock.Deposit(Wood, 500.0);

        Assert.Equal(500.0, accepted, 9);
        Assert.Equal(2000.0, stock.AmountOf(Wood), 9);
        Assert.Null(stock.LastOverflow);
        Assert.Equal(0.0, stock.CumulativeOverflow, 9);
        Assert.Equal(0, stock.OverflowCount);
    }

    [Fact] // boundary — a deposit at the cap is refused whole and recorded (tick-stamped)
    public void Deposit_at_capacity_is_refused_with_overflow_record()
    {
        var clock = new SteppedClock();
        var stock = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        stock.Deposit(Wood, 2000.0);
        clock.Advance(7);

        double accepted = stock.Deposit(Wood, 300.0);

        Assert.Equal(0.0, accepted, 9);
        Assert.NotNull(stock.LastOverflow);
        Assert.Equal(7, stock.LastOverflow!.Tick);
        Assert.Equal(Wood, stock.LastOverflow.Resource);
        Assert.Equal(300.0, stock.LastOverflow.RefusedAmount, 9);
        Assert.Equal(300.0, stock.CumulativeOverflow, 9);
        Assert.Equal(1, stock.OverflowCount);
        Assert.Equal(2000.0, stock.AmountOf(Wood), 9); // 拒收不销毁已存
    }

    [Fact] // boundary — a straddling deposit takes the headroom and refuses the rest
    public void Deposit_above_headroom_accepts_partial_and_records_refused_part()
    {
        var clock = new SteppedClock();
        var stock = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        stock.Deposit(Wood, 1900.0);

        double accepted = stock.Deposit(Wood, 300.0);

        Assert.Equal(100.0, accepted, 9);
        Assert.Equal(2000.0, stock.AmountOf(Wood), 9);
        Assert.Equal(200.0, stock.LastOverflow!.RefusedAmount, 9);
        Assert.Equal(200.0, stock.CumulativeOverflow, 9);
    }

    [Fact] // boundary — an empty deposit at the cap raises no overflow record
    public void Zero_deposit_at_capacity_records_nothing()
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);
        stock.Deposit(Wood, 2000.0);

        double accepted = stock.Deposit(Wood, 0.0);

        Assert.Equal(0.0, accepted, 9);
        Assert.Null(stock.LastOverflow);
        Assert.Equal(0, stock.OverflowCount);
    }

    [Fact] // legal — withdraw takes what is there and never goes negative
    public void TryWithdraw_never_drives_a_pool_negative()
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);
        stock.Deposit(Wood, 500.0);

        double first = stock.TryWithdraw(Wood, 200.0);
        double second = stock.TryWithdraw(Wood, 400.0);

        Assert.Equal(200.0, first, 9);
        Assert.Equal(300.0, second, 9);
        Assert.Equal(0.0, stock.AmountOf(Wood), 9);
        Assert.Equal(0.0, stock.TryWithdraw(Wood, 10.0), 9);
    }

    [Fact] // legal — withdrawing headroom re-opens deposit room (溢出后可再入池)
    public void Withdraw_reopens_headroom_for_later_deposits()
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);
        stock.Deposit(Stone, 2000.0);
        Assert.Equal(0.0, stock.Deposit(Stone, 5.0), 9);

        stock.TryWithdraw(Stone, 100.0);

        Assert.Equal(5.0, stock.Deposit(Stone, 5.0), 9);
        Assert.Equal(1905.0, stock.AmountOf(Stone), 9);
    }

    // ---- 非法输入 ---------------------------------------------------------

    [Theory] // illegal — non-finite or negative amounts rejected on both directions
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Deposit_and_withdraw_reject_bad_amounts(double amount)
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);

        Assert.Throws<ArgumentException>(() => stock.Deposit(Wood, amount));
        Assert.Throws<ArgumentException>(() => stock.TryWithdraw(Wood, amount));
    }

    [Fact] // illegal — null resource keys rejected
    public void Null_resource_rejected()
    {
        var stock = new ResourceStock(new SteppedClock(), ResourceStock.E1WarehouseLevel);

        Assert.Throws<ArgumentNullException>(() => stock.Deposit(null!, 1.0));
        Assert.Throws<ArgumentNullException>(() => stock.TryWithdraw(null!, 1.0));
        Assert.Throws<ArgumentNullException>(() => stock.AmountOf(null!));
    }

    [Theory] // illegal — warehouse level below 1 rejected (E1=1，跃迁+1，无 0 级)
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_warehouse_level(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceStock(new SteppedClock(), level));
    }

    [Fact] // illegal — null clock rejected (CS-1.4: time only via the port)
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() => new ResourceStock(null!, ResourceStock.E1WarehouseLevel));
    }

    // ---- 状态哈希 ---------------------------------------------------------

    [Fact] // legal — same operation sequence ⇒ equal hashes; divergence ⇒ different
    public void State_hash_is_operation_sensitive()
    {
        long Run(out ResourceStock stock)
        {
            var clock = new SteppedClock();
            stock = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
            stock.Deposit(Wood, 120.0);
            stock.Deposit(Stone, 30.0);
            stock.TryWithdraw(Wood, 20.0);
            return stock.ComputeStateHash();
        }

        long first = Run(out ResourceStock a);
        long second = Run(out ResourceStock b);

        Assert.Equal(first, second);
        Assert.Equal(first, a.ComputeStateHash()); // still equal: nothing changed since

        b.Deposit(Food, 1.0);

        Assert.NotEqual(first, b.ComputeStateHash());
    }
}
