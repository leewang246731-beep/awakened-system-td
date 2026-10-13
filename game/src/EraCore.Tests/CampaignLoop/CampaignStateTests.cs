using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.CampaignLoop;

// [C-12][F-005] 战役壳（T-059）：战役域（K-7 campaign_meta）资源池 roundtrip
// 逐字段无损、整数量化守卫与域校验拦截。
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

    [Fact] // illegal — 域校验：存档时代/种子与壳不匹配确实被拒
    public void Mismatched_era_or_seed_rejects_the_load()
    {
        SqliteSavePort eraPort = CreatePort();
        eraPort.WriteCampaign(
            new CampaignMetaDto(WorldSeed, 2, new Dictionary<string, long>(), new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
            SaveVersion.Current);
        CampaignState campaign = NewCampaign();
        InvalidOperationException eraEx = Assert.Throws<InvalidOperationException>(
            () => campaign.LoadPoolFrom(eraPort));
        Assert.Contains("era", eraEx.Message, StringComparison.OrdinalIgnoreCase);

        SqliteSavePort seedPort = CreatePort();
        seedPort.WriteCampaign(
            new CampaignMetaDto(WorldSeed + 1, CampaignState.E1EraIndex, new Dictionary<string, long>(), new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
            SaveVersion.Current);
        InvalidOperationException seedEx = Assert.Throws<InvalidOperationException>(
            () => campaign.LoadPoolFrom(seedPort));
        Assert.Contains("seed", seedEx.Message, StringComparison.OrdinalIgnoreCase);
    }
}
