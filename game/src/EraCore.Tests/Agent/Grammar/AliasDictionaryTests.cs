using System.Text.Json;
using EraCore.Agent.Grammar;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] 塔名别名词典（T-052）：era1 towers.json 的 id+name_zh+name_en+aliases
// 同源装载（K-2/SP-2 配套口径）+ 同音容错（编辑距离 ≤1 的等长窗口修复）。
public class AliasDictionaryTests
{
    private static AliasDictionary LoadEra1()
        => AliasDictionary.FromTowerJson(GrammarTestPaths.Era1TowersJson());

    [Theory] // legal：era1 四塔的精确表面 → namespaced id
    [InlineData("木箭塔", "era1:tower-wood-arrow")]
    [InlineData("箭塔", "era1:tower-wood-arrow")]
    [InlineData("arrowtower", "era1:tower-wood-arrow")]
    [InlineData("落石台", "era1:tower-rockfall")]
    [InlineData("落石", "era1:tower-rockfall")]
    [InlineData("rockfall", "era1:tower-rockfall")]
    [InlineData("尖刺陷阱", "era1:tower-spike-trap")]
    [InlineData("陷阱", "era1:tower-spike-trap")]
    [InlineData("篝火", "era1:tower-campfire")]
    [InlineData("火堆", "era1:tower-campfire")]
    public void Era1_aliases_resolve_to_registry_ids(string surface, string expectedId)
    {
        var dictionary = LoadEra1();

        Assert.True(dictionary.TryResolve(surface, out var id));
        Assert.Equal(expectedId, id);
    }

    [Theory] // boundary：英文复数（build two arrow towers 的捕获面）
    [InlineData("arrowtowers", "era1:tower-wood-arrow")]
    [InlineData("rockfallplatforms", "era1:tower-rockfall")]
    [InlineData("spiketraps", "era1:tower-spike-trap")]
    [InlineData("campfires", "era1:tower-campfire")]
    public void English_plurals_resolve_to_singular_ids(string surface, string expectedId)
    {
        var dictionary = LoadEra1();

        Assert.True(dictionary.TryResolve(surface, out var id));
        Assert.Equal(expectedId, id);
    }

    [Theory] // illegal：未知表面 → false，绝不猜
    [InlineData("unknown")]
    [InlineData("箭")]
    [InlineData("塔")]
    [InlineData("woodtower")]
    public void Unknown_surfaces_do_not_resolve(string surface)
    {
        Assert.False(LoadEra1().TryResolve(surface, out _));
    }

    [Theory] // legal：同音容错——等长窗口单字替换（v3.2 §11.5「同音容错」）
    [InlineData("建箭楼", "建箭塔")]
    [InlineData("木材还剩多少", "木材还剩多少")] // 无塔名近失 → 原样返回 null（由调用方保留原文）
    [InlineData("campfira", "campfire")]
    [InlineData("rockfalm", "rockfall")]
    public void FuzzyRepair_fixes_single_substitution_near_misses(string input, string expected)
    {
        var repaired = LoadEra1().FuzzyRepair(input);

        Assert.Equal(expected, repaired ?? input);
    }

    [Fact] // boundary：精确命中/已含精确塔名的窗口不触发修复（防吃字：建十座箭塔 的 座箭塔 不得被改写成 木箭塔）
    public void FuzzyRepair_leaves_exact_surfaces_alone()
    {
        Assert.Null(LoadEra1().FuzzyRepair("篝火"));
        Assert.Null(LoadEra1().FuzzyRepair("东门敌情"));
        Assert.Null(LoadEra1().FuzzyRepair("建箭塔"));
        Assert.Null(LoadEra1().FuzzyRepair("建十座箭塔"));
    }

    // ---- T-061 §5 缺陷回归：修复窗口不得破坏紧邻的精确词（T-069 修复项） -------

    [Theory] // legal：16-t061 报告 §5 两条自然军令——守卫升级后原样保留（窗口与相邻精确塔面/词典词重叠 → 放弃修复）
    [InlineData("在南面建两座火堆")] // 越权案例一：窗口「座火」与精确塔面「火堆」共用「火」，旧守卫改写成「篝火」破坏全句
    [InlineData("查一下石头")]       // 越权案例二：窗口「下石」与资源词「石头」重叠，旧守卫改写成「落石」
    public void FuzzyRepair_does_not_overwrite_adjacent_exact_words(string input)
    {
        var towers = LoadEra1();
        var protectedWords = new[] { "南面", "石头", "东门", "北墙", "木材", "食物", "下一波", "敌情" };

        Assert.Null(towers.FuzzyRepair(input, protectedWords));
    }

    [Theory] // boundary：守卫只挡「重叠」不挡「相邻」——窗口紧贴但不覆盖精确词的合法修复仍放行
    [InlineData("东门建箭楼", "东门建箭塔")] // 窗口 箭楼 与受保护区 东门 相邻不重叠
    [InlineData("在北门造一座火推", "在北门造一座火堆")] // T-061 语料正例：火推→火堆 等长修复不受影响
    public void FuzzyRepair_still_fixes_near_misses_adjacent_to_protected_words(string input, string expected)
    {
        var repaired = LoadEra1().FuzzyRepair(input, new[] { "东门", "北门" });

        Assert.Equal(expected, repaired ?? input);
    }

    [Theory] // legal：±1 长度错字（删/插字）修复——仅 ≥6 字符长表面启用（T-061 miss #205 camfire）
    [InlineData("buildtwocamfireatthesouthgate", "buildtwocampfireatthesouthgate")] // camfire=删字母错字
    [InlineData("use the rockfalll", "usetherockfall")] // rockfalll=插字母错字（the 由归一保留）
    public void FuzzyRepair_fixes_single_indel_near_misses(string input, string expected)
    {
        var repaired = LoadEra1().FuzzyRepair(TextNormalizer.Normalize(input));

        Assert.Equal(expected, repaired ?? TextNormalizer.Normalize(input));
    }

    [Theory] // illegal：未近失文本不动（含受保护词本身、无塔名近失、已含精确面）
    [InlineData("篝火堆")]   // 火堆 精确面已在 → 无修复
    [InlineData("建个东西")] // 无近失 → 无修复
    [InlineData("东门造陷阱")] // 陷阱 精确面已在 → 无修复
    public void FuzzyRepair_rejects_clean_text(string input)
    {
        var repaired = LoadEra1().FuzzyRepair(input, new[] { "东门", "石头" });

        Assert.Equal(input, repaired ?? input);
    }

    [Theory] // illegal：跨越短语边界的窗口一律拒绝（T-069 序数/属格短语守卫）
    [InlineData("出售3号塔")]    // 号塔 窗口 → 不得改写为 箭塔（序数指认）
    [InlineData("升级东门的塔")] // 的塔 窗口 → 不得改写为 箭塔（分区属格）
    public void FuzzyRepair_rejects_phrase_boundary_windows(string input)
    {
        var repaired = LoadEra1().FuzzyRepair(input, new[] { "东门" });

        Assert.Equal(input, repaired ?? input);
    }

    [Fact] // boundary：编辑距离判定——等长 1 处差异 true，2 处 false，长度不等 false
    public void WithinEditDistance1_classifies_pairs()
    {
        Assert.True(AliasDictionary.WithinEditDistance1("箭塔", "箭楼"));
        Assert.True(AliasDictionary.WithinEditDistance1("abc", "abc"));
        Assert.False(AliasDictionary.WithinEditDistance1("箭塔", "火堆"));
        Assert.False(AliasDictionary.WithinEditDistance1("箭塔", "箭"));
    }

    [Fact] // legal：交替项按长度降序（最长表面优先匹配）
    public void TowerAlternation_orders_longest_first()
    {
        var alternation = LoadEra1().TowerAlternation();

        var woodArrow = alternation.IndexOf("woodarrowtower", StringComparison.Ordinal);
        var arrow = alternation.IndexOf("arrowtower", StringComparison.Ordinal);
        Assert.True(woodArrow >= 0 && arrow >= 0 && woodArrow < arrow);
        Assert.Contains("(?:es|s)?", alternation);
    }

    [Fact] // illegal：坏 JSON（JsonReaderException 属 JsonException 家族）/ 缺 items → JsonException
    public void FromTowerJson_rejects_damaged_documents()
    {
        Assert.ThrowsAny<JsonException>(() => AliasDictionary.FromTowerJson("not json"));
        Assert.Throws<JsonException>(() => AliasDictionary.FromTowerJson("{\"lang\":\"zh\"}"));
    }

    [Fact] // illegal：同一表面两塔认领 → 冲突必须浮出（K-2 纪律）
    public void FromTowerJson_surfaces_alias_conflicts()
    {
        const string json = """
            {"items": [
              {"id": "x:tower-b", "name_zh": "箭塔", "name_en": "B"},
              {"id": "a:tower-a", "name_zh": "箭塔", "name_en": "A"}
            ]}
            """;

        Assert.Throws<InvalidOperationException>(() => AliasDictionary.FromTowerJson(json));
    }

    [Fact] // legal：装载次序按 id 排序（确定性）——Surfaces 输出稳定
    public void FromTowerJson_is_deterministic()
    {
        var first = LoadEra1();
        var second = LoadEra1();

        Assert.Equal(first.Surfaces, second.Surfaces);
    }
}
