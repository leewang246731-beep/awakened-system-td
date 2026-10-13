using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Agent.Governance;

/// <summary>
/// System.Text.Json source-generation context for the C-14 governance
/// payloads (T-050): the ledger's JSONL audit events and the level-scoped
/// approval session. AOT red line (07 D-05): serialization must go
/// through generated <see cref="JsonSerializerContext"/> metadata —
/// reflection-based (de)serialization is forbidden in EraCore, so this
/// context is the ONLY entry point for governance JSON. Enums serialize
/// as names so audit lines stay human-greppable ("Verb":"SellTower").
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SystemPointLedgerEvent))]
[JsonSerializable(typeof(ApprovalSessionState))]
public sealed partial class GovernanceJsonContext : JsonSerializerContext
{
}
