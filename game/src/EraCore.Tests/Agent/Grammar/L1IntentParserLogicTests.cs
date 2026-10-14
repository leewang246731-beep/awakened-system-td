using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [F-077] L1 文法 E1 高频意图正反例矩阵（T-052 验收：E1 高频 4 意图可解析）。
// 意图集=任务卡口径（守住X/建造/敌情查询/储备查询）∪ SP-2 配套口径（+设闹）。
public class L1IntentParserLogicTests
{
    private static L1IntentParser Parser() => L1IntentParserTests.CreateParser();

    // ---- 守住X（SP-2 #1 target=分区/城门名，目标级模板） --------------------

    public static TheoryData<string, string> HoldRegionPositives => new()
    {
        // legal：中文目标级
        { "守住东门", "target=east_gate" },
        { "死守北墙", "target=north_wall" },
        { "西门不能丢", "target=west_gate" },
        { "给我守住南门", "target=south_gate" },
        { "全力守住城中心", "target=center" },
        { "守好东边", "target=east_side" },
        // legal：英文目标级
        { "Hold the east gate", "target=east_gate" },
        { "Don't let them through the west gate", "target=west_gate" },
        { "guard the south gate", "target=south_gate" },
    };

    [Theory] // legal
    [MemberData(nameof(HoldRegionPositives))]
    public void HoldRegion_positives_parse_to_target_slot(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.HoldRegion, intent!.Kind);
        AssertSlots(text, intent, expectedSlots);
    }

    [Theory] // illegal：无目标/未知目标 → 澄清卡
    [InlineData("守住")]
    [InlineData("hold")]
    [InlineData("守住那里")]
    [InlineData("defend")]
    public void HoldRegion_negatives_route_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 建造（SP-2 #2 tower/qty/where：塔名别名+中文数字/量词+分区方位） ----

    public static TheoryData<string, string> BuildPositives => new()
    {
        // legal：中文（量词/别名/中文数字/阿拉伯数字全覆盖）
        { "在东门建两座木箭塔", "tower=era1:tower-wood-arrow|qty=2|where=east_gate" },
        { "在南门造一座篝火", "tower=era1:tower-campfire|qty=1|where=south_gate" },
        { "西边建3个尖刺陷阱", "tower=era1:tower-spike-trap|qty=3|where=west_side" },
        { "建十座箭塔", "tower=era1:tower-wood-arrow|qty=10" },
        { "在北墙建两座箭楼", "tower=era1:tower-wood-arrow|qty=2|where=north_wall" }, // 同音容错：箭楼→箭塔
        // legal：英文（复数/数词/不定冠词）
        { "Build two arrow towers at the east gate", "tower=era1:tower-wood-arrow|qty=2|where=east_gate" },
        { "build a campfire at the south gate", "tower=era1:tower-campfire|qty=1|where=south_gate" },
        { "construct ten wood arrow towers", "tower=era1:tower-wood-arrow|qty=10" },
        { "put up 3 spike traps at the west side", "tower=era1:tower-spike-trap|qty=3|where=west_side" },
    };

    [Theory] // legal
    [MemberData(nameof(BuildPositives))]
    public void Build_positives_parse_to_tower_qty_where(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.Build, intent!.Kind);
        AssertSlots(text, intent, expectedSlots);
    }

    [Theory] // illegal：缺塔名/裸动词/未知塔 → 澄清卡
    [InlineData("在东门建")]
    [InlineData("build")]
    [InlineData("建个东西")]
    [InlineData("建两座")]
    public void Build_negatives_route_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 敌情查询（SP-2 #9 scope=下一波/当前波/分区威胁） -------------------

    public static TheoryData<string, string> QueryThreatPositives => new()
    {
        // legal：三种 scope 形态（常量句式/词典槽位）
        { "下一波怕什么", "scope=next_wave" },
        { "下一波敌人是什么", "scope=next_wave" },
        { "现在敌情怎么样", "scope=current" },
        { "敌情汇报", "scope=current" },
        { "东门有什么威胁", "scope=east_gate" },
        { "北边敌情怎么样", "scope=north_side" },
        // legal：英文
        { "What's coming next wave", "scope=next_wave" },
        { "current threat situation", "scope=current" },
        { "any threats at the east side", "scope=east_side" },
    };

    [Theory] // legal
    [MemberData(nameof(QueryThreatPositives))]
    public void QueryThreat_positives_parse_to_scope(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.QueryThreat, intent!.Kind);
        AssertSlots(text, intent, expectedSlots);
    }

    [Theory] // illegal：光杆问句/孤立关键词 → 澄清卡
    [InlineData("怕什么")]
    [InlineData("threats")]
    [InlineData("下一波")]
    [InlineData("threat")]
    public void QueryThreat_negatives_route_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 储备查询（SP-2 #8 item=物资名，可省=全量） --------------------------

    public static TheoryData<string, string> QueryStockpilePositives => new()
    {
        // legal：中文（口语别名/全量）
        { "我们还剩多少木材", "item=era1:res-wood" },
        { "石头还有多少", "item=era1:res-stone" },
        { "食物够不够", "item=era1:res-food" },
        { "查一下木材", "item=era1:res-wood" },
        { "还剩什么资源", "item=all" },
        // legal：英文
        { "How much wood is left", "item=era1:res-wood" },
        { "do we have enough food", "item=era1:res-food" },
        { "stockpile report", "item=all" },
        { "how are we on stone", "item=era1:res-stone" },
    };

    [Theory] // legal
    [MemberData(nameof(QueryStockpilePositives))]
    public void QueryStockpile_positives_parse_to_item(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.QueryStockpile, intent!.Kind);
        AssertSlots(text, intent, expectedSlots);
    }

    [Theory] // illegal：缺物资名/光杆词 → 澄清卡
    [InlineData("还剩多少")]
    [InlineData("how much")]
    [InlineData("木材")]
    [InlineData("wood")]
    public void QueryStockpile_negatives_route_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 设闹（SP-2 #10 after/reason；SP-2 配套口径 [NEW-原型校准] 行的 E1 高频第 4 意图）

    public static TheoryData<string, string> SetAlarmPositives => new()
    {
        // legal：中文（中文数字/阿拉伯数字/分/秒）
        { "三分钟后叫我", "after=180" },
        { "5分钟后提醒我", "after=300" },
        { "设个2分钟的闹钟", "after=120" },
        { "十分钟后叫我一声", "after=600" },
        { "90秒后提醒我一下", "after=90" },
        // legal：英文（数词/数字/单位复数）
        { "Remind me in three minutes", "after=180" },
        { "set an alarm for 5 minutes", "after=300" },
        { "notify me in ten minutes", "after=600" },
        { "ping me in 90 seconds", "after=90" },
    };

    [Theory] // legal
    [MemberData(nameof(SetAlarmPositives))]
    public void SetAlarm_positives_parse_to_after(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.SetAlarm, intent!.Kind);
        AssertSlots(text, intent, expectedSlots);
    }

    [Theory] // illegal：缺时长/光杆请求 → 澄清卡
    [InlineData("叫我")]
    [InlineData("remind me")]
    [InlineData("三分钟后")]
    [InlineData("later")]
    public void SetAlarm_negatives_route_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- T-061 语料 miss 修复回归（28 条清单的正例逐条钉死，16-t061 报告 §4） ----

    [Theory] // legal：句式补全 21 条（zh 11 + en 10）——T-061 句式缺修复
    [InlineData("东门顶住", "target=east_gate")]
    [InlineData("别放敌人进北门", "target=north_gate")]
    [InlineData("南门给我守住", "target=south_gate")]
    [InlineData("全力防守东门", "target=east_gate")]
    [InlineData("东边造个火堆", "where=east_side|qty=1|tower=era1:tower-campfire")]
    [InlineData("南门再来两座箭塔", "where=south_gate|qty=2|tower=era1:tower-wood-arrow")]
    [InlineData("本波威胁是什么", "scope=current")]
    [InlineData("下一波是什么敌人", "scope=next_wave")]
    [InlineData("现在的敌情如何", "scope=current")]
    [InlineData("敌情如何", "scope=current")]
    [InlineData("木材多吗", "item=era1:res-wood")]
    [InlineData("hold the north gate at all costs", "target=north_gate")]
    [InlineData("hold the west wall at all costs", "target=west_wall")]
    [InlineData("keep them off the north side", "target=north_side")]
    [InlineData("east gate, hold it", "target=east_gate")]
    [InlineData("I want arrow towers at the east gate", "where=east_gate|tower=era1:tower-wood-arrow")]
    [InlineData("incoming wave threats", "scope=next_wave")]
    [InlineData("what's coming in the next wave", "scope=next_wave")]
    [InlineData("how's the situation", "scope=current")]
    [InlineData("wood?", "item=era1:res-wood")]
    [InlineData("are we running low on wood", "item=era1:res-wood")]
    public void T061_pattern_gap_misses_now_parse(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        AssertSlots(text, intent!, expectedSlots);
    }

    [Theory] // legal：词典补词 4 处 + 容错修正 3 处（FuzzyRepair 越权 2 + 非等长错字 1）
    [InlineData("粮草还剩多少", "item=era1:res-food")]
    [InlineData("hold the western gate", "target=west_gate")]
    [InlineData("半分钟后叫我", "after=30")]
    [InlineData("remind me in half a minute", "after=30")]
    [InlineData("在南面建两座火堆", "where=south_side|qty=2|tower=era1:tower-campfire")] // T-061 §5 越权案例一
    [InlineData("查一下石头", "item=era1:res-stone")] // T-061 §5 越权案例二
    [InlineData("build two camfire at the south gate", "where=south_gate|qty=2|tower=era1:tower-campfire")] // miss #205 非等长错字
    public void T061_lexicon_and_tolerance_misses_now_parse(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text).Intent;

        Assert.NotNull(intent);
        AssertSlots(text, intent!, expectedSlots);
    }

    [Theory] // illegal：T-061 反例仍拒绝（修复不得让反例漏过）
    [InlineData("多造几个箭塔")]
    [InlineData("建塔")]
    [InlineData("下一波几点来")]
    [InlineData("明早八点叫我")]
    [InlineData("build towers at the east gate")]
    [InlineData("how much hp does the boss have")]
    public void T061_negatives_still_fail(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 断言辅助 -----------------------------------------------------------

    /// <summary>逐槽断言（k=v|k2=v2 编码）；键集与值都须完全一致。</summary>
    private static void AssertSlots(string text, Intent intent, string expectedSlots)
    {
        var expected = expectedSlots.Length == 0
            ? new Dictionary<string, string>()
            : expectedSlots.Split('|')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(kv => kv[0], kv => kv[1]);

        Assert.Equal(expected.Count, intent.Slots.Count);
        foreach (var (slot, value) in expected)
        {
            Assert.True(
                intent.Slots.TryGetValue(slot, out var actual) && actual == value,
                $"{text}: slot '{slot}' expected '{value}', got '{intent.Slots.GetValueOrDefault(slot)}'");
        }
    }
}
