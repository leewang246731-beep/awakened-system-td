using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Legacy;
using EraCore.Tests.Persistence;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.CampaignLoop;

// [C-20][F-057][F-168] 新大陆生成正式通道（T-125 遗迹映射正式版）：跃迁主线
// （EraTransitionService）产出的 campaign 文档经 RelicContinentState.Rehydrate
// 用同一 RelicMapper/NewContinentFactory/RelicDigSites 生产通道重建新大陆——
// "两级存档"口径的战役壳侧消费（非测试夹具）：快照负载持久化 + (WorldSeed,
// era) 确定性推导 ⇒ 回读回放与事务时结果逐字段相等（A5：核心区集合自负载
// 解码，不做几何重算）。CS-3.1 三行制。
public class RelicContinentStateTests : SqliteSavePortTestBase
{
    private static EraTransitionResult CommitTransition(CountingSavePort port)
    {
        var pool = Pool(500, 400, 300);
        var tech = Tree(1, Transition().FinalLevels[1]);
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(E1Wonder().Id);
        return new EraTransitionService().Execute(
            Request(pool, tech, ledger, CityFacts(21, 40, 12, Mapping().Orientations),
                port, EmptyMeta(Seed, 1)));
    }

    // ---- 正式通道等价 --------------------------------------------------- legal

    [Fact] // legal — 回读回放=事务时结果：地图哈希/结局哈希/挖掘位逐字段相等（两级存档）
    public void rehydrate_replays_the_transaction_channel_field_for_field()
    {
        var counting = new CountingSavePort(CreatePort());
        EraTransitionResult committed = CommitTransition(counting);
        Assert.Equal(2, committed.ToEraIndex);

        RelicContinentState state = RelicContinentState.Rehydrate(
            counting.ReadCampaign(SaveVersion.Current), Mapping(), new LcgRng(999));
        Assert.True(state.HasContinent);
        Assert.Equal(2, state.EraIndex);
        Assert.Equal(committed.Continent.MapHash, state.Continent!.MapHash); // 入口 rng 无关（派生 seed 唯一权威）
        Assert.Equal(committed.Mapping.ComputeOutcomeHash(), state.Mapping!.ComputeOutcomeHash());
        Assert.Equal(committed.Mapping.RelicRegistry, state.Mapping.RelicRegistry);
        Assert.Equal(committed.Mapping.AbstractList, state.Mapping.AbstractList);
        Assert.Equal(committed.DigSites.Sites, state.DigSites!.Sites);
        Assert.Equal(committed.DigSites.UnanchoredRuinCount, state.DigSites.UnanchoredRuinCount);

        // A5 口径：回读侧核心区成员只来自负载解码（快照集合=持久化事实，不重算）
        Assert.True(state.Mapping.IsNoLoss);
    }

    [Fact] // legal — 确定性：同一文档两次回读产物全等（存档续读幂等）
    public void rehydrate_is_idempotent_for_the_same_document()
    {
        var counting = new CountingSavePort(CreatePort());
        _ = CommitTransition(counting);
        var meta = counting.ReadCampaign(SaveVersion.Current);
        RelicContinentState first = RelicContinentState.Rehydrate(meta, Mapping(), new LcgRng(1));
        RelicContinentState second = RelicContinentState.Rehydrate(meta, Mapping(), new LcgRng(2));
        Assert.Equal(first.Continent!.MapHash, second.Continent!.MapHash);
        Assert.Equal(first.Mapping!.ComputeOutcomeHash(), second.Mapping!.ComputeOutcomeHash());
        Assert.Equal(first.DigSites!.Sites, second.DigSites!.Sites);
    }

    // ---- boundary ----------------------------------------------------------

    [Fact] // boundary — E1 文档：无遗迹大陆（空态而非报错）
    public void era1_document_has_no_relic_continent()
    {
        var state = RelicContinentState.Rehydrate(EmptyMeta(Seed, 1), Mapping(), new LcgRng(1));
        Assert.Equal(1, state.EraIndex);
        Assert.False(state.HasContinent);
        Assert.Null(state.Continent);
        Assert.Null(state.Mapping);
        Assert.Null(state.DigSites);
    }

    [Fact] // boundary — era≥2 文档缺遗迹城快照槽=损坏存档（点名拒绝，不静默空态）
    public void post_transition_document_without_snapshot_slot_is_refused()
    {
        var meta = EmptyMeta(Seed, 2); // era=2 但零布局快照（未经跃迁事务产出）
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            RelicContinentState.Rehydrate(meta, Mapping(), new LcgRng(1)));
        Assert.Contains("legacy:relic-city-era1", ex.Message, StringComparison.Ordinal);
    }

    // ---- illegal -----------------------------------------------------------

    [Fact] // illegal — 空入参与 era<1 拒绝
    public void rehydrate_rejects_null_and_pre_era_documents()
    {
        var counting = new CountingSavePort(CreatePort());
        _ = CommitTransition(counting);
        var meta = counting.ReadCampaign(SaveVersion.Current);
        Assert.Throws<ArgumentNullException>(() =>
            RelicContinentState.Rehydrate(null!, Mapping(), new LcgRng(1)));
        Assert.Throws<ArgumentNullException>(() =>
            RelicContinentState.Rehydrate(meta, null!, new LcgRng(1)));
        Assert.Throws<ArgumentNullException>(() =>
            RelicContinentState.Rehydrate(meta, Mapping(), null!));
        Assert.Throws<InvalidOperationException>(() =>
            RelicContinentState.Rehydrate(EmptyMeta(Seed, 0), Mapping(), new LcgRng(1)));
    }
}
