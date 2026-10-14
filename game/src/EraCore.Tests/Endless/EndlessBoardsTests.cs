using EraCore.Agent.Autopilot;
using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-080] 三榜雏形：分榜追加+全局到达序号、SP-7c 同分排序（分降/先到前）、
// 确定性编解码（同态同字节）、campaign 域保留槽位（endless:boards）合并落账——
// 他槽（legacy:campaign-ledger）原样透传，损坏载荷拒收且实例不动。
public class EndlessBoardsTests
{
    private static EndlessScoreRecord Rec(
        EndlessBoardKind board, long arrival, long waves, long elite, long boss, long affix, long score) =>
        new(board, arrival, waves, elite, boss, affix, score);

    // ------------------------------------------------ 排序 -------------

    [Fact] // legal：同分先到达者列前（SP-7c）——分降序，平分按到达序升序
    public void Ranking_is_score_desc_then_arrival_asc()
    {
        var boards = new EndlessBoards();
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, waves: 10, 0, 0, 0, score: 1000)); // arrival 1
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, waves: 12, 0, 0, 0, score: 1200)); // arrival 2
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, waves: 10, 0, 0, 0, score: 1000)); // arrival 3（同分后到）
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, waves: 8, 0, 0, 0, score: 800));   // arrival 4

        IReadOnlyList<EndlessScoreRecord> ranking = boards.Ranking(EndlessBoardKind.Manual);

        Assert.Equal(new long[] { 1200, 1000, 1000, 800 }, ranking.Select(record => record.Score));
        Assert.Equal(1, ranking[1].Arrival);  // 同分 1000：先到（1）列前
        Assert.Equal(3, ranking[2].Arrival);  // 后到（3）列后
    }

    [Fact] // legal：三榜分记——各榜只收自己的行；到达序号全局单调跨榜
    public void Boards_record_separately_with_a_global_arrival()
    {
        var boards = new EndlessBoards();
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, 10, 0, 0, 0, 1000));
        _ = boards.Add(Rec(EndlessBoardKind.Assisted, 0, 20, 0, 0, 0, 2000));
        _ = boards.Add(Rec(EndlessBoardKind.Autopilot, 0, 30, 0, 0, 0, 3000));

        Assert.Single(boards.Of(EndlessBoardKind.Manual));
        Assert.Single(boards.Of(EndlessBoardKind.Assisted));
        Assert.Single(boards.Of(EndlessBoardKind.Autopilot));
        Assert.Equal(3, boards.Arrival);
        Assert.Equal(4, boards.NextArrival);
        Assert.Equal(new long[] { 1, 2, 3 }, new[]
        {
            boards.Of(EndlessBoardKind.Manual)[0].Arrival,
            boards.Of(EndlessBoardKind.Assisted)[0].Arrival,
            boards.Of(EndlessBoardKind.Autopilot)[0].Arrival,
        });
    }

    [Fact] // legal：通道 id——手动/辅助榜自带通道；全托管榜复用 T-081 预留通道
    public void Channel_ids_follow_the_t081_hook()
    {
        Assert.Equal("endless.manual", EndlessBoardChannels.Of(EndlessBoardKind.Manual));
        Assert.Equal("endless.assisted", EndlessBoardChannels.Of(EndlessBoardKind.Assisted));
        Assert.Equal(
            AutopilotPenalties.EndlessAutopilotChannelId,
            EndlessBoardChannels.Of(EndlessBoardKind.Autopilot));
        Assert.Equal("endless.autopilot", EndlessBoardChannels.Of(EndlessBoardKind.Autopilot));
    }

    // ------------------------------------------------ 编解码 -----------

    [Fact] // legal：编解码往返逐字段相等+哈希一致（同态同字节）
    public void Encode_decode_round_trip_is_exact()
    {
        var boards = new EndlessBoards();
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, 40, 3, 1, 100, 4360));
        _ = boards.Add(Rec(EndlessBoardKind.Assisted, 0, 55, 7, 2, 400, 6340));
        _ = boards.Add(Rec(EndlessBoardKind.Autopilot, 0, 73, 9, 3, 1180, 9240));
        long hashBefore = boards.ComputeStateHash();
        byte[] payload = boards.Encode();

        var reloaded = new EndlessBoards();
        reloaded.DecodeInto(payload);

        Assert.Equal(hashBefore, reloaded.ComputeStateHash());
        Assert.Equal(boards.Arrival, reloaded.Arrival);
        Assert.Equal(boards.Of(EndlessBoardKind.Manual), reloaded.Of(EndlessBoardKind.Manual));
        Assert.Equal(boards.Of(EndlessBoardKind.Assisted), reloaded.Of(EndlessBoardKind.Assisted));
        Assert.Equal(boards.Of(EndlessBoardKind.Autopilot), reloaded.Of(EndlessBoardKind.Autopilot));
    }

    [Fact] // boundary：坏魔数/截断/负计数/错榜落位/到达序超全局计数→SaveCorrupt 且实例不动
    public void Damaged_payloads_are_refused_untouched()
    {
        var boards = new EndlessBoards();
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, 10, 0, 0, 0, 1000));
        byte[] payload = boards.Encode();
        long hashBefore = boards.ComputeStateHash();

        byte[] badMagic = (byte[])payload.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<SaveCorruptException>(() => new EndlessBoards().DecodeInto(badMagic));
        Assert.Throws<SaveCorruptException>(() => new EndlessBoards().DecodeInto(payload[..^1])); // 截断
        Assert.Throws<SaveCorruptException>(() => new EndlessBoards().DecodeInto(
            new byte[] { (byte)'E', (byte)'1', (byte)'E', (byte)'B', 2 })); // 版本不符
        Assert.Throws<ArgumentNullException>(() => new EndlessBoards().DecodeInto(null!));

        // 到达序超过全局计数：按 Encode 布局定位首条记录的 Arrival 域并篡改（counter=1）
        // magic(4)+version(1)+arrival(8)+ManualCount(4)+board(4) ⇒ 记录 Arrival 低字节在偏移 21
        byte[] forged = (byte[])payload.Clone();
        forged[21] = 99;
        Assert.Throws<SaveCorruptException>(() => new EndlessBoards().DecodeInto(forged));

        // 本实例未被任何拒收路径改动
        Assert.Equal(hashBefore, boards.ComputeStateHash());
        Assert.Single(boards.Of(EndlessBoardKind.Manual));
    }

    // ------------------------------------------------ campaign 域落账 --

    [Fact] // legal：合并写保留他槽（legacy ledger 透传）+ 精确替换 endless:boards 槽
    public void Merge_replaces_only_the_endless_slot()
    {
        var boards = new EndlessBoards();
        _ = boards.Add(Rec(EndlessBoardKind.Manual, 0, 42, 1, 1, 10, 1000));
        byte[] legacyPayload = { 1, 2, 3, 4 };
        var previous = new CampaignMetaDto(
            WorldSeed: 777,
            EraIndex: 2,
            new Dictionary<string, long> { ["era1:res-wood"] = 12 },
            new List<string> { "tech:alpha" },
            new List<string> { "relic:beta" },
            new List<LevelLayoutSnapshotDto>
            {
                new("legacy:campaign-ledger", legacyPayload),
                new(EndlessBoards.BoardsSlotId, new byte[] { 9, 9 }),
            });

        ICampaignMeta merged = EndlessBoardStore.MergeInto(boards, previous);

        Assert.Equal(777L, merged.WorldSeed);
        Assert.Equal(2, merged.EraIndex);
        Assert.Equal(12L, merged.ResourcePool["era1:res-wood"]);
        Assert.Equal(new List<string> { "tech:alpha" }, merged.UnlockedTechs.ToList());
        Assert.Equal(new List<string> { "relic:beta" }, merged.RelicRegistry.ToList());
        Assert.Equal(2, merged.LevelLayoutSnapshots.Count);
        ILevelLayoutSnapshot legacy = merged.LevelLayoutSnapshots.Single(slot => slot.LevelId == "legacy:campaign-ledger");
        Assert.Equal(legacyPayload, legacy.LayoutPayload);                        // 他槽字节不动
        ILevelLayoutSnapshot endless = merged.LevelLayoutSnapshots.Single(slot => slot.LevelId == EndlessBoards.BoardsSlotId);
        EndlessBoards reloaded = EndlessBoardStore.FromMeta(merged);
        Assert.Single(reloaded.Of(EndlessBoardKind.Manual));                      // 新载荷可读
        Assert.Equal(endless.LayoutPayload, reloaded.Encode());
    }

    [Fact] // legal：Commit→Load 落账往返（K-7 campaign 域）；空仓首局=空榜不炸
    public void Commit_and_load_round_trip_through_the_port()
    {
        var port = new InMemorySavePort();
        Assert.Empty(EndlessBoardStore.Load(port).Of(EndlessBoardKind.Manual)); // 无文档→空榜

        var boards = new EndlessBoards();
        EndlessScoreRecord stamped = EndlessBoardStore.Commit(
            boards, Rec(EndlessBoardKind.Assisted, 0, 50, 5, 1, 300, 5400), port, previousMeta: null);
        Assert.Equal(1, stamped.Arrival);
        Assert.Equal(1, port.CampaignWrites);

        EndlessBoards reloaded = EndlessBoardStore.Load(port);
        Assert.Single(reloaded.Of(EndlessBoardKind.Assisted));
        Assert.Equal(5400L, reloaded.Ranking(EndlessBoardKind.Assisted)[0].Score);
        Assert.Equal(2, reloaded.NextArrival); // 到达序号持久化续号

        _ = EndlessBoardStore.Commit(
            EndlessBoardStore.Load(port), Rec(EndlessBoardKind.Assisted, 0, 60, 5, 1, 480, 6580), port, port.ReadCampaign(SaveVersion.Current));
        EndlessBoards again = EndlessBoardStore.Load(port);
        Assert.Equal(2, again.Of(EndlessBoardKind.Assisted).Count);
        Assert.Equal(2, again.Arrival);
    }

    [Fact] // boundary：未知榜单 Add 拒绝；空 previousMeta→最小壳文档（seed 0/空族+本槽）
    public void Add_rejects_unknown_boards_and_null_meta_falls_back_to_shell()
    {
        var boards = new EndlessBoards();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => boards.Add(Rec((EndlessBoardKind)7, 0, 1, 0, 0, 0, 1)));

        ICampaignMeta shell = EndlessBoardStore.MergeInto(new EndlessBoards(), null);
        Assert.Equal(0L, shell.WorldSeed);
        Assert.Empty(shell.ResourcePool);
        Assert.Single(shell.LevelLayoutSnapshots);
        Assert.Equal(EndlessBoards.BoardsSlotId, shell.LevelLayoutSnapshots[0].LevelId);
    }
}
