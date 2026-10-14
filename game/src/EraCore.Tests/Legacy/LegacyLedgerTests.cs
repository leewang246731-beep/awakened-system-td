using Xunit;
using EraCore.Legacy;
using EraCore.Persistence;

namespace EraCore.Tests.Legacy;

// [C-20][F-003] 聚合账本（T-074）：K-7 载荷编解码往返/确定性/损坏语义 +
// 跃迁遗留事实（奇观/抽象清单/转移史）。
public class LegacyLedgerTests
{
    private static LegacyLedger Ledger()
    {
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt(LegacyFixture.E1Wonder().Id);
        return ledger;
    }

    [Fact] // legal — 空账本编解码往返全等
    public void empty_ledger_roundtrips()
    {
        LegacyLedger ledger = Ledger();
        byte[] payload = ledger.Encode();
        var decoded = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        decoded.DecodeInto(payload);
        Assert.True(decoded.Encode().AsSpan().SequenceEqual(payload));
        Assert.Contains("campaign:wonder-pyramid", decoded.BuiltWonderIds);
    }

    [Fact] // legal — 全态账本往返：残卷/馆/基金兑付/奇观/抽象清单/转移史
    public void full_ledger_roundtrips_every_family()
    {
        LegacyLedger ledger = Ledger();
        _ = ledger.Scrolls.Read("legacy:scroll-mem-e1-siege");
        ledger.Memorials.RegisterMemorial();
        ledger.Memorials.RegisterMemorial();
        ledger.Fund.DepositFund(150);
        _ = ledger.Fund.Redeem("legacy:shop-bp-gilded-granary"); // 40
        ledger.InheritAbstractList(new[] { "ruin|id=1|type=t|tier=T3|cause=terrain|near=(1,2)" });
        ledger.RecordTransition(1, 2, fundGained: 42);

        byte[] payload = ledger.Encode();
        var decoded = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        decoded.DecodeInto(payload);

        Assert.Equal(new[] { "legacy:scroll-mem-e1-siege" }, decoded.Scrolls.ReadScrollIds);
        Assert.Equal(2, decoded.Memorials.Memorials.Count);
        Assert.Equal(0.04, decoded.Memorials.EffectiveResearchRelief(), 9); // 馆光环回放
        Assert.Equal(110, decoded.Fund.Balance);
        Assert.Equal(new[] { "legacy:shop-bp-gilded-granary" }, decoded.Fund.RedeemedIds);
        Assert.Contains("campaign:wonder-pyramid", decoded.BuiltWonderIds);
        Assert.Equal(ledger.AbstractList, decoded.AbstractList);
        Assert.Equal(ledger.Transitions, decoded.Transitions);
        Assert.Equal(1, decoded.Transitions[0].FromEra);
        Assert.Equal(42, decoded.Transitions[0].FundGained);
        Assert.Equal(ledger.ComputeStateHash(), decoded.ComputeStateHash());
    }

    [Fact] // legal — 编码字节确定性：同操作序两账本字节全等
    public void encoding_is_byte_deterministic()
    {
        LegacyLedger first = Ledger();
        LegacyLedger second = Ledger();
        foreach (LegacyLedger ledger in new[] { first, second })
        {
            _ = ledger.Scrolls.Read("legacy:scroll-mem-e1-porter");
            ledger.Memorials.RegisterMemorial();
            ledger.Fund.DepositFund(9);
        }

        Assert.Equal(first.Encode(), second.Encode());
    }

    [Fact] // boundary — 抽象清单幂等重放（同批 ingest 两次不双记）
    public void abstract_list_ingest_is_idempotent()
    {
        LegacyLedger ledger = Ledger();
        string[] entries = { "ruin|id=1|type=t|tier=T2|cause=resource|near=(3,4)", "abstract|id=2|type=t|tier=T4|reason=density-cap" };
        ledger.InheritAbstractList(entries);
        ledger.InheritAbstractList(entries);
        Assert.Equal(entries, ledger.AbstractList);
    }

    [Theory] // illegal — 损坏载荷抛 SaveCorruptException 且实例不动
    [InlineData("bad-magic")]
    [InlineData("truncate-tail")]
    public void damaged_payload_rejects_without_mutation(string mode)
    {
        LegacyLedger ledger = Ledger();
        byte[] payload = ledger.Encode();
        byte[] damaged = mode switch
        {
            "bad-magic" => new byte[] { 1, 2, 3, 4 }.Concat(payload[4..]).ToArray(),
            "truncate-tail" => payload[..^3],
            _ => throw new InvalidOperationException(mode),
        };
        long hashBefore = ledger.ComputeStateHash();
        Assert.Throws<SaveCorruptException>(() => ledger.DecodeInto(damaged));
        Assert.Equal(hashBefore, ledger.ComputeStateHash()); // 实例未被动过
    }

    [Fact] // illegal — 引用未知内容的载荷拒绝（未来内容变更防御）
    public void payload_referencing_unknown_content_rejects()
    {
        LegacyLedger ledger = Ledger();
        ledger.Fund.DepositFund(100);
        _ = ledger.Fund.Redeem("legacy:shop-bp-gilded-granary");
        byte[] payload = ledger.Encode();
        // 用缺该货架行的内容表解码 → SaveCorruptException（不静默丢兑付）
        var narrowedShop = new HeritageShopTable(LegacyFixture.Shop().Items.Where(i => i.Id != "legacy:shop-bp-gilded-granary").ToList());
        var stranger = new LegacyLedger(LegacyFixture.Archaeology(), narrowedShop);
        Assert.Throws<SaveCorruptException>(() => stranger.DecodeInto(payload));
    }

    [Theory] // illegal — 转移史域违例拒绝（非 +1 步长/越界）
    [InlineData(1, 3)]
    [InlineData(0, 1)]
    [InlineData(6, 7)]
    public void transition_record_rejects_illegal_steps(int from, int to)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger().RecordTransition(from, to, 0));
    }

    [Fact] // illegal — 空白奇观 id 拒绝
    public void wonder_id_must_not_be_blank()
    {
        Assert.Throws<ArgumentException>(() => Ledger().MarkWonderBuilt(" "));
    }
}
