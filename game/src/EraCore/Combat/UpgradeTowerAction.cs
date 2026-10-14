using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;

namespace EraCore.Combat;

/// <summary>
/// K-3 UpgradeTower handler (C-09 full, T-065; schema table:
/// <c>target_id</c> + <c>to_tier 1..4</c>, unlock era E2, ask tier): quotes
/// and applies one in-place tower upgrade through the combat
/// <see cref="TowerEstateOps"/> — same-cell replacement under the same
/// entity id, sticker-price differential charged, 50% trade-in refund of the
/// old row deposited. Every failure is a typed <see cref="ActionResult"/>
/// with a machine-readable reason; a failed invocation mutates nothing.
/// <para>
/// Lives in Combat (the domain it acts on) and returns contract-level
/// results, so the agent-side registry dispatches it without Combat learning
/// the agent namespace — the Agent → Combat direction stays one-way.
/// Argument parsing is defensive (total, no-throw): the registry's schema
/// gate already guarantees the field shapes, and this door never trusts its
/// caller blindly.
/// </para>
/// </summary>
public sealed class UpgradeTowerAction
{
    private readonly TowerEstateOps _estate;

    /// <param name="estate">Combat tower estate the upgrade lands in.</param>
    public UpgradeTowerAction(TowerEstateOps estate)
    {
        ArgumentNullException.ThrowIfNull(estate);
        _estate = estate;
    }

    /// <summary>
    /// Validates and executes one UpgradeTower payload. Status mapping:
    /// unknown target → <see cref="ActionResultStatus.ErrTargetInvalid"/>;
    /// every other refusal (line end, tier jump, growth-law breach,
    /// insufficient funds) → <see cref="ActionResultStatus.Err"/> with the
    /// estate's refusal code; success → Ok with the
    /// <see cref="UpgradeTowerReceipt"/> payload.
    /// </summary>
    /// <param name="actionJson">The schema-validated action payload (raw JSON).</param>
    public (ActionResult Result, UpgradeTowerReceipt? Receipt) Invoke(string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        try
        {
            if (!TryParseArguments(actionJson, out long targetId, out int toTier))
            {
                return (new ActionResult(
                    ActionResultStatus.ErrSchemaDenied,
                    "upgrade-tower-arguments:target_id/to_tier unreadable after schema gate"), null);
            }

            UpgradeTowerReceipt? receipt = _estate.ApplyUpgrade(new EntityId(targetId), toTier, out string? refusal);
            if (receipt is null)
            {
                return (Refuse(refusal!), null);
            }

            return (new ActionResult(ActionResultStatus.Ok), receipt);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            // Total door: the estate refuses malformed state by contract and
            // leaves its own state untouched — surface it as a typed failure
            // instead of letting an exception escape the registry.
            return (new ActionResult(ActionResultStatus.Err, $"estate-refused:{ex.Message}"), null);
        }
    }

    private static ActionResult Refuse(string refusal) =>
        refusal.StartsWith("target-unknown:", StringComparison.Ordinal)
            ? new ActionResult(
                ActionResultStatus.ErrTargetInvalid,
                $"target-invalid:{refusal} — no placed tower carries this id; nothing was upgraded")
            : new ActionResult(ActionResultStatus.Err, refusal);

    private static bool TryParseArguments(string actionJson, out long targetId, out int toTier)
    {
        targetId = 0;
        toTier = 0;
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("target_id", out JsonElement targetElement)
            && targetElement.ValueKind == JsonValueKind.Number
            && targetElement.TryGetInt64(out targetId)
            && targetId >= 1
            && root.TryGetProperty("to_tier", out JsonElement tierElement)
            && tierElement.ValueKind == JsonValueKind.Number
            && tierElement.TryGetInt32(out toTier);
    }
}
