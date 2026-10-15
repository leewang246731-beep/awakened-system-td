using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-124] T-093 词汇表（F1 帮助面板术语表数据化）：解析三行
// （合法/边界/非法）+ 搜索（含 zh/en/别名/空查询/无命中）+ related 互链完整性。
public class GlossaryTests
{
    private const string MinimalDoc =
        """
        {
          "content_type": "Glossary",
          "items": [
            { "id": "efficiency", "term_zh": "效率", "term_en": "Efficiency",
              "def_zh": "供弹满足率。", "def_en": "Ammo satisfaction ratio.",
              "aliases": ["供弹效率", "eff"], "related": ["bottleneck"] },
            { "id": "bottleneck", "term_zh": "断流", "term_en": "Bottleneck",
              "def_zh": "断供事件。", "def_en": "Starvation event.",
              "aliases": ["瓶颈"], "related": [] }
          ]
        }
        """;

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 完整文档解析：条目序=文件序，字段逐项落位
    public void Parse_reads_entries_in_file_order()
    {
        Glossary glossary = Glossary.Parse(MinimalDoc, "test-glossary");

        Assert.Equal(2, glossary.Count);
        Assert.Equal("efficiency", glossary.Entries[0].Id);
        Assert.Equal("效率", glossary.Entries[0].TermZh);
        Assert.Equal("Efficiency", glossary.Entries[0].TermEn);
        Assert.Equal("断流", glossary.Entries[1].TermZh);
        Assert.True(glossary.TryGet("bottleneck", out GlossaryEntry entry));
        Assert.Contains("瓶颈", entry.Aliases);
    }

    [Theory] // legal — 搜索命中面：id/zh/en/别名（大小写不敏感）
    [InlineData("efficiency", 1)]      // id（小写原样）
    [InlineData("EFFICIENCY", 1)]      // id 大写
    [InlineData("效率", 1)]            // zh 术语
    [InlineData("Bottle", 1)]          // en 术语前缀
    [InlineData("供弹效率", 1)]        // 别名
    public void Search_hits_id_term_and_aliases(string query, int expectedHits)
    {
        Glossary glossary = Glossary.Parse(MinimalDoc, "test-glossary");

        Assert.Equal(expectedHits, glossary.Search(query).Count);
    }

    [Fact] // legal — 空白查询返回全量（面板初始列表）；TryGet 未命中返回 false
    public void Blank_query_returns_every_entry_and_misses_return_false()
    {
        Glossary glossary = Glossary.Parse(MinimalDoc, "test-glossary");

        Assert.Equal(2, glossary.Search("   ").Count);
        Assert.False(glossary.TryGet("nope", out _));
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 空串 vs 空白串：都算空查询；null 查询拒绝
    public void Boundary_blank_queries_and_null_rejection()
    {
        Glossary glossary = Glossary.Parse(MinimalDoc, "test-glossary");

        Assert.Equal(2, glossary.Search("").Count);
        Assert.Throws<ArgumentNullException>(() => glossary.Search(null!));
    }

    [Fact] // boundary — 无命中查询返回空列表（不是 null、不是异常）
    public void No_hit_query_returns_empty_list()
    {
        Glossary glossary = Glossary.Parse(MinimalDoc, "test-glossary");

        Assert.Empty(glossary.Search("不存在"));
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 结构/取值违规全部抛 ArgumentException
    [InlineData("wrong-type", "content_type must be")]
    [InlineData("empty-items", "non-empty")]
    [InlineData("dup-id", "duplicate")]
    [InlineData("blank-term", "non-blank")]
    [InlineData("dangling-related", "does not resolve")]
    [InlineData("broken-json", "invalid JSON")]
    public void Parse_rejects_broken_documents(string kind, string messagePart)
    {
        string json = kind switch
        {
            "wrong-type" => """{"content_type": "NotGlossary", "items": [{"id":"a","term_zh":"甲","term_en":"A","def_zh":"d","def_en":"d"}]}""",
            "empty-items" => """{"content_type": "Glossary", "items": []}""",
            "dup-id" => """
                {"content_type": "Glossary", "items": [
                  {"id":"a","term_zh":"甲","term_en":"A","def_zh":"d","def_en":"d"},
                  {"id":"a","term_zh":"乙","term_en":"B","def_zh":"d","def_en":"d"}]}
                """,
            "blank-term" => """{"content_type": "Glossary", "items": [{"id":"a","term_zh":" ","term_en":"A","def_zh":"d","def_en":"d"}]}""",
            "dangling-related" => """{"content_type": "Glossary", "items": [{"id":"a","term_zh":"甲","term_en":"A","def_zh":"d","def_en":"d","related":["ghost"]}]}""",
            "broken-json" => """{"content_type": "Glossary", "items": [""",
            _ => throw new InvalidOperationException(),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Glossary.Parse(json, "test-glossary"));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Glossary.Parse(null!, "x"));
    }

    // ---- T-110 desktop_only（F-164 Web UI 禁用适配） ------------------------

    [Fact] // legal — desktop_only 解析：true 落位、缺省/显式 false 均 false
    public void Desktop_only_flag_parses_with_false_default()
    {
        const string doc =
            """
            {
              "content_type": "Glossary",
              "items": [
                { "id": "a", "term_zh": "甲", "term_en": "A", "def_zh": "d", "def_en": "d",
                  "desktop_only": true },
                { "id": "b", "term_zh": "乙", "term_en": "B", "def_zh": "d", "def_en": "d",
                  "desktop_only": false },
                { "id": "c", "term_zh": "丙", "term_en": "C", "def_zh": "d", "def_en": "d" }
              ]
            }
            """;

        Glossary glossary = Glossary.Parse(doc, "test-glossary");

        Assert.True(glossary.Entries[0].DesktopOnly);
        Assert.False(glossary.Entries[1].DesktopOnly);
        Assert.False(glossary.Entries[2].DesktopOnly);
    }

    [Fact] // boundary — desktop_only 不改变搜索/互链行为（只影响 Web 标注面）
    public void Desktop_only_flag_does_not_affect_search_or_links()
    {
        const string doc =
            """
            {
              "content_type": "Glossary",
              "items": [
                { "id": "a", "term_zh": "甲", "term_en": "A", "def_zh": "d", "def_en": "d",
                  "aliases": ["byok"], "related": ["b"], "desktop_only": true },
                { "id": "b", "term_zh": "乙", "term_en": "B", "def_zh": "d", "def_en": "d" }
              ]
            }
            """;

        Glossary glossary = Glossary.Parse(doc, "test-glossary");

        Assert.Single(glossary.Search("byok"));
        Assert.Contains("b", glossary.Entries[0].Related); // 互链解析不因新字段变化
    }
}
