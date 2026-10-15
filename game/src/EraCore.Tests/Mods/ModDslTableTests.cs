using System.Text.Json;
using EraCore.Mods;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-094][F-158] DSL 三族（效果原语/世界操作/系统命令）封闭词表——T-103。
// 三行覆盖：合法（三族各一）/边界（值域边界+封闭形状）/非法（未知 op/verb+行号）。

public class ModDslTableTests
{
    private static IReadOnlyList<ModSchemaViolation> Validate(
        ModDslFamily family, string entryJson, string pointer = "/items/0/on_hit/0")
    {
        JsonElement element = JsonDocument.Parse(entryJson).RootElement;
        return ModDslTable.ValidateEntry(family, element, pointer, ModJsonLineMap.Index(Wrap(entryJson)));
    }

    private static string Wrap(string entryJson) => "{\n  \"items\": [\n    {\n      \"on_hit\": [\n        " + entryJson + "\n      ]\n    }\n  ]\n}";

    // ---- legal ----------------------------------------------------------

    [Fact] // [F-094] 效果原语：封闭清单内 op+合法参数过
    public void EffectPrimitive_KnownOpsWithValidArgs_Pass()
    {
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, """{"op":"slow_percent","percent":30,"ticks":120}"""));
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, """{"op":"damage_bonus_percent","percent":15.5}"""));
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, """{"op":"burn_dps","dps":2.0,"ticks":90}"""));
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, """{"op":"heal_percent","percent":0}"""));
    }

    [Fact] // [F-094] 世界操作：封闭清单内 op 过
    public void WorldOperation_KnownOpsWithValidArgs_Pass()
    {
        Assert.Empty(Validate(ModDslFamily.WorldOperation, """{"op":"grant_resource","resource":"era2:res-wood","amount":5.0}"""));
        Assert.Empty(Validate(ModDslFamily.WorldOperation, """{"op":"reveal_zone","zone":"barbican"}"""));
        Assert.Empty(Validate(ModDslFamily.WorldOperation, """{"op":"set_lane_stance","lane":"north","stance":"hold"}"""));
        Assert.Empty(Validate(ModDslFamily.WorldOperation, """{"op":"set_terrain_cost","x":3,"y":4,"cost":2.0}"""));
    }

    [Fact] // [F-158] 系统命令：verb 必须落在 K-3 19 动词封闭清单内（动词不可扩展）
    public void SystemCommand_VerbSet_IsExactlyTheK3Nineteen()
    {
        Assert.Equal(19, ModDslTable.SystemVerbs.Count);
        Assert.Contains("FocusFire", ModDslTable.SystemVerbs);
        Assert.Contains("BuildPlan", ModDslTable.SystemVerbs);
        Assert.DoesNotContain("NukeEverything", ModDslTable.SystemVerbs);

        Assert.Empty(Validate(ModDslFamily.SystemCommand, """{"verb":"FocusFire"}"""));
        Assert.Empty(Validate(ModDslFamily.SystemCommand, """{"verb":"SetAlarm","args":{"after_seconds":60,"reason":"test"}}"""));
    }

    // ---- boundary -------------------------------------------------------

    [Theory] // [F-094] 边界值合法：percent 0 与 100、tick 0
    [InlineData("""{"op":"slow_percent","percent":0,"ticks":0}""")]
    [InlineData("""{"op":"slow_percent","percent":100,"ticks":0}""")]
    [InlineData("""{"op":"root_ticks","ticks":0}""")]
    public void EffectPrimitive_BoundaryValues_Pass(string entryJson)
    {
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, entryJson));
    }

    [Fact] // [F-094] 注解键（'_' 前缀）在封闭形状内合法（注解惯例）
    public void Entry_AnnotationKeys_AreLegal()
    {
        Assert.Empty(Validate(ModDslFamily.EffectPrimitive, """{"op":"heal_percent","percent":5,"_source":"SP-5"}"""));
    }

    // ---- illegal --------------------------------------------------------

    [Fact] // [F-094] 未知效果原语 op：拒绝且带 JSON 指针+1-based 行号（行级报错样例）
    public void EffectPrimitive_UnknownOp_RejectedWithPointerAndLine()
    {
        IReadOnlyList<ModSchemaViolation> violations = Validate(
            ModDslFamily.EffectPrimitive, """{"op":"instant_win","percent":1}""");

        ModSchemaViolation violation = Assert.Single(violations);
        Assert.Equal("/items/0/on_hit/0", violation.Pointer);
        Assert.Equal(5, violation.Line); // Wrap() 把 entry 放在第 5 行
        Assert.Equal(ModErrorKind.Dsl, violation.Kind);
        Assert.Contains("unknown op 'instant_win'", violation.Message);
    }

    [Fact] // [F-094] 未知世界操作 op：拒绝+行号
    public void WorldOperation_UnknownOp_Rejected()
    {
        IReadOnlyList<ModSchemaViolation> violations = Validate(
            ModDslFamily.WorldOperation, """{"op":"delete_save","zone":"*"}""");

        ModSchemaViolation violation = Assert.Single(violations);
        Assert.Equal(ModErrorKind.Dsl, violation.Kind);
        Assert.Contains("unknown op 'delete_save'", violation.Message);
        Assert.Equal(5, violation.Line);
    }

    [Fact] // [F-158] 未知系统命令 verb：拒绝（19 动词清单封闭）+行号
    public void SystemCommand_UnknownVerb_Rejected()
    {
        IReadOnlyList<ModSchemaViolation> violations = Validate(
            ModDslFamily.SystemCommand, """{"verb":"SpawnBoss"}""");

        ModSchemaViolation violation = Assert.Single(violations);
        Assert.Equal(ModErrorKind.Dsl, violation.Kind);
        Assert.Contains("unknown verb 'SpawnBoss'", violation.Message);
        Assert.Equal(5, violation.Line);
    }

    [Theory] // [F-094] 非法参数形态：缺参/越界/类型错/枚举外
    [InlineData("""{"op":"slow_percent","percent":150,"ticks":30}""", "percent")]
    [InlineData("""{"op":"slow_percent","ticks":30}""", "requires arg 'percent'")]
    [InlineData("""{"op":"burn_dps","dps":"hot","ticks":30}""", "'dps'")]
    [InlineData("""{"op":"root_ticks","ticks":2.5}""", "integer")]
    [InlineData("""{"op":"set_lane_stance","lane":"n","stance":"charge"}""", "stance")]
    public void Entries_WithBadArgs_RejectedWithDetail(string entryJson, string expectedFragment)
    {
        ModDslFamily family = entryJson.Contains("\"verb\"")
            ? ModDslFamily.SystemCommand
            : (entryJson.Contains("set_lane_stance") || entryJson.Contains("grant_resource")
                ? ModDslFamily.WorldOperation
                : ModDslFamily.EffectPrimitive);

        IReadOnlyList<ModSchemaViolation> violations = Validate(family, entryJson);

        Assert.Contains(violations, v => v.Message.Contains(expectedFragment) && v.Kind == ModErrorKind.Dsl);
    }

    [Fact] // [F-094] 封闭形状：entry 出现未声明键 → 拒绝
    public void Entry_UndeclaredKey_Rejected()
    {
        IReadOnlyList<ModSchemaViolation> violations = Validate(
            ModDslFamily.EffectPrimitive, """{"op":"heal_percent","percent":5,"bonus_op":"x"}""");

        Assert.Contains(violations, v => v.Pointer.EndsWith("/bonus_op") && v.Message.Contains("undeclared key 'bonus_op'"));
    }

    [Fact] // [F-094] entry 非对象 / 缺 op 字段：拒绝
    public void Entry_NotAnObject_OrMissingOp_Rejected()
    {
        Assert.NotEmpty(Validate(ModDslFamily.EffectPrimitive, "\"slow_percent\""));
        Assert.NotEmpty(Validate(ModDslFamily.EffectPrimitive, """{"percent":10}"""));
    }
}
