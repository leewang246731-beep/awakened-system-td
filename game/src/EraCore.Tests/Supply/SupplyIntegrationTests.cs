using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Supply;
using EraCore.World;
using Xunit;
using AStarPathfinder = EraCore.Logistics.AStarPathfinder;
using static EraCore.Tests.Logistics.LogisticsWorlds;

namespace EraCore.Tests.Supply;

/// <summary>
/// Full-chain integration of the 断供降速 logic layer (T-048, F-046) over
/// the REAL reused stack — no doubles (RL2): grid world, A* + path cache,
/// labor-camp ledger, wood producer, converter, porters on both legs, the
/// tower magazine, a real wood-arrow <see cref="WeaponFire"/> paced by the
/// monitor curve, and the bottleneck detector. Scenario (60 s = 1800
/// ticks): steady full supply; t=20 s a wall column fully cuts the ammo
/// leg (no reachable path ⇒ porters park, deliveries stop, tower
/// efficiency slides 1.0 → 0.0 through the window, DPS follows the curve
/// down to zero firing — no stall, no crash: the wood economy keeps
/// running and the converter backs up gracefully); t=40 s the wall is
/// torn down ⇒ paths reopen, deliveries resume, efficiency recovers to
/// 1.0 and the fire cadence with it. DPS is sampled in 5 s buckets and
/// asserted against Σefficiency/30 (the 按效率下降 coupling), and at least
/// one structured bottleneck event lands with the full payload (tower
/// id/position/efficiency/break cell on the shortest path).
/// </summary>
// [C-06][C-07][F-046] 断链全链：降速不停摆 + 瓶颈事件 + 恢复回升
public class SupplyIntegrationTests
{
    private const long Seed = 20261014; // same seed family as the joint tests

    private static readonly ResourceKey Wood = new("era1:res-wood");

    private const int CutTick = 600; // t = 20 s
    private const int RestoreTick = 1200; // t = 40 s
    private const int EndTick = 1800; // t = 60 s

    /// <summary>
    /// The replayable scenario (also the determinism harness): 20×12 plain
    /// world; 6-seat wood producer at (2,2) feeding 4 staggered wood
    /// porters into the converter at (7,2); the converter feeds 3
    /// staggered ammo porters over the tower magazine at (12,2) into a
    /// wood-arrow weapon loop paced by the monitor. Wall column x=9 cuts
    /// the ammo leg at <see cref="CutTick"/> and is torn down at
    /// <see cref="RestoreTick"/>.
    /// </summary>
    // [F-046] 场景 harness（确定性重放复用）
    internal sealed class Scenario
    {
        public readonly SteppedClock Clock = new();
        public readonly GridWorld World;
        public readonly PathCache Cache;
        public readonly EnergyLedger Ledger;
        public readonly Producer Producer;
        public readonly AmmoConverter Converter;
        public readonly TowerMagazine Magazine;
        public readonly WeaponFire Weapon;
        public readonly TowerSupplyMonitor Monitor;
        public readonly BottleneckDetector Detector;
        public readonly SuppliedWeaponFire Pacer;
        public readonly List<Carrier> WoodPorters = new();
        public readonly List<SupplyLinkGuard> WoodGuards = new();
        public readonly List<int> WoodStagger = new();
        public readonly List<Carrier> AmmoPorters = new();
        public readonly List<SupplyLinkGuard> AmmoGuards = new();
        public readonly List<int> AmmoStagger = new();
        public readonly List<long> FireTicks = new();
        public readonly List<double> EfficiencyByTick = new() { 0.0 }; // index = tick
        public readonly List<long> DeliveredByTick = new() { 0L }; // cumulative shots, index = tick
        public readonly List<SupplyBottleneckEvent> Events = new();

        public Scenario()
        {
            World = PlainWorld(20, 12, Seed);
            Cache = new PathCache(new AStarPathfinder(World));
            Ledger = new EnergyLedger(Clock);
            Ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp); // 10/s

            Producer = new Producer(new EntityId(1), Wood, seats: 6, tier: 1, Ledger, new EnergyNodeId(2));
            Converter = new AmmoConverter(Clock, Ledger, new EnergyNodeId(3), AmmoConverter.E1ArrowRecipe);

            // Wood leg: (2,2)→(7,2), 4 staggered porters keep the hopper fed
            // (≥ 1 wood/s — the converter's full-rate demand).
            PathQueryResult woodRoute = Cache.GetPath(new CellCoord(2, 2), new CellCoord(7, 2));
            Assert.Equal(PathQueryStatus.Found, woodRoute.Status);
            int[] woodStagger = { 0, 50, 100, 150 };
            for (int i = 0; i < woodStagger.Length; i++)
            {
                var porter = new Carrier(new EntityId(10 + i), Wood, Producer, Converter, woodRoute.Cells);
                WoodPorters.Add(porter);
                WoodGuards.Add(new SupplyLinkGuard(porter, Cache, World, woodRoute.Cells));
                WoodStagger.Add(woodStagger[i]);
            }

            // Combat side: real registry wood-arrow tower at (12,2) with a
            // starter quiver of one ammo item (10 shots) — everything else
            // it fires was hauled to it.
            TowerSpec spec = Tests.Combat.CombatContent.Tower("era1:tower-wood-arrow");
            Weapon = new WeaponFire(new EntityId(20), spec, initialAmmoShots: AmmoConverter.ShotsPerItem);
            Magazine = new TowerMagazine(new ResourceKey(spec.AmmoItem!), Weapon);

            // Ammo leg: (7,2)→(12,2), 3 staggered porters (arrival spacing
            // ~67 ticks < the 90-tick window ⇒ a healthy chain reads a
            // steady 1.0 instead of burst oscillation).
            PathQueryResult ammoRoute = Cache.GetPath(new CellCoord(7, 2), new CellCoord(12, 2));
            Assert.Equal(PathQueryStatus.Found, ammoRoute.Status);
            int[] ammoStagger = { 0, 67, 134 };
            for (int i = 0; i < ammoStagger.Length; i++)
            {
                var porter = new Carrier(
                    new EntityId(30 + i), Converter.Recipe.AmmoResource, Converter, Magazine, ammoRoute.Cells);
                AmmoPorters.Add(porter);
                AmmoGuards.Add(new SupplyLinkGuard(porter, Cache, World, ammoRoute.Cells));
                AmmoStagger.Add(ammoStagger[i]);
            }

            // Monitor + detector with the shipped defaults (window 90, warm-up
            // 600, threshold 0.7, streak 60).
            Monitor = new TowerSupplyMonitor();
            Monitor.RegisterTower(new TowerSupplyRegistration(
                new EntityId(20), new CellCoord(12, 2),
                spec.ReloadTicks!.Value, spec.AmmoPerShot, spec.DamagePerShot,
                () => Magazine.CumulativeShotsDelivered,
                ammoRoute.Cells));
            Monitor.RegisterRelay(new EntityId(3), new CellCoord(7, 2), () => Converter.OutputBuffer);
            Detector = new BottleneckDetector(Clock, Monitor);
            Detector.BottleneckDetected += Events.Add;
            Pacer = new SuppliedWeaponFire(Weapon, Monitor, new EntityId(20));
        }

        public long ShotsDelivered => Magazine.CumulativeShotsDelivered;

        public int FiresIn(int fromTick, int toTick) => FireTicks.Count(t => t > fromTick && t <= toTick);

        public double EfficiencyAt(int tick) => EfficiencyByTick[tick];

        /// <summary>Drives the scenario to <paramref name="untilTick"/> (wall events at their fixed ticks).</summary>
        public void Run(int untilTick)
        {
            for (int tick = 1; tick <= untilTick; tick++)
            {
                if (tick == CutTick)
                {
                    ApplyWall();
                }

                if (tick == RestoreTick)
                {
                    TearDownWall();
                }

                Step(tick);
            }
        }

        private void Step(int tick)
        {
            Clock.Advance();
            Ledger.Settle();
            Producer.Tick();
            for (int i = 0; i < WoodGuards.Count; i++)
            {
                if (tick > WoodStagger[i])
                {
                    WoodGuards[i].Tick();
                }
            }

            Converter.Tick();
            for (int i = 0; i < AmmoGuards.Count; i++)
            {
                if (tick > AmmoStagger[i])
                {
                    AmmoGuards[i].Tick();
                }
            }

            Monitor.Tick();
            EfficiencyByTick.Add(Monitor.EfficiencyOf(new EntityId(20)));
            DeliveredByTick.Add(Magazine.CumulativeShotsDelivered);
            Detector.Tick();
            Pacer.Tick();
            WeaponFireEvent? shot = Pacer.TryFire(new EntityId(99), Clock.Tick);
            if (shot is not null)
            {
                FireTicks.Add(shot.Tick);
            }
        }

        /// <summary>Walls the full column x=9 — the ammo leg has no reachable path.</summary>
        private void ApplyWall()
        {
            var changed = new List<CellCoord>();
            for (int y = 0; y < 12; y++)
            {
                var cell = new CellCoord(9, y);
                World.SetTerrain(cell, TerrainKind.Wall);
                changed.Add(cell);
            }

            foreach (SupplyLinkGuard guard in AmmoGuards)
            {
                guard.OnTerrainChanged(changed);
            }

            CellCoord? breakCell = AmmoGuards.Select(g => g.BreakCell).FirstOrDefault(c => c is not null);
            if (breakCell is not null)
            {
                Monitor.NotifyRouteBlocked(new EntityId(20), breakCell);
            }
        }

        private void TearDownWall()
        {
            var changed = new List<CellCoord>();
            for (int y = 0; y < 12; y++)
            {
                var cell = new CellCoord(9, y);
                World.SetTerrain(cell, TerrainKind.Plain);
                changed.Add(cell);
            }

            foreach (SupplyLinkGuard guard in AmmoGuards.Concat(WoodGuards))
            {
                guard.OnTerrainChanged(changed);
            }

            Monitor.NotifyRouteRestored(new EntityId(20));
        }
    }

    [Fact]
    public void FullChainCutSlowsDpsByEfficiencyAndRecovers()
    {
        // [F-046] 断链 DPS 按效率下降不停摆 + 瓶颈事件可观测 + 恢复回升
        var s = new Scenario();
        s.Run(EndTick);

        // ---- pre-cut: efficiency pegged at 1.0 (warm-up + dense arrivals)
        Assert.Equal(1.0, s.EfficiencyAt(CutTick), 9);
        Assert.Equal(1.0, s.EfficiencyAt(CutTick - 1), 9);

        // ---- cut: efficiency holds 1 while the window still contains the
        // last pre-cut delivery (~t=588), then slides to exactly 0 and
        // stays 0 for the rest of the cut (按新交付率=0)
        Assert.Equal(1.0, s.EfficiencyAt(640), 9);
        Assert.Equal(0.0, s.EfficiencyAt(700), 9);
        Assert.Equal(0.0, s.EfficiencyAt(1000), 9);
        Assert.Equal(0.0, s.EfficiencyAt(1199), 9);
        for (int tick = 700; tick <= 1199; tick++)
        {
            Assert.True(s.EfficiencyAt(tick) <= 1e-9, $"efficiency must stay 0 during the deep cut, got {s.EfficiencyAt(tick)} at {tick}");
        }

        // deliveries are truly stopped while the wall stands (计数器冻结)
        Assert.Equal(s.DeliveredByTick[800], s.DeliveredByTick[1199]);

        // ---- recovery: paths reopen, deliveries resume, efficiency returns to 1
        Assert.True(s.DeliveredByTick[EndTick] > s.DeliveredByTick[1199], "deliveries must resume after the wall comes down");
        Assert.Equal(1.0, s.EfficiencyAt(EndTick), 9);
        Assert.True(s.FiresIn(RestoreTick, EndTick) >= 10, "the tower must be firing again in the recovery phase");

        // ---- DPS 按效率下降 (sampled 5 s buckets vs Σefficiency/30)
        for (int bucket = 0; bucket < EndTick / 150; bucket++)
        {
            int from = bucket * 150;
            int to = from + 150;
            double expected = 0;
            for (int tick = from + 1; tick <= to; tick++)
            {
                expected += s.EfficiencyAt(tick) / 30.0;
            }

            int actual = s.FiresIn(from, to);
            Assert.True(
                Math.Abs(actual - expected) <= 1.0,
                $"bucket ({from},{to}]: fired {actual} vs efficiency-implied {expected:F2}");
        }

        // deep-cut bucket: zero firing while the curve reads 0
        Assert.Equal(0, s.FiresIn(900, 1050));
        // full-speed bucket before the cut and at the end: the 30-tick cadence
        Assert.Equal(5, s.FiresIn(450, 600));
        Assert.Equal(5, s.FiresIn(1650, 1800));

        // ---- bottleneck events: at least one, payload fully structured
        Assert.True(s.Events.Count >= 1, "the cut must raise a bottleneck event");
        SupplyBottleneckEvent evt = s.Events[0];
        Assert.Equal(new EntityId(20), evt.TowerId);
        Assert.Equal(new CellCoord(12, 2), evt.TowerPosition);
        Assert.True(evt.Efficiency < evt.Threshold, $"event efficiency {evt.Efficiency} must be under {evt.Threshold}");
        Assert.Equal(0.0, evt.Efficiency, 9);
        Assert.Equal(1.0, evt.DemandShotsPerSecond, 9); // wood-arrow: 1 shot/s
        Assert.True(evt.Tick > CutTick && evt.Tick < RestoreTick, "the event belongs to the cut window");
        Assert.Equal(SupplyBottleneckSuggestionKind.RouteBreakCell, evt.SuggestionKind);
        Assert.Equal(new CellCoord(9, 2), evt.SuggestedCell); // 最短路径上的断点格
        Assert.Equal(1L, evt.Seq);
        for (int i = 1; i < s.Events.Count; i++)
        {
            Assert.True(s.Events[i].Tick > s.Events[i - 1].Tick, "events must be strictly tick-ordered");
            Assert.Equal(s.Events[i - 1].Seq + 1, s.Events[i].Seq);
        }

        // ---- 不停摆/不崩: held porters parked, wood economy alive, buffers bounded
        Assert.True(s.AmmoGuards.Sum(g => g.TicksHeld) > 0, "ammo porters must have been parked by the cut");
        Assert.True(s.WoodGuards.Sum(g => g.TicksHeld) == 0, "the wood leg was never blocked");
        Assert.True(s.WoodPorters[0].DeliveredItems > 0 && s.WoodPorters[^1].DeliveredItems > 0, "wood keeps flowing during the cut");
        Assert.True(s.Converter.OutputBuffer > 0, "the converter backs up (满仓停产) instead of crashing");
        Assert.True(s.Converter.OutputBuffer <= AmmoConverter.OutputBufferCapacity, "buffer bounded");
        Assert.True(s.Producer.OutputBuffer <= Producer.OutputBufferCapacity, "producer buffer bounded");
        Assert.Equal(1.0, s.Ledger.Satisfaction, 9); // energy untouched by the route cut

        // ---- conservation laws across the whole run (starter quiver + hauls)
        long ammoInFlight = s.AmmoPorters.Sum(p => p.CargoItems);
        Assert.Equal(s.Converter.CumulativeCrafts, s.Magazine.ItemsAccepted + s.Converter.OutputBuffer + ammoInFlight);
        Assert.Equal(
            (long)AmmoConverter.ShotsPerItem + s.ShotsDelivered,
            s.Weapon.FiredCount + (long)s.Weapon.AmmoShotsRemaining);
        Assert.Equal(
            s.Producer.CumulativeHarvested,
            s.Producer.OutputBuffer + s.WoodPorters.Sum(p => p.CargoItems) + s.WoodPorters.Sum(p => p.DeliveredItems));
    }

    [Fact] // legal — the chain reaches a healthy steady state before the cut
    public void SteadyStateBeforeTheCutFeedsTheTower()
    {
        // [F-046] 前置健康态: by t=20 s deliveries dwarf the demand — the
        // magazine holds shots, efficiency is 1 and the tower fires on the
        // bare 30-tick cadence
        var s = new Scenario();
        s.Run(CutTick - 1);

        Assert.True(s.ShotsDelivered >= 40, $"expected a fed magazine by t=20 s, got {s.ShotsDelivered} shots");
        Assert.Equal(1.0, s.EfficiencyAt(CutTick - 1), 9);
        Assert.True(s.FiresIn(300, CutTick - 1) >= 8, "the tower fires on the full cadence pre-cut");
        Assert.True(s.Weapon.FiredCount > 0);
    }
}
