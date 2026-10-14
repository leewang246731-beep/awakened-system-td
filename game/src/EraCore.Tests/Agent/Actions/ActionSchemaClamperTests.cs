using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062][F-074] T-070 DEBT-4 裁定落地：SetAlarm.after_seconds 越界=钳到边界
// 而非拒绝（SP-2 #10/G.2 原文"钳制[30,600]"措辞；11-spec-tables 变更记录
// v1.4 登记）。三档：合法原样返回（同引用）、边界值不动、越界单侧钳制；
// 非法类型仍由闭 schema 拒绝（类型语义与数值域语义分离）。
// 注：载荷一律经 ActionJsonSamples/本文件构造器拼装（T4 扫描器口径）。

/// <summary>[F-074] Schema-declared clamp rewriting (SetAlarm.after_seconds [30,600]).</summary>
public class ActionSchemaClamperTests
{
    private static string SetAlarmJson(double seconds) =>
        "{\"action\":\"SetAlarm\",\"after_seconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"reason\":\"北门敌军逼近\"}";

    private static double ReadAfterSeconds(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("after_seconds").GetDouble();
    }

    [Theory] // legal — 区间内一律原样返回（同引用，未发生任何重写）
    [InlineData(30.0)]
    [InlineData(90.0)]
    [InlineData(375.5)]
    [InlineData(600.0)]
    public void In_range_payloads_return_unchanged(double seconds)
    {
        string payload = SetAlarmJson(seconds);
        Assert.Same(payload, ActionSchemaClamper.Clamp(payload));
    }

    [Theory] // boundary → clamp — 越界值钳到最近边界（下溢→30，上溢→600）
    [InlineData(-5.0, 30.0)]
    [InlineData(0.0, 30.0)]
    [InlineData(29.9, 30.0)]
    [InlineData(600.1, 600.0)]
    [InlineData(9000.0, 600.0)]
    public void Out_of_range_values_clamp_to_the_nearest_boundary(double input, double expected)
    {
        string clamped = ActionSchemaClamper.Clamp(SetAlarmJson(input));
        Assert.Equal(expected, ReadAfterSeconds(clamped));
    }

    [Fact] // legal — 钳制不碰其它字段：reason 原文保留、动词不变
    public void Clamp_preserves_every_other_field()
    {
        string clamped = ActionSchemaClamper.Clamp(SetAlarmJson(9000.0));
        using JsonDocument document = JsonDocument.Parse(clamped);
        Assert.Equal("SetAlarm", document.RootElement.GetProperty("action").GetString());
        Assert.Equal("北门敌军逼近", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(3, document.RootElement.EnumerateObject().Count()); // action+after_seconds+reason，无新增字段
    }

    [Theory] // illegal 形态 — 非法载荷原样返回，由闭 schema 统一报错（单一上报路径）
    [InlineData("{\"action\":\"SetAlarm\",\"reason\":\"r\"}")]
    [InlineData("{\"action\":\"Nope\",\"after_seconds\":9000.0}")]
    [InlineData("[9000.0]")]
    [InlineData("")]
    public void Malformed_or_unknown_verb_payloads_return_unchanged(string payload)
    {
        Assert.Same(payload, ActionSchemaClamper.Clamp(payload));
    }

    [Fact] // boundary — 钳制只看本字段：缺 reason 的载荷其 after_seconds 照样被钳（缺失由闭 schema 报）
    public void Clamp_field_is_rewritten_even_when_other_fields_are_invalid()
    {
        string payload = "{\"action\":\"SetAlarm\",\"after_seconds\":9000.0}";
        string clamped = ActionSchemaClamper.Clamp(payload);
        Assert.NotSame(payload, clamped);
        Assert.Equal(600.0, ReadAfterSeconds(clamped));

        var validator = new JsonSchemaValidator();
        Assert.False(validator.Validate(clamped).IsValid); // reason 缺失仍被拒绝（单一上报路径）
    }

    [Fact] // legal — 非 SetAlarm 的数值域字段不受钳制（rate 0~1 仍是拒绝语义）
    public void Non_clamp_fields_are_not_rewritten()
    {
        string payload = ActionJsonSamples.RerouteRate(1.5);
        Assert.Same(payload, ActionSchemaClamper.Clamp(payload));
    }

    [Fact] // boundary — 组合语义：钳制后过校验必绿（钳制字段不再产生数值域错误）
    public void Clamped_payloads_always_validate()
    {
        var validator = new JsonSchemaValidator();
        SchemaValidationResult result = validator.Validate(ActionSchemaClamper.Clamp(SetAlarmJson(0.0)));
        Assert.True(result.IsValid);
        Assert.Equal(30.0, ReadAfterSeconds(ActionSchemaClamper.Clamp(SetAlarmJson(0.0))));
    }

    [Fact] // illegal — 类型错（布尔）不钳制不吞错：仍被闭 schema 拒绝
    public void Type_mismatches_still_fail_validation()
    {
        var validator = new JsonSchemaValidator();
        SchemaValidationResult result = validator.Validate(ActionJsonSamples.TypeMismatch("after-bool"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Pointer == "/after_seconds");
    }

    [Fact] // boundary — 注册表唯一门：越界 SetAlarm 不再 ErrSchemaDenied（钳制后过闸，语义=钳制非拒绝）
    public void Registry_clamps_before_the_closed_schema_gate()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(SetAlarmJson(9000.0));
        Assert.NotEqual(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.DoesNotContain("must lie in", outcome.Result.ErrorMessage, StringComparison.Ordinal);
    }
}
