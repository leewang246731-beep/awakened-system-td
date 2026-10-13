using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Supply;
using EraCore.World;
using Xunit;
using static EraCore.Tests.Logistics.LogisticsWorlds;
using AStarPathfinder = EraCore.Logistics.AStarPathfinder;
using StockSupplier = EraCore.Tests.Logistics.StockSupplier;
using GateReceiver = EraCore.Tests.Logistics.GateReceiver;

namespace EraCore.Tests.Supply;

/// <summary>
/// Logic tests of the carrier link guard (断链语义, T-048, F-046) on a real
/// <see cref="GridWorld"/> + A* + <see cref="PathCache"/> + porter (RL2: no
/// doubles): a full wall cut mid-walk parks the porter on the spot with
/// frozen state (停在原地/最后可达点, 效率源停摆但不崩), a single-cell wall
/// reroutes onto the fresh detour, a cut during loading holds the departure
/// (民夫不驶入被堵的路), a cut during unloading still completes the delivery
/// and then holds the return, and terrain restoration resumes everything
/// (恢复路径后效率回升的前提).
/// </summary>
// [C-07][F-040][F-046] 断链语义：断链保持/改道/装卸相位处置/恢复
public class SupplyLinkGuardTests
{
    private const long Seed = 20261014; // same seed family as the Logistics joint tests

    private static readonly ResourceKey Cargo = new("core:arrows");

    /// <summary>15×7 plain world harness: cache + porter on the straight (0,3)→(14,3) road.</summary>
    private sealed class LinkHarness
    {
        public readonly GridWorld World = PlainWorld(15, 7, Seed);
        public readonly PathCache Cache;
        public readonly Carrier Porter;
        public readonly SupplyLinkGuard Guard;

        public LinkHarness()
        {
            Cache = new PathCache(new AStarPathfinder(World));
            PathQueryResult route = Cache.GetPath(new CellCoord(0, 3), new CellCoord(14, 3));
            Assert.Equal(PathQueryStatus.Found, route.Status);
            Porter = new Carrier(
                new EntityId(9), Cargo, new StockSupplier(int.MaxValue), new GateReceiver(int.MaxValue),
                route.Cells);
            Guard = new SupplyLinkGuard(Porter, Cache, World, route.Cells);
        }

        /// <summary>Walls the full column x=7 — splits the map, no path from west to east.</summary>
        public List<CellCoord> WallColumn() => WallAndReport(Enumerable.Range(0, 7).Select(y => new CellCoord(7, y)));

        /// <summary>Walls one cell of the road — a detour around it exists.</summary>
        public List<CellCoord> WallRoadCell() => WallAndReport(new[] { new CellCoord(7, 3) });

        public void Restore(IEnumerable<CellCoord> cells) =>
            Guard.OnTerrainChanged(cells.Select(c =>
            {
                World.SetTerrain(c, TerrainKind.Plain);
                return c;
            }));

        public void Drive(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Guard.Tick();
            }
        }

        private List<CellCoord> WallAndReport(IEnumerable<CellCoord> cells)
        {
            var changed = new List<CellCoord>();
            foreach (CellCoord cell in cells)
            {
                World.SetTerrain(cell, TerrainKind.Wall);
                changed.Add(cell);
            }

            Guard.OnTerrainChanged(changed);
            return changed;
        }
    }

    [Fact] // legal — full cut mid-walk parks the porter on the spot, frozen
    public void Full_cut_mid_walk_holds_the_porter_frozen()
    {
        // [F-046] t=100: porter outbound at (5,3); wall the x=7 column ⇒ no
        // path ⇒ held: no ticks lived, no movement, break cell on the path
        var h = new LinkHarness();
        h.Drive(100);
        Assert.Equal(CarrierPhase.Outbound, h.Porter.Phase);
        Assert.Equal(new CellCoord(5, 3), h.Porter.CurrentCell);

        List<CellCoord> wall = h.WallColumn();

        Assert.False(h.Guard.IsLinked);
        Assert.Equal(new CellCoord(7, 3), h.Guard.BreakCell); // 最短路径上的断点格
        Assert.Equal(7, wall.Count);
        long ticksAlive = h.Porter.TicksAlive;
        CellCoord parked = h.Porter.CurrentCell;

        h.Drive(50); // the cut lasts — the porter stays parked

        Assert.Equal(ticksAlive, h.Porter.TicksAlive); // 停在原地：carrier.Tick 未被调用
        Assert.Equal(parked, h.Porter.CurrentCell);
        Assert.Equal(50, h.Guard.TicksHeld);
        Assert.True(h.Porter.CargoItems > 0, "cargo frozen aboard, nothing lost");

        h.Restore(wall); // 拆墙

        Assert.True(h.Guard.IsLinked);
        h.Drive(600);
        Assert.True(h.Porter.TicksAlive > ticksAlive, "carrier resumes ticking after restoration");
        Assert.True(h.Porter.DeliveredItems >= 2, $"delivery must complete after recovery, got {h.Porter.DeliveredItems}");
    }

    [Fact] // legal — a single wall cell reroutes onto the detour without holding
    public void Single_wall_mid_walk_reroutes_and_still_delivers()
    {
        // [F-040][F-046] 加墙→失效→重算→改道: one road cell walled at t=100
        // still leaves a detour — the guard reroutes (no hold ticks at the
        // wall moment) and the delivery completes
        var h = new LinkHarness();
        h.Drive(100);
        Assert.Equal(CarrierPhase.Outbound, h.Porter.Phase);

        h.WallRoadCell();

        Assert.True(h.Guard.IsLinked);
        Assert.Null(h.Guard.BreakCell); // a working detour is not a break

        h.Drive(600);

        Assert.True(h.Porter.DeliveredItems >= 2, $"detour delivery must complete, got {h.Porter.DeliveredItems}");
        Assert.Equal(0, h.Guard.TicksHeld);
    }

    [Fact] // legal — a cut while loading holds the departure, not the cargo
    public void Full_cut_while_loading_holds_the_departure()
    {
        // [F-046] t=10: porter still in the load window; wall the column —
        // departure route blocked ⇒ held at home (民夫不驶入被堵的路)
        var h = new LinkHarness();
        h.Drive(10);
        Assert.Equal(CarrierPhase.Loading, h.Porter.Phase);

        List<CellCoord> wall = h.WallColumn();

        Assert.False(h.Guard.IsLinked);
        long ticksAlive = h.Porter.TicksAlive;
        h.Drive(40);
        Assert.Equal(ticksAlive, h.Porter.TicksAlive);
        Assert.Equal(40, h.Guard.TicksHeld);
        Assert.Equal(CarrierPhase.Loading, h.Porter.Phase);

        h.Restore(wall);
        Assert.True(h.Guard.IsLinked);
        h.Drive(600);
        Assert.True(h.Porter.DeliveredItems >= 2, $"delivery must complete after the road reopens, got {h.Porter.DeliveredItems}");
    }

    [Fact] // legal — a cut while unloading completes the delivery, then holds the return
    public void Full_cut_while_unloading_completes_delivery_then_holds_return()
    {
        // [F-046] t=250: porter unloading at (14,3) (arrival t=244); wall the
        // column — the handover finishes (cargo must not strand), the return
        // leg then parks at the destination (最后可达点)
        var h = new LinkHarness();
        h.Drive(250);
        Assert.Equal(CarrierPhase.Unloading, h.Porter.Phase);

        List<CellCoord> wall = h.WallColumn();

        Assert.True(h.Guard.IsLinked); // unloading always completes

        h.Drive(20); // unload window (t=264) drains

        Assert.Equal(2L, h.Porter.DeliveredItems); // the delivery landed
        Assert.Equal(CarrierPhase.Inbound, h.Porter.Phase);
        Assert.False(h.Guard.IsLinked); // return blocked ⇒ parked
        long ticksAlive = h.Porter.TicksAlive;
        h.Drive(30);
        Assert.Equal(ticksAlive, h.Porter.TicksAlive);

        h.Restore(wall);
        // t=300 park + 224-tick walk home ⇒ Loading around t=505..524 (the
        // next departure would be t=525 — the assertion sits inside that
        // window; a longer drive would already be mid-second-cycle)
        h.Drive(240);
        Assert.Equal(CarrierPhase.Loading, h.Porter.Phase); // home again — cycle resumes
    }

    [Fact] // legal — the guard hash replays the hold/route history
    public void State_hash_replays_the_link_history()
    {
        // [F-046] guard replay hash
        long RunCut()
        {
            var h = new LinkHarness();
            h.Drive(100);
            h.WallColumn();
            h.Drive(50);
            return h.Guard.ComputeStateHash();
        }

        Assert.Equal(RunCut(), RunCut());

        long RunFree()
        {
            var h = new LinkHarness();
            h.Drive(150);
            return h.Guard.ComputeStateHash();
        }

        Assert.NotEqual(RunCut(), RunFree());
    }

    [Fact] // illegal — constructor validation
    public void Constructor_rejects_mismatched_routes()
    {
        // [F-046] route endpoint validation
        var h = new LinkHarness();
        Assert.Throws<ArgumentNullException>(() => new SupplyLinkGuard(null!, h.Cache, h.World, new[] { new CellCoord(0, 3), new CellCoord(1, 3) }));
        Assert.Throws<ArgumentNullException>(() => new SupplyLinkGuard(h.Porter, null!, h.World, new[] { new CellCoord(0, 3), new CellCoord(1, 3) }));
        Assert.Throws<ArgumentNullException>(() => new SupplyLinkGuard(h.Porter, h.Cache, null!, new[] { new CellCoord(0, 3), new CellCoord(1, 3) }));
        Assert.Throws<ArgumentNullException>(() => new SupplyLinkGuard(h.Porter, h.Cache, h.World, null!));
        Assert.Throws<ArgumentException>(() => new SupplyLinkGuard(h.Porter, h.Cache, h.World, new[] { new CellCoord(0, 3) }));
        Assert.Throws<ArgumentException>(() => new SupplyLinkGuard(
            h.Porter, h.Cache, h.World, new[] { new CellCoord(1, 3), new CellCoord(2, 3), new CellCoord(3, 3) }));
    }

    [Fact] // illegal — terrain hook requires the cell list
    public void Terrain_hook_requires_cells()
    {
        // [F-046] terrain hook validation
        var h = new LinkHarness();
        Assert.Throws<ArgumentNullException>(() => h.Guard.OnTerrainChanged(null!));
    }
}
