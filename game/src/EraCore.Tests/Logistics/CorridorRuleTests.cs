using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Tests.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the productized corridor rule (C-07 T-064, [F-045];
/// spike2-report §5 pit#2 清偿): geometry (lane cells, lining cells with
/// 2-wide underpass holes), structure application onto a real
/// <see cref="GridWorld"/> (Road lane + untearable Wall lining, idempotent),
/// the enemy dual-cost view (lane repriced, passability/blood forwarded),
/// pricing delegation (pit#4) and the manager registration glue (pit#3).
/// </summary>
// [F-045] 走廊交规产品化（v3.2 §6：地形结构（衬墙）+双代价视图，缺一即漏）
public class CorridorRuleTests
{
    private const long Seed = 20261015;

    private static CorridorRule NewRule(int[]? underpasses = null) =>
        new(new CellRegion(2, 4, 20, 2), underpasses ?? Array.Empty<int>());

    // ------------------------------------------------------------ geometry

    [Fact]
    public void GeometryEnumeratesLaneAndLiningWithUnderpassHoles()
    {
        // [F-045] 几何：车道 2 行×20 列=40 格；衬墙 4 行×20 列减去涵洞 2 列
        // = 72 格（y2..3 与 y6..7，x11..12 开洞）；无涵洞时 80 格
        CorridorRule plain = NewRule();
        CorridorRule withHole = NewRule(new[] { 11 });

        Assert.Equal(40, plain.LaneCells.Count);
        Assert.Equal(80, plain.LiningCells.Count);
        Assert.Equal(72, withHole.LiningCells.Count);

        Assert.True(plain.IsLaneCell(new CellCoord(2, 4)));
        Assert.True(plain.IsLaneCell(new CellCoord(21, 5)));
        Assert.False(plain.IsLaneCell(new CellCoord(22, 4)));
        Assert.False(plain.IsLaneCell(new CellCoord(2, 6)));

        Assert.True(plain.IsLiningCell(new CellCoord(11, 3)));
        Assert.False(withHole.IsLiningCell(new CellCoord(11, 3)));
        Assert.False(withHole.IsLiningCell(new CellCoord(12, 6)));
        Assert.True(withHole.IsLiningCell(new CellCoord(13, 3)));
    }

    [Theory] // illegal
    [InlineData("lane-height-1")]
    [InlineData("lane-too-short")]
    [InlineData("underpass-at-mouth")]
    [InlineData("underpass-overlapping-mouth")]
    [InlineData("underpass-not-ascending")]
    public void MalformedGeometryIsRejected(string site)
    {
        // [F-045] 非法：单行车道/窄于 3 列/涵洞骑在嘴上/涵洞越界/乱序 → 即拒
        (CellRegion lane, int[] underpasses) = site switch
        {
            "lane-height-1" => (new CellRegion(2, 4, 20, 1), Array.Empty<int>()),
            "lane-too-short" => (new CellRegion(2, 4, 2, 2), Array.Empty<int>()),
            "underpass-at-mouth" => (new CellRegion(2, 4, 20, 2), new[] { 2 }),
            "underpass-overlapping-mouth" => (new CellRegion(2, 4, 20, 2), new[] { 20 }),
            _ => (new CellRegion(2, 4, 20, 2), new[] { 12, 11 }),
        };

        Assert.Throws<ArgumentException>(() => new CorridorRule(lane, underpasses));
    }

    [Fact] // illegal
    public void NullGeometryIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CorridorRule(null!, Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() => new CorridorRule(new CellRegion(2, 4, 20, 2), null!));
    }

    // ------------------------------------------------------------ structure application

    [Fact]
    public void ApplyStructurePavesTheLaneAndWallsTheLining()
    {
        // [F-045] 结构半边：40 格 Road 车道 + 72 格不可拆 Wall 衬墙（涵洞开），
        // 已是 Wall 的格保留；重复应用幂等（第二次 0 写入）
        GridWorld world = LogisticsWorlds.PlainWorld(24, 9, Seed);
        world.SetTerrain(new CellCoord(15, 3), TerrainKind.Wall); // 预置墙（如蛇廊柱）
        CorridorRule rule = NewRule(new[] { 11 });

        int written = rule.ApplyStructure(world);

        Assert.Equal(71, written); // 72 格衬墙位中 1 格已是 Wall
        Assert.Equal(TerrainKind.Road, world.TerrainAt(new CellCoord(2, 4)));
        Assert.Equal(TerrainKind.Road, world.TerrainAt(new CellCoord(21, 5)));
        Assert.Equal(TerrainKind.Wall, world.TerrainAt(new CellCoord(2, 2)));
        Assert.Equal(TerrainKind.Wall, world.TerrainAt(new CellCoord(21, 7)));
        Assert.Equal(TerrainKind.Plain, world.TerrainAt(new CellCoord(11, 3))); // 涵洞开
        Assert.Equal(TerrainKind.Plain, world.TerrainAt(new CellCoord(12, 6)));
        Assert.Equal(TerrainKind.Wall, world.TerrainAt(new CellCoord(15, 3))); // 预置墙保留

        Assert.Equal(0, rule.ApplyStructure(world)); // 幂等
        int wallCells = 0;
        for (int x = 0; x < 24; x++)
        {
            for (int y = 0; y < 9; y++)
            {
                if (world.TerrainAt(new CellCoord(x, y)) == TerrainKind.Wall)
                {
                    wallCells++;
                }
            }
        }

        Assert.Equal(72, wallCells); // 71 新写入 + 1 格预置蛇廊墙（保留计入）
    }

    // ------------------------------------------------------------ dual-cost view

    [Fact]
    public void EnemyViewRepricesLaneCellsAndForwardsEverythingElse()
    {
        // [F-045] 双代价半边：推导前车道=下限价 2；推导后=自动价；非车道格
        // 代价/通行性/血量原样转发（拆墙语义不受影响）
        MapGrid composed = new MapGrid(24, 9);
        composed.SetCost(9, 0, 4); // 非车道格自定义代价
        composed.Block(10, 0); // 非车道格建筑占用
        composed.Blood(10, 0, 300);
        CorridorRule rule = NewRule();
        IFlowGrid view = rule.CreateEnemyView(composed);

        Assert.Equal(CorridorPricing.MinimumEnemyPrice, rule.EnemyPricePerCell);
        Assert.Equal(CorridorPricing.MinimumEnemyPrice, view.Cost(new CellCoord(5, 4)));
        Assert.Equal(4, view.Cost(new CellCoord(9, 0)));
        Assert.False(view.Passable(new CellCoord(10, 0)));
        Assert.Equal(300, view.BloodCost(new CellCoord(10, 0)));

        rule.DerivePricing(composed);
        Assert.Equal(22, rule.EnemyPricePerCell);
        Assert.Equal(22, view.Cost(new CellCoord(5, 4)));
        Assert.Equal(4, view.Cost(new CellCoord(9, 0)));
        Assert.NotNull(rule.LastPricing);
        Assert.Equal(22, rule.LastPricing!.PricePerCell); // 推导审计结果与生效价一致
    }

    [Fact]
    public void DerivePricingReReadingTheWorldPicksUpStructuralChanges()
    {
        // [F-045] 重定价：贴廊阴影行 (y3/y6, x2..21) 砌墙后替代路被迫抬到
        // y2/y7 —— 24 步 × 10 = 250 ⇒ P: ceil(1.75×230/19)=22 →
        // ceil(1.75×250/19)=24（替代 spike 手工 350 的一次性标定）
        MapGrid composed = new MapGrid(24, 9);
        CorridorRule rule = NewRule();

        int before = rule.DerivePricing(composed).PricePerCell;
        Assert.Equal(22, before);

        for (int x = 2; x <= 21; x++)
        {
            composed.Block(x, 3);
            composed.Block(x, 6);
        }

        int after = rule.DerivePricing(composed).PricePerCell;

        Assert.Equal(24, after);
        Assert.Equal(after, rule.EnemyPricePerCell);
    }

    [Fact] // illegal
    public void EnemyViewAndPricingRejectNullGrids()
    {
        CorridorRule rule = NewRule();
        Assert.Throws<ArgumentNullException>(() => rule.CreateEnemyView(null!));
        Assert.Throws<ArgumentNullException>(() => rule.DerivePricing(null!));
    }

    // ------------------------------------------------------------ pit#3 registration glue

    [Fact]
    public void RegisterEnemyLaneAttachesTheViewToTheManagerBucket()
    {
        // [F-045] pit#3 落点：每 lane 一视图注册进 FlowFieldManager 正式接口
        //（敌 lane=重价视图，其余桶=默认网格），注册计数与视图可查
        var world = LogisticsWorlds.PlainWorld(24, 9, Seed);
        CorridorRule rule = NewRule();
        rule.ApplyStructure(world);
        rule.DerivePricing(world);
        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);

        rule.RegisterEnemyLane(manager, enemyKey, world);

        Assert.Equal(1, manager.RegisteredViewCount);
        Assert.NotNull(manager.GetGridView(enemyKey));
        // 衬墙砌好后替代路绕行 y1/y8（27 步 ×10 = 270）⇒ P = ceil(1.75×270/19) = 25
        Assert.Equal(25, manager.GetGridView(enemyKey)!.Cost(new CellCoord(5, 4)));
        Assert.Null(manager.GetGridView(new FlowFieldKey(0, 0)));
    }
}
