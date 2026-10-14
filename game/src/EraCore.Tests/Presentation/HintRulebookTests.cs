using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-110] T-093 谓词提示库（hints.json 解析）：三行——合法（冷却秒→
// tick 换算/双语必备）、边界（priority=1/cooldown=0）、非法（未知谓词/
// 悬空 term/重复 id/非双语/负冷却/priority<1）。
public class HintRulebookTests
{
    private static readonly string GlossaryDoc =
        """
        {
          "content_type": "Glossary",
          "items": [
            { "id": "efficiency", "term_zh": "效率", "term_en": "Efficiency",
              "def_zh": "供弹满足率。", "def_en": "Ammo satisfaction ratio.",
              "aliases": [], "related": [] }
          ]
        }
        """;

    private const string MinimalDoc =
        """
        {
          "content_type": "HintTable",
          "items": [
            { "id": "hint-b", "predicate": "FirstTower", "priority": 30,
              "cooldown_s": 0, "once": true,
              "text_zh": "首塔。", "text_en": "First tower.", "term": "efficiency" },
            { "id": "hint-a", "predicate": "FirstTower", "priority": 20,
              "cooldown_s": 30, "once": false,
              "text_zh": "另一个。", "text_en": "Another.", "term": null }
          ]
        }
        """;

    private static Glossary LoadGlossary() => Glossary.Parse(GlossaryDoc, "test-glossary");

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 冷却秒按 30 Hz 换算成 tick（K-1 域），term 链落位
    public void Parse_converts_cooldown_seconds_to_ticks()
    {
        HintRulebook book = HintRulebook.Parse(MinimalDoc, "test-hints", LoadGlossary());

        Assert.Equal(2, book.Count);
        HintRule withCooldown = book.Rules.First(rule => rule.Id == "hint-a");
        Assert.Equal(30 * HintRulebook.TicksPerSecond, withCooldown.CooldownTicks);
        HintRule linked = book.Rules.First(rule => rule.Id == "hint-b");
        Assert.Equal("efficiency", linked.Term);
        Assert.True(linked.Once);
        Assert.False(withCooldown.Once);
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — priority 下界 1 合法；cooldown 0（无冷静期）合法
    public void Parse_accepts_priority_one_and_zero_cooldown()
    {
        const string doc =
            """
            {
              "content_type": "HintTable",
              "items": [
                { "id": "hint-x", "predicate": "GameStart", "priority": 1,
                  "cooldown_s": 0, "once": false,
                  "text_zh": "开场。", "text_en": "Start.", "term": null }
              ]
            }
            """;

        HintRulebook book = HintRulebook.Parse(doc, "test-hints", LoadGlossary());

        Assert.Equal(1, book.Rules[0].Priority);
        Assert.Equal(0, book.Rules[0].CooldownTicks);
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 六类违规逐项抛（含未知谓词与悬空 term）
    [InlineData("wrong-type", "content_type")]
    [InlineData("empty-items", "non-empty")]
    [InlineData("unknown-predicate", "HintPredicateKind")]
    [InlineData("dangling-term", "does not resolve")]
    [InlineData("dup-id", "duplicate")]
    [InlineData("missing-en", "non-blank")]
    [InlineData("negative-cooldown", "cooldown_s")]
    [InlineData("zero-priority", "priority")]
    public void Parse_rejects_broken_documents(string kind, string messagePart)
    {
        string json = kind switch
        {
            "wrong-type" => """{"content_type":"Nope","items":[{"id":"h","predicate":"GameStart","priority":1,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":"A"}]}""",
            "empty-items" => """{"content_type":"HintTable","items":[]}""",
            "unknown-predicate" => """{"content_type":"HintTable","items":[{"id":"h","predicate":"NotAKind","priority":1,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":"A"}]}""",
            "dangling-term" => """{"content_type":"HintTable","items":[{"id":"h","predicate":"GameStart","priority":1,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":"A","term":"ghost"}]}""",
            "dup-id" => """
                {"content_type":"HintTable","items":[
                  {"id":"h","predicate":"GameStart","priority":1,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":"A"},
                  {"id":"h","predicate":"GameStart","priority":2,"cooldown_s":0,"once":true,"text_zh":"乙","text_en":"B"}]}
                """,
            "missing-en" => """{"content_type":"HintTable","items":[{"id":"h","predicate":"GameStart","priority":1,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":""}]}""",
            "negative-cooldown" => """{"content_type":"HintTable","items":[{"id":"h","predicate":"GameStart","priority":1,"cooldown_s":-1,"once":true,"text_zh":"甲","text_en":"A"}]}""",
            "zero-priority" => """{"content_type":"HintTable","items":[{"id":"h","predicate":"GameStart","priority":0,"cooldown_s":0,"once":true,"text_zh":"甲","text_en":"A"}]}""",
            _ => throw new InvalidOperationException(),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => HintRulebook.Parse(json, "test-hints", LoadGlossary()));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => HintRulebook.Parse(null!, "x", LoadGlossary()));
        Assert.Throws<ArgumentNullException>(
            () => HintRulebook.Parse(MinimalDoc, "x", null!));
    }
}
