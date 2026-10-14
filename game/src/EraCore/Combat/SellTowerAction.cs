using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;

namespace EraCore.Combat;

/// <summary>
/// K-3 SellTower handler (C-09 full, T-065; schema table:
/// <c>target_id</c> + <c>confirm_sell</c> danger bit, unlock era E2, ask
/// tier — the danger bit is MANDATORY and never auto-approvable, v3.2
/// §11.4/F.4). A payload WITHOUT <c>confirm_sell:true</c> never reaches this
/// handler: the closed-schema gate refuses it with
/// <see cref="ActionResultStatus.ErrSchemaDenied"/> (F-014 acceptance:
/// confirm_sell 缺省拒绝). With the bit set, the sale removes the tower,
/// deposits the 30% refund (v3.2 §11.4 拆除返还 30%) and leaves a
/// <see cref="TowerRuin"/> carrying the Checkpoint FailureTiers repair-bill
/// semantics.
/// <para>
/// Same placement rationale as <see cref="UpgradeTowerAction"/>: Combat
/// domain, contract-level results, defensive parsing — Agent → Combat stays
/// one-way and the door never throws.
/// </para>
/// </summary>
public sealed class SellTowerAction
{
    private readonly TowerEstateOps _estate;

    /// <param name="estate">Combat tower estate the sale lands in.</param>
    public SellTowerAction(TowerEstateOps estate)
    {
        ArgumentNullException.ThrowIfNull(estate);
        _estate = estate;
    }

    /// <summary>
    /// Validates and executes one SellTower payload. Status mapping: missing
    /// danger bit → <see cref="ActionResultStatus.ErrSchemaDenied"/>
    /// (defensive re-check — the schema gate already refuses these);
    /// unknown target → <see cref="ActionResultStatus.ErrTargetInvalid"/>;
    /// success → Ok with the <see cref="SellTowerReceipt"/> payload.
    /// </summary>
    /// <param name="actionJson">The schema-validated action payload (raw JSON).</param>
    public (ActionResult Result, SellTowerReceipt? Receipt) Invoke(string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        try
        {
            if (!TryParseArguments(actionJson, out long targetId, out bool confirmSell))
            {
                return (new ActionResult(
                    ActionResultStatus.ErrSchemaDenied,
                    "sell-tower-arguments:target_id/confirm_sell unreadable after schema gate"), null);
            }

            if (!confirmSell)
            {
                // Defense in depth: the schema gate's MustBeTrue rule already
                // denies a missing/false bit; this door repeats the refusal so
                // a direct invocation can never sell without the danger bit.
                return (new ActionResult(
                    ActionResultStatus.ErrSchemaDenied,
                    "confirm_sell-must-be-true:SellTower carries an explicit danger bit and is never"
                    + " auto-approvable (v3.2 §11.4/F.4) — the sale is refused without it"), null);
            }

            SellTowerReceipt? receipt = _estate.ApplySell(new EntityId(targetId), out string? refusal);
            if (receipt is null)
            {
                return (refusal!.StartsWith("target-unknown:", StringComparison.Ordinal)
                    ? (new ActionResult(
                        ActionResultStatus.ErrTargetInvalid,
                        $"target-invalid:{refusal} — no placed tower carries this id; nothing was sold"), null)
                    : (new ActionResult(ActionResultStatus.Err, refusal), null));
            }

            return (new ActionResult(ActionResultStatus.Ok), receipt);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return (new ActionResult(ActionResultStatus.Err, $"estate-refused:{ex.Message}"), null);
        }
    }

    private static bool TryParseArguments(string actionJson, out long targetId, out bool confirmSell)
    {
        targetId = 0;
        confirmSell = false;
        using JsonDocument document = JsonDocument.Parse(actionJson);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("target_id", out JsonElement targetElement)
            || targetElement.ValueKind != JsonValueKind.Number
            || !targetElement.TryGetInt64(out targetId)
            || targetId < 1)
        {
            return false;
        }

        // A missing bit reads false — the caller then hits the danger-bit
        // refusal above (schema-identical outcome).
        confirmSell = root.TryGetProperty("confirm_sell", out JsonElement confirmElement)
            && confirmElement.ValueKind == JsonValueKind.True;
        return true;
    }
}
