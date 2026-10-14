using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Tests.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Traffic-rule tests of the productized corridor on a real 24×9 forest
/// world (C-07 T-064, [F-045]; v3.2 §6 "物流交规（补给走廊/城门）"; spike②
/// pit#2 的产品化复验): 敌绕 (the enemy field on the registered dual-cost
/// bucket rounds the lining), 民走 (porter A* takes the cost-1 road tube),
/// the two negative proofs of 缺一即漏 — without the dual price the enemy
/// cuts straight through the tube, and without the lining the enemy
/// "shadow-flies" alongside the lane at a discount — plus the pit#3 bucket
/// discipline (per-key views, ≤6 cap, LRU eviction keeping registrations,
/// rebuild on view change).
/// </summary>
// [F-045] 走廊交规正反+缺一即漏+lane×costType 分桶（T-064 验收：走廊城门交规全绿）
public class CorridorTrafficRuleTests
{
    private const long Seed = 20261016;

    private const int Width = 24;

    private const int Height = 9;

    private static readonly CellRegion Lane = new(2, 4, 20, 2);

    private static readonly CellCoord EnemySpawn = new(1, 7);

    private static readonly CellCoord Base = new(22, 1);

    /// <summary>Lined forest world: corridor rule applied (Road lane + Wall lining), price auto-derived.</summary>
    private static (GridWorld World, CorridorRule Rule) LinedWorld()
    {
        GridWorld world = LogisticsWorlds.ForestWorld(Width, Height, Seed);
        var rule = new CorridorRule(Lane, Array.Empty<int>());
        int walls = rule.ApplyStructure(world);
        Assert.Equal(80, walls); // 结构写入前置断言（见 CorridorRuleTests 细测）
        CorridorPricingResult pricing = rule.DerivePricing(world);
        Assert.Equal(50, pricing.PricePerCell); // 27 步×20=540 替代 ⇒ ceil(1.75×540/19)=50
        return (world, rule);
    }

    private static FlowField BuildField(FlowFieldManager manager, FlowFieldKey key, CellCoord target)
    {
        FlowField field = manager.Acquire(key, target);
        while (!field.IsReady)
        {
            manager.Update(FlowFieldManager.DefaultFrameBudgetMs);
            field = manager.Acquire(key, target);
        }

        return field;
    }

    private static int LaneCellsOn(IReadOnlyList<CellCoord> path, CorridorRule rule) =>
        path.Count(c => rule.IsLaneCell(c));

    // ------------------------------------------------------------ 正：敌绕 + 民走

    [Fact]
    public void EnemyRoundsTheLiningOnTheDualCostBucketAndPorterTakesTheTube()
    {
        // [F-045] 正向双证（同图同价表）：
        // 敌绕——重价桶 (lane0,costType1) 上敌梯度距离 = 北绕 27 步×20 = 540，
        //   路线 0 车道格、所有 x∈[2,21] 段格 y≤1（真绕行，不借道）；
        // 民走——民夫 A* (1,4)→(22,4) 穿管：代价 220（20×Road10 + 终点森林20），
        //   路线 20 车道格（走廊对民夫=平价近路）
        (GridWorld world, CorridorRule rule) = LinedWorld();
        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);
        rule.RegisterEnemyLane(manager, enemyKey, world);

        FlowField enemyField = BuildField(manager, enemyKey, Base);
        Assert.Equal(FlowFieldMode.Normal, enemyField.Mode);
        Assert.Equal(540L, enemyField.Distance(EnemySpawn));

        IReadOnlyList<CellCoord> enemyPath = enemyField.BuildPath(EnemySpawn);
        Assert.Equal(0, LaneCellsOn(enemyPath, rule));
        Assert.All(enemyPath, c => Assert.True(c.X < 2 || c.X > 21 || c.Y <= 1 || c.Y >= 8, $"enemy path rides the corridor band at ({c.X},{c.Y})"));

        var pathfinder = new AStarPathfinder(world);
        PathQueryResult porterRoute = pathfinder.FindPath(new CellCoord(1, 4), new CellCoord(22, 4));
        Assert.Equal(PathQueryStatus.Found, porterRoute.Status);
        Assert.Equal(220L, porterRoute.Cost);
        Assert.Equal(20, porterRoute.Cells.Count(rule.IsLaneCell));
    }

    [Fact]
    public void WithoutTheDualPriceTheEnemyCutsStraightThroughTheTube()
    {
        // [F-045] 反证之一（缺双代价即漏）：同一世界、无重价桶 (costType0=基础
        // 网格)——管内 Road 平价，敌梯度直接穿管：实测距离 324（管内 Road 平价
        // 200 + 两端森林腿与对角收边），远低于北绕 540，路线 ≥15 车道格。
        // 交规失效形态可观测
        (GridWorld world, CorridorRule rule) = LinedWorld();
        var manager = new FlowFieldManager(world);

        FlowField baseField = BuildField(manager, new FlowFieldKey(0, 0), Base);

        Assert.InRange(baseField.Distance(EnemySpawn), 300L, 360L); // 穿管实测口径
        IReadOnlyList<CellCoord> basePath = baseField.BuildPath(EnemySpawn);
        Assert.True(LaneCellsOn(basePath, rule) >= 15, "without the repricing the enemy flow must visibly use the tube");
    }

    [Fact]
    public void WithoutTheLiningTheEnemyShadowFliesAlongsideTheLane()
    {
        // [F-045] 反证之二（缺衬墙即漏）：同几何只铺 Road、不砌衬墙，重价视图
        // 照挂——敌仍不进管（0 车道格），但沿管缘森林"伴飞"：对角贴边钻出
        // 504 ≪ 砌墙后的 540，且路线含贴廊阴影格 (y∈{3,6}, x∈[2,21])。
        // 纯代价封不住走廊——衬墙+双代价必须双管齐下（spike2 pit#2 结论）
        GridWorld world = LogisticsWorlds.ForestWorld(Width, Height, Seed);
        var rule = new CorridorRule(Lane, Array.Empty<int>());
        foreach (CellCoord cell in rule.LaneCells)
        {
            world.SetTerrain(cell, TerrainKind.Road); // 只铺管，不砌墙
        }

        CorridorPricingResult pricing = rule.DerivePricing(world);
        Assert.Equal(43, pricing.PricePerCell); // 阴影行 23 步×20=460 ⇒ ceil(1.75×460/19)=43

        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);
        rule.RegisterEnemyLane(manager, enemyKey, world);
        FlowField field = BuildField(manager, enemyKey, Base);

        Assert.Equal(504L, field.Distance(EnemySpawn)); // 3×20 + 3×28 + 18×20（贴边对角×3）
        IReadOnlyList<CellCoord> path = field.BuildPath(EnemySpawn);
        Assert.Equal(0, LaneCellsOn(path, rule));
        Assert.Contains(path, c => c.X is >= 2 and <= 21 && (c.Y == 3 || c.Y == 6)); // 伴飞阴影格

        // 同图对照：砌上衬墙后同价表 ⇒ 540（真绕行）——结构半边把漏堵上
        (GridWorld lined, CorridorRule linedRule) = LinedWorld();
        var linedManager = new FlowFieldManager(lined);
        linedRule.RegisterEnemyLane(linedManager, new FlowFieldKey(0, 1), lined);
        FlowField linedField = BuildField(linedManager, new FlowFieldKey(0, 1), Base);
        Assert.Equal(540L, linedField.Distance(EnemySpawn));
        Assert.True(linedField.Distance(EnemySpawn) > field.Distance(EnemySpawn));
    }

    // ------------------------------------------------------------ pit#3: bucket discipline

    [Fact]
    public void PerKeyViewsAreCappedAtTheBucketCapAndReRegistrationReplaces()
    {
        // [F-045] pit#3 防视图数膨胀：lane×costType 每键一视图，注册数 ≤ 桶上限
        // （≤6）；第 7 键注册确实被拒；同键重注册=替换不增计数
        (GridWorld world, CorridorRule rule) = LinedWorld();
        var manager = new FlowFieldManager(world);
        var keys = Enumerable.Range(0, 6).Select(i => new FlowFieldKey(i, 1)).ToArray();

        foreach (FlowFieldKey key in keys)
        {
            rule.RegisterEnemyLane(manager, key, world);
        }

        Assert.Equal(6, manager.RegisteredViewCount);

        Assert.Throws<InvalidOperationException>(
            () => rule.RegisterEnemyLane(manager, new FlowFieldKey(6, 1), world));

        manager.RegisterGridView(keys[0], rule.CreateEnemyView(world)); // 替换不增计数
        Assert.Equal(6, manager.RegisteredViewCount);
    }

    [Fact]
    public void SevenBucketsStayUnderTheCapAndEvictedViewsSurviveForRebuild()
    {
        // [F-045] pit#3 分桶：7 个键 Acquire 后活跃桶 ≤6（LRU 逐出）；被逐出键的
        // 视图注册保留，重 Acquire 在视图上重建（敌绕距离复现，不是默认网格值）
        (GridWorld world, CorridorRule rule) = LinedWorld();
        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);
        rule.RegisterEnemyLane(manager, enemyKey, world);

        FlowField enemyField = BuildField(manager, enemyKey, Base);
        Assert.Equal(540L, enemyField.Distance(EnemySpawn));

        // 六个基础键把敌桶挤出活跃集（LRU），再重 Acquire 敌桶
        for (int i = 1; i <= 6; i++)
        {
            BuildField(manager, new FlowFieldKey(i, 0), Base);
        }

        Assert.True(manager.ActiveFieldCount <= FlowFieldManager.DefaultMaxActiveFields,
            $"active fields must stay under the cap (got {manager.ActiveFieldCount})");
        Assert.NotNull(manager.GetGridView(enemyKey)); // 注册随驱逐保留

        FlowField rebuilt = BuildField(manager, enemyKey, Base);
        Assert.Equal(540L, rebuilt.Distance(EnemySpawn)); // 在注册视图上重建 ⇒ 同一敌绕距离
    }

    [Fact]
    public void RegisteringADifferentViewFlagsTheBucketForRebuild()
    {
        // [F-045] pit#3 视图更换：敌桶建好后重注册更贵视图 → 下一次 Update 重建
        // （RebuildCount+1），新距离反映新价（540 → 70+200×60=12070 时绕行外圈
        // 反而更便宜 ⇒ 距离抬升）
        (GridWorld world, CorridorRule rule) = LinedWorld();
        var manager = new FlowFieldManager(world);
        var enemyKey = new FlowFieldKey(0, 1);
        rule.RegisterEnemyLane(manager, enemyKey, world);

        FlowField before = BuildField(manager, enemyKey, Base);
        long distanceBefore = before.Distance(EnemySpawn);
        int rebuildsBefore = manager.GetRebuildCount(enemyKey);

        // 提价：封死北侧绕行（x∈[2,21] 的 y1 行砌墙）→ 替代路只剩南侧 29 步
        for (int x = 2; x <= 21; x++)
        {
            world.SetTerrain(new CellCoord(x, 1), TerrainKind.Wall);
        }

        rule.DerivePricing(world); // 重推导 → 新价（54：北侧绕行被抬到 y0，580/19×1.75）
        rule.RegisterEnemyLane(manager, enemyKey, world); // 重注册（价变了）

        // 重注册只插脏旗——泵 Update 直到重建落地，再取新场（Acquire 在重建
        // 完成前会继续供旧场，这是 MarkDirty 同款语义）
        while (manager.GetRebuildCount(enemyKey) <= rebuildsBefore)
        {
            manager.Update(FlowFieldManager.DefaultFrameBudgetMs);
        }

        FlowField? after = manager.GetField(enemyKey);
        Assert.NotNull(after);
        Assert.True(after!.IsReady);
        Assert.True(manager.GetRebuildCount(enemyKey) > rebuildsBefore, "view change must rebuild the bucket");
        Assert.True(after.Distance(EnemySpawn) > distanceBefore, "higher price must raise the enemy distance");
    }

    // ------------------------------------------------------------ argument discipline

    [Fact] // illegal
    public void ViewRegistrationValidatesArguments()
    {
        (GridWorld world, _) = LinedWorld();
        var manager = new FlowFieldManager(world);
        var key = new FlowFieldKey(0, 1);

        Assert.Throws<ArgumentNullException>(() => manager.RegisterGridView(null!, world));
        Assert.Throws<ArgumentNullException>(() => manager.RegisterGridView(key, null!));
        Assert.Throws<ArgumentException>(() => manager.RegisterGridView(key, new MapGrid(23, 9))); // 尺寸不符
        Assert.Throws<ArgumentNullException>(() => manager.GetGridView(null!));
    }
}
