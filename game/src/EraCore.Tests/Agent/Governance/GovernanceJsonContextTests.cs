using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-064] Governance JSON context (T-050): the only serialization
// entry point of the C-14 payloads (D-05 AOT red line) — ledger audit
// events and approval session state round-trip through the
// source-generated metadata.

/// <summary>[C-14][F-064] Source-gen JSON round-trips for governance payloads.</summary>
public class GovernanceJsonContextTests
{
    [Fact] // legal — a spend event round-trips losslessly, verb as enum payload
    public void Ledger_event_round_trips()
    {
        var evt = new SystemPointLedgerEvent(
            Type: "spend_ok", Era: 4, Wave: 7, Verb: SystemActionKind.SellTower,
            Amount: 2, BalanceAfter: 5, WaveSpentAfter: 4, Reason: "boss_surge_priced");

        string json = JsonSerializer.Serialize(evt, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        var back = JsonSerializer.Deserialize(json, GovernanceJsonContext.Default.SystemPointLedgerEvent);

        Assert.Equal(evt, back);
    }

    [Fact] // legal/boundary — a refill event carries a null verb and null reason without loss
    public void Ledger_event_round_trips_with_null_optionals()
    {
        var evt = new SystemPointLedgerEvent(
            Type: "wave_refill", Era: 2, Wave: 1, Verb: null,
            Amount: 5, BalanceAfter: 5, WaveSpentAfter: 0, Reason: null);

        string json = JsonSerializer.Serialize(evt, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        var back = JsonSerializer.Deserialize(json, GovernanceJsonContext.Default.SystemPointLedgerEvent);

        Assert.Equal(evt, back);
        Assert.Null(back!.Verb);
    }

    [Fact] // legal — the audit line is one compact JSON line (JSONL: no embedded newline)
    public void Serialized_event_is_one_line()
    {
        var evt = new SystemPointLedgerEvent(
            "spend_denied", 6, 30, SystemActionKind.TriggerScroll, 2, 1, 5, "wave_cap_exceeded:5+2>5");
        string json = JsonSerializer.Serialize(evt, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        Assert.DoesNotContain('\n', json);
    }

    [Fact] // legal — session state round-trips losslessly
    public void Session_state_round_trips()
    {
        var state = new ApprovalSessionState(3, new[] { "BuildPlan", "FocusFire" });

        string json = state.Save();
        var back = JsonSerializer.Deserialize(json, GovernanceJsonContext.Default.ApprovalSessionState);

        Assert.Equal(3, back!.EraIndex);
        Assert.Equal(new[] { "BuildPlan", "FocusFire" }, back.AutoApprovedVerbs);
    }

    [Fact] // illegal — a damaged session payload fails fast at deserialize (callers wrap into save corruption)
    public void Damaged_session_payload_fails_to_deserialize()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("{ not json", GovernanceJsonContext.Default.ApprovalSessionState));
    }
}
