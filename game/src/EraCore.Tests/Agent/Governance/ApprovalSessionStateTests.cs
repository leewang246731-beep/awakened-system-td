using System.Text.Json;
using EraCore.Agent.Governance;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-063] Approval session state (T-050): the level-scoped
// "本关卡内自动允许" memory is serializable through the frozen context
// (关卡级，可序列化，随关卡存档) and its JSON shape stays stable for the
// save pipeline.

/// <summary>[C-14][F-063] Session-state JSON contract.</summary>
public class ApprovalSessionStateTests
{
    [Fact] // legal — Save produces the stable two-field JSON line (era + verb names)
    public void Save_produces_the_stable_shape()
    {
        var state = new ApprovalSessionState(2, new[] { "BuildPlan" });
        string json = state.Save();

        Assert.Contains("\"EraIndex\":2", json);
        Assert.Contains("\"AutoApprovedVerbs\":[\"BuildPlan\"]", json);
    }

    [Fact] // legal — empty whitelist serializes as an empty list, not null (save stays schema-stable)
    public void Empty_whitelist_serializes_as_empty_list()
    {
        var state = new ApprovalSessionState(1, Array.Empty<string>());
        string json = state.Save();
        Assert.Contains("\"AutoApprovedVerbs\":[]", json);
    }

    [Fact] // legal — what Save writes, the context reads back verbatim (field-by-field: the deserialized verb list is a List, not an array)
    public void Round_trip_preserves_state()
    {
        var state = new ApprovalSessionState(5, new[] { "SetStockpile", "TriggerScroll" });
        var back = JsonSerializer.Deserialize(state.Save(), GovernanceJsonContext.Default.ApprovalSessionState);
        Assert.Equal(5, back!.EraIndex);
        Assert.Equal(state.AutoApprovedVerbs, back.AutoApprovedVerbs);
    }

    [Fact] // boundary — the maximum lawful payload (every auto-approvable ask verb) round-trips
    public void Full_whitelist_round_trips()
    {
        // every Ask-tier verb except the never-auto SellTower (9 of the 10 ask verbs)
        var names = new[] { "BuildPlan", "UpgradeTower", "SetRally", "RerouteLogistics", "SetStockpile", "FocusFire", "SetTowerMode", "TriggerScroll", "HeroOrder" };
        var state = new ApprovalSessionState(6, names);

        var back = JsonSerializer.Deserialize(state.Save(), GovernanceJsonContext.Default.ApprovalSessionState);
        Assert.Equal(names, back!.AutoApprovedVerbs);
        Assert.Equal(6, back.EraIndex);
    }

    // ---- illegal inputs（REV-20261014-wave2-round1 #4 弱测加固）------------
    // Fail 语义按实测行为断言并注明：null 字符串→ArgumentNullException fail-fast
    // （STJ string 重载非空注解，实测行为）；JSON "null" 字面量→null 引用（无会话
    // 态，调用方按空白名单处理）；结构损坏/类型错位→JsonException（绝不产出半态）。

    [Fact] // illegal — a null payload string is a contract violation: the STJ string
           // overload is non-nullable annotated and fails fast with ArgumentNullException
           // (measured behavior — NOT a null return; the "null" JSON literal is the
           // absent-state form, see the next test)
    public void Null_payload_throws_ArgumentNullException()
    {
        string? absentPayload = null;

        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(
            absentPayload!, GovernanceJsonContext.Default.ApprovalSessionState));
    }

    [Fact] // illegal — the JSON literal "null" yields a null reference, not an exception
           // (absent session = no saved state; callers treat null as an empty whitelist)
    public void Json_null_literal_deserializes_to_null_reference()
    {
        Assert.Null(JsonSerializer.Deserialize(
            "null", GovernanceJsonContext.Default.ApprovalSessionState));
    }

    [Fact] // illegal — a truncated payload fails fast with JsonException (no half-state;
           // complements GovernanceJsonContextTests' malformed-open payload case)
    public void Truncated_payload_throws_JsonException()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"EraIndex\":2,\"AutoApprovedVerbs\":[\"Build",
            GovernanceJsonContext.Default.ApprovalSessionState));
    }

    [Fact] // illegal — a wrong-typed field fails fast with JsonException (no silent coercion)
    public void Wrong_typed_field_throws_JsonException()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"EraIndex\":\"two\",\"AutoApprovedVerbs\":[]}",
            GovernanceJsonContext.Default.ApprovalSessionState));
    }

    [Fact] // illegal — an empty object yields the default state, not an exception (STJ
           // constructor binding: missing properties bind default(T) — era 0, null list;
           // the gate revalidates on load, so a null whitelist cannot ride in as data)
    public void Empty_object_yields_default_state()
    {
        var back = JsonSerializer.Deserialize(
            "{}", GovernanceJsonContext.Default.ApprovalSessionState);

        Assert.NotNull(back);
        Assert.Equal(0, back!.EraIndex);
        Assert.Null(back.AutoApprovedVerbs);
    }
}
