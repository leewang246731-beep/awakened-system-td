using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// The T-064 six-tier throughput acceptance (C-07 全量, [F-041]): every
/// ladder rung measured at the FROZEN reference distance — one-way 160 px =
/// 5 straight cells (batch2 review F5, 08-task-plan v1.2) — against the
/// tier-table formula and the v3.2 §5 design anchor (0.3/0.6/10/15/20/∞
/// 件/s), plus the tier-specific mechanics: 断轨降级 (rail degrades to cart
/// speed), 无人机无视地形 (drone crosses what ground A* cannot), and the
/// six-tier mixed-run determinism (same seed ⇒ identical hash trace).
/// </summary>
// [F-041] 六档吞吐断言表（160px 同距口径；T-064 验收主表）
public class SixTierThroughputTests
{
    private static readonly ResourceKey Grain = new("core:grain");

    /// <summary>Reference route: straight 5 cells = 160 px (T-047 caliper).</summary>
    private static CellCoord[] ReferenceRoute() =>
        Enumerable.Range(0, 6).Select(x => new CellCoord(x, 0)).ToArray();

    private static double MeasureTrip(TransportTier tier, IReadOnlyList<CellCoord> route, ITierSpeedPolicy? policy, int windowTicks)
    {
        // Flight tiers cross the direct segment: the reference chain collapses
        // to its two endpoints (same 160 px span).
        IReadOnlyList<CellCoord> flightRoute = tier.Form == TransportForm.TripFlight
            ? new[] { route[0], route[^1] }
            : route;
        var carrier = new Carrier(
            new EntityId(1), Grain, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue),
            flightRoute, tier, policy);
        for (int tick = 1; tick <= windowTicks; tick++)
        {
            carrier.Tick();
        }

        return carrier.DeliveredItems / (windowTicks / (double)Carrier.TicksPerSecond);
    }

    // ------------------------------------------------------------ the six-rung table @160 px

    [Theory] // legal — 六档同距吞吐表：行程档模拟实测 vs 公式值 vs 设计锚点
    [InlineData("porter", 600)]
    [InlineData("cart", 1000)]
    [InlineData("mine-rail", 600)]
    [InlineData("drone", 600)]
    public void TripTiersHitTheirFormulaAndAnchorAtTheReferenceDistance(string tierId, int windowTicks)
    {
        // [F-041] 行程档（民夫/马车/矿轨/无人机）：模拟窗口实测 ∈ 公式值±5%
        // 且 ∈ 设计锚点±5%（160px 冻结口径，两带都过）
        TransportTier tier = TransportTiers.ById(tierId)!;
        double formula = tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx);
        double anchor = tier.ThroughputAnchorItemsPerSecond;

        double measured = MeasureTrip(tier, ReferenceRoute(), policy: null, windowTicks);

        Assert.InRange(measured, formula * (1 - TransportTiers.AnchorTolerance), formula * (1 + TransportTiers.AnchorTolerance));
        Assert.InRange(measured, anchor * (1 - TransportTiers.AnchorTolerance), anchor * (1 + TransportTiers.AnchorTolerance));
    }

    [Fact]
    public void BeltRungHitsFifteenItemsPerSecondOnTheReferenceSpan()
    {
        // [F-041] 传送带档：6 格链覆盖 160px 参考距（两端点间）612 tick 窗口
        // ——306 个推进周期，装填 6 周期后首交（t=14），交付 = 306−6 = 300 件
        // ⇒ 300 ÷ 20.4 s = 14.71 件/s ∈ 15±5%
        var belt = new ConveyorBelt(
            ReferenceRoute(), Grain, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue));

        const int windowTicks = 612;
        for (int tick = 1; tick <= windowTicks; tick++)
        {
            belt.Tick();
        }

        Assert.Equal(300L, belt.DeliveredItems);
        double measured = belt.DeliveredItems / (windowTicks / (double)Carrier.TicksPerSecond);
        Assert.InRange(
            measured,
            TransportTiers.Belt.ThroughputAnchorItemsPerSecond * (1 - TransportTiers.AnchorTolerance),
            TransportTiers.Belt.ThroughputAnchorItemsPerSecond * (1 + TransportTiers.AnchorTolerance));
    }

    [Fact]
    public void TeleporterRungIsUnboundedAndOnlyEnergyLimited()
    {
        // [F-041] 传送阵档（∞）：单次调用瞬时搬 1200 件（≥ 60 s × 20 件/s 的
        // 无人机档等效量），能量 6000 精确扣清——限流器是能量不是时间
        var tank = new TestEnergyTank(6000);
        var link = new TeleporterLink(tank);

        int moved = link.Transfer(
            new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue), Grain, 1200);

        Assert.Equal(1200, moved);
        Assert.Equal(1200L * TeleporterLink.EnergyCostPerItem, link.EnergySpent);
        Assert.True(
            moved / (1.0 / Carrier.TicksPerSecond) > TransportTiers.Drone.ThroughputAnchorItemsPerSecond,
            "one tick's teleport must outpace the drone tier's per-second anchor");
    }

    // ------------------------------------------------------------ 矿轨断轨降级

    [Fact]
    public void BrokenRailDegradesThroughputToTheCartSpeedFormula()
    {
        // [F-042] 断轨降为马车速：两端轨格 (0,0)/(5,0) 都断 → 去回两程的
        // 前方轨道均含断点 → 全程 3.5 px/tick；离散周期 = 20 + 46 + 20 + 46
        // = 132 tick（每腿 160/3.5=45.71 → 46），吞吐 = 20 ÷ (132/30) =
        // 4.55 件/s（连续公式 4.57 的离散化），显著低于完好轨的 10 件/s
        GridWorld world = LogisticsWorlds.PlainWorld(6, 3, 20261017);
        CellCoord[] railCells = ReferenceRoute();
        var rail = new RailLine(railCells, world);
        double intact = MeasureTrip(TransportTiers.MineRail, railCells, rail, windowTicks: 600);
        Assert.InRange(
            intact,
            TransportTiers.MineRail.ThroughputAnchorItemsPerSecond * (1 - TransportTiers.AnchorTolerance),
            TransportTiers.MineRail.ThroughputAnchorItemsPerSecond * (1 + TransportTiers.AnchorTolerance));

        world.SetTerrain(new CellCoord(0, 0), TerrainKind.Wall); // 断轨在家端格
        world.SetTerrain(new CellCoord(5, 0), TerrainKind.Wall); // 断轨在目的格
        double degraded = MeasureTrip(TransportTiers.MineRail, railCells, rail, windowTicks: 1320);

        double degradedExpected = TransportTiers.MineRail.CapacityItems / (132.0 / Carrier.TicksPerSecond);
        Assert.InRange(
            degraded,
            degradedExpected * (1 - TransportTiers.AnchorTolerance),
            degradedExpected * (1 + TransportTiers.AnchorTolerance));
        Assert.True(degraded < intact * 0.9, $"broken rail must clearly degrade throughput ({degraded:F2} vs intact {intact:F2})");
    }

    // ------------------------------------------------------------ 无人机无视地形

    [Fact]
    public void DroneCrossesTerrainThatGroundSearchCannot()
    {
        // [F-042] 无人机 [NEW]：x=5 全高墙封死 11×3 图——地面 A* (0,0)→(10,0)
        // PathNotFound（负向对照），无人机两格直飞路线照常交付（160+px 直线，
        // 无视地形）
        GridWorld world = LogisticsWorlds.PlainWorld(11, 3, 20261017);
        for (int y = 0; y < 3; y++)
        {
            world.SetTerrain(new CellCoord(5, y), TerrainKind.Wall);
        }

        var groundSearch = new AStarPathfinder(world);
        Assert.Equal(PathQueryStatus.PathNotFound, groundSearch.FindPath(new CellCoord(0, 0), new CellCoord(10, 0)).Status);

        var drone = new Carrier(
            new EntityId(5), Grain, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue),
            new[] { new CellCoord(0, 0), new CellCoord(10, 0) }, TransportTiers.Drone);
        for (int tick = 1; tick <= 600; tick++)
        {
            drone.Tick();
        }

        Assert.True(drone.DeliveredItems > 0, "drone must deliver straight over the sealed column");
        Assert.Equal(320.0, Carrier.RouteLengthPx(new[] { new CellCoord(0, 0), new CellCoord(10, 0) }), 9);
    }

    [Theory] // illegal — 飞行路线恰两格；非行程档位禁入 Carrier
    [InlineData("flight-three-cells")]
    [InlineData("belt-tier")]
    public void CarrierRejectsWrongFormRoutes(string site)
    {
        // [F-042] 负向：飞行路线三格（必须恰 [home,dest] 两格）；传送带档
        // （非行程档）构造 Carrier 即拒
        var supplier = new StockSupplier(1);
        var receiver = new GateReceiver(1);
        if (site == "flight-three-cells")
        {
            Assert.Throws<ArgumentException>(
                () => new Carrier(
                    new EntityId(6), Grain, supplier, receiver,
                    new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(2, 0) }, TransportTiers.Drone));
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => new Carrier(new EntityId(6), Grain, supplier, receiver, ReferenceRoute(), TransportTiers.Belt));
        }
    }

    // ------------------------------------------------------------ 六档混跑确定性

    [Fact]
    public void SixTierMixOnOneSeededWorldReplaysIdentically()
    {
        // [F-041][F-042] 确定性：同一 seed 世界上六档并行（民夫/马车/矿轨/
        // 传送带/无人机/传送阵），每 60 tick 采样一次全态哈希——两次独立构建
        // 与运行逐点相等（CS-1.4：无时钟无随机，纯输入函数）
        List<long> RunOnce()
        {
            GridWorld world = LogisticsWorlds.ForestWorld(24, 12, 20261018);
            var corridor = new CorridorRule(new CellRegion(2, 5, 20, 2), new[] { 11 });
            corridor.ApplyStructure(world);
            corridor.DerivePricing(world);

            var resource = new ResourceKey("core:grain");
            var pathfinder = new AStarPathfinder(world);
            var cache = new PathCache(pathfinder);
            PathQueryResult tubeRoute = cache.GetPath(new CellCoord(1, 5), new CellCoord(22, 5));
            Assert.Equal(PathQueryStatus.Found, tubeRoute.Status);

            CellCoord[] railCells = Enumerable.Range(2, 20).Select(x => new CellCoord(x, 5)).ToArray();
            var rail = new RailLine(railCells, world);
            CellCoord[] beltCells = Enumerable.Range(2, 20).Select(x => new CellCoord(x, 6)).ToArray();

            var porter = new Carrier(new EntityId(101), resource, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue), tubeRoute.Cells, TransportTiers.Porter);
            var cart = new Carrier(new EntityId(102), resource, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue), tubeRoute.Cells, TransportTiers.Cart);
            var railCarrier = new Carrier(new EntityId(103), resource, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue), railCells, TransportTiers.MineRail, rail);
            var belt = new ConveyorBelt(beltCells, resource, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue));
            var drone = new Carrier(
                new EntityId(104), resource, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue),
                new[] { new CellCoord(1, 5), new CellCoord(22, 5) }, TransportTiers.Drone);
            var teleporter = new TeleporterLink(new TestEnergyTank(1_000_000));
            var teleportSource = new StockSupplier(int.MaxValue);
            var teleportReceiver = new GateReceiver(int.MaxValue);

            var trace = new List<long>();
            for (int tick = 1; tick <= 600; tick++)
            {
                porter.Tick();
                cart.Tick();
                railCarrier.Tick();
                belt.Tick();
                drone.Tick();
                teleporter.Transfer(teleportSource, teleportReceiver, resource, 100);
                if (tick % 60 == 0)
                {
                    trace.Add(HashSnapshot(porter, cart, railCarrier, belt, drone, teleporter));
                }
            }

            Assert.True(porter.DeliveredItems > 0, "porter liveness");
            Assert.True(cart.DeliveredItems > 0, "cart liveness");
            Assert.True(railCarrier.DeliveredItems > 0, "rail liveness");
            Assert.True(belt.DeliveredItems > 0, "belt liveness");
            Assert.True(drone.DeliveredItems > 0, "drone liveness");
            Assert.True(teleporter.DeliveredItems > 0, "teleporter liveness");
            return trace;
        }

        List<long> first = RunOnce();
        List<long> second = RunOnce();

        Assert.Equal(10, first.Count);
        Assert.Equal(first, second); // 同 seed 同构造 → 哈希序列逐点一致
    }

    /// <summary>Mutable in-memory energy port for the teleport tier (test seam).</summary>
    private sealed class TestEnergyTank : ITeleportEnergyPort
    {
        private int _available;

        public TestEnergyTank(int units) => _available = units;

        public int Available => _available;

        public void Draw(int units)
        {
            Guard.EnsureInRange(units, 0, _available, nameof(units));
            _available -= units;
        }
    }

    private static long HashSnapshot(
        Carrier porter, Carrier cart, Carrier railCarrier, ConveyorBelt belt, Carrier drone, TeleporterLink teleporter)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (Carrier carrier in new[] { porter, cart, railCarrier, drone })
            {
                h = Mix(h, (ulong)(int)carrier.Phase);
                h = Mix(h, (ulong)carrier.CargoItems);
                h = Mix(h, (ulong)carrier.DeliveredItems);
                h = Mix(h, (ulong)carrier.CurrentCell.X);
                h = Mix(h, (ulong)carrier.CurrentCell.Y);
                h = Mix(h, (ulong)carrier.TicksAlive);
            }

            h = Mix(h, (ulong)belt.DeliveredItems);
            h = Mix(h, (ulong)belt.ItemsOnBelt);
            h = Mix(h, (ulong)belt.StalledTicks);
            h = Mix(h, (ulong)teleporter.DeliveredItems);
            h = Mix(h, (ulong)teleporter.BufferedItems);
            h = Mix(h, (ulong)teleporter.EnergySpent);
            return (long)h;
        }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        const ulong prime = 1099511628211UL;
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (value >> shift) & 0xFFUL;
            hash *= prime;
        }

        return hash;
    }
}
