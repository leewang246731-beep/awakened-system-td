using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-117] T-093 分区命名标注层（§10.5/§10.12 镜头靠近浮显+令簿地名
// 解析）：三行——合法（可见窗口/地名解析）、边界（含边/出窗/空查询）、
// 非法（越界 cell/重复 id/错 content_type/非双语）。
public class ZoneLabelsTests
{
    private const string Doc =
        """
        {
          "content_type": "ZoneLabels",
          "items": [
            { "id": "east_gate", "name_zh": "东门", "name_en": "East Gate",
              "cell": { "x": 12, "y": 12 }, "aliases": ["东门", "east gate"] },
            { "id": "base_camp", "name_zh": "营垒", "name_en": "Base Camp",
              "cell": { "x": 8, "y": 23 }, "aliases": ["基地", "base"] },
            { "id": "north_ruins", "name_zh": "北废墟", "name_en": "North Ruins",
              "cell": { "x": 8, "y": 1 }, "aliases": [] }
          ]
        }
        """;

    private static ZoneLabelTable Table() => ZoneLabelTable.Parse(Doc, "test-zones", 16, 24);

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 解析落位：文件序、cell 坐标、别名
    public void Parse_reads_zones_in_file_order()
    {
        ZoneLabelTable table = Table();

        Assert.Equal(3, table.Count);
        Assert.Equal("east_gate", table.Zones[0].Id);
        Assert.Equal(new CellCoord(12, 12), table.Zones[0].Cell);
        Assert.Equal("营垒", table.Zones[1].NameZh);
        Assert.Contains("base", table.Zones[1].Aliases);
    }

    [Fact] // legal — 相机窗（格域）内浮显：包含边；窗外不显
    public void VisibleIn_returns_only_zones_inside_the_camera_rect()
    {
        ZoneLabelTable table = Table();

        var mid = table.VisibleIn(0, 10, 15, 15);               // 中带窗：只有东门 (12,12)
        Assert.Single(mid);
        Assert.Equal("east_gate", mid[0].Id);
        var upper = table.VisibleIn(0, 0, 15, 5);
        Assert.Single(upper);
        Assert.Equal("north_ruins", upper[0].Id);
        Assert.Equal(3, table.VisibleIn(0, 0, 15, 23).Count);   // 全图窗=全部
        Assert.Empty(table.VisibleIn(16, 16, 20, 20));          // 图外窗=空
    }

    [Theory] // legal — 地名解析（令簿地名点击定位的查找面）：id/zh/en/别名
    [InlineData("east_gate", "east_gate")]
    [InlineData("东门", "east_gate")]
    [InlineData("EAST GATE", "east_gate")]
    [InlineData("基地", "base_camp")]
    [InlineData("base", "base_camp")]
    public void TryResolve_matches_id_names_and_aliases(string query, string expectedId)
    {
        Assert.Equal(expectedId, Table().TryResolve(query)!.Id);
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 空白查询解析为 null（点空白≠乱跳）；无命中同样 null
    public void Blank_and_missed_queries_resolve_null()
    {
        ZoneLabelTable table = Table();

        Assert.Null(table.TryResolve("   "));
        Assert.Null(table.TryResolve("不存在的地名"));
        Assert.Throws<ArgumentNullException>(() => table.TryResolve(null!));
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 越界 cell / 重复 id / 错 content_type / 非双语 / 空 items / 坏 JSON
    [InlineData("out-of-bounds", "outside")]
    [InlineData("dup-id", "duplicate")]
    [InlineData("wrong-type", "content_type")]
    [InlineData("blank-name", "non-blank")]
    [InlineData("empty-items", "non-empty")]
    [InlineData("broken-json", "invalid JSON")]
    public void Parse_rejects_broken_documents(string kind, string messagePart)
    {
        string json = kind switch
        {
            "out-of-bounds" => """{"content_type":"ZoneLabels","items":[{"id":"a","name_zh":"甲","name_en":"A","cell":{"x":16,"y":12}}]}""",
            "dup-id" => """
                {"content_type":"ZoneLabels","items":[
                  {"id":"a","name_zh":"甲","name_en":"A","cell":{"x":1,"y":1}},
                  {"id":"a","name_zh":"乙","name_en":"B","cell":{"x":2,"y":2}}]}
                """,
            "wrong-type" => """{"content_type":"Zones","items":[{"id":"a","name_zh":"甲","name_en":"A","cell":{"x":1,"y":1}}]}""",
            "blank-name" => """{"content_type":"ZoneLabels","items":[{"id":"a","name_zh":"","name_en":"A","cell":{"x":1,"y":1}}]}""",
            "empty-items" => """{"content_type":"ZoneLabels","items":[]}""",
            "broken-json" => """{"content_type":"ZoneLabels","items":[{"id":"a","cell":""",
            _ => throw new InvalidOperationException(),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ZoneLabelTable.Parse(json, "test-zones", 16, 24));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // illegal — 非法地图边界（宽高 ≤0）与 null json
    public void Bad_map_bounds_and_null_rejected()
    {
        Assert.Throws<ArgumentException>(() => ZoneLabelTable.Parse(Doc, "test-zones", 0, 24));
        Assert.Throws<ArgumentNullException>(() => ZoneLabelTable.Parse(null!, "x", 16, 24));
    }
}
