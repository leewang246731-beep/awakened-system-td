using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] [F-077] L1 解析器单元面（T-052）：构造/子集常量/空输入兜底/
// 确定性指纹。逐意图正反例矩阵在 L1IntentParserLogicTests。
public class L1IntentParserTests
{
    /// <summary>从仓库内容构造的完整解析器（两语言书+词典+era1 塔别名）。</summary>
    internal static L1IntentParser CreateParser()
        => L1IntentParser.FromJson(
            GrammarTestPaths.ReadGrammarFile("patterns-zh.json"),
            GrammarTestPaths.ReadGrammarFile("patterns-en.json"),
            GrammarTestPaths.ReadGrammarFile("lexicon.json"),
            GrammarTestPaths.Era1TowersJson());

    [Fact] // legal：T-052 子集=并集 5 意图（任务卡 4 意图 + SP-2 [NEW-原型校准] 行的设闹）
    public void SupportedIntents_is_the_t052_union_subset()
    {
        Assert.Equal(
            new[]
            {
                IntentKind.HoldRegion,
                IntentKind.Build,
                IntentKind.QueryThreat,
                IntentKind.QueryStockpile,
                IntentKind.SetAlarm,
            },
            L1IntentParser.SupportedIntents);
    }

    [Theory] // illegal：空/纯标点输入走澄清兜底（契约：失败不抛异常）
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("！！！")]
    public void Parse_routes_empty_input_to_default_clarification(string? text)
    {
        var result = CreateParser().Parse(text!);

        Assert.Null(result.Intent);
        Assert.NotNull(result.Failure);
        Assert.Equal(L1IntentParser.DefaultClarifyingKey, result.Failure!.ClarifyingKey);
    }

    [Fact] // boundary：subset-gap 与 default 两个澄清话术键并存
    public void Parse_distinguishes_subset_gap_from_unparsed()
    {
        var parser = CreateParser();

        var deferred = parser.Parse("把箭塔升到三级");
        Assert.Equal(L1IntentParser.SubsetGapClarifyingKey, deferred.Failure!.ClarifyingKey);

        var unknown = parser.Parse("呃呃呃呃");
        Assert.Equal(L1IntentParser.DefaultClarifyingKey, unknown.Failure!.ClarifyingKey);
    }

    [Fact] // [F-077] 确定性：两个独立构造的实例对同一批输入（含失败）指纹一致
    public void Parse_is_deterministic_across_instances()
    {
        var left = CreateParser();
        var right = CreateParser();
        var inputs = new[]
        {
            "在东门建两座木箭塔",
            "守住东门",
            "下一波怕什么",
            "我们还剩多少木材",
            "三分钟后叫我",
            "Build two arrow towers at the east gate",
            "hold the east gate",
            "remind me in three minutes",
            "把箭塔升到三级", // 失败路径也要确定性
            "呃呃呃呃",
        };

        foreach (var input in inputs)
        {
            var first = L1IntentParser.Fingerprint(left.Parse(input));
            var second = L1IntentParser.Fingerprint(right.Parse(input));
            Assert.Equal(first, second);
        }
    }

    [Fact] // [F-077] 确定性：同实例重复解析同输入 → 同指纹
    public void Parse_is_deterministic_across_repeated_calls()
    {
        var parser = CreateParser();

        Assert.Equal(
            L1IntentParser.Fingerprint(parser.Parse("Build two arrow towers at the east gate")),
            L1IntentParser.Fingerprint(parser.Parse("Build two arrow towers at the east gate")));
    }

    [Fact] // [F-077] 指纹区分不同输入与不同结果形态
    public void Fingerprint_separates_distinct_results()
    {
        var parser = CreateParser();

        var intent = L1IntentParser.Fingerprint(parser.Parse("守住东门"));
        var failure = L1IntentParser.Fingerprint(parser.Parse("守住"));
        var otherIntent = L1IntentParser.Fingerprint(parser.Parse("守住南门"));

        Assert.NotEqual(intent, failure);
        Assert.NotEqual(intent, otherIntent);
    }

    [Fact] // illegal：契约要求 Intent/Failure 恰一非空——两者皆空是程序错误
    public void Fingerprint_rejects_empty_result()
    {
        Assert.Throws<ArgumentException>(() => L1IntentParser.Fingerprint(new IntentParseResult(null, null)));
    }

    [Fact] // boundary：解析结果保留原文（K-8 战报关联），槽位是规范化 id 值
    public void Parse_keeps_raw_text_and_canonical_slots()
    {
        var result = CreateParser().Parse("在东门建两座木箭塔");

        Assert.Equal("在东门建两座木箭塔", result.Intent!.RawText);
        Assert.Equal("era1:tower-wood-arrow", result.Intent.Slots["tower"]);
        Assert.Equal("2", result.Intent.Slots["qty"]);
        Assert.Equal("east_gate", result.Intent.Slots["where"]);
    }
}
