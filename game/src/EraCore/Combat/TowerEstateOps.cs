using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.World;

namespace EraCore.Combat;

/// <summary>
/// Quoted terms of one in-place tower upgrade (T-065, C-09): the successor
/// row, the sticker-price differential and the old-tower trade-in refund.
/// </summary>
/// <param name="TowerId">Stable entity id of the tower being upgraded (unchanged by the upgrade).</param>
/// <param name="Cell">Cell the tower stands on (the replacement lands on the same cell).</param>
/// <param name="Current">Registry row currently placed.</param>
/// <param name="Target">Registry successor row (<c>upgrades_to</c> wiring).</param>
/// <param name="DifferentialCost">
/// Sticker-price differential <c>target.CostTotal − current.CostTotal</c>
/// (成本差价) — the amount withdrawn from the pool when applying.
/// </param>
/// <param name="OldTowerRefund">
/// Trade-in refund of the replaced tower: <c>OldTowerRefundFraction ×
/// current.CostTotal</c> (旧塔 50% 返还, [NEW] T-065 — a better rate than
/// the 30% sell refund so upgrading beats sell-and-rebuild), deposited back
/// on a successful apply.
/// </param>
public sealed record UpgradeQuote(
    EntityId TowerId,
    CellCoord Cell,
    TowerSpec Current,
    TowerSpec Target,
    double DifferentialCost,
    double OldTowerRefund);

/// <summary>Settled receipt of one applied upgrade (K-3 UpgradeTower payload).</summary>
/// <param name="Quote">The terms this upgrade settled under.</param>
/// <param name="Charged">Amount actually withdrawn (equals the differential on success).</param>
/// <param name="Refunded">Amount actually deposited back (equals the trade-in refund).</param>
/// <param name="NetCost">Charged − Refunded; the pool's net movement for this upgrade.</param>
public sealed record UpgradeTowerReceipt(UpgradeQuote Quote, double Charged, double Refunded, double NetCost);

/// <summary>Quoted terms of one tower sale (T-065).</summary>
/// <param name="TowerId">Stable entity id of the tower being sold.</param>
/// <param name="Cell">Cell the tower stood on (freed by the sale).</param>
/// <param name="Current">Registry row placed at sale time.</param>
/// <param name="Refund">Sale refund: <c>SellRefundFraction × current.CostTotal</c> (出售返还 30%，[NEW-T-065] 无文档锚点，登记于 REV-wave3-round1).</param>
public sealed record SellQuote(EntityId TowerId, CellCoord Cell, TowerSpec Current, double Refund);

/// <summary>
/// One demolition ruin (T-065 薄版): selling a tower leaves a ruin record —
/// the repair-bill ledger entry carrying the Checkpoint <see cref="EraCore.
/// Checkpoint.FailureTiers"/> accounting semantics (残骸修复成本 = 原造价 ×
/// 修复比例; the fraction itself is single-sourced from FailureTiers and
/// wired in by the composition root, keeping Combat independent of the
/// Checkpoint namespace). Full C-12 wiring (T-067) attaches these records
/// to the grid/save domains.
/// </summary>
/// <param name="RuinId">Fresh entity id of the ruin record.</param>
/// <param name="Cell">Cell the sold tower occupied.</param>
/// <param name="ContentId">Content id of the demolished tower (cost/history key).</param>
/// <param name="Tier">Tier of the demolished tower.</param>
/// <param name="OriginalCostPoints">Original build cost of the demolished row.</param>
/// <param name="RepairCostPoints">Repair bill: original × ruin-repair fraction (FailureTiers semantics).</param>
/// <param name="SellRefundPoints">Refund actually paid out by the sale.</param>
public sealed record TowerRuin(
    EntityId RuinId,
    CellCoord Cell,
    string ContentId,
    int Tier,
    double OriginalCostPoints,
    double RepairCostPoints,
    double SellRefundPoints);

/// <summary>Settled receipt of one applied sale (K-3 SellTower payload).</summary>
/// <param name="Quote">The terms this sale settled under.</param>
/// <param name="Refunded">Amount actually deposited (equals the refund on success).</param>
/// <param name="Ruin">The ruin record left behind on the freed cell.</param>
public sealed record SellTowerReceipt(SellQuote Quote, double Refunded, TowerRuin Ruin);

/// <summary>
/// Combat-domain tower estate executor (C-09 full, T-065): the single
/// mutator for in-place tower upgrades and sales over the combat-owned
/// <see cref="BuildingRegistry"/>, the tower catalog and the shared
/// <see cref="ResourceStock"/> pool. The K-3 action handlers
/// (<see cref="UpgradeTowerAction"/>/<see cref="SellTowerAction"/>) validate
/// and execute through THIS class; the C-12 orchestrator (T-067) wires the
/// live simulation's building registry here.
/// <para>
/// Upgrade economics (task card T-065): pay the sticker-price differential
/// (成本差价), receive a 50% trade-in refund of the old row ([NEW] — better
/// than the 30% sale rate, so upgrading beats sell-and-rebuild); the
/// replacement keeps the SAME entity id and cell (同格替换 — targeting and
/// supply identities stay stable). Every step is law-checked first
/// (<see cref="UpgradeLaw"/>): a successor row outside the frozen law is
/// refused, never patched.
/// </para>
/// <para>
/// All money movements round to whole resource points (AwayFromZero) —
/// registry costs are integers and the pool arithmetic stays replay-stable.
/// Every refusal is a machine-readable code returned via <c>out</c>; a
/// refused call mutates nothing (full pre-validation before any withdrawal).
/// </para>
/// </summary>
public sealed class TowerEstateOps
{
    /// <summary>
    /// Trade-in refund fraction of the replaced tower on upgrade
    /// (<c>旧塔 50% 返还</c>, [NEW] T-065 — no document anchor; deliberately
    /// above the 30% sale rate so the in-place path is strictly better than
    /// sell-and-rebuild).
    /// </summary>
    public const double UpgradeRefundFraction = 0.50;

    /// <summary>
    /// Sale refund fraction (出售返还 30%，[NEW-T-065] 无文档锚点，登记于
    /// REV-wave3-round1 — sell refunds less
    /// than upgrading trades in; selling is the full teardown).
    /// </summary>
    public const double SellRefundFraction = 0.30;

    private readonly BuildingRegistry _buildings;
    private readonly IReadOnlyList<TowerSpec> _catalog;
    private readonly ResourceStock _stock;
    private readonly ResourceKey _currency;
    private readonly double _ruinRepairCostFraction;
    private readonly List<TowerRuin> _ruins = new();
    private long _nextId;

    /// <param name="buildings">Combat building registry towers live in (the sim's instance at composition time).</param>
    /// <param name="catalog">Tower rows (towers.json items) — successor and spec lookups.</param>
    /// <param name="stock">Shared resource pool the differentials/refunds move through.</param>
    /// <param name="currency">Cost currency key (era.json cost currency).</param>
    /// <param name="ruinRepairCostFraction">
    /// Repair-bill fraction of the original build cost carried by every
    /// <see cref="TowerRuin"/> — pass
    /// <c>EraCore.Checkpoint.FailureTiers.RepairCostFraction</c> (the 30%
    /// authority, v3.2 §3) at composition time; Combat takes it as a
    /// parameter so the Checkpoint namespace stays un-referenced from
    /// Combat (Checkpoint → Combat is the existing dependency direction).
    /// </param>
    /// <param name="firstFreeId">First entity id this estate hands out (ids below it belong to the host).</param>
    public TowerEstateOps(
        BuildingRegistry buildings,
        IReadOnlyList<TowerSpec> catalog,
        ResourceStock stock,
        ResourceKey currency,
        double ruinRepairCostFraction,
        long firstFreeId = 1)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(currency);
        if (ruinRepairCostFraction < 0)
        {
            throw new ArgumentException(
                $"Ruin repair fraction must be non-negative (got {ruinRepairCostFraction}).",
                nameof(ruinRepairCostFraction));
        }

        _buildings = buildings;
        _catalog = catalog;
        _stock = stock;
        _currency = currency;
        _ruinRepairCostFraction = ruinRepairCostFraction;
        _nextId = firstFreeId;
    }

    /// <summary>Demolition ruins in sell order (薄版 ledger; grid attach lands with C-12/T-067).</summary>
    public IReadOnlyList<TowerRuin> Ruins => _ruins;

    /// <summary>
    /// Places one tower row at full sticker price (thin host entry: build
    /// placement itself is the BuildPlan lane's job — this only stocks the
    /// estate for hosts/tests so upgrades/sales have something to act on).
    /// Returns the new entity id.
    /// </summary>
    public EntityId Place(TowerSpec spec, CellCoord cell)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(cell);
        if (!TrySpec(spec.Id, out _))
        {
            throw new InvalidOperationException($"spec-unknown:{spec.Id} is not in this estate's catalog.");
        }

        if (_buildings.TryGetAt(cell, out _))
        {
            throw new InvalidOperationException($"cell-occupied:({cell.X},{cell.Y})");
        }

        if (!_buildings.Passable(cell))
        {
            throw new InvalidOperationException($"cell-not-buildable:({cell.X},{cell.Y})");
        }

        double taken = _stock.TryWithdraw(_currency, spec.CostTotal);
        if (taken < spec.CostTotal)
        {
            _ = _stock.Deposit(_currency, taken); // roll the partial withdrawal back
            throw new InvalidOperationException(
                $"insufficient-funds:need-{spec.CostTotal}-have-{_stock.AmountOf(_currency) + taken}");
        }

        var id = new EntityId(_nextId++);
        _ = _buildings.Place(id, spec, cell, tier: spec.Tier);
        return id;
    }

    /// <summary>
    /// Quotes one in-place upgrade without mutating anything. Refusal codes:
    /// <c>target-unknown:{id}</c> / <c>spec-unknown:{contentId}</c> /
    /// <c>no-successor:{contentId}</c> / <c>to-tier-must-be-next</c> /
    /// <c>successor-unknown</c> / <c>growth-law:…</c> /
    /// <c>insufficient-funds:…</c>.
    /// </summary>
    public bool TryQuoteUpgrade(EntityId towerId, int toTier, out UpgradeQuote? quote, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        quote = null;
        if (!TryResolveTower(towerId, out TowerSpec current, out CellCoord cell, out refusal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(current.UpgradesTo))
        {
            refusal = $"no-successor:{current.Id} sits at the end of its line (tier {current.Tier}/{current.MaxTier})";
            return false;
        }

        if (toTier != current.Tier + 1)
        {
            refusal = $"to-tier-must-be-next:{current.Id} is at tier {current.Tier}; requested {toTier}"
                + " (upgrades advance exactly one tier per action, law steps are +1)";
            return false;
        }

        if (!TrySpec(current.UpgradesTo, out TowerSpec target))
        {
            refusal = $"successor-unknown:{current.UpgradesTo} wired by {current.Id} is not in the catalog";
            return false;
        }

        var lawViolations = new List<string>(UpgradeLaw.ValidateGrowthDeclaration(current));
        lawViolations.AddRange(UpgradeLaw.ValidateStep(current, target));
        if (lawViolations.Count > 0)
        {
            refusal = "growth-law:" + string.Join(";", lawViolations);
            return false;
        }

        double differential = target.CostTotal - current.CostTotal;
        double refund = Math.Round(current.CostTotal * UpgradeRefundFraction, MidpointRounding.AwayFromZero);
        double available = _stock.AmountOf(_currency);
        if (available < differential)
        {
            refusal = $"insufficient-funds:need-{differential}-have-{available}";
            return false;
        }

        quote = new UpgradeQuote(towerId, cell, current, target, differential, refund);
        refusal = null;
        return true;
    }

    /// <summary>
    /// Applies one in-place upgrade: full pre-validation
    /// (<see cref="TryQuoteUpgrade"/>), then withdraw the differential,
    /// replace the building on the same cell under the same entity id, and
    /// deposit the trade-in refund. A refused call mutates nothing.
    /// </summary>
    public UpgradeTowerReceipt? ApplyUpgrade(EntityId towerId, int toTier, out string? refusal)
    {
        if (!TryQuoteUpgrade(towerId, toTier, out UpgradeQuote? quote, out refusal))
        {
            return null;
        }

        double charged = _stock.TryWithdraw(_currency, quote!.DifferentialCost);
        if (charged < quote.DifferentialCost)
        {
            _ = _stock.Deposit(_currency, charged); // roll back; pre-validation narrowing excluded this window
            refusal = $"insufficient-funds:need-{quote.DifferentialCost}-have-{charged}";
            return null;
        }

        // 同格替换: free our own cell, then place the successor under the SAME
        // id — targeting/supply identities survive the upgrade. Place cannot
        // reject here (the cell was ours, the id was ours, the tier is the
        // row's own).
        if (!_buildings.Remove(towerId))
        {
            _ = _stock.Deposit(_currency, charged);
            refusal = $"target-unknown:{towerId.Value} disappeared while upgrading";
            return null;
        }

        _ = _buildings.Place(towerId, quote.Target, quote.Cell, tier: quote.Target.Tier);
        double refunded = _stock.Deposit(_currency, quote.OldTowerRefund);
        return new UpgradeTowerReceipt(quote, charged, refunded, charged - refunded);
    }

    /// <summary>
    /// Quotes one sale without mutating anything. Refusal codes:
    /// <c>target-unknown:{id}</c> / <c>spec-unknown:{contentId}</c>.
    /// </summary>
    public bool TryQuoteSell(EntityId towerId, out SellQuote? quote, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        quote = null;
        if (!TryResolveTower(towerId, out TowerSpec current, out CellCoord cell, out refusal))
        {
            return false;
        }

        double refund = Math.Round(current.CostTotal * SellRefundFraction, MidpointRounding.AwayFromZero);
        quote = new SellQuote(towerId, cell, current, refund);
        refusal = null;
        return true;
    }

    /// <summary>
    /// Applies one sale: remove the tower, deposit the 30% refund, append the
    /// ruin record (repair bill per the wired FailureTiers fraction). A
    /// refused call mutates nothing.
    /// </summary>
    public SellTowerReceipt? ApplySell(EntityId towerId, out string? refusal)
    {
        if (!TryQuoteSell(towerId, out SellQuote? quote, out refusal))
        {
            return null;
        }

        if (!_buildings.Remove(towerId))
        {
            refusal = $"target-unknown:{towerId.Value} disappeared while selling";
            return null;
        }

        double refunded = _stock.Deposit(_currency, quote!.Refund);
        var ruin = new TowerRuin(
            new EntityId(_nextId++),
            quote.Cell,
            quote.Current.Id,
            quote.Current.Tier,
            quote.Current.CostTotal,
            Math.Round(quote.Current.CostTotal * _ruinRepairCostFraction, MidpointRounding.AwayFromZero),
            refunded);
        _ruins.Add(ruin);
        return new SellTowerReceipt(quote, refunded, ruin);
    }

    private bool TryResolveTower(EntityId towerId, out TowerSpec spec, out CellCoord cell, out string? refusal)
    {
        spec = null!;
        cell = null!;
        if (!_buildings.TryGetById(towerId, out IBuilding building))
        {
            refusal = $"target-unknown:{towerId.Value} is not a placed building";
            return false;
        }

        if (!TrySpec(building.ContentId, out spec))
        {
            refusal = $"spec-unknown:{building.ContentId} is not in this estate's catalog";
            return false;
        }

        cell = building.Cell;
        refusal = null;
        return true;
    }

    private bool TrySpec(string contentId, out TowerSpec spec)
    {
        foreach (TowerSpec candidate in _catalog)
        {
            if (string.Equals(candidate.Id, contentId, StringComparison.Ordinal))
            {
                spec = candidate;
                return true;
            }
        }

        spec = null!;
        return false;
    }
}
