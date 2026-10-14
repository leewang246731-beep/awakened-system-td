using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-120] [F-046] T-093 物流断流可视化（三层信息③：瓶颈红圈+断流
// 灰线+因果提示文案）：三行——合法（事件落位/TTL 衰减/同塔覆盖）、边界
// （TTL 到点即清）、非法（TTL<1/负 tick/null 事件）。
public class BottleneckHighlightTests
{
    private static SupplyBottleneckEvent MakeEvent(
        long tick = 1000,
        EntityId? towerId = null,
        CellCoord? suggestedCell = null,
        SupplyBottleneckSuggestionKind kind = SupplyBottleneckSuggestionKind.None,
        EntityId? relayId = null) => new(
        Tick: tick,
        Seq: 1,
        TowerId: towerId ?? new EntityId(7),
        TowerPosition: new CellCoord(6, 21),
        Efficiency: 0.42,
        Threshold: 0.7,
        DemandShotsPerSecond: 1.0,
        DeliveryShotsPerSecond: 0.42,
        SuggestionKind: kind,
        SuggestedCell: suggestedCell,
        SuggestedRelayId: relayId,
        SuggestedRelayStock: relayId is null ? null : 3L);

    private static readonly CellCoord Fallback = new(2, 14); // 仓库格（SimHost.WarehouseCell）

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 无建议事件：红圈落塔位、灰线=仓库→塔、因果行含效率与指向
    public void None_suggestion_rings_the_tower_and_greys_from_the_fallback()
    {
        var board = new BottleneckHighlightBoard();
        SupplyBottleneckEvent evt = MakeEvent();

        BottleneckHighlightView view = board.On(evt, Fallback);

        Assert.Equal(new CellCoord(6, 21), view.RingCell);
        Assert.Equal(Fallback, view.LineFromCell);
        Assert.Equal(new CellCoord(6, 21), view.LineToCell);
        Assert.Contains("42%", view.ReasonLine, StringComparison.Ordinal);
        Assert.Contains("检查供给线", view.ReasonLine, StringComparison.Ordinal);
        Assert.Equal(1000 + BottleneckHighlightBoard.DefaultTtlTicks, view.ExpiresAtTick);
        Assert.True(board.IsHighlighted(evt.TowerId));
    }

    [Fact] // legal — 建议事件：红圈随断点格/中转位走（T-063 断点高亮语义）；
           // 灰线源=中转建议时随中转，断点建议时回退供应商（仓库）
    public void Suggested_cell_wins_the_ring_and_the_relay_wins_the_line_source()
    {
        var board = new BottleneckHighlightBoard();
        var breakCell = new CellCoord(5, 18);
        BottleneckHighlightView breakView = board.On(
            MakeEvent(suggestedCell: breakCell, kind: SupplyBottleneckSuggestionKind.RouteBreakCell),
            Fallback);
        var relayCell = new CellCoord(9, 18);
        BottleneckHighlightView relayView = board.On(
            MakeEvent(
                towerId: new EntityId(9),
                suggestedCell: relayCell,
                kind: SupplyBottleneckSuggestionKind.LowestStockRelay,
                relayId: new EntityId(4)),
            Fallback);

        Assert.Equal(breakCell, breakView.RingCell);
        Assert.Equal(Fallback, breakView.LineFromCell); // RouteBreakCell：圈在断点，线=仓库→塔
        Assert.Equal(relayCell, relayView.RingCell);
        Assert.Equal(relayCell, relayView.LineFromCell); // LowestStockRelay：线从低库中转画
    }

    [Fact] // legal — TTL 衰减：到期清除；同塔新事件覆盖旧圈（episode 语义）
    public void Ttl_decays_and_new_episode_replaces_the_same_tower()
    {
        var board = new BottleneckHighlightBoard();
        SupplyBottleneckEvent first = MakeEvent(tick: 1000, towerId: new EntityId(7));
        board.On(first, Fallback);

        board.Tick(1000 + BottleneckHighlightBoard.DefaultTtlTicks - 1);
        Assert.Single(board.Active);

        board.On(MakeEvent(tick: 2000, towerId: new EntityId(7)), Fallback); // 同塔新事件
        Assert.Single(board.Active); // 覆盖而非并存

        board.Tick(2000 + BottleneckHighlightBoard.DefaultTtlTicks);
        Assert.Empty(board.Active);
        Assert.False(board.IsHighlighted(new EntityId(7)));
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — TTL=1 合法（出队 tick+1 即清）；过期前 Tick 可重入（幂等）
    public void Ttl_one_is_legal_and_tick_is_idempotent()
    {
        var board = new BottleneckHighlightBoard(ttlTicks: 1);
        board.On(MakeEvent(tick: 50), Fallback); // ExpiresAtTick = 51

        board.Tick(50);
        board.Tick(50); // 重入：无异常、无重复删除（50 < 51 仍在窗）
        Assert.Single(board.Active);
        board.Tick(51); // 到点即清
        Assert.Empty(board.Active);
    }

    // ---- illegal ---------------------------------------------------------

    [Fact] // illegal — TTL<1 / 负 tick / null 事件
    public void Degenerate_arguments_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BottleneckHighlightBoard(ttlTicks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BottleneckHighlightBoard().Tick(-1));
        Assert.Throws<ArgumentNullException>(() => new BottleneckHighlightBoard().On(null!, Fallback));
    }
}
