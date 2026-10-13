using EraCore.Contracts.Foundation;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Supply;

/// <summary>
/// Unit tests of the per-tower efficiency curve (T-048, F-046): demand
/// derivation from the WeaponFire numbers (reload_ticks + ammo_per_shot ⇒
/// 发/s, damage_per_shot ⇒ full DPS anchor), the sliding-window mean
/// <c>efficiency = clamp(交付率/需求率, 0, 1)</c> against a scripted
/// delivery counter (轮询弹药计数差分), the 需求≤0 恒 1 law, warm-up grace,
/// break-cell bookkeeping and the replay hash. Delivery streams are dense
/// (sub-tick fractional rates via integer bursts) so the window ratios are
/// EXACT hand values — including the 0.2 断供 ratio the graded curve must
/// realize.
/// </summary>
// [C-06][F-046] 效率曲线：滑动窗均值 + 需求推导 + 宽限/断点/哈希
public class TowerSupplyMonitorTests
{
    private static readonly EntityId Tower = new(1);

    private static readonly CellCoord Position = new(4, 2);

    /// <summary>Scriptable cumulative delivery counter (monotonic by construction).</summary>
    private sealed class DeliveryCounter
    {
        private long _shots;

        public void Add(int shots) => _shots += shots;

        public long Read() => _shots;

        public Func<long> AsFunc() => () => _shots;
    }

    private static TowerSupplyRegistration Registration(
        DeliveryCounter counter,
        int reloadTicks = 3,
        int ammoPerShot = 1,
        double damagePerShot = 10.0) =>
        new(Tower, Position, reloadTicks, ammoPerShot, damagePerShot, counter.AsFunc());

    /// <summary>Drives n ticks delivering one shot every <paramref name="period"/> ticks (rate 30/period 发/s).</summary>
    private static void Drive(TowerSupplyMonitor monitor, DeliveryCounter counter, int ticks, int period)
    {
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (tick % period == 0)
            {
                counter.Add(1);
            }

            monitor.Tick();
        }
    }

    // ------------------------------------------------------------ demand derivation

    [Fact] // legal — 发/s and DPS derived from the WeaponFire numbers
    public void Demand_rate_derives_from_reload_and_ammo_per_shot()
    {
        // [F-046] reload 30 + 1 shot/firing + damage 10 (the era1 wood-arrow
        // anchors): 发/s = 1×30/30 = 1, full DPS = 10×1 = 10
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter, reloadTicks: 30, ammoPerShot: 1, damagePerShot: 10.0));

        Assert.Equal(1.0, monitor.DemandShotsPerSecondOf(Tower), 9);
        Assert.Equal(10.0, monitor.FullDpsOf(Tower), 9);

        // reload 3 (10 shots/s), 2 shots per firing (20 shots/s demand)
        monitor.RegisterTower(new TowerSupplyRegistration(
            new EntityId(2), new CellCoord(1, 1), 3, 2, 5.0, counter.AsFunc()));
        Assert.Equal(20.0, monitor.DemandShotsPerSecondOf(new EntityId(2)), 9);
        Assert.Equal(100.0, monitor.FullDpsOf(new EntityId(2)), 9); // 5 × 20
    }

    [Fact] // legal — registration order is the iteration order (determinism)
    public void Towers_lists_registrations_in_order()
    {
        // [F-046] registration order determinism
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor();
        monitor.RegisterTower(new TowerSupplyRegistration(
            new EntityId(7), Position, 30, 1, 10.0, counter.AsFunc()));
        monitor.RegisterTower(new TowerSupplyRegistration(
            new EntityId(3), Position, 30, 1, 10.0, counter.AsFunc()));

        Assert.Equal(new[] { new EntityId(7), new EntityId(3) }, monitor.Towers);
    }

    // ------------------------------------------------------------ efficiency curve

    [Fact] // legal — full supply: window ratio reaches exactly 1 and stays
    public void Full_delivery_stream_reads_efficiency_one()
    {
        // [F-046] demand 1/3 shot/tick (reload 3): one shot every 3 ticks =
        // 10 发/s ⇒ window(90) sums 30/30 ⇒ 1.0
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter));

        Drive(monitor, counter, 270, period: 3);

        Assert.Equal(1.0, monitor.EfficiencyOf(Tower), 9);
        Assert.Equal(10.0, monitor.DeliveryShotsPerSecondOf(Tower), 9);
    }

    [Fact] // legal — 断供 0.2: delivery at 1/5 of the demand reads exactly 0.2
    public void One_fifth_delivery_stream_reads_efficiency_exactly_zero_point_two()
    {
        // [F-046] demand 1/3 shot/tick ⇒ window(90) demand 30 shots; one
        // shot every 15 ticks = 2 发/s ⇒ window sum 6 ⇒ 6/30 = 0.2 exact
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter));

        Drive(monitor, counter, 180, period: 15);

        Assert.Equal(0.2, monitor.EfficiencyOf(Tower), 9);
        Assert.Equal(2.0, monitor.DeliveryShotsPerSecondOf(Tower), 9);
    }

    [Theory] // legal + boundary — graded curve over several exact ratios
    [InlineData(3, 1.0)] // full
    [InlineData(5, 0.6)] // 6/10 of demand
    [InlineData(6, 0.5)] // half
    [InlineData(15, 0.2)] // 断供 0.2
    [InlineData(30, 0.1)] // deep cut
    [InlineData(2, 1.0)] // over-supply clamps to 1 (交付率>需求率)
    public void Window_ratio_matches_the_hand_computed_value(int period, double expected)
    {
        // [F-046] window demand 30 shots (reload 3 × 90 ticks); delivering
        // one shot every `period` ticks gives 90/period shots ⇒ efficiency
        // = clamp((90/period)/30, 0, 1) = the hand value above
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter));

        Drive(monitor, counter, 90, period);

        Assert.Equal(expected, monitor.EfficiencyOf(Tower), 9);
    }

    [Fact] // legal — sliding window: past deliveries age out within N ticks
    public void Window_slides_cut_off_old_deliveries()
    {
        // [F-046] deliver at full rate for 90 ticks (eff 1), then freeze:
        // after another 90 window-lengths the window holds nothing ⇒ 0 —
        // the curve follows the CURRENT rate, not history
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter));

        Drive(monitor, counter, 90, period: 3);
        Assert.Equal(1.0, monitor.EfficiencyOf(Tower), 9);

        Drive(monitor, counter, 90, period: 1_000); // period > ticks: no delivery
        Assert.Equal(0.0, monitor.EfficiencyOf(Tower), 9);
        Assert.Equal(0.0, monitor.DeliveryShotsPerSecondOf(Tower), 9);
    }

    [Fact] // legal — 需求≤0 恒 1: no ammo need ⇒ curve never binds
    public void Zero_demand_tower_always_reads_one()
    {
        // [F-046][F-021] ammo_per_shot 0 (no ammo need): efficiency is 1 by
        // the §5.3 law even with zero deliveries
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(Registration(counter, ammoPerShot: 0));

        Drive(monitor, counter, 200, period: 1_000);

        Assert.Equal(1.0, monitor.EfficiencyOf(Tower), 9);
        Assert.Equal(0.0, monitor.DemandShotsPerSecondOf(Tower), 9);
    }

    [Fact] // legal — warm-up: a fresh registration reads 1 until the grace expires
    public void Warmup_holds_one_until_the_curve_binds()
    {
        // [F-046] 新建链路宽限: no delivery at all yet — tick 10 (== warmup)
        // still reads 1; tick 11 binds the curve ⇒ 0 (window demand 11/3
        // shots, nothing delivered)
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 10);
        monitor.RegisterTower(Registration(counter));

        for (int tick = 1; tick <= 10; tick++)
        {
            monitor.Tick();
        }

        Assert.Equal(1.0, monitor.EfficiencyOf(Tower), 9);

        monitor.Tick();
        Assert.Equal(0.0, monitor.EfficiencyOf(Tower), 9);
    }

    // ------------------------------------------------------------ observability inputs

    [Fact] // legal — break cell bookkeeping feeds the bottleneck suggestion
    public void Break_cell_is_reported_and_cleared()
    {
        // [F-046] break cell bookkeeping
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor();
        monitor.RegisterTower(Registration(counter));
        Assert.Null(monitor.BreakCellOf(Tower));

        var wall = new CellCoord(9, 2);
        monitor.NotifyRouteBlocked(Tower, wall);
        Assert.Equal(wall, monitor.BreakCellOf(Tower));

        monitor.NotifyRouteRestored(Tower);
        Assert.Null(monitor.BreakCellOf(Tower));
    }

    [Fact] // legal — lowest-stock relay wins with registration-order tie-break
    public void Lowest_stock_relay_is_selected_deterministically()
    {
        // [F-046] relay suggestion tie-break
        var counter = new DeliveryCounter();
        var monitor = new TowerSupplyMonitor();
        monitor.RegisterRelay(new EntityId(21), new CellCoord(1, 1), () => 5);
        monitor.RegisterRelay(new EntityId(22), new CellCoord(2, 2), () => 0);
        monitor.RegisterRelay(new EntityId(23), new CellCoord(3, 3), () => 0); // tie with 22

        (EntityId Id, CellCoord Position, long StockItems)? relay = monitor.LowestStockRelay();

        Assert.NotNull(relay);
        Assert.Equal(new EntityId(22), relay!.Value.Id); // first of the minimal pair
        Assert.Equal(new CellCoord(2, 2), relay.Value.Position);
        Assert.Equal(0L, relay.Value.StockItems);
    }

    [Fact] // legal — replay hash: same stream ⇒ equal, different stream ⇒ different
    public void State_hash_tracks_the_delivery_stream()
    {
        // [F-046] monitor replay hash
        TowerSupplyMonitor Run(int period)
        {
            var counter = new DeliveryCounter();
            var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
            monitor.RegisterTower(Registration(counter));
            Drive(monitor, counter, 90, period);
            return monitor;
        }

        Assert.Equal(Run(3).ComputeStateHash(), Run(3).ComputeStateHash());
        Assert.NotEqual(Run(3).ComputeStateHash(), Run(15).ComputeStateHash());
    }

    // ------------------------------------------------------------ negative cases

    [Fact] // illegal — a regressing delivery counter is a wiring bug
    public void Regressing_delivery_counter_is_rejected()
    {
        // [F-046] monotonic counter guard
        long cumulative = 10;
        var monitor = new TowerSupplyMonitor(windowTicks: 90, warmupTicks: 0);
        monitor.RegisterTower(new TowerSupplyRegistration(
            Tower, Position, 3, 1, 10.0, () => cumulative));

        monitor.Tick();
        cumulative = 9; // counter went backwards between polls

        Assert.Throws<InvalidOperationException>(() => monitor.Tick());
    }

    [Theory] // illegal — constructor bounds
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Window_out_of_bounds_is_rejected(int windowTicks)
    {
        // [F-046] window bounds
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TowerSupplyMonitor(windowTicks: windowTicks));
    }

    [Fact] // illegal — negative warm-up
    public void Negative_warmup_is_rejected()
    {
        // [F-046] warmup bounds
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TowerSupplyMonitor(warmupTicks: -1));
    }

    [Fact] // illegal — malformed registration payloads
    public void Malformed_registrations_are_rejected()
    {
        // [F-046] registration validation
        var counter = new DeliveryCounter();
        Assert.Throws<ArgumentNullException>(
            () => new TowerSupplyMonitor().RegisterTower(null!));
        Assert.Throws<ArgumentException>(
            () => new TowerSupplyMonitor().RegisterTower(Registration(counter, reloadTicks: 0)));
        Assert.Throws<ArgumentException>(
            () => new TowerSupplyMonitor().RegisterTower(Registration(counter, ammoPerShot: -1)));
        Assert.Throws<ArgumentException>(
            () => new TowerSupplyMonitor().RegisterTower(Registration(counter, damagePerShot: -1)));
        Assert.Throws<ArgumentException>(() => new TowerSupplyMonitor().RegisterTower(
            new TowerSupplyRegistration(Tower, Position, 3, 1, 10.0, counter.AsFunc(), new[] { Position })));

        var monitor = new TowerSupplyMonitor();
        monitor.RegisterTower(Registration(counter));
        Assert.Throws<ArgumentException>(() => monitor.RegisterTower(Registration(counter))); // duplicate
    }

    [Fact] // illegal — unknown tower id on every accessor
    public void Unknown_tower_accessors_are_rejected()
    {
        // [F-046] unknown tower validation
        var monitor = new TowerSupplyMonitor();
        Assert.Throws<ArgumentException>(() => monitor.EfficiencyOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.PositionOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.DemandShotsPerSecondOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.FullDpsOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.DeliveryShotsPerSecondOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.BreakCellOf(Tower));
        Assert.Throws<ArgumentException>(() => monitor.NotifyRouteBlocked(Tower, new CellCoord(0, 0)));
        Assert.Throws<ArgumentException>(() => monitor.NotifyRouteRestored(Tower));
    }
}
