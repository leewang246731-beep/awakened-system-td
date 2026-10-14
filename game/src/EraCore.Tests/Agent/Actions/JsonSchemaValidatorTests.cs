using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062] C-03 闭 schema 校验器正反矩阵（字段存在性/类型/枚举/数值范围/指针+行号）
// 注：载荷一律经 ActionJsonSamples 构造（方法体内不写字面 JSON——T4 扫描器
// 按花括号配对截取方法体，内嵌 JSON 会切断断言扫描）。
public class JsonSchemaValidatorTests
{
    private readonly JsonSchemaValidator _validator = new();

    private static string Describe(SchemaValidationResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Pointer}@L{e.Line}: {e.Message}"));

    private SchemaValidationResult Validate(string payload) => _validator.Validate(payload);

    // ---------------------------------------------------- 数值范围（legal/boundary/illegal）

    [Theory] // legal
    [InlineData(15.0)]
    [InlineData(60.0)]
    [InlineData(120.0)]
    public void SpawnScout_duration_accepts_in_range(double duration)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.SpawnScoutDuration(duration));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // illegal：duration 钳制区间 [15,120] 之外即拒
    [InlineData(14.9)]
    [InlineData(120.1)]
    [InlineData(-1.0)]
    public void SpawnScout_duration_rejects_out_of_range(double duration)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.SpawnScoutDuration(duration));
        Assert.False(result.IsValid, $"duration={duration} 应非法：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/duration");
    }

    [Theory] // boundary：SetAlarm after_seconds [30,600] 端点合法
    [InlineData(30.0)]
    [InlineData(600.0)]
    public void SetAlarm_after_seconds_accepts_endpoints(double seconds)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.SetAlarmAfter(seconds));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // legal（T-070 DEBT-4 裁定：钳制而非拒绝）——after_seconds 越界值
    // 不再是 schema 违规：ActionSchemaClamper 在闭 schema 闸前把越界值钳到
    // 边界（SP-2 #10/G.2"钳制[30,600]"原文措辞；裁定登记=11-spec-tables
    // 变更记录 v1.4）。钳制行为本身见 ActionSchemaClamperTests。
    [InlineData(0.0)]
    [InlineData(29.9)]
    [InlineData(600.1)]
    [InlineData(9000.0)]
    public void SetAlarm_after_seconds_out_of_range_is_clamped_not_rejected(double seconds)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.SetAlarmAfter(seconds));
        Assert.True(result.IsValid, $"after_seconds={seconds} 应过闸待钳（钳制语义）：{Describe(result)}");
    }

    [Theory] // boundary：rate [0.0,1.0] 端点合法
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void RerouteLogistics_rate_accepts_endpoints(double rate)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.RerouteRate(rate));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // illegal
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void RerouteLogistics_rate_rejects_out_of_range(double rate)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.RerouteRate(rate));
        Assert.False(result.IsValid, $"rate={rate} 应非法：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/rate");
    }

    [Theory] // boundary：to_tier 1..4 端点合法
    [InlineData(1)]
    [InlineData(4)]
    public void UpgradeTower_tier_accepts_endpoints(int tier)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.UpgradeTier(tier));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // illegal
    [InlineData(0)]
    [InlineData(5)]
    public void UpgradeTower_tier_rejects_out_of_range(int tier)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.UpgradeTier(tier));
        Assert.False(result.IsValid, $"to_tier={tier} 应非法：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/to_tier");
    }

    [Theory] // boundary：head_limit 1..20 端点合法
    [InlineData(1)]
    [InlineData(20)]
    public void QueryWorld_head_limit_accepts_endpoints(int limit)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.QueryHeadLimit(limit));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // illegal
    [InlineData(0)]
    [InlineData(21)]
    public void QueryWorld_head_limit_rejects_out_of_range(int limit)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.QueryHeadLimit(limit));
        Assert.False(result.IsValid, $"head_limit={limit} 应非法：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/head_limit");
    }

    // ---------------------------------------------------- 时代相关的 cells 上限（4+2×era）

    [Fact]
    public void BuildPlan_cells_cap_scales_with_era()
    {
        // boundary：E1 上限=4+2×1=6，E2 上限=4+2×2=8
        SchemaValidationResult sixAtE1 = Validate(ActionJsonSamples.BuildPlan(cellCount: 6));
        Assert.True(sixAtE1.IsValid, Describe(sixAtE1));
        SchemaValidationResult sevenAtE1 = Validate(ActionJsonSamples.BuildPlan(cellCount: 7));
        Assert.False(sevenAtE1.IsValid, Describe(sevenAtE1));
        Assert.Contains(sevenAtE1.Errors, e => e.Pointer == "/cells");

        var era2 = new JsonSchemaValidator(() => EraOrdinals.E2);
        Assert.True(era2.Validate(ActionJsonSamples.BuildPlan(cellCount: 7)).IsValid);
        Assert.True(era2.Validate(ActionJsonSamples.BuildPlan(cellCount: 8)).IsValid);
        Assert.False(era2.Validate(ActionJsonSamples.BuildPlan(cellCount: 9)).IsValid);
    }

    [Fact]
    public void BuildPlan_cells_requires_at_least_one_cell()
    {
        // illegal：空 cells 数组
        SchemaValidationResult result = Validate(ActionJsonSamples.BuildPlanCells(cellsJson: "[]"));
        Assert.False(result.IsValid, Describe(result));
        Assert.Contains(result.Errors, e => e.Pointer == "/cells");
    }

    // ---------------------------------------------------- 枚举值

    [Theory] // legal：枚举内值
    [InlineData("stats")]
    [InlineData("supply")]
    public void InspectTile_aspect_accepts_frozen_members(string aspect)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.InspectTileAspect(aspect));
        Assert.True(result.IsValid, Describe(result));
    }

    [Theory] // illegal：枚举外值
    [InlineData("weather")]
    [InlineData("STATS")]
    public void InspectTile_aspect_rejects_values_outside_the_enum(string aspect)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.InspectTileAspect(aspect));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Pointer == "/aspect" && e.Message.Contains("stats|threat|supply"));
    }

    [Theory] // illegal：闭枚举外的值（大小写漂移同判——Ordinal 精确匹配）
    [InlineData("aspect", "weather")]
    [InlineData("stance", "run")]
    [InlineData("mode", "weakest")]
    [InlineData("order", "jump")]
    [InlineData("verdict", "MAYBE")]
    public void Closed_enums_reject_values_outside_the_frozen_sets(string field, string value)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.EnumPayload(field, value));
        Assert.False(result.IsValid, $"{field}={value} 应被拒绝：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/" + field);
    }

    [Theory] // legal：QueryWorld key 四值
    [InlineData("wave")]
    [InlineData("resource")]
    [InlineData("threat")]
    [InlineData("paths")]
    public void QueryWorld_key_accepts_the_four_frozen_values(string key)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.QueryWorld(key));
        Assert.True(result.IsValid, Describe(result));
    }

    // ---------------------------------------------------- 长度与条数上限

    [Fact]
    public void ReportToPlayer_summary_accepts_exactly_200_chars()
    {
        // boundary：≤200 字（中文按字符计）
        string summary = new string('波', 200);
        SchemaValidationResult result = Validate(ActionJsonSamples.ReportToPlayer(summary: summary));
        Assert.True(result.IsValid, Describe(result));
    }

    [Fact]
    public void ReportToPlayer_summary_rejects_201_chars()
    {
        // illegal
        string summary = new string('波', 201);
        SchemaValidationResult result = Validate(ActionJsonSamples.ReportToPlayer(summary: summary));
        Assert.False(result.IsValid, Describe(result));
        Assert.Contains(result.Errors, e => e.Pointer == "/summary" && e.Message.Contains("200"));
    }

    [Fact]
    public void ReportToPlayer_findings_cap_is_eight()
    {
        // boundary：8 条合法
        SchemaValidationResult eight = Validate(ActionJsonSamples.ReportFindings(8));
        Assert.True(eight.IsValid, Describe(eight));

        // illegal：9 条拒绝
        SchemaValidationResult nine = Validate(ActionJsonSamples.ReportFindings(9));
        Assert.False(nine.IsValid, Describe(nine));
        Assert.Contains(nine.Errors, e => e.Pointer == "/findings");
    }

    [Fact]
    public void AskPlayer_options_cap_and_label_length()
    {
        // boundary：4 项合法；label 恰 8 字合法
        SchemaValidationResult four = Validate(ActionJsonSamples.AskPlayerOptions(4));
        Assert.True(four.IsValid, Describe(four));
        SchemaValidationResult eightChar = Validate(ActionJsonSamples.AskPlayerOptions(2, label: "一二三四五六七八"));
        Assert.True(eightChar.IsValid, Describe(eightChar));

        // illegal：5 项拒绝；9 字 label 拒绝（≤8 字）
        SchemaValidationResult five = Validate(ActionJsonSamples.AskPlayerOptions(5));
        Assert.False(five.IsValid, Describe(five));
        Assert.Contains(five.Errors, e => e.Pointer == "/options");
        SchemaValidationResult longLabel = Validate(ActionJsonSamples.AskPlayerOptions(2, label: "一二三四五六七八九"));
        Assert.False(longLabel.IsValid);
        Assert.Contains(longLabel.Errors, e => e.Pointer == "/options/0/label");
    }

    [Fact]
    public void SetTowerMode_target_array_cap_is_six()
    {
        // boundary：6 个合法；illegal：7 个拒绝
        SchemaValidationResult six = Validate(ActionJsonSamples.SetTowerModeIds(6));
        Assert.True(six.IsValid, Describe(six));
        SchemaValidationResult seven = Validate(ActionJsonSamples.SetTowerModeIds(7));
        Assert.False(seven.IsValid, Describe(seven));
        Assert.Contains(seven.Errors, e => e.Pointer == "/target_id");
    }

    // ---------------------------------------------------- 字段存在性 / 类型 / 危险位

    [Theory] // illegal：必填字段缺失（指针指向缺失字段名）
    [InlineData("BuildPlan", "tower_id")]
    [InlineData("BuildPlan", "cells")]
    [InlineData("SetRally", "stance")]
    [InlineData("SetAlarm", "reason")]
    [InlineData("AskPlayer", "multi_select")]
    [InlineData("SpawnScout", "duration")]
    [InlineData("SellTower", "confirm_sell")]
    public void Missing_required_fields_are_rejected(string verb, string field)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.WithFieldRemoved(verb, field));
        Assert.False(result.IsValid, $"{verb} 缺 {field} 应非法：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Pointer == "/" + field);
    }

    [Fact]
    public void Missing_action_verb_is_rejected()
    {
        // illegal：缺失判别字段
        SchemaValidationResult result = Validate(ActionJsonSamples.TowerOnlyPayload());
        Assert.False(result.IsValid, Describe(result));
        Assert.Contains(result.Errors, e => e.Pointer == "/action");
    }

    [Theory] // illegal：类型错
    [InlineData("duration-string")]
    [InlineData("after-bool")]
    [InlineData("target-string")]
    [InlineData("cells-object")]
    [InlineData("target-fractional")]
    [InlineData("aspect-number")]
    public void Type_mismatches_are_rejected(string kind)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.TypeMismatch(kind));
        Assert.False(result.IsValid, $"{kind} 应被拒绝：{Describe(result)}");
        Assert.Contains(result.Errors, e => e.Message.Contains("must be"));
    }

    [Fact]
    public void Bad_cell_coordinates_report_item_pointers()
    {
        // illegal：cells 数组第 2 项 y 非整数 → 指针精确到 /cells/1/y
        SchemaValidationResult result = Validate(ActionJsonSamples.BuildPlanCells(
            "[{\"x\":1,\"y\":1},{\"x\":2,\"y\":1.5}]"));
        Assert.False(result.IsValid, Describe(result));
        Assert.Contains(result.Errors, e => e.Pointer == "/cells/1/y");
    }

    [Fact]
    public void SellTower_requires_explicit_true_danger_bit()
    {
        // legal：显式 true
        SchemaValidationResult valid = Validate(ActionJsonSamples.SellTower());
        Assert.True(valid.IsValid, Describe(valid));

        // illegal：false / 缺失同判（危险位永不缺省通过）
        SchemaValidationResult falseBit = Validate(ActionJsonSamples.SellTowerDangerBit(confirmSell: "false"));
        Assert.False(falseBit.IsValid);
        Assert.Contains(falseBit.Errors, e => e.Pointer == "/confirm_sell" && e.Message.Contains("danger bit"));
        SchemaValidationResult absent = Validate(ActionJsonSamples.SellTowerDangerBit(confirmSell: null));
        Assert.False(absent.IsValid, Describe(absent));
        Assert.Contains(absent.Errors, e => e.Pointer == "/confirm_sell");
    }

    // ---------------------------------------------------- 根级非法形态

    [Theory] // illegal：根级形态（数组/标量/空/坏 JSON）
    [InlineData("array")]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("truncated")]
    public void Non_object_or_malformed_payloads_are_rejected(string kind)
    {
        SchemaValidationResult result = Validate(ActionJsonSamples.RootKind(kind));
        Assert.False(result.IsValid, $"{kind} 应被拒绝");
    }

    [Fact]
    public void Malformed_json_error_carries_the_parse_line()
    {
        // illegal：多行载荷第 3 行断裂 → 行号式错误定位到出错行之后
        SchemaValidationResult result = Validate(ActionJsonSamples.MalformedMultiline());
        Assert.False(result.IsValid);
        SchemaValidationError error = Assert.Single(result.Errors);
        Assert.True(error.Line >= 2, $"行号应≥2（实际 {error.Line}）");
    }

    [Fact]
    public void Errors_carry_pointer_and_line_for_multiline_payloads()
    {
        // illegal：三行载荷，越界 duration 在第 3 行 → 指针+行号双精确
        SchemaValidationResult result = Validate(ActionJsonSamples.MultilineBadDuration());
        Assert.False(result.IsValid);
        SchemaValidationError error = Assert.Single(result.Errors);
        Assert.Equal("/duration", error.Pointer);
        Assert.Equal(3, error.Line);
    }

    // ---------------------------------------------------- 判别字段读取

    [Fact]
    public void TryReadVerb_extracts_the_discriminating_verb()
    {
        // legal
        Assert.True(JsonSchemaValidator.TryReadVerb(ActionJsonSamples.LookAround(), out string? verb));
        Assert.Equal("LookAround", verb);

        // illegal：非对象/缺字段/坏 JSON 返回 false
        Assert.False(JsonSchemaValidator.TryReadVerb(ActionJsonSamples.TowerOnlyPayload(), out _));
        Assert.False(JsonSchemaValidator.TryReadVerb(ActionJsonSamples.RootKind("truncated"), out _));
        Assert.False(JsonSchemaValidator.TryReadVerb(ActionJsonSamples.RootKind("array"), out _));
    }
}
