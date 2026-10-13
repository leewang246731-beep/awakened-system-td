using System.Text.Json;
using EraCore.Contracts.Actions;

namespace EraCore.Agent.Governance;

/// <summary>
/// Level-scoped approval session state (T-050, C-14 / feature F-063):
/// the "本关卡内自动允许" checkbox memory (关卡级，随关卡存档, v3.2 §11.4).
/// Serializable only through the source-generated
/// <see cref="GovernanceJsonContext"/> (D-05). The gate validates the
/// payload on load: danger verbs (SellTower) can never ride in.
/// </summary>
/// <param name="EraIndex">Era index the whitelist belongs to.</param>
/// <param name="AutoApprovedVerbs">Ask-tier verbs the player whitelisted this level, by enum name.</param>
public sealed record ApprovalSessionState(
    int EraIndex,
    IReadOnlyList<string> AutoApprovedVerbs)
{
    /// <summary>Serializes the session via the generated metadata (one line, no indentation).</summary>
    public string Save()
    {
        return JsonSerializer.Serialize(this, GovernanceJsonContext.Default.ApprovalSessionState);
    }
}
