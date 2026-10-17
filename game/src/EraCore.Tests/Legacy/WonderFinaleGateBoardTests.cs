using EraCore.Legacy;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-060][F-033] 纪元之门板回归（T-135，附录 F.3 奇观链第六个：终章
// 通关之门）：wonders.json finale_gate 行 × LegacyLedger 建成态的唯一交点
//（WonderThroughputBoard/WonderRadarSharingBoard/WonderEnergyOutputBoard
// 同纪律的第六板）。通关之门语义=跃迁硬条件语义的终点形态——前五环各闸
// 一次时代跃迁（Precheck），第六环闸全游戏终局关（campaign:level-e6-final，
// levels/e6.json requires_wonder 数据位=本板谓词的关卡侧孪生）。
public class WonderFinaleGateBoardTests
{
    [Fact] // [F-060] legal — 冻结内容：纪元之门=唯一 finale_gate 行（era_index 6）
    public void Frozen_wonder_table_carries_exactly_one_finale_gate_row()
    {
        var wonders = Wonders();
        var gateRows = wonders.Where(w => w.BonusKind == "finale_gate").ToList();
        var gate = Assert.Single(gateRows);
        Assert.Equal("campaign:wonder-era-gate", gate.Id);
        Assert.Equal(6, gate.EraIndex); // 附录 F.3 奇观链第六环（终章）
        Assert.Equal("纪元之门", gate.NameZh);
        Assert.Equal(6, wonders.Count); // 奇观链六环齐备（E1 金字塔→E5 戴森云→终章纪元之门）
    }

    [Fact] // [F-060] legal — 门锁：未建纪元之门 → 终局关不可通（点名 id）
    public void Unbuilt_gate_keeps_the_final_level_locked_and_names_the_blocker()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        var board = new WonderFinaleGateBoard(Wonders(), ledger);

        Assert.False(board.FinaleLevelEnterable); // 通关之门未立——终局关锁
        Assert.Equal("campaign:wonder-era-gate", board.BlockingWonderId); // 点名提示位

        // 其它五环奇观全建（E1~E5）也不放行：第六环独立咬合（AND 族语义）
        foreach (var wonder in Wonders().Where(w => w.EraIndex <= 5))
        {
            ledger.MarkWonderBuilt(wonder.Id);
        }

        Assert.False(board.FinaleLevelEnterable);
        Assert.Equal("campaign:wonder-era-gate", board.BlockingWonderId);
    }

    [Fact] // [F-060] legal — 开门：纪元之门建成 → 终局关可通（建成后终局关可通）
    public void Built_gate_opens_the_final_level()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        var board = new WonderFinaleGateBoard(Wonders(), ledger);

        ledger.MarkWonderBuilt("campaign:wonder-era-gate");
        Assert.True(board.FinaleLevelEnterable);
        Assert.Null(board.BlockingWonderId);

        // 全链建成（六环）同形：门开（AND 族单调——多建不减开）
        foreach (var wonder in Wonders())
        {
            ledger.MarkWonderBuilt(wonder.Id);
        }

        Assert.True(board.FinaleLevelEnterable);
    }

    [Fact] // [F-060][F-055] legal — 两侧同源闭环：e6-final requires_wonder 数据位与板谓词互证
    public void Final_level_requires_wonder_data_bit_matches_the_board_predicate()
    {
        // levels/e6.json e6-final 行的 requires_wonder=板闸的关卡侧孪生——
        // 门开当且仅当 requires_wonder 指向的奇观建成（两侧同 id 同一真值）。
        var finalLevel = Tests.Levels.HandcraftedLevelData.Load("e6.json")
            .Items.Single(level => level.Id == "campaign:level-e6-final");
        Assert.Equal("campaign:wonder-era-gate", finalLevel.RequiresWonder);

        var lockedLedger = new LegacyLedger(Archaeology(), Shop());
        var board = new WonderFinaleGateBoard(Wonders(), lockedLedger);
        Assert.False(board.FinaleLevelEnterable);

        lockedLedger.MarkWonderBuilt(finalLevel.RequiresWonder!); // 数据位驱动开锁
        Assert.True(board.FinaleLevelEnterable);
    }

    [Fact] // illegal — 构造守卫：null 表/账本拒绝（第六板与四先例同构）
    public void Constructor_rejects_null_dependencies()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WonderFinaleGateBoard(null!, new LegacyLedger(Archaeology(), Shop())));
        Assert.Throws<ArgumentNullException>(() => new WonderFinaleGateBoard(Wonders(), null!));
    }
}
