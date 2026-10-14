using EraCore.Agent.Governance;
using EraCore.Cinematics;
using EraCore.IO;
using EraCore.Progression;
using Xunit;
using static EraCore.Tests.Cinematics.VictoryCinematicsFixture;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-002] T-087 结算状态入存档：既定演出先存后演（重进不重播、
// 可回看取同字节）、最新覆盖（同关再结算以新决策为准）、缺文件/坏 JSON/
// 缺 required 成员回出厂缺省（结算标记永不阻断启动）、Encode/Decode 逐
// 字节稳定、空 id/null 入参拒绝。沙盒同 CinematicProgressStoreTests
// （D 盘开发根下 .tmp-tests，gitignore 运行时数据）。
public class VictorySettlementStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public VictorySettlementStoreTests()
    {
        _store = new DevFileStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static VictorySettlementArchive ArchiveOf(ObjectiveTier tier, string levelId) =>
        VictorySettlementStore.Upsert(
            VictorySettlementArchive.Default,
            new VictorySettlementRecord
            {
                LevelId = levelId,
                Decided = Decided(tier),
                Viewed = false,
            });

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 缺文件：出厂缺省（什么都没结算过）
    public void Load_missing_file_yields_the_factory_default()
    {
        Assert.Empty(VictorySettlementStore.Load(_store).Records);
        Assert.Empty(VictorySettlementArchive.Default.Records);
        Assert.False(_store.Exists(VictorySettlementStore.RelativePath));
    }

    [Fact] // legal — 先存后演往返：存档重载取回逐字节相同的既定演出（RL12 持久面）
    public void Save_then_load_roundtrips_the_decided_bytes()
    {
        DecidedVictoryPerformance decided = Decided(ObjectiveTier.GoldExtra);
        VictorySettlementStore.Save(_store, ArchiveOf(ObjectiveTier.GoldExtra, "campaign:level-e1-01"));

        VictorySettlementArchive reloaded = VictorySettlementStore.Load(_store);
        VictorySettlementRecord record = Assert.Single(reloaded.Records);

        Assert.Equal("campaign:level-e1-01", record.LevelId);
        Assert.False(record.Viewed);
        Assert.Equal(decided, record.Decided);
        Assert.Equal(VictorySettlementStore.Encode(decided), VictorySettlementStore.Encode(record.Decided));
    }

    [Fact] // legal — Encode/Decode 往返：逐字节稳定（三态等价的比较缝）
    public void Encode_decode_roundtrips_byte_stably()
    {
        DecidedVictoryPerformance decided = Decided(
            ObjectiveTier.Gold, persona: SystemPersona.Tsundere,
            badgeAtVictory: TrustBadge.Devoted, badgeAtLevelStart: TrustBadge.Trustworthy);

        Assert.Equal(VictorySettlementStore.Encode(decided),
            VictorySettlementStore.Encode(VictorySettlementStore.Decode(VictorySettlementStore.Encode(decided))));
    }

    [Fact] // legal — 最新覆盖：同关再结算以新决策为准（位置守序，他关不动）
    public void Upsert_keeps_the_latest_decision_per_level()
    {
        VictorySettlementArchive first = ArchiveOf(ObjectiveTier.Silver, "campaign:level-e1-01");
        VictorySettlementArchive other = VictorySettlementStore.Upsert(
            first,
            new VictorySettlementRecord
            {
                LevelId = "campaign:level-e1-02",
                Decided = Decided(ObjectiveTier.Bronze),
                Viewed = false,
            });
        VictorySettlementArchive updated = VictorySettlementStore.Upsert(
            other,
            new VictorySettlementRecord
            {
                LevelId = "campaign:level-e1-01",
                Decided = Decided(ObjectiveTier.GoldExtra),
                Viewed = false,
            });

        Assert.Equal(2, updated.Records.Count); // 两关各一条
        Assert.Equal(ObjectiveTier.GoldExtra,
            VictorySettlementStore.LatestFor(updated, "campaign:level-e1-01")!.Decided.Tier);
        Assert.Equal(ObjectiveTier.Bronze,
            VictorySettlementStore.LatestFor(updated, "campaign:level-e1-02")!.Decided.Tier);
        Assert.Null(VictorySettlementStore.LatestFor(updated, "campaign:level-e9-99"));
    }

    [Fact] // legal — MarkViewed：回看完成标记（值拷贝幂等）
    public void MarkViewed_flags_the_record()
    {
        VictorySettlementRecord record = Assert.Single(ArchiveOf(ObjectiveTier.Gold, "L").Records);

        Assert.False(record.Viewed);
        Assert.True(VictorySettlementStore.MarkViewed(record).Viewed);
        Assert.Equal(record.Decided, VictorySettlementStore.MarkViewed(record).Decided);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 坏 JSON：回出厂缺省不抛（存档损坏=没有可回看结算，不阻断启动）
    public void Load_corrupt_file_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(VictorySettlementStore.RelativePath, new byte[] { 0x7B, 0x00, 0xFF });

        Assert.Equal(VictorySettlementArchive.Default, VictorySettlementStore.Load(_store));
    }

    [Fact] // boundary — 缺 required 成员：回出厂缺省（源生成解码拒绝半截文件）
    public void Load_member_incomplete_file_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(
            VictorySettlementStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(VictorySettlementArchive.Default, VictorySettlementStore.Load(_store));
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 空/空白关卡 id：Upsert/LatestFor 拒绝
    public void Blank_level_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => VictorySettlementStore.Upsert(
            VictorySettlementArchive.Default,
            new VictorySettlementRecord { LevelId = " ", Decided = Decided(ObjectiveTier.Gold), Viewed = false }));
        Assert.Throws<ArgumentException>(
            () => VictorySettlementStore.LatestFor(VictorySettlementArchive.Default, ""));
    }

    [Fact] // illegal — null 入参：逐 API 拒绝
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => VictorySettlementStore.Load(null!));
        Assert.Throws<ArgumentNullException>(() => VictorySettlementStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => VictorySettlementStore.Upsert(null!, Assert.Single(ArchiveOf(ObjectiveTier.Gold, "L").Records)));
        Assert.Throws<ArgumentNullException>(
            () => VictorySettlementStore.LatestFor(null!, "L"));
        Assert.Throws<ArgumentNullException>(() => VictorySettlementStore.MarkViewed(null!));
        Assert.Throws<ArgumentNullException>(() => VictorySettlementStore.Encode(null!));
        Assert.Throws<ArgumentNullException>(() => VictorySettlementStore.Decode(null!));
    }

    [Fact] // illegal — Decode 坏字节：JsonException（回看通道对坏数据显式失败，不静默）
    public void Decode_malformed_bytes_throws()
    {
        Assert.Throws<System.Text.Json.JsonException>(
            () => VictorySettlementStore.Decode(new byte[] { 0x01, 0x02 }));
    }

    [Fact] // illegal — Archive 内混入重复行：LatestFor 取尾行（保守合并口径）
    public void Duplicate_rows_collapse_onto_the_tail_record()
    {
        var archive = new VictorySettlementArchive
        {
            Records = new[]
            {
                new VictorySettlementRecord { LevelId = "L", Decided = Decided(ObjectiveTier.Silver), Viewed = true },
                new VictorySettlementRecord { LevelId = "L", Decided = Decided(ObjectiveTier.Gold), Viewed = false },
            },
        };

        Assert.Equal(ObjectiveTier.Gold, VictorySettlementStore.LatestFor(archive, "L")!.Decided.Tier);
    }
}
