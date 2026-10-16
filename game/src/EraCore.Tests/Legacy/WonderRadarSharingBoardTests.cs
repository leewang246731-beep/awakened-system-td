using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-060] 互联网奇观雷达全图共享通道（T-124）：互联网（E4，雷达全图共享，
// 附录 F.3）经 WonderRadarSharingBoard 回答唯一问题——一座雷达站是否覆盖全图。
// 建成前：覆盖=自身光环圆（Euclidean 半径，K-4 格域）；建成后：全图任意格被
// 任意站覆盖（半径顶格=远角跨度）。档位/塔表对奇观保持不可知，奇观表对塔表
// 保持不可知，本板是唯一交点（单一通道纪律，WonderThroughputBoard 同款）。
// 雷达塔行（era4:tower-radar traits.aura_radius_cells=12 + reveal_stealth）
// =夜枭隐形（era4:enemy-boss traits.stealth.countered_by_radar）的唯一反制
// 数据位——本断言面钉死『建成→效果』的最小通道语义。
public class WonderRadarSharingBoardTests
{
    private static LegacyLedger Ledger() => new(Archaeology(), Shop());

    private static WonderSpec Internet() => Wonders().Single(w => w.BonusKind == WonderRadarSharingBoard.RadarBonusKind);

    // ---- legal --------------------------------------------------------------

    [Fact] // [F-060] legal — 内容驱动通道：互联网未建=光环圆；建成后=全图共享（真 wonders.json 值）
    public void internet_wonder_flips_the_radar_coverage_to_the_whole_map()
    {
        WonderSpec internet = Internet();
        Assert.Equal("campaign:wonder-internet", internet.Id); // E4 奇观=互联网
        Assert.Equal(4, internet.EraIndex);
        Assert.Equal(1.0, internet.BonusValue, 12); // 全图份额=1.0（二值通道的内容面）

        var ledger = Ledger();
        var board = new WonderRadarSharingBoard(Wonders(), ledger);
        Assert.False(board.IsFullMapShared); // 未建：光环圆

        ledger.MarkWonderBuilt(internet.Id);
        Assert.True(board.IsFullMapShared); // 建成：雷达全图共享
    }

    [Fact] // [F-060] legal — 效果实装：20×26 图（e4.json 关卡面）西北角雷达站逐格覆盖
    public void built_internet_makes_every_map_cell_covered_from_any_station()
    {
        var ledger = Ledger();
        ledger.MarkWonderBuilt(Internet().Id);
        var board = new WonderRadarSharingBoard(Wonders(), ledger);

        const int width = 20; // e4.json 五关同几何（20×26，era.json 关卡面）
        const int height = 26;
        var station = new CellCoord(0, 0); // 最劣站址=西北角（对角远端最难覆盖）
        const double aura = 12.0; // era4:tower-radar traits.aura_radius_cells（era4/towers.json）

        int covered = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (board.IsCellCovered(station, new CellCoord(x, y), aura, width, height))
                {
                    covered++;
                }
            }
        }

        Assert.Equal(width * height, covered); // 全图 520 格全覆盖（全图化效果）
        Assert.Equal(
            Math.Sqrt(19.0 * 19.0 + 25.0 * 25.0),
            board.EffectiveRadarRadiusCells(aura, width, height), 9); // 半径顶格=远角跨度
    }

    [Fact] // [F-060] legal — 建成前覆盖=自身光环圆（Euclidean 半径，同塔行射程口径）
    public void unbuilt_channel_keeps_the_aura_circle_semantics()
    {
        var board = new WonderRadarSharingBoard(Wonders(), Ledger());
        Assert.False(board.IsFullMapShared);

        const int width = 20;
        const int height = 26;
        var station = new CellCoord(10, 13); // 图心站
        const double aura = 12.0;

        // legal — 光环内格覆盖（dx²+dy² ≤ r²）：(10,13)±12 轴向与 (2,5) 斜向在环内
        Assert.True(board.IsCellCovered(station, new CellCoord(10, 1), aura, width, height)); // dy=12
        Assert.True(board.IsCellCovered(station, new CellCoord(2, 13), aura, width, height)); // dx=8
        Assert.True(board.IsCellCovered(station, new CellCoord(2, 5), aura, width, height));  // dx=dy=8 → √128≈11.3

        // boundary — 恰在环沿（dx²+dy²=r²=144）覆盖；越环一步（+1）不覆盖
        Assert.True(board.IsCellCovered(station, new CellCoord(10, 25), aura, width, height)); // dx=0,dy=12 → 144=144
        Assert.False(board.IsCellCovered(station, new CellCoord(9, 25), aura, width, height)); // dx=1,dy=12 → 145>144
        Assert.False(board.IsCellCovered(station, new CellCoord(16, 24), aura, width, height)); // dx=6,dy=11 → 157>144

        // 未共享时远角格不覆盖——全图化效果的反面读数
        Assert.False(board.IsCellCovered(station, new CellCoord(0, 0), aura, width, height)); // 距离≈16.3 > 12
        Assert.Equal(aura, board.EffectiveRadarRadiusCells(aura, width, height), 12);
    }

    [Fact] // [F-060] legal — 其它奇观不进雷达通道：金字塔/大图书馆/世博会/戴森云建成仍光环圆
    public void non_radar_wonders_leave_the_channel_unshared()
    {
        var ledger = Ledger();
        foreach (WonderSpec wonder in Wonders().Where(w => w.BonusKind != WonderRadarSharingBoard.RadarBonusKind))
        {
            ledger.MarkWonderBuilt(wonder.Id);
        }

        var board = new WonderRadarSharingBoard(Wonders(), ledger);
        Assert.False(board.IsFullMapShared);
        Assert.Equal(
            12.0,
            board.EffectiveRadarRadiusCells(12.0, 20, 26), 12); // 半径=站自身光环（通道关闭）
    }

    // ---- boundary -----------------------------------------------------------

    [Fact] // [F-060] boundary — 越界格不在任何覆盖语义内（共享与否同拒）
    public void out_of_bounds_cells_are_never_covered()
    {
        var ledger = Ledger();
        ledger.MarkWonderBuilt(Internet().Id);
        var board = new WonderRadarSharingBoard(Wonders(), ledger);
        var station = new CellCoord(0, 0);

        Assert.False(board.IsCellCovered(station, new CellCoord(-1, 0), 12.0, 20, 26)); // x<0
        Assert.False(board.IsCellCovered(station, new CellCoord(0, 26), 12.0, 20, 26)); // y≥height
        Assert.True(board.IsCellCovered(station, new CellCoord(19, 25), 12.0, 20, 26)); // 图内远角=全覆盖（对照）
    }

    // ---- illegal ------------------------------------------------------------

    [Theory] // [F-060] illegal — 雷达份额非 [1,∞) 有限值：板构造即拒（全图共享=二值通道）
    [InlineData(0.99)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void partial_or_broken_share_fractions_are_rejected_at_construction(double value)
    {
        WonderSpec broken = Internet() with { BonusValue = value };
        Assert.Throws<InvalidOperationException>(() => new WonderRadarSharingBoard(new[] { broken }, Ledger()));
    }

    [Fact] // [F-060] illegal — 站半径非正有限值 / 非法网格：覆盖计算拒绝
    public void non_positive_station_radius_and_degenerate_grids_are_rejected()
    {
        var board = new WonderRadarSharingBoard(Wonders(), Ledger());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => board.EffectiveRadarRadiusCells(0.0, 20, 26));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => board.EffectiveRadarRadiusCells(double.NaN, 20, 26));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => board.EffectiveRadarRadiusCells(12.0, 0, 26));
    }
}
