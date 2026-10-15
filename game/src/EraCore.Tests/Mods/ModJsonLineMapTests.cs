using EraCore.Mods;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-165] JSON 模组管线——行号索引（T-051 Utf8JsonReader TokenStartIndex 先例
// 的 C-02 复刻）：file:line 行级报错的基础件。三行覆盖：合法/边界/非法。

public class ModJsonLineMapTests
{
    private const string TwoItems = """
        {
          "content_type": "Building",
          "items": [
            {
              "id": "mod-a:tower",
              "dps": 15.0
            },
            {
              "id": "mod-a:tower-2",
              "on_hit": [
                { "op": "slow_percent", "percent": 10 }
                ]
            }
          ]
        }
        """;

    // ---- legal ----------------------------------------------------------

    [Fact] // [F-165] 根/数组/字段多级指针的行号均可解析（1-based）
    public void Index_MultiLevelPointers_ResolveToOneBasedLines()
    {
        IReadOnlyDictionary<string, int> lines = ModJsonLineMap.Index(TwoItems);

        Assert.Equal(1, lines[""]);
        Assert.Equal(3, lines["/items"]);
        Assert.Equal(4, lines["/items/0"]);
        Assert.Equal(5, lines["/items/0/id"]);
        Assert.Equal(6, lines["/items/0/dps"]);
        Assert.Equal(10, lines["/items/1/on_hit"]);
        Assert.Equal(11, lines["/items/1/on_hit/0"]);
        Assert.Equal(11, lines["/items/1/on_hit/0/op"]);
        Assert.Equal(11, lines["/items/1/on_hit/0/percent"]);
    }

    [Fact] // [F-165] 汉字多字节内容下行号仍按行首换行解析（字节偏移→行换算不因多字节漂移）
    public void Index_MultibyteContent_LineNumbersStayStable()
    {
        string json = "{\n  \"name_zh\": \"木箭塔\",\n  \"items\": [\"倒刺\"]\n}";
        IReadOnlyDictionary<string, int> lines = ModJsonLineMap.Index(json);

        Assert.Equal(2, lines["/name_zh"]);
        Assert.Equal(3, lines["/items"]);
        Assert.Equal(3, lines["/items/0"]);
    }

    // ---- boundary -------------------------------------------------------

    [Fact] // [F-165] 单行紧凑 JSON：全部 token 都在第 1 行；未知指针返回 0（K-3 契约同口径）
    public void LineOf_SingleLineDocument_AndUnknownPointer()
    {
        IReadOnlyDictionary<string, int> lines = ModJsonLineMap.Index("{\"a\":1,\"b\":[2]}");

        Assert.Equal(1, lines["/a"]);
        Assert.Equal(1, lines["/b"]);
        Assert.Equal(1, lines["/b/0"]);
        Assert.Equal(0, ModJsonLineMap.LineOf(lines, "/nope"));
    }

    [Fact] // [F-165] 尾行无换行/空文档不抛异常
    public void Index_TrailingLineWithoutNewline_AndEmptyDocument()
    {
        IReadOnlyDictionary<string, int> oneLine = ModJsonLineMap.Index("{\"x\":1}");
        Assert.Equal(1, oneLine[""]);
        Assert.Equal(1, oneLine["/x"]);

        IReadOnlyDictionary<string, int> empty = ModJsonLineMap.Index(string.Empty);
        Assert.Empty(empty);
    }

    // ---- illegal --------------------------------------------------------

    [Fact] // [F-165] 截断 JSON：不抛异常，已索引前缀保留（调用方先报解析错误行）
    public void Index_MalformedJson_KeepsIndexedPrefix()
    {
        IReadOnlyDictionary<string, int> lines = ModJsonLineMap.Index("{\n  \"a\": 1,\n  \"b\": ");

        Assert.Equal(2, lines["/a"]);
        // 半途属性：名字 token 已索引（第 3 行），值 token 永不到来——保留前缀不抛
        Assert.True(lines.ContainsKey("/b"));
        Assert.Equal(3, lines["/b"]);
    }

    [Fact] // [F-165] null 入参抛 ArgumentNullException（防御边界）
    public void Index_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ModJsonLineMap.Index(null!));
    }
}
