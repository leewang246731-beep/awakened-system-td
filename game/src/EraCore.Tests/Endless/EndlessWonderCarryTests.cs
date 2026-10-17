using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Tests.Legacy;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143] 奇观带入（CampaignState/战役进度联动——只读消费）：入口可带
// 已解锁奇观，Resolve 对照宿主递交的已建集合（来源=跃迁域 LegacyLedger 的
// BuiltWonderIds 只读面）；未解锁拒绝（负向锚点）；无联动（null）=T-118 透传；
// 只读证明=带入+整局+结算落账后账本哈希不变、K-7 文档不写跃迁槽（勿改跃迁域）。
public class EndlessWonderCarryTests
{
    private const long Seed = 20261432;

    private static readonly string[] Built = { "campaign:wonder-pyramid", "campaign:wonder-internet" };

    // ------------------------------------------------ Resolve 纯函数 -----

    [Fact] // legal：已解锁子集通过并保序冻结
    public void Unlocked_subset_resolves_in_request_order()
    {
        IReadOnlyList<string> carried = EndlessWonderCarry.Resolve(
            new[] { "campaign:wonder-internet", "campaign:wonder-pyramid" }, Built);

        Assert.Equal(new[] { "campaign:wonder-internet", "campaign:wonder-pyramid" }, carried);
    }

    [Fact] // legal：trim 口径匹配（带空白入参按修剪后比对，输出保原拼写）
    public void Matching_is_trimmed_and_ordinal()
    {
        IReadOnlyList<string> carried = EndlessWonderCarry.Resolve(
            new[] { "  campaign:wonder-pyramid  " }, Built);

        Assert.Equal(new[] { "  campaign:wonder-pyramid  " }, carried); // 原拼写冻结
    }

    [Fact] // illegal：未解锁奇观拒绝（战役进度未建）
    public void Locked_wonders_are_rejected()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => EndlessWonderCarry.Resolve(
                new[] { "campaign:wonder-pyramid", "campaign:wonder-great-library" }, Built));
        Assert.Contains("campaign:wonder-great-library", ex.Message);
        Assert.Contains("is not built", ex.Message); // 未解锁拒绝（英文字面锚定）
    }

    [Fact] // illegal：空白菜单拒绝（联动在场时）
    public void Blank_ids_are_rejected_under_linkage()
    {
        Assert.Throws<ArgumentException>(
            () => EndlessWonderCarry.Resolve(new[] { " " }, Built));
    }

    [Fact] // boundary：空请求+联动 → 空带入（合法：什么都不带）
    public void Empty_request_carries_nothing()
    {
        Assert.Empty(EndlessWonderCarry.Resolve(Array.Empty<string>(), Built));
    }

    [Fact] // legal：无联动（null）=T-118 透传语义（宿主自证信任面）
    public void No_linkage_passes_the_request_through()
    {
        IReadOnlyList<string> carried = EndlessWonderCarry.Resolve(
            new[] { "wonder:anything" }, builtWonderIds: null);

        Assert.Equal(new[] { "wonder:anything" }, carried);
    }

    [Fact] // boundary：空已建集合+非空请求=全拒（诚实的空进度面）
    public void Empty_built_set_rejects_everything()
    {
        Assert.Throws<ArgumentException>(
            () => EndlessWonderCarry.Resolve(new[] { "campaign:wonder-pyramid" }, Array.Empty<string>()));
    }

    // ------------------------------------------------ 入口联动（只读消费）-----

    /// <summary>最小 era 内容集（单时代：常规/精锐非 Boss 行+一 Boss 行）.</summary>
    private static EndlessEraContent Content() => new(
        EraIndex: 1,
        Enemies: new[]
        {
            Enemy("era1:light", 100),
            Enemy("era1:heavy", 900),
            Enemy("era1:boss", 5000, isBoss: true),
        },
        WaveBaseCount: 6, WaveBegin: 1, WaveSpacing: 3, WaveScaling: 1.0);

    private static EnemySpec Enemy(string id, int hp, bool isBoss = false) =>
        new(id, $"sp-{id}", $"名-{id}", isBoss, hp, 0, 1.0, 0, isBoss ? 96 : 32);

    private static EndlessEntryRequest Request(
        IReadOnlyList<string> wonders,
        IReadOnlyList<string>? built = null) => new(
        Seed, 1, EndlessParticipationMode.Manual, 0.9,
        new[] { Content() }, wonders,
        BuiltWonderIds: built);

    [Fact] // legal：入口带已解锁奇观——校验通过、配置冻结
    public void Entry_carries_unlocked_wonders_through_the_linkage()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(new[] { "campaign:wonder-pyramid" }, Built), new LcgRng(Seed));

        Assert.Equal(new[] { "campaign:wonder-pyramid" }, session.Run.Config.WonderIds);
    }

    [Fact] // illegal：入口带未解锁奇观——Begin 即拒（带不动）
    public void Entry_rejects_wonders_the_campaign_has_not_built()
    {
        Assert.Throws<ArgumentException>(
            () => EndlessEntry.Begin(
                Request(new[] { "campaign:wonder-colossus" }, Built), new LcgRng(Seed)));
    }

    [Fact] // legal：入口无联动（默认）——请求透传（T-118 行为不回退）
    public void Entry_without_linkage_keeps_the_t118_passthrough()
    {
        EndlessSession session = EndlessEntry.Begin(
            Request(new[] { "wonder:offline-any" }), new LcgRng(Seed));

        Assert.Equal(new[] { "wonder:offline-any" }, session.Run.Config.WonderIds);
    }

    // ------------------------------------------------ 只读消费证明（勿改跃迁域）-----

    [Fact] // legal：真实账本只读联动——带入+整局+落账结算后，账本哈希与已建集合不变、不写跃迁槽
    public void Real_ledger_linkage_is_read_only_across_a_full_run()
    {
        // 真内容装配账本（legacy 考古表+传承商店；IO 由测试自持，RL9）
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt("campaign:wonder-pyramid");
        long hashBefore = ledger.ComputeStateHash();

        var port = new InMemorySavePort();
        var request = new EndlessEntryRequest(
            Seed, 1, EndlessParticipationMode.Manual, 0.9,
            new[] { Content() }, new[] { "campaign:wonder-pyramid" },
            BuiltWonderIds: ledger.BuiltWonderIds); // 只读面递交
        EndlessSession session = EndlessEntry.Begin(request, new LcgRng(Seed), port);
        EndlessFixture.PlayWaves(session.Run, 12);
        _ = session.Die();

        Assert.Equal(new[] { "campaign:wonder-pyramid" }, ledger.BuiltWonderIds); // 已建集合不变
        Assert.Equal(hashBefore, ledger.ComputeStateHash());                      // 账本字节态不变
        ICampaignMeta stored = port.ReadCampaign(SaveVersion.Current);
        Assert.DoesNotContain(stored.LevelLayoutSnapshots, slot => slot.LevelId == LegacyLedger.LedgerSlotId); // 结算不写跃迁槽
        Assert.Contains(stored.LevelLayoutSnapshots, slot => slot.LevelId == EndlessBoards.BoardsSlotId);       // 只落三榜槽
    }
}
