using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-110] T-093 谓词驱动提示队列（§10.1/附录 G.3：行为触发+优先级
// +冷静期+已读持久化）：同一谓词按 priority 出队、once 已读永不再弹、
// 非 once 冷静期内静默、MarkRead 与入参校验、词汇互链解析。
public class HintQueueTests
{
    private const string Doc =
        """
        {
          "content_type": "HintTable",
          "items": [
            { "id": "hint-high", "predicate": "FirstBottleneck", "priority": 5,
              "cooldown_s": 45, "once": true,
              "text_zh": "查供给线。", "text_en": "Check the supply line.", "term": "efficiency" },
            { "id": "hint-low", "predicate": "FirstBottleneck", "priority": 50,
              "cooldown_s": 0, "once": false,
              "text_zh": "兜底行。", "text_en": "Fallback.", "term": null },
            { "id": "hint-alarm", "predicate": "LowIntegrity", "priority": 1,
              "cooldown_s": 2, "once": false,
              "text_zh": "加固。", "text_en": "Reinforce.", "term": "efficiency" }
          ]
        }
        """;

    private const string GlossaryDoc =
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

    private static (HintRulebook Book, Glossary Catalog) Load()
    {
        var glossary = Glossary.Parse(GlossaryDoc, "test-glossary");
        return (HintRulebook.Parse(Doc, "test-hints", glossary), glossary);
    }

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 同谓词多行：priority 小者先出；term 链经 BindGlossary 解析
    public void Observe_returns_highest_priority_rule_with_term_link()
    {
        (HintRulebook book, Glossary catalog) = Load();
        var queue = new HintQueue(book, new HashSet<string>());
        queue.BindGlossary(catalog);

        HintDue? due = queue.Observe(HintPredicateKind.FirstBottleneck, tick: 1000);

        Assert.NotNull(due);
        Assert.Equal("hint-high", due!.Rule.Id);
        Assert.NotNull(due.TermEntry);
        Assert.Equal("效率", due.TermEntry!.TermZh);
    }

    [Fact] // legal — once 已读后永不再弹，回落到同谓词次优先行
    public void Read_once_hint_never_refires_and_falls_back()
    {
        (HintRulebook book, _) = Load();
        var queue = new HintQueue(book, new HashSet<string> { "hint-high" });

        HintDue? due = queue.Observe(HintPredicateKind.FirstBottleneck, tick: 1000);

        Assert.NotNull(due);
        Assert.Equal("hint-low", due!.Rule.Id);
        queue.MarkRead("hint-low");
        Assert.Null(queue.Observe(HintPredicateKind.FirstBottleneck, tick: 100_000));
    }

    [Fact] // legal — 非 once 行的冷静期：期内重复观察静默，期满重新可弹
    public void Cooldown_silences_then_rearms()
    {
        (HintRulebook book, _) = Load();
        var queue = new HintQueue(book, new HashSet<string> { "hint-high" }); // 只剩兜底行（无冷却）
        queue.MarkRead("hint-low");
        var alarm = new HintQueue(book, new HashSet<string>()); // 用 hint-alarm（2 s 冷却）验证窗口

        HintDue? first = alarm.Observe(HintPredicateKind.LowIntegrity, tick: 0);
        HintDue? inside = alarm.Observe(HintPredicateKind.LowIntegrity, tick: 30); // < 2s×30
        HintDue? after = alarm.Observe(HintPredicateKind.LowIntegrity, tick: 60 + 1); // 冷静期满

        Assert.NotNull(first);
        Assert.Null(inside); // 冷静期内——同提示不连发（附录 G.3）
        Assert.NotNull(after);
        Assert.Null(queue.Observe(HintPredicateKind.FirstBottleneck, tick: 1));
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — tick=0 合法；未知谓词（无行匹配）返回 null 而非异常
    public void Tick_zero_is_legal_and_unmatched_predicate_returns_null()
    {
        (HintRulebook book, _) = Load();
        var queue = new HintQueue(book, new HashSet<string>());

        Assert.NotNull(queue.Observe(HintPredicateKind.LowIntegrity, tick: 0));
        Assert.Null(queue.Observe(HintPredicateKind.CameraNearZone, tick: 0));
        Assert.Empty(queue.ReadIds);
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 负 tick / 空 id / 未知 id / null 集合
    [InlineData(-1)]
    public void Negative_tick_rejected(long tick)
    {
        (HintRulebook book, _) = Load();
        var queue = new HintQueue(book, new HashSet<string>());

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Observe(HintPredicateKind.GameStart, tick));
    }

    [Fact] // illegal — MarkRead 的 id 必须属于本库
    public void MarkRead_rejects_unknown_and_blank_ids()
    {
        (HintRulebook book, _) = Load();
        var queue = new HintQueue(book, new HashSet<string>());

        Assert.Throws<ArgumentException>(() => queue.MarkRead("not-a-rule"));
        Assert.Throws<ArgumentException>(() => queue.MarkRead(" "));
        Assert.Throws<ArgumentNullException>(() => new HintQueue(book, null!));
    }
}
