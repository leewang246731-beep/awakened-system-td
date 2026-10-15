using EraCore.Contracts.Content;
using EraCore.Mods;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-156] 注册表模式——生产 ContentRegistry（T-103）：K-2 冻结契约的行为级
// 断言（精确 id/名称三段解析序/别名/排序/重复拒绝）。三行覆盖合法/边界/非法。

public class ContentRegistryTests
{
    // 单一助手（命名实参取 aliases/sourceModId）：曾用 params 重载对——
    // Item(id, type, "木箭塔") 会按普通形态优先绑定到 (id,type,sourceModId,params)
    // 把别名吃进 SourceModId（重载陷阱，已并回单方法）。
    private static JsonContentItem Item(
        string id, ContentType type, string[]? aliases = null, string? sourceModId = null) =>
        new(id, type, aliases ?? Array.Empty<string>(), $"{{\"id\":\"{id}\"}}", "test.json", 1, sourceModId, Patched: false);

    // ---- legal ----------------------------------------------------------

    [Fact] // [F-156] 精确 id 命中 + List 按 (类型内) id 序排序（确定化注册序）
    public void Register_AndList_DeterministicIdOrder()
    {
        var registry = new ContentRegistry();
        registry.Register(Item("mod-b:t2", ContentType.Building));
        registry.Register(Item("mod-a:t9", ContentType.Building));
        registry.Register(Item("mod-a:t1", ContentType.Building));
        registry.Register(Item("mod-a:e1", ContentType.Enemy));

        Assert.Equal("mod-a:t1", registry.Get(ContentType.Building, "mod-a:t1").Id);
        Assert.Equal(
            new[] { "mod-a:t1", "mod-a:t9", "mod-b:t2" },
            registry.List(ContentType.Building).Select(i => i.Id).ToArray());
        Assert.Single(registry.List(ContentType.Enemy));
        Assert.Equal(4, registry.Count);
    }

    [Fact] // [F-157] GetByName 三段解析序：全名 id → 裸 id → 别名
    public void GetByName_ResolutionOrder_FullId_ThenBareId_ThenAlias()
    {
        var registry = new ContentRegistry();
        registry.Register(Item("era1:tower-wood-arrow", ContentType.Building, aliases: new[] { "木箭塔" }));
        registry.Register(Item("mod-x:tower-wood-arrow", ContentType.Building, sourceModId: "mod-x"));

        // full namespaced id wins even when a same-bare-id modded item exists
        Assert.Equal("era1:tower-wood-arrow", registry.GetByName(ContentType.Building, "era1:tower-wood-arrow").Id);
        // bare id resolves, first-party preferred over modded bare matches
        Assert.Equal("era1:tower-wood-arrow", registry.GetByName(ContentType.Building, "tower-wood-arrow").Id);
        // alias dictionary last
        Assert.Equal("era1:tower-wood-arrow", registry.GetByName(ContentType.Building, "木箭塔").Id);
    }

    [Fact] // [F-157] FindByName 未命中返回 null（区别于 GetByName 抛 KeyNotFoundException）
    public void FindByName_Miss_ReturnsNull()
    {
        var registry = new ContentRegistry();
        Assert.Null(registry.FindByName(ContentType.Building, "no-such-name"));
    }

    // ---- boundary -------------------------------------------------------

    [Fact] // [F-156] 同 id 不同类型不冲突（K-2 键=(类型,id) 二元组）
    public void Register_SameIdDifferentKind_Allowed()
    {
        var registry = new ContentRegistry();
        registry.Register(Item("shared:id", ContentType.Building));
        registry.Register(Item("shared:id", ContentType.Enemy));

        Assert.Equal(2, registry.Count);
        Assert.Equal(ContentType.Enemy, registry.Get(ContentType.Enemy, "shared:id").ContentType);
    }

    // ---- illegal --------------------------------------------------------

    [Fact] // [F-156] 重复 (类型,id) 抛 InvalidOperationException——冲突必须显式冒头，
    //        覆盖通道在装载器 overlay 层（patch:true），注册表永不静默覆盖
    public void Register_DuplicateId_Throws()
    {
        var registry = new ContentRegistry();
        registry.Register(Item("era1:tower", ContentType.Building));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Item("era1:tower", ContentType.Building)));
    }

    [Fact] // [F-156] 非 JsonContentItem 载荷拒绝（注册表存储管线载荷，单一形状）
    public void Register_ForeignItemShape_Throws()
    {
        var registry = new ContentRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new ForeignItem("x", ContentType.Building)));
    }

    [Fact] // [F-156] Get 未知 id 抛 KeyNotFoundException
    public void Get_UnknownId_Throws()
    {
        var registry = new ContentRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.Get(ContentType.Building, "nope"));
    }

    private sealed record ForeignItem(string Id, ContentType ContentType) : IContentItem
    {
        public IReadOnlyList<string> Aliases => Array.Empty<string>();
    }
}
