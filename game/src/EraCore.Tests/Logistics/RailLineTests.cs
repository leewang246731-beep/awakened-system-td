using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Tests.Pathfinding;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the mine-rail guided transport (C-07 全量, T-064,
/// [F-042]): construction validation, the integrity probe and the speed
/// rule "轨道格连续通行时全速、断轨降为马车速" — broken rail degrades the
/// speed to the cart tier, repair restores it, off-rail carriers count as
/// broken ahead (safe fallback).
/// </summary>
// [F-042] 矿轨档（新运输形态：沿轨道直线高速+断轨降级）
public class RailLineTests
{
    private static CellCoord[] StraightTrack(int cells = 6) =>
        Enumerable.Range(0, cells).Select(x => new CellCoord(x, 0)).ToArray();

    // ------------------------------------------------------------ construction

    [Fact]
    public void ConstructionFreezesTheTrackAndTheTwoSpeedTiers()
    {
        // [F-042] 合法：轨道=有序相邻格集合；全速 16（矿轨档）、降级 3.5（马车档）
        var rail = new RailLine(StraightTrack(), new MapGrid(6, 1));

        Assert.Equal(6, rail.Cells.Count);
        Assert.Equal(16.0, rail.RailSpeedPxPerTick, 9);
        Assert.Equal(3.5, rail.DegradedSpeedPxPerTick, 9);
        Assert.True(rail.IsRailCell(new CellCoord(0, 0)));
        Assert.True(rail.IsRailCell(new CellCoord(5, 0)));
        Assert.False(rail.IsRailCell(new CellCoord(9, 9)));
    }

    [Theory] // illegal
    [InlineData("single-cell")]
    [InlineData("gap")]
    [InlineData("diagonal-jump")]
    [InlineData("repeat")]
    public void ConstructionRejectsMalformedTracks(string site)
    {
        // [F-042] 非法：单格/断档/跳格/重复格 → 构造即拒
        CellCoord[] cells = site switch
        {
            "single-cell" => new[] { new CellCoord(0, 0) },
            "gap" => new[] { new CellCoord(0, 0), new CellCoord(2, 0) },
            "diagonal-jump" => new[] { new CellCoord(0, 0), new CellCoord(2, 2) },
            _ => new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(0, 0) },
        };
        Assert.Throws<ArgumentException>(() => new RailLine(cells, new MapGrid(8, 8)));
    }

    [Fact] // illegal
    public void ConstructionRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new RailLine(null!, new MapGrid(4, 4)));
        Assert.Throws<ArgumentNullException>(() => new RailLine(StraightTrack(2), null!));
    }

    // ------------------------------------------------------------ integrity probe

    [Fact]
    public void IntegrityProbeSeesExactlyTheCellsBetweenTheIndices()
    {
        // [F-042] 完好性探针：断轨格命中区间才判坏——(2,0) 断轨时 [0..3] 坏、
        // [3..5] 好、[0..1] 好（区间外不受影响，双向同判）
        var grid = new MapGrid(6, 3).Block(2, 0);
        var rail = new RailLine(StraightTrack(), grid);

        Assert.False(rail.IsIntact(0, 5));
        Assert.False(rail.IsIntact(0, 3));
        Assert.True(rail.IsIntact(3, 5));
        Assert.True(rail.IsIntact(0, 1));
        Assert.True(rail.IsIntact(5, 3)); // 反向区间同判
    }

    [Theory] // illegal — 越界索引
    [InlineData(-1, 2)]
    [InlineData(0, 6)]
    [InlineData(7, 2)]
    public void IntegrityProbeRejectsOutOfRangeIndices(int from, int to)
    {
        // [F-042] 负向：索引越界确实被拦
        var rail = new RailLine(StraightTrack(), new MapGrid(6, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rail.IsIntact(from, to));
    }

    // ------------------------------------------------------------ speed rule

    [Fact]
    public void IntactTrackGrantsRailSpeedAndABreakAheadDegradesToCartSpeed()
    {
        // [F-042] 断轨降级规则（任务卡口径）：前方轨道连续通行 → 16 px/tick；
        // 前方断轨 → 3.5 px/tick（马车速）；完好轨道（修复后）恢复全速
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);

        var brokenGrid = new MapGrid(6, 3).Block(3, 0);
        var brokenRail = new RailLine(StraightTrack(), brokenGrid);
        var brokenCarrier = new Carrier(
            new EntityId(1), new ResourceKey("core:ore"), supplier, receiver,
            StraightTrack(), TransportTiers.MineRail, brokenRail);
        TickMany(brokenCarrier, TransportTiers.MineRail.LoadTicks);

        Assert.Equal(CarrierPhase.Outbound, brokenCarrier.Phase);
        Assert.Equal(3.5, brokenRail.EffectiveSpeedPxPerTick(brokenCarrier), 9); // 断轨在前方：降级

        var intactRail = new RailLine(StraightTrack(), new MapGrid(6, 3));
        var intactCarrier = new Carrier(
            new EntityId(2), new ResourceKey("core:ore"), supplier, receiver,
            StraightTrack(), TransportTiers.MineRail, intactRail);
        TickMany(intactCarrier, TransportTiers.MineRail.LoadTicks);

        Assert.Equal(CarrierPhase.Outbound, intactCarrier.Phase);
        Assert.Equal(16.0, intactRail.EffectiveSpeedPxPerTick(intactCarrier), 9); // 连续通行：全速
    }

    [Fact]
    public void CarrierOffTheTrackCountsAsBrokenAhead()
    {
        // [F-042] 安全回退：载体不在轨道上（如编排器改道走陆路）→ 降级速而非异常
        var rail = new RailLine(StraightTrack(), new MapGrid(8, 8));
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var overlandRoute = new[] { new CellCoord(6, 4), new CellCoord(7, 4), new CellCoord(7, 5) };
        var carrier = new Carrier(
            new EntityId(3), new ResourceKey("core:ore"), supplier, receiver,
            overlandRoute, TransportTiers.MineRail, rail);
        TickMany(carrier, TransportTiers.MineRail.LoadTicks);

        Assert.Equal(CarrierPhase.Outbound, carrier.Phase);
        Assert.Equal(3.5, rail.EffectiveSpeedPxPerTick(carrier), 9);
    }

    [Fact] // illegal
    public void SpeedRuleRejectsNullCarrier()
    {
        var rail = new RailLine(StraightTrack(), new MapGrid(6, 1));
        Assert.Throws<ArgumentNullException>(() => rail.EffectiveSpeedPxPerTick(null!));
    }

    private static void TickMany(Carrier carrier, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            carrier.Tick();
        }
    }
}
