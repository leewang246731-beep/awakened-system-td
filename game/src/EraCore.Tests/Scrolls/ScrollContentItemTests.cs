using EraCore.Contracts.Content;
using EraCore.Scrolls;
using Xunit;

namespace EraCore.Tests.Scrolls;

// [K-2][F-038] T-071 卷轴内容注册：ScrollContentItem 以 ContentType.Scroll
// 过 IContentRegistry 同管线装载（模组与本体同门，CS-2.5）。
public class ScrollContentItemTests
{
    private static string LoadEra2ScrollsJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "scrolls", "era2.json");
            if (File.Exists(probe))
            {
                return File.ReadAllText(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/scrolls/era2.json（测试须在仓库内运行）");
    }

    [Fact]
    public void Era2_scrolls_register_under_content_type_scroll_and_list_sorted()
    {
        // legal: 7 张全量注册 → List 按 id 确定性排序。
        var registry = new TestContentRegistry();
        ScrollDataLoader.RegisterInto(registry, LoadEra2ScrollsJson());

        IReadOnlyList<IContentItem> scrolls = registry.List(ContentType.Scroll);
        Assert.Equal(7, scrolls.Count);
        Assert.Equal(
            scrolls.Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            scrolls.Select(i => i.Id).ToList());
        Assert.All(scrolls, item => Assert.Equal(ContentType.Scroll, item.ContentType));
        Assert.All(scrolls, item => Assert.IsType<ScrollContentItem>(item));
    }

    [Fact]
    public void Era2_scrolls_resolve_by_alias_for_the_commander_grammar()
    {
        // legal: GetByName 走别名词典（"瓮城反击"→era2:scroll-barbican-retort）。
        var registry = new TestContentRegistry();
        ScrollDataLoader.RegisterInto(registry, LoadEra2ScrollsJson());

        IContentItem item = registry.GetByName(ContentType.Scroll, "瓮城反击");
        Assert.Equal("era2:scroll-barbican-retort", item.Id);

        ScrollContentItem scroll = (ScrollContentItem)registry.Get(ContentType.Scroll, "era2:scroll-silence");
        Assert.Equal("SC-007", scroll.Definition.SpId);
    }

    [Fact]
    public void Duplicate_registration_surfaces_as_conflict()
    {
        // illegal: 同 kind+id 重复注册 → InvalidOperationException（冲突必须暴露）。
        var registry = new TestContentRegistry();
        string json = LoadEra2ScrollsJson();
        ScrollDataLoader.RegisterInto(registry, json);
        Assert.Throws<InvalidOperationException>(() => ScrollDataLoader.RegisterInto(registry, json));
    }

    [Fact]
    public void Unknown_scroll_lookup_throws()
    {
        // illegal: 未注册 id/别名 → KeyNotFoundException（FindByName 返回 null）。
        var registry = new TestContentRegistry();
        ScrollDataLoader.RegisterInto(registry, LoadEra2ScrollsJson());

        Assert.Null(registry.FindByName(ContentType.Scroll, "不存在的卷轴"));
        _ = Assert.Throws<KeyNotFoundException>(() => registry.Get(ContentType.Scroll, "era2:scroll-none"));
    }
}
