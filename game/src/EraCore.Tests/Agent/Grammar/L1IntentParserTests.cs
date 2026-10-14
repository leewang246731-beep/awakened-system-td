using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] [F-077] L1 解析器单元面（T-052）：构造/子集常量/空输入兜底/
// 确定性指纹。逐意图正反例矩阵在 L1IntentParserLogicTests。
public class L1IntentParserTests
{
    /// <summary>
    /// 从仓库内容构造的完整解析器（两语言书+词典+era1 塔/敌别名，T-069；
    /// T-090 起卷轴面自 scrolls/era2.json 内容注册表装载——封闭别名词典扩容）。
    /// </summary>
    internal static L1IntentParser CreateParser()
        => L1IntentParser.FromJson(
            GrammarTestPaths.ReadGrammarFile("patterns-zh.json"),
            GrammarTestPaths.ReadGrammarFile("patterns-en.json"),
            GrammarTestPaths.ReadGrammarFile("lexicon.json"),
            GrammarTestPaths.Era1TowersJson(),
            GrammarTestPaths.Era1EnemiesJson(),
            GrammarTestPaths.Era2ScrollsJson());

    [Fact] // legal：T-069 全量=SP-2 12 意图冻结表（T-052 起步 5 意图子集扩容收口）
    public void SupportedIntents_is_the_full_sp2_table()
    {
        Assert.Equal(
            new[]
            {
                IntentKind.HoldRegion,
                IntentKind.Build,
                IntentKind.Upgrade,
                IntentKind.Sell,
                IntentKind.FocusFire,
                IntentKind.ChangeFocusMode,
                IntentKind.RerouteLogistics,
                IntentKind.QueryStockpile,
                IntentKind.QueryThreat,
                IntentKind.SetAlarm,
                IntentKind.Patrol,
                IntentKind.ScrollControl,
            },
            L1IntentParser.SupportedIntents);
    }

    [Theory] // legal：T-069 后 7 个缺口意图真正可解析（T-052 时代落 subset_gap，见语料 v1 注解）
    [InlineData("把箭塔升到三级", IntentKind.Upgrade)]
    [InlineData("卖掉北墙的旧塔", IntentKind.Sell)]
    [InlineData("集火那个攻城锤", IntentKind.FocusFire)]
    [InlineData("upgrade the arrow tower to tier 3", IntentKind.Upgrade)]
    [InlineData("focus the ram", IntentKind.FocusFire)]
    [InlineData("开火油卷轴", IntentKind.ScrollControl)]
    public void Former_gap_intents_now_parse(string text, IntentKind kind)
    {
        var intent = CreateParser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(kind, intent!.Kind);
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

    [Fact] // boundary：全部 12 意图可解析后，澄清兜底只剩 default 键（subset_gap 键随 T-069 退役）
    public void Parse_distinguishes_unparsed_from_resolved_intents()
    {
        var parser = CreateParser();

        var unknown = parser.Parse("呃呃呃呃");
        Assert.Null(unknown.Intent);
        Assert.Equal(L1IntentParser.DefaultClarifyingKey, unknown.Failure!.ClarifyingKey);

        var resolved = parser.Parse("把箭塔升到三级");
        Assert.NotNull(resolved.Intent);
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
