using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Tests.Pathfinding;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the automatic enemy corridor pricing (C-07 T-064, [F-045];
/// spike2-report §5 pit#4 清偿：按"最短替代路径 × 1.5~2.0 倍"自动推导，替代
/// spike 手工标定的 350). Three hand-calculated map scenarios (plain baseline,
/// forest double-cost, underpass split) verify the measured alternative
/// costs and the derived price; multiplier band and clamp behavior, the
/// sealed-overland fallback and argument validation complete the cover.
/// </summary>
// [F-045] 敌走廊定价自动推导（pit#4：公式=ceil(带内乘子 × max(A_i/h_i))）
public class CorridorPricingTests
{
    private static MapGrid PlainGrid(int width = 24, int height = 9, int cost = 1)
    {
        var grid = new MapGrid(width, height);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                grid.SetCost(x, y, cost);
            }
        }

        return grid;
    }

    // ------------------------------------------------------------ scenario 1: plain baseline

    [Fact]
    public void PlainBaselineDerivesTwentyTwoFromTheHandCalc()
    {
        // [F-045] 手算 S1：24×9 平原图（代价 1），车道 (2,4)-(21,5)，无涵洞。
        // 嘴到嘴的陆上替代 = 绕行 23 直步 × 10 = 230（对角两端均被卡角规则
        // 拒绝：(2,4)/(21,4) 是封闭车道格）；h = 19 ⇒ P = ceil(1.75×230/19)
        // = ceil(21.18) = 22
        MapGrid grid = PlainGrid();
        var lane = new CellRegion(2, 4, 20, 2);

        CorridorPricingResult result = CorridorPricing.Derive(grid, lane, Array.Empty<int>());

        Assert.Single(result.Hops);
        Assert.Equal(19, result.Hops[0].HopCells);
        Assert.Equal(230L, result.Hops[0].AlternativeCost);
        Assert.False(result.Hops[0].Sealed);
        Assert.Equal(1.75, result.EffectiveMultiplier, 9);
        Assert.Equal(22, result.PricePerCell);
    }

    // ------------------------------------------------------------ scenario 2: forest double cost

    [Fact]
    public void ForestDetourDoublesTheAlternativeAndThePrice()
    {
        // [F-045] 手算 S2：同几何、全图森林（代价 2）——替代路 23 步 × 20 = 460
        // ⇒ P = ceil(1.75×460/19) = ceil(42.37) = 43（替代路径变贵 ⇒ 定价同比上浮）
        MapGrid grid = PlainGrid(cost: 2);
        var lane = new CellRegion(2, 4, 20, 2);

        CorridorPricingResult result = CorridorPricing.Derive(grid, lane, Array.Empty<int>());

        Assert.Equal(460L, result.Hops[0].AlternativeCost);
        Assert.Equal(43, result.PricePerCell);
    }

    // ------------------------------------------------------------ scenario 3: underpass split

    [Fact]
    public void UnderpassSplitsTheLaneIntoTwoIndependentlyPricedHops()
    {
        // [F-045] 手算 S3：车道中段涵洞（西列 x=11，洞 x11..12 开放）。两跳：
        // 西嘴→涵洞 h=9，北锚 (1,4)→(11,3) = 10+90+10 = 110（末端对角
        // (10,3)→(11,4) 被 (10,4) 封闭卡角拒绝）；涵洞→东嘴 h=10，北锚
        // (11,3)→(22,4) = 110+10 = 120。约束跳 = 110/9 = 12.22 ⇒
        // P = ceil(1.75×12.22) = 22
        MapGrid grid = PlainGrid();
        var lane = new CellRegion(2, 4, 20, 2);

        CorridorPricingResult result = CorridorPricing.Derive(grid, lane, new[] { 11 });

        Assert.Equal(2, result.Hops.Count);
        Assert.Equal(9, result.Hops[0].HopCells);
        Assert.Equal(110L, result.Hops[0].AlternativeCost);
        Assert.Equal(10, result.Hops[1].HopCells);
        Assert.Equal(120L, result.Hops[1].AlternativeCost);
        Assert.All(result.Hops, h => Assert.False(h.Sealed));
        Assert.Equal(22, result.PricePerCell);
    }

    // ------------------------------------------------------------ multiplier band

    [Theory] // legal — 带内乘子
    [InlineData(1.5, 19)]  // ceil(1.5×230/19) = ceil(18.16)
    [InlineData(1.75, 22)] // ceil(1.75×230/19) = ceil(21.18)
    [InlineData(2.0, 25)]  // ceil(2.0×230/19)  = ceil(24.21)
    public void InBandMultipliersApplyVerbatim(double multiplier, int expectedPrice)
    {
        // [F-045] 参数数据化：定价随乘子在带内线性取整变化
        CorridorPricingResult result = CorridorPricing.Derive(
            PlainGrid(), new CellRegion(2, 4, 20, 2), Array.Empty<int>(), multiplier);

        Assert.Equal(multiplier, result.EffectiveMultiplier, 9);
        Assert.Equal(expectedPrice, result.PricePerCell);
    }

    [Theory] // boundary / illegal — 带外乘子收敛到带沿
    [InlineData(1.0, 1.5)]
    [InlineData(1.49, 1.5)]
    [InlineData(2.01, 2.0)]
    [InlineData(3.0, 2.0)]
    public void OutOfBandMultipliersClampIntoTheBand(double multiplier, double effective)
    {
        // [F-045] 边界：任务卡定价带 [1.5, 2.0]，带外一律钳回带沿
        Assert.Equal(effective, CorridorPricing.ClampMultiplier(multiplier), 9);
        CorridorPricingResult result = CorridorPricing.Derive(
            PlainGrid(), new CellRegion(2, 4, 20, 2), Array.Empty<int>(), multiplier);
        Assert.Equal(effective, result.EffectiveMultiplier, 9);
    }

    // ------------------------------------------------------------ sealed overland fallback

    [Fact]
    public void SealedOverlandFallsBackToTheFloorPrice()
    {
        // [F-045] 负向-结构：x=1/x=22 两列全封 → 锚点不可通行，替代路不存在
        // （Sealed=true，代价 −1）→ 管内跳无可替代的陆上腿，定价退回下限 2，
        // 交规由衬墙结构兜底（pit#2 双管齐下的另一半）
        MapGrid grid = PlainGrid();
        grid.BlockColumn(1, 0, 8);
        grid.BlockColumn(22, 0, 8);

        CorridorPricingResult result = CorridorPricing.Derive(grid, new CellRegion(2, 4, 20, 2), Array.Empty<int>());

        Assert.True(result.Hops[0].Sealed);
        Assert.Equal(-1L, result.Hops[0].AlternativeCost);
        Assert.Equal(CorridorPricing.MinimumEnemyPrice, result.PricePerCell);
    }

    // ------------------------------------------------------------ argument validation

    [Theory] // illegal
    [InlineData("lane-height-3")]
    [InlineData("lane-no-margin-west")]
    [InlineData("lane-no-margin-north")]
    [InlineData("underpass-at-mouth")]
    [InlineData("underpass-not-ascending")]
    public void MalformedGeometryIsRejected(string site)
    {
        // [F-045] 非法：非 2 行车道/贴边车道（锚点出界）/涵洞在嘴上/涵洞乱序
        // → 构造即拒
        MapGrid grid = PlainGrid();
        (CellRegion lane, int[] underpasses) = site switch
        {
            "lane-height-3" => (new CellRegion(2, 4, 20, 3), Array.Empty<int>()),
            "lane-no-margin-west" => (new CellRegion(0, 4, 20, 2), Array.Empty<int>()),
            "lane-no-margin-north" => (new CellRegion(2, 0, 20, 2), Array.Empty<int>()),
            "underpass-at-mouth" => (new CellRegion(2, 4, 20, 2), new[] { 2 }),
            _ => (new CellRegion(2, 4, 20, 2), new[] { 15, 12 }),
        };

        Assert.Throws<ArgumentException>(() => CorridorPricing.Derive(grid, lane, underpasses));
    }

    [Fact] // illegal
    public void NullArgumentsAreRejected()
    {
        MapGrid grid = PlainGrid();
        var lane = new CellRegion(2, 4, 20, 2);
        Assert.Throws<ArgumentNullException>(() => CorridorPricing.Derive(null!, lane, Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() => CorridorPricing.Derive(grid, null!, Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() => CorridorPricing.Derive(grid, lane, null!));
    }
}
