using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [F-077] [C-15] T-069 验收矩阵：12 意图 × 2 语言 ×（≥8 正例断言槽位 + ≥2 反例）
// 全绿（08-task-plan T-069 行验收标准）。正例含 T-061 语料 7 缺口意图的全部
// 70 条行文（槽位值在此逐条钉死——语料文件只注解了 gap_intent）；槽位规范值
// 方案=lexicon.json _canonical_scheme（塔指认复合值/敌指认/pointing 标记）。
// 5 个 T-052 意图的正反例由 L1IntentParserLogicTests 承载，本矩阵各带一组精简
// 回归以保持 12 意图完整覆盖。
// [F-077] [C-15] 类级关联：本矩阵全部方法覆盖 T-069 12 意图×2 语言验收矩阵
public class FullGrammarMatrixTests
{
    private static readonly PointingContext MatrixPointing =
        new(SelectedId: "era1:tower-campfire", SelectedCellX: 3, SelectedCellY: 4, CameraCellX: 12, CameraCellY: 34);

    private static L1IntentParser Parser() => L1IntentParserTests.CreateParser();

    // ---- #1 守住X ------------------------------------------------------------

    public static TheoryData<string, string> HoldZh => new()
    {
        { "守住东门", "target=east_gate" },
        { "顶住南门", "target=south_gate" },        // T-061 miss #19
        { "别放敌人进北门", "target=north_gate" },   // T-061 miss #20
        { "全力防守东门", "target=east_gate" },      // T-061 miss #22
        { "西墙不能丢", "target=west_wall" },
        { "重点防守北边", "target=north_side" },
    };

    public static TheoryData<string, string> HoldEn => new()
    {
        { "hold the east gate", "target=east_gate" },
        { "hold the north gate at all costs", "target=north_gate" }, // T-061 miss #169
        { "keep them off the west side", "target=west_side" },       // T-061 miss #183
        { "east gate, hold it", "target=east_gate" },                // T-061 miss #184
        { "hold the western gate", "target=west_gate" },             // T-061 miss #182
        { "defend the southern gate", "target=south_gate" },         // 对齐 eastern gate 的别名扩容
    };

    [Theory] // legal
    [MemberData(nameof(HoldZh))]
    [MemberData(nameof(HoldEn))]
    public void HoldRegion_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.HoldRegion, text, expectedSlots);

    [Theory] // illegal：无目标/未知目标 → 澄清卡
    [InlineData("守住那个地方")]
    [InlineData("hold the line everywhere")]
    public void HoldRegion_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #2 建造 -------------------------------------------------------------

    public static TheoryData<string, string> BuildZh => new()
    {
        { "在东门建两座箭塔", "where=east_gate|qty=2|tower=era1:tower-wood-arrow" },
        { "东边造个火堆", "where=east_side|qty=1|tower=era1:tower-campfire" },      // T-061 miss #46（裸量词=1）
        { "南门再来两座箭塔", "where=south_gate|qty=2|tower=era1:tower-wood-arrow" }, // T-061 miss #47
        { "在南面建两座火堆", "where=south_side|qty=2|tower=era1:tower-campfire" },  // T-061 §5 越权案例一
        { "造三座落石台", "qty=3|tower=era1:tower-rockfall" },
    };

    public static TheoryData<string, string> BuildEn => new()
    {
        { "build two arrow towers at the east gate", "where=east_gate|qty=2|tower=era1:tower-wood-arrow" },
        { "I want arrow towers at the east gate", "where=east_gate|tower=era1:tower-wood-arrow" }, // T-061 miss #204
        { "build two camfire at the south gate", "where=south_gate|qty=2|tower=era1:tower-campfire" }, // miss #205
        { "put up a spike trap at the north wall", "where=north_wall|qty=1|tower=era1:tower-spike-trap" },
    };

    [Theory] // legal
    [MemberData(nameof(BuildZh))]
    [MemberData(nameof(BuildEn))]
    public void Build_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.Build, text, expectedSlots);

    [Theory] // illegal
    [InlineData("多造几个箭塔")]
    [InlineData("build towers at the east gate")]
    public void Build_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #3 升级（T-069 新增） ------------------------------------------------

    public static TheoryData<string, string> UpgradeZh => new()
    {
        { "把箭楼升到三级", "target=era1:tower-wood-arrow|to_tier=3" },  // 同音容错 箭楼→箭塔
        { "升级东门的塔", "target=east_gate+any" },
        { "把北墙的塔升到2级", "target=north_wall+any|to_tier=2" },
        { "箭塔升级", "target=era1:tower-wood-arrow" },
        { "把落石台升级", "target=era1:tower-rockfall" },
        { "给尖刺陷阱升级", "target=era1:tower-spike-trap" },
        { "把篝火升到四级", "target=era1:tower-campfire|to_tier=4" },
        { "帮我升级箭塔", "target=era1:tower-wood-arrow" },
    };

    public static TheoryData<string, string> UpgradeEn => new()
    {
        { "upgrade the arrow tower to tier 3", "target=era1:tower-wood-arrow|to_tier=3" },
        { "upgrade all towers on the north wall", "target=north_wall+any" },
        { "upgrade the campfire", "target=era1:tower-campfire" },
        { "upgrade both rockfalls", "target=era1:tower-rockfallx2" },
        { "upgrade the spike traps", "target=era1:tower-spike-trap" },
        { "level up the rockfall", "target=era1:tower-rockfall" },
        { "max out the campfire", "target=era1:tower-campfire|to_tier=4" },
        { "upgrade everything on the west side", "target=west_side+any" },
    };

    [Theory] // legal
    [MemberData(nameof(UpgradeZh))]
    [MemberData(nameof(UpgradeEn))]
    public void Upgrade_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.Upgrade, text, expectedSlots);

    [Theory] // illegal：无目标指认 → 澄清卡
    [InlineData("升级")]
    [InlineData("把那个东西升级")]
    [InlineData("upgrade")]
    [InlineData("upgrade the castle")]
    public void Upgrade_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #4 出售（T-069 新增） ------------------------------------------------

    public static TheoryData<string, string> SellZh => new()
    {
        { "卖掉北墙的旧塔", "target=north_wall+any" },
        { "出售东门的两座箭塔", "target=east_gate+era1:tower-wood-arrowx2" },
        { "把南门的陷阱卖了", "target=south_gate+era1:tower-spike-trap" },
        { "这个塔卖掉", "target=sel:era1:tower-campfire" }, // 多模态指认（MatrixPointing）
        { "出售篝火", "target=era1:tower-campfire" },
        { "把两座箭塔卖了", "target=era1:tower-wood-arrowx2" },
        { "卖掉全部的塔", "target=all" },
        { "出售3号塔", "target=ord:3" },
    };

    public static TheoryData<string, string> SellEn => new()
    {
        { "sell the old tower on the north wall", "target=north_wall+any" },
        { "sell two campfires at the east gate", "target=east_gate+era1:tower-campfirex2" },
        { "sell everything on the west side", "target=west_side+any" },
        { "sell the spike traps", "target=era1:tower-spike-trap" },
        { "sell that campfire", "target=era1:tower-campfire" },
        { "sell all towers", "target=all" },
        { "sell tower 3", "target=ord:3" },
        { "sell this tower", "target=sel:era1:tower-campfire" },
    };

    [Theory] // legal
    [MemberData(nameof(SellZh))]
    [MemberData(nameof(SellEn))]
    public void Sell_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.Sell, text, expectedSlots, MatrixPointing);

    [Theory] // illegal：无指认的裸卖出请求 → 澄清卡（危险位意图绝不猜）
    [InlineData("卖掉")]
    [InlineData("卖掉那座房子")]
    [InlineData("sell")]
    [InlineData("sell the castle")]
    public void Sell_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #5 集火（T-069 新增） ------------------------------------------------

    public static TheoryData<string, string> FocusZh => new()
    {
        { "集火那个攻城锤", "target=era2:enemy-ram" },
        { "集火攻城锤", "target=era2:enemy-ram" },
        { "大家集火boss", "target=era1:enemy-boss" },
        { "先集火自爆鼩", "target=era1:enemy-boomshrew" },
        { "集火左边的敌人", "target=side:left" },
        { "集火3号敌人", "target=ord:3" },
        { "集火敌人", "target=any" },
        { "集火东门的敌人", "target=east_gate" },
    };

    public static TheoryData<string, string> FocusEn => new()
    {
        { "focus the ram", "target=era2:enemy-ram" },
        { "focus fire on the boar", "target=era1:enemy-boar" },
        { "everyone focus the boss", "target=era1:enemy-boss" },
        { "focus the boomshrew", "target=era1:enemy-boomshrew" },
        { "focus the enemies at the east gate", "target=east_gate" },
        { "focus enemy 3", "target=ord:3" },
        { "focus the enemies", "target=any" },
        { "focus the armored boar", "target=era1:enemy-boar" },
    };

    [Theory] // legal
    [MemberData(nameof(FocusZh))]
    [MemberData(nameof(FocusEn))]
    public void FocusFire_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.FocusFire, text, expectedSlots);

    [Theory] // illegal
    [InlineData("集火")]
    [InlineData("集火那条龙")]
    [InlineData("focus the dragon")]
    [InlineData("focus")]
    public void FocusFire_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #6 改集火模式（T-069 新增） -------------------------------------------

    public static TheoryData<string, string> ModeZh => new()
    {
        { "改模式优先打强的", "mode=strong" },
        { "切换目标到最近的敌人", "mode=closest" },
        { "箭塔改模式为打最强", "target_towers=era1:tower-wood-arrow|mode=strong" },
        { "切换目标模式为最近的", "mode=closest" },
        { "东门箭塔切换目标到最强", "target_towers=east_gate+era1:tower-wood-arrow|mode=strong" },
        { "全体改模式为先打", "mode=first" },
        { "改成最近的", "mode=closest" },
        { "箭塔改成打最后的", "target_towers=era1:tower-wood-arrow|mode=last" },
    };

    public static TheoryData<string, string> ModeEn => new()
    {
        { "target mode: strongest first", "mode=strong" },
        { "switch target mode to closest", "mode=closest" },
        { "set target mode to first", "mode=first" },
        { "change target mode to last", "mode=last" },
        { "arrow towers target mode strongest", "target_towers=era1:tower-wood-arrow|mode=strong" },
        { "set the arrow towers to target strongest", "target_towers=era1:tower-wood-arrow|mode=strong" },
        { "switch the rockfalls to closest", "target_towers=era1:tower-rockfall|mode=closest" },
        { "make the spike traps target first", "target_towers=era1:tower-spike-trap|mode=first" },
    };

    [Theory] // legal
    [MemberData(nameof(ModeZh))]
    [MemberData(nameof(ModeEn))]
    public void ChangeFocusMode_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.ChangeFocusMode, text, expectedSlots);

    [Theory] // illegal：无模式词 → 澄清卡
    [InlineData("改模式")]
    [InlineData("切换目标")]
    [InlineData("target mode")]
    [InlineData("switch target mode")]
    public void ChangeFocusMode_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #7 物流改道（T-069 新增） ---------------------------------------------

    public static TheoryData<string, string> RerouteZh => new()
    {
        { "木材先送南门", "item=era1:res-wood|to=south_gate" },
        { "把木头改道去东门", "item=era1:res-wood|to=east_gate" },
        { "石料先送北墙", "item=era1:res-stone|to=north_wall" },
        { "食物改道送到西边", "item=era1:res-food|to=west_side" },
        { "先送粮草去南墙", "item=era1:res-food|to=south_wall" },
        { "东门的木材改道送南门", "from=east_gate|item=era1:res-wood|to=south_gate" },
        { "把石头从北门改道到西门", "from=north_gate|item=era1:res-stone|to=west_gate" },
        { "木材全部送去东门", "item=era1:res-wood|to=east_gate|rate=1" },
    };

    public static TheoryData<string, string> RerouteEn => new()
    {
        { "route wood to the south gate first", "item=era1:res-wood|to=south_gate" },
        { "route all timber to the north wall", "item=era1:res-wood|to=north_wall|rate=1" },
        { "reroute stone to the east gate", "item=era1:res-stone|to=east_gate" },
        { "route food to the west side", "item=era1:res-food|to=west_side" },
        { "route everything to the center", "item=all|to=center" },
        { "route the wood from the east gate to the north wall", "from=east_gate|item=era1:res-wood|to=north_wall" },
        { "divert grain to the south wall", "item=era1:res-food|to=south_wall" },
        { "route half the stone to the west gate", "item=era1:res-stone|to=west_gate|rate=0.5" },
    };

    [Theory] // legal
    [MemberData(nameof(RerouteZh))]
    [MemberData(nameof(RerouteEn))]
    public void RerouteLogistics_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.RerouteLogistics, text, expectedSlots);

    [Theory] // illegal
    [InlineData("改道")]
    [InlineData("木材改道")]
    [InlineData("route")]
    [InlineData("reroute")]
    public void RerouteLogistics_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #8 储备查询 -----------------------------------------------------------

    public static TheoryData<string, string> StockZh => new()
    {
        { "查一下石头", "item=era1:res-stone" },   // T-061 §5 越权案例二
        { "粮草还剩多少", "item=era1:res-food" },  // T-061 miss #97
        { "木材多吗", "item=era1:res-wood" },      // T-061 miss #96
        { "查木材", "item=era1:res-wood" },
    };

    public static TheoryData<string, string> StockEn => new()
    {
        { "wood?", "item=era1:res-wood" },                      // T-061 miss #258（疑问语调裸词）
        { "are we running low on wood", "item=era1:res-wood" }, // T-061 miss #259
        { "stockpile report", "item=all" },
    };

    [Theory] // legal
    [MemberData(nameof(StockZh))]
    public void QueryStockpile_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.QueryStockpile, text, expectedSlots);

    [Theory] // legal（en）
    [MemberData(nameof(StockEn))]
    public void QueryStockpile_matrix_en_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.QueryStockpile, text, expectedSlots);

    [Theory] // illegal：裸词无问号/未知物资 → 澄清卡
    [InlineData("木材")]
    [InlineData("wood")]
    [InlineData("how much ammo do we have")]
    [InlineData("how much iron is left")]
    public void QueryStockpile_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #9 敌情查询 -----------------------------------------------------------

    public static TheoryData<string, string> ThreatZh => new()
    {
        { "本波威胁是什么", "scope=current" },    // T-061 miss #69
        { "下一波是什么敌人", "scope=next_wave" }, // T-061 miss #70
        { "现在的敌情如何", "scope=current" },     // T-061 miss #71
        { "敌情如何", "scope=current" },           // T-061 miss #72
        { "南门有什么威胁", "scope=south_gate" },
    };

    public static TheoryData<string, string> ThreatEn => new()
    {
        { "incoming wave threats", "scope=next_wave" },      // T-061 miss #232
        { "what's coming in the next wave", "scope=next_wave" }, // T-061 miss #233
        { "how's the situation", "scope=current" },          // T-061 miss #234
        { "any threats at the west wall", "scope=west_wall" },
    };

    [Theory] // legal
    [MemberData(nameof(ThreatZh))]
    [MemberData(nameof(ThreatEn))]
    public void QueryThreat_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.QueryThreat, text, expectedSlots);

    [Theory] // illegal
    [InlineData("下一波几点来")]
    [InlineData("when does the next wave arrive")]
    public void QueryThreat_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #10 设闹 ---------------------------------------------------------------

    public static TheoryData<string, string> AlarmZh => new()
    {
        { "半分钟后叫我", "after=30" }, // T-061 miss #122
        { "三分钟后叫我", "after=180" },
    };

    public static TheoryData<string, string> AlarmEn => new()
    {
        { "remind me in half a minute", "after=30" }, // T-061 miss #284
        { "remind me in three minutes", "after=180" },
    };

    [Theory] // legal
    [MemberData(nameof(AlarmZh))]
    [MemberData(nameof(AlarmEn))]
    public void SetAlarm_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.SetAlarm, text, expectedSlots);

    [Theory] // illegal
    [InlineData("明早八点叫我")]
    [InlineData("wake me up at 8 am")]
    public void SetAlarm_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #11 巡逻（T-069 新增） --------------------------------------------------

    public static TheoryData<string, string> PatrolZh => new()
    {
        { "去西边巡逻", "lane=lane_west|duration=60" },
        { "北门转一圈", "lane=lane_north|duration=60" },
        { "派侦察去东边巡逻", "lane=lane_east|duration=60" },
        { "南边巡逻一下", "lane=lane_south|duration=60" },
        { "去敌后转一圈", "lane=lane_rear|duration=60" },
        { "去西边看看", "lane=lane_west|duration=60" },
        { "去北线巡逻两分钟", "lane=lane_north|duration=120" },
        { "侦察南线", "lane=lane_south|duration=60" },
    };

    public static TheoryData<string, string> PatrolEn => new()
    {
        { "patrol the west lane", "lane=lane_west|duration=60" },
        { "patrol the east side", "lane=lane_east|duration=60" },
        { "send a scout to patrol the north gate", "lane=lane_north|duration=60" },
        { "patrol both walls", "lane=lane_walls|duration=60" },
        { "patrol the south road", "lane=lane_south|duration=60" },
        { "scout the enemy rear", "lane=lane_rear|duration=60" },
        { "patrol the west lane for three minutes", "lane=lane_west|duration=180" },
        { "go check the north side", "lane=lane_north|duration=60" },
    };

    [Theory] // legal
    [MemberData(nameof(PatrolZh))]
    [MemberData(nameof(PatrolEn))]
    public void Patrol_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.Patrol, text, expectedSlots);

    [Theory] // illegal：无通道 → 澄清卡
    [InlineData("巡逻")]
    [InlineData("去皇宫巡逻")]
    [InlineData("patrol")]
    [InlineData("scout")]
    public void Patrol_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- #12 卷轴触发+停止（T-069 新增） ------------------------------------------

    public static TheoryData<string, string> ScrollZh => new()
    {
        { "开火油卷轴", "scroll=era2:scroll-fire-oil" },
        { "用卷轴", "scroll=any" },
        { "开冰霜卷轴", "scroll=era2:scroll-frost" },
        { "卷轴都停掉", "scroll=any|task=all" },
        { "把卷轴停了", "task=all" },
        { "开力量卷轴", "scroll=era2:scroll-strength" },
        { "把火油卷轴开了", "scroll=era2:scroll-fire-oil" },
        { "停掉所有卷轴", "task=all" },
    };

    public static TheoryData<string, string> ScrollEn => new()
    {
        { "use the fire scroll", "scroll=era2:scroll-fire-oil" },
        { "trigger the ice scroll", "scroll=era2:scroll-frost" },
        { "stop all scrolls", "task=all" },
        { "scroll of strength, go", "scroll=era2:scroll-strength" },
        { "cancel the fire scroll", "task=era2:scroll-fire-oil" },
        { "activate the frost scroll", "scroll=era2:scroll-frost" },
        { "stop the scrolls", "task=all" },
        { "shut down all scrolls", "task=all" },
    };

    [Theory] // legal
    [MemberData(nameof(ScrollZh))]
    [MemberData(nameof(ScrollEn))]
    public void ScrollControl_matrix_positives(string text, string expectedSlots)
        => AssertIntent(IntentKind.ScrollControl, text, expectedSlots);

    [Theory] // illegal
    [InlineData("卷轴")]
    [InlineData("开卷帘")]
    [InlineData("scroll")]
    [InlineData("use the")]
    public void ScrollControl_matrix_negatives(string text)
    {
        var result = Parser().Parse(text, MatrixPointing);

        Assert.Null(result.Intent);
        Assert.False(string.IsNullOrEmpty(result.Failure?.ClarifyingKey), $"{text}: 澄清话术键为空");
    }

    // ---- 多模态指认（v3.2 §11.2；T-069 任务说明第 4 条） ---------------------------

    [Theory] // legal：这里/这个位置 → 最近选中格优先，镜头中心格兜底
    [InlineData("守住这里", "target=cell:3,4")]
    [InlineData("守住这个位置", "target=cell:3,4")]
    [InlineData("hold here", "target=cell:12,34")] // 无选中格 → 镜头中心（MatrixPointingOnlyCamera）
    public void Pointing_place_demonstratives_resolve_to_cells(string text, string expectedSlots)
    {
        var withSelection = new PointingContext("era1:tower-campfire", 3, 4, 12, 34);
        var cameraOnly = new PointingContext(null, null, null, 12, 34);
        var context = text == "hold here" ? cameraOnly : withSelection;

        var intent = Parser().Parse(text, context).Intent;

        Assert.NotNull(intent);
        AssertSlots(text, intent!, expectedSlots);
    }

    [Theory] // legal：这个/那个 → 最近选中实体；@x,y 后缀覆盖一切指认
    [InlineData("守住这个", "target=sel:era1:tower-campfire")]
    [InlineData("卖掉那个塔", "target=sel:era1:tower-campfire")]
    [InlineData("守住这里 @56,78", "target=cell:56,78")]
    [InlineData("卖掉这个塔 @12,34", "target=cell:12,34")]
    [InlineData("在这里建箭塔", "where=cell:3,4|tower=era1:tower-wood-arrow")]
    [InlineData("在东门建两座箭塔@40,50", "where=cell:40,50|qty=2|tower=era1:tower-wood-arrow")]
    public void Pointing_entity_demonstratives_and_coordinate_suffix(string text, string expectedSlots)
    {
        var intent = Parser().Parse(text, new PointingContext("era1:tower-campfire", 3, 4, 12, 34)).Intent;

        Assert.NotNull(intent);
        AssertSlots(text, intent!, expectedSlots);
    }

    [Theory] // illegal：指认槽无上下文可解 → 澄清卡（缺槽澄清，绝不猜）
    [InlineData("守住这里")]
    [InlineData("卖掉这个塔")]
    [InlineData("hold here")]
    public void Pointing_without_context_routes_to_clarification(string text)
    {
        var result = Parser().Parse(text);

        Assert.Null(result.Intent);
        Assert.Equal(L1IntentParser.DefaultClarifyingKey, result.Failure!.ClarifyingKey);
    }

    // ---- 断言辅助 ---------------------------------------------------------------

    private static void AssertIntent(IntentKind kind, string text, string expectedSlots, PointingContext? pointing = null)
    {
        var result = Parser().Parse(text, pointing ?? MatrixPointing);

        Assert.NotNull(result.Intent);
        Assert.Equal(kind, result.Intent!.Kind);
        AssertSlots(text, result.Intent, expectedSlots);
    }

    /// <summary>逐槽断言（k=v|k2=v2 编码）；键集与值都须完全一致。</summary>
    private static void AssertSlots(string text, Intent intent, string expectedSlots)
    {
        var expected = expectedSlots.Length == 0
            ? new Dictionary<string, string>()
            : expectedSlots.Split('|')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => pair[1]);

        Assert.True(expected.Count == intent.Slots.Count,
            $"{text}: slot count expected {expected.Count}, got {intent.Slots.Count} [{string.Join(",", intent.Slots.Select(s => s.Key + "=" + s.Value))}]");
        foreach (var (slot, value) in expected)
        {
            Assert.True(
                intent.Slots.TryGetValue(slot, out var actual) && actual == value,
                $"{text}: slot '{slot}' expected '{value}', got '{intent.Slots.GetValueOrDefault(slot)}'");
        }
    }
}
