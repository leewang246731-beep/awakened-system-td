using EraCore.Combat;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-120] T-093 克制索引（塔详情卡数据源，敌表 counters 列反演）：
// 三行——合法（反演+甲列）、边界（无 counters 行跳过/空目录）、非法
// （未知类名/错 content_type）。
public class CounterIndexTests
{
    private const string Doc =
        """
        {
          "content_type": "Enemy",
          "items": [
            { "id": "e-beast", "name_zh": "兽崽", "armor": 0, "counters": ["pierce"] },
            { "id": "e-boar", "name_zh": "铠猪", "armor": 2, "counters": ["physical"] },
            { "id": "e-warrior", "name_zh": "蛮勇士", "armor": 3, "counters": ["physical", "pierce"] }
          ]
        }
        """;

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 反演：每敌的 counters 并入对应塔类，文件序保持，甲列随行
    public void Inverts_enemy_counter_rows_per_tower_class()
    {
        CounterIndex index = CounterIndex.FromEnemiesJson(Doc, "test-enemies");

        IReadOnlyList<CounterRow> pierce = index.CounteredBy(TowerClass.Pierce);
        Assert.Equal(2, pierce.Count);
        Assert.Equal("兽崽", pierce[0].NameZh);
        Assert.Equal(0, pierce[0].Armor);
        Assert.Equal("蛮勇士", pierce[1].NameZh);

        IReadOnlyList<CounterRow> physical = index.CounteredBy(TowerClass.Physical);
        Assert.Equal(2, physical.Count);
        Assert.Contains(physical, row => row.EnemyId == "e-boar" && row.Armor == 2);
    }

    [Fact] // legal — 未被任何敌点名的塔类返回空列表（非 null）
    public void Unmentioned_class_returns_empty_list()
    {
        CounterIndex index = CounterIndex.FromEnemiesJson(Doc, "test-enemies");

        Assert.Empty(index.CounteredBy(TowerClass.Support));
        Assert.Empty(index.CounteredBy(TowerClass.Energy));
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 无 counters 行跳过；战术词（focus_fire）跳过不入索引
    public void Rows_without_counters_and_strategy_tokens_are_skipped()
    {
        const string doc =
            """
            {
              "content_type": "Enemy",
              "items": [
                { "id": "e-plain", "name_zh": "无克制行", "armor": 1 },
                { "id": "e-null", "name_zh": "空克制行", "armor": 1, "counters": null },
                { "id": "e-shrew", "name_zh": "自爆鼩", "armor": 0, "counters": ["focus_fire"] },
                { "id": "e-boss", "name_zh": "踏群者", "armor": 6, "counters": ["physical", "focus_fire"] }
              ]
            }
            """;

        CounterIndex index = CounterIndex.FromEnemiesJson(doc, "test-enemies");

        IReadOnlyList<CounterRow> physical = index.CounteredBy(TowerClass.Physical);
        Assert.Single(physical); // focus_fire 战术词不入塔类索引（SP-3a 混合列语义）
        Assert.Equal("踏群者", physical[0].NameZh);
        Assert.Empty(index.CounteredBy(TowerClass.Pierce));

        CounterIndex empty = CounterIndex.FromEnemiesJson(
            """{"content_type": "Enemy", "items": []}""", "test-enemies");
        Assert.Empty(empty.CounteredBy(TowerClass.Pierce));
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 错 content_type / 坏 JSON / counters 带空 id
    [InlineData("wrong-type", "content_type")]
    [InlineData("broken-json", "invalid JSON")]
    [InlineData("blank-id", "non-blank")]
    public void Parse_rejects_broken_documents(string kind, string messagePart)
    {
        string json = kind switch
        {
            "wrong-type" => """{"content_type":"Building","items":[{"id":"e","name_zh":"甲","armor":0,"counters":["pierce"]}]}""",
            "broken-json" => """{"content_type":"Enemy","items":[{"id":"e","counters":""",
            "blank-id" => """{"content_type":"Enemy","items":[{"id":" ","name_zh":"甲","armor":0,"counters":["pierce"]}]}""",
            _ => throw new InvalidOperationException(),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => CounterIndex.FromEnemiesJson(json, "test-enemies"));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_json_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CounterIndex.FromEnemiesJson(null!, "x"));
    }
}
