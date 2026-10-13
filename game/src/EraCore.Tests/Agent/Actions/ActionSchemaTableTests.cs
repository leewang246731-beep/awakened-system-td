using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062] C-03 schema 表完整性：19 动作各有一行约束表且封闭（清单外字段拒绝）
public class ActionSchemaTableTests
{
    [Theory] // legal：19 动作的合法样例全部过表（每个动词都有可达的合法形态）
    [InlineData("LookAround")]
    [InlineData("InspectTile")]
    [InlineData("QueryWorld")]
    [InlineData("BuildPlan")]
    [InlineData("UpgradeTower")]
    [InlineData("SellTower")]
    [InlineData("SetRally")]
    [InlineData("RerouteLogistics")]
    [InlineData("SetStockpile")]
    [InlineData("FocusFire")]
    [InlineData("SetTowerMode")]
    [InlineData("TriggerScroll")]
    [InlineData("HeroOrder")]
    [InlineData("AskPlayer")]
    [InlineData("ReportToPlayer")]
    [InlineData("TodoBoard")]
    [InlineData("SpawnScout")]
    [InlineData("SetAlarm")]
    [InlineData("StopTask")]
    public void Every_verb_has_a_reachable_valid_payload(string verb)
    {
        var validator = new JsonSchemaValidator();
        SchemaValidationResult result = validator.Validate(ActionJsonSamples.All()[verb]());
        Assert.True(result.IsValid, $"{verb} 样例应合法，错误：{Describe(result)}");
    }

    [Theory] // illegal：清单外字段在所有 19 动作上都被拒绝（封闭 schema）
    [InlineData("LookAround")]
    [InlineData("InspectTile")]
    [InlineData("QueryWorld")]
    [InlineData("BuildPlan")]
    [InlineData("UpgradeTower")]
    [InlineData("SellTower")]
    [InlineData("SetRally")]
    [InlineData("RerouteLogistics")]
    [InlineData("SetStockpile")]
    [InlineData("FocusFire")]
    [InlineData("SetTowerMode")]
    [InlineData("TriggerScroll")]
    [InlineData("HeroOrder")]
    [InlineData("AskPlayer")]
    [InlineData("ReportToPlayer")]
    [InlineData("TodoBoard")]
    [InlineData("SpawnScout")]
    [InlineData("SetAlarm")]
    [InlineData("StopTask")]
    public void Undeclared_fields_are_rejected_on_every_verb(string verb)
    {
        var validator = new JsonSchemaValidator();
        string tampered = ActionJsonSamples.WithSurpriseField(verb);
        SchemaValidationResult result = validator.Validate(tampered);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Pointer == "/surprise" && e.Message.Contains("outside the closed schema"));
    }

    [Fact]
    public void LookAround_is_an_empty_interface()
    {
        // boundary：空接口动词——零字段合法，任意字段即非法（v2.1 §11.3 原文）
        var validator = new JsonSchemaValidator();
        Assert.True(validator.Validate(ActionJsonSamples.LookAround()).IsValid);
        SchemaValidationResult result = validator.Validate("{\"action\":\"LookAround\",\"cell\":{\"x\":1,\"y\":1}}");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Pointer == "/cell");
    }

    [Fact]
    public void Discriminator_must_be_a_known_verb_of_the_closed_list()
    {
        // illegal：清单外动词（大小写漂移同判）
        var validator = new JsonSchemaValidator();
        SchemaValidationResult result = validator.Validate("{\"action\":\"lookaround\"}");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Pointer == "/action" && e.Message.Contains("outside the closed 19-action list"));
    }

    private static string Describe(SchemaValidationResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Pointer}@L{e.Line}: {e.Message}"));
}
