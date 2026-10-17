using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Economy;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.CampaignLoop;

// [C-12][F-005] 战役壳（T-059）：战役域（K-7 campaign_meta）资源池 roundtrip
// 逐字段无损、整数量化守卫与域校验拦截；T-134 清账（REV-20261017-v10-fullclear
// §八3 DEBT）：E1~E5 完整战役域宿主面——SavePoolTo 写当前时代（不硬写 1）、
// LoadPoolFrom 恢复任意 E1~E5 文档、AdvanceEra 逐跳推进+网格重绘。
public class CampaignStateTests : SqliteSavePortTestBase
{
    // ---------------------------------------------------------- legal

    [Fact] // legal — 池经 campaign 域存取逐字段无损（含空池）
    public void Pool_roundtrips_field_for_field_through_the_campaign_domain()
    {
        CampaignState campaign = NewCampaign();
        Assert.Equal(500.0, campaign.Pool.Deposit(Wood, 500.0), 9);
        Assert.Equal(120.0, campaign.Pool.Deposit(new ResourceKey("era1:res-stone"), 120.0), 9);
        SqliteSavePort port = CreatePort();

        campaign.SavePoolTo(port);

        CampaignState reloaded = NewCampaign();
        foreach (ResourceGrant deposit in reloaded.LoadPoolFrom(port))
        {
            Assert.Equal(0.0, deposit.Refused, 9);
        }

        Assert.Equal(campaign.Pool.Resources.Count, reloaded.Pool.Resources.Count);
        foreach (ResourceKey resource in campaign.Pool.Resources)
        {
            Assert.Equal(campaign.Pool.AmountOf(resource), reloaded.Pool.AmountOf(resource), 9);
        }

        Assert.Equal(campaign.Pool.WarehouseLevel, reloaded.Pool.WarehouseLevel);
        Assert.Equal(campaign.Pool.Capacity, reloaded.Pool.Capacity, 9);
        Assert.Equal(WorldSeed, reloaded.WorldSeed);
    }

    [Fact] // legal — 空池 roundtrip：campaign 域文档为空资源字典
    public void Empty_pool_roundtrips_as_an_empty_document()
    {
        CampaignState campaign = NewCampaign();
        SqliteSavePort port = CreatePort();
        campaign.SavePoolTo(port);

        CampaignState reloaded = NewCampaign();
        Assert.Empty(reloaded.LoadPoolFrom(port));
        Assert.Empty(reloaded.Pool.Resources);
    }

    [Fact] // legal — 满池 roundtrip：上限满额存取同样无损（容量=入池全额）
    public void Full_pool_roundtrips_losslessly()
    {
        CampaignState campaign = NewCampaign();
        Assert.Equal(campaign.Pool.Capacity, campaign.Pool.Deposit(Wood, campaign.Pool.Capacity + 100.0), 9);
        Assert.Equal(campaign.Pool.Capacity, campaign.Pool.AmountOf(Wood), 9);
        SqliteSavePort port = CreatePort();
        campaign.SavePoolTo(port);

        CampaignState reloaded = NewCampaign();
        foreach (ResourceGrant deposit in reloaded.LoadPoolFrom(port))
        {
            Assert.Equal(0.0, deposit.Refused, 9);
        }

        Assert.Equal(campaign.Pool.AmountOf(Wood), reloaded.Pool.AmountOf(Wood), 9);
    }

    // ---------------------------------------------------------- boundary

    [Fact] // boundary — 读前无存档：LoadPoolFrom 报"库不存在"（InvalidOperationException）
    public void Load_without_a_stored_campaign_is_rejected()
    {
        CampaignState campaign = NewCampaign();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => campaign.LoadPoolFrom(CreatePort()));
        Assert.Contains("database", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------- illegal

    [Fact] // illegal — 分数余额入档被拒（K-7 long 量化纪律：banking 必须整件）
    public void Fractional_pool_amount_rejects_the_save()
    {
        CampaignState campaign = NewCampaign();
        Assert.Equal(0.5, campaign.Pool.Deposit(Wood, 0.5), 9); // 余额出现分数

        ArgumentException ex = Assert.Throws<ArgumentException>(() => campaign.SavePoolTo(CreatePort()));
        Assert.Contains(Wood.Value, ex.Message);
    }

    [Fact] // illegal — 域校验：存档时代出宿主面（终章=T-135 域）/种子与壳不匹配确实被拒
    public void Out_of_host_era_or_mismatched_seed_rejects_the_load()
    {
        // T-134 清账：宿主面=E1~E5（era=2 的存档现在可恢复——旧 E1-only 拒绝
        // 已清除；出面的拒绝面=终章 E6（T-135 域）与 0/越界档）。
        foreach (int outOfFace in new[] { 0, 6, 7 })
        {
            SqliteSavePort eraPort = CreatePort();
            eraPort.WriteCampaign(
                new CampaignMetaDto(WorldSeed, outOfFace, new Dictionary<string, long>(), new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
                SaveVersion.Current);
            CampaignState campaign = NewCampaign();
            InvalidOperationException eraEx = Assert.Throws<InvalidOperationException>(
                () => campaign.LoadPoolFrom(eraPort));
            Assert.Contains("era", eraEx.Message, StringComparison.OrdinalIgnoreCase);
        }

        SqliteSavePort seedPort = CreatePort();
        seedPort.WriteCampaign(
            new CampaignMetaDto(WorldSeed + 1, CampaignState.E1EraIndex, new Dictionary<string, long>(), new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
            SaveVersion.Current);
        CampaignState seedCampaign = NewCampaign();
        InvalidOperationException seedEx = Assert.Throws<InvalidOperationException>(
            () => seedCampaign.LoadPoolFrom(seedPort));
        Assert.Contains("seed", seedEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ T-134 清账：E1~E5 宿主面

    [Fact] // legal — 时代步行 E1→E5：SavePoolTo 写当前时代、LoadPoolFrom 任意时代恢复
    public void Era_host_face_walks_E1_to_E5_and_roundtrips_at_every_era()
    {
        // [F-005][F-003] T-134 清账（REV-20261017-v10-fullclear §八3 DEBT）：
        // SavePoolTo 不再硬写 eraIndex=1——写到哪一代就写哪一代；LoadPoolFrom
        // 恢复任意 E1~E5 文档（池按存档时代仓库等级重建=同容量无损）。时代步行
        // 经 AdvanceEra 逐跳推进（新池=跃迁事务 NewPool 形：仓库等级=时代号）。
        CampaignState campaign = NewCampaign();
        Assert.Equal(1, campaign.EraIndex);
        Assert.Equal(ResourceStock.E1WarehouseLevel, campaign.Pool.WarehouseLevel);

        for (int era = 2; era <= CampaignState.MaxHostedEraIndex; era++)
        {
            var transitionPool = new ResourceStock(new SimClock(), warehouseLevel: era);
            _ = transitionPool.Deposit(Wood, 100 * era); // 携带侧入新池（30% levy 形）
            campaign.AdvanceEra(era, transitionPool);

            Assert.Equal(era, campaign.EraIndex);
            Assert.Equal(era, campaign.Pool.WarehouseLevel);
            Assert.Equal(2000.0 * era, campaign.Pool.Capacity, 9); // F.3 池上限公式
            Assert.Equal(100.0 * era, campaign.Pool.AmountOf(Wood), 9);

            // 每一代存档→恢复：文档时代=当前时代、池逐字段无损
            SqliteSavePort port = CreatePort();
            campaign.SavePoolTo(port);
            ICampaignMeta written = port.ReadCampaign(SaveVersion.Current);
            Assert.Equal(era, written.EraIndex); // ← 清账断言：不再硬写 1

            CampaignState reloaded = NewCampaign();
            foreach (ResourceGrant deposit in reloaded.LoadPoolFrom(port))
            {
                Assert.Equal(0.0, deposit.Refused, 9);
            }

            Assert.Equal(era, reloaded.EraIndex);
            Assert.Equal(era, reloaded.Pool.WarehouseLevel);
            Assert.Equal(campaign.Pool.AmountOf(Wood), reloaded.Pool.AmountOf(Wood), 9);
        }

        Assert.Equal(CampaignState.MaxHostedEraIndex, campaign.EraIndex); // E5 上界
    }

    [Fact] // legal — 时代跳变后选图网格重绘（同 seed 确定性重放+新池入账）
    public void Era_advance_redraws_the_selection_grid_on_the_same_seed()
    {
        // 跳代=新大陆（CampaignState.AdvanceEra 语义）：网格按冻结 seed 重绘
        //（IRng replay 契约——与构造时同布局）、占领重置为主城、奖励银行切到
        // 新池实例（旧占领在新代不保留——thin 文档不携带选图占领，同新战役）。
        // 高 Level 密度夹具网格（camp 0.05/level 0.70）：主城 4 邻接带必有可占
        // Level 节点（Occupy 语义=未占 Level 且邻接已占地面；seed 固定=确定性）。
        var campaign = new CampaignState(
            WorldSeed, new SimRng(WorldSeed), 9, 9,
            new CampaignGridOptions(0.05, 0.70));
        CampaignGrid grid = campaign.Grid;
        var home = grid.Home;

        var adjacent = new[]
        {
            (home.X, home.Y - 1), (home.X, home.Y + 1), (home.X - 1, home.Y), (home.X + 1, home.Y),
        };
        var selectable = adjacent
            .Where(cell => grid.TryGetCell(cell.Item1, cell.Item2)?.Kind == CampaignCellKind.Level)
            .ToList();
        Assert.NotEmpty(selectable); // 固定 seed 下必然（level 0.70 密度）
        var (nodeX, nodeY) = selectable[0];

        _ = grid.Occupy(nodeX, nodeY); // E1 占一个主城邻接点（返回结算记录，占用态走 IsOccupied）
        Assert.Equal(2, grid.OccupiedCells.Count);

        var era2Pool = new ResourceStock(new SimClock(), warehouseLevel: 2);
        campaign.AdvanceEra(2, era2Pool);

        CampaignGrid after = campaign.Grid;
        Assert.Same(grid, after); // 同一网格实例重绘（不换对象——Grid 引用稳定）
        Assert.Single(after.OccupiedCells); // 占领重置=仅主城
        Assert.True(after.IsOccupied(home.X, home.Y));
        Assert.False(after.IsOccupied(nodeX, nodeY));

        // 同 seed 重绘=布局逐格全等（重放契约：与同参新建网格对照）
        var fresh = new CampaignState(
            WorldSeed, new SimRng(WorldSeed), 9, 9, new CampaignGridOptions(0.05, 0.70));
        for (int y = 0; y < after.Height; y++)
        {
            for (int x = 0; x < after.Width; x++)
            {
                Assert.Equal(
                    fresh.Grid.TryGetCell(x, y)?.Kind,
                    after.TryGetCell(x, y)?.Kind);
            }
        }
    }

    [Fact] // illegal — 时代步行守卫：跳步/越界/仓库等级不符均拒
    public void Era_advance_refuses_skips_out_of_face_and_mismatched_pools()
    {
        CampaignState campaign = NewCampaign();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => campaign.AdvanceEra(3, new ResourceStock(new SimClock(), warehouseLevel: 3))); // 跳步 E1→E3
        Assert.Throws<ArgumentOutOfRangeException>(
            () => campaign.AdvanceEra(6, new ResourceStock(new SimClock(), warehouseLevel: 6))); // 终章=T-135 域
        Assert.Throws<ArgumentOutOfRangeException>(
            () => campaign.AdvanceEra(1, new ResourceStock(new SimClock(), warehouseLevel: 1))); // 倒退
        Assert.Throws<ArgumentException>(
            () => campaign.AdvanceEra(2, new ResourceStock(new SimClock(), warehouseLevel: 4))); // 仓库等级≠时代
        Assert.Equal(1, campaign.EraIndex); // 全拒后状态不变
    }
}
