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
}
