using Xunit;
using EraCore.Legacy;

namespace EraCore.Tests.Legacy;

// [C-20][F-058][F-099] 知识残卷板（T-074）：F.3 同一科技节点至多 1 张执法 +
// F-099 记忆恢复载荷。
public class KnowledgeScrollBoardTests
{
    private static KnowledgeScrollBoard Board() => new(LegacyFixture.Archaeology());

    [Fact] // legal — 读残卷=恢复记忆碎片（F-099 叙事载荷）+ 减免入账
    public void reading_restores_memory_and_binds_relief()
    {
        KnowledgeScrollBoard board = Board();
        KnowledgeScrollRead read = board.Read("legacy:scroll-mem-e1-siege");
        Assert.Equal("campaign:tech-e2-arrow", read.TechNode);
        Assert.Equal(0.10, read.ReliefShare, 9);
        Assert.Contains("箭", read.MemoryZh); // 原文记忆碎片（非空壳文案）
        Assert.False(string.IsNullOrEmpty(read.MemoryEn));
        Assert.Equal(0.10, board.ReliefFor("campaign:tech-e2-arrow"), 9);
        Assert.Equal(0.0, board.ReliefFor("campaign:tech-e2-crossbow"), 9);
    }

    [Fact] // illegal — 同一张残卷重复读拒绝
    public void same_scroll_cannot_be_read_twice()
    {
        KnowledgeScrollBoard board = Board();
        _ = board.Read("legacy:scroll-mem-e1-porter");
        Assert.Throws<InvalidOperationException>(() => board.Read("legacy:scroll-mem-e1-porter"));
    }

    [Fact] // illegal — 未知残卷拒绝
    public void unknown_scroll_is_refused()
    {
        KnowledgeScrollBoard board = Board();
        Assert.Throws<ArgumentException>(() => board.Read("legacy:scroll-nope"));
        Assert.Throws<ArgumentException>(() => board.Read(" "));
    }

    [Fact] // legal — 表内四张绑定四个不同节点，全部可读且序可回放
    public void full_catalog_reads_in_order()
    {
        KnowledgeScrollBoard board = Board();
        string[] ids =
        {
            "legacy:scroll-mem-e1-siege", "legacy:scroll-mem-e1-granary",
            "legacy:scroll-mem-e1-beacon", "legacy:scroll-mem-e1-porter",
        };
        foreach (string id in ids)
        {
            KnowledgeScrollRead read = board.Read(id);
            Assert.Equal(0.10, board.ReliefFor(read.TechNode), 9);
        }

        Assert.Equal(ids, board.ReadScrollIds);
    }

    [Fact] // illegal — 同节点双张执法（F.3：同一科技节点至多 1 张）——以同节点双绑目录直测
    public void two_scrolls_bound_to_one_node_are_refused()
    {
        ArchaeologyTable table = LegacyFixture.Archaeology();
        var scrolls = table.Scrolls.ToList();
        scrolls.Add(scrolls[0] with { Id = "legacy:scroll-dup-node" }); // 同节点第二张
        var doctored = table with { Scrolls = scrolls };
        var board = new KnowledgeScrollBoard(doctored);
        _ = board.Read(scrolls[0].Id);
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => board.Read("legacy:scroll-dup-node"));
        Assert.Contains("同一科技节点", ex.Message); // 拒绝语名出 F.3 条款
    }
}
