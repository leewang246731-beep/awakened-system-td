using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.Sim;

/// <summary>
/// Pay-then-place with narrow-window rollback (T-053 DEBT ③, 08-task-plan
/// T-053 行尾挂账): the four E1 build verbs withdraw the build cost BEFORE
/// the subsystem placement call — a placement that then throws would leak
/// the withdrawal. This helper owns that window: withdraw, run the
/// placement, and on a subsystem refusal re-deposit the exact amount so
/// the stock ends where it started (库存复原), returning a machine-readable
/// rejection code that names the compensation (事件记录 — the command
/// result stream <see cref="Simulation.CommandResults"/> carries it).
/// <para>
/// Withdrawal semantics are exactly the retired inline
/// <c>TryPayCost</c>: cost ≤ 0 is free; an amount above the balance is
/// refused without touching the stock; otherwise the full cost is
/// withdrawn. The rollback deposit is lossless by construction — the
/// withdrawn amount came from below the pool capacity, so re-depositing
/// it cannot hit the cap (ResourceStock 入池只计上限内).
/// </para>
/// Deterministic (CS-1.4): no clock, no RNG.
/// </summary>
public static class BuildCostCompensation
{
    /// <summary>Rejection prefix when the placement threw and the cost was refunded.</summary>
    public const string RefundedRejectionPrefix = "place-failed-cost-refunded";

    /// <summary>
    /// Withdraws <paramref name="cost"/>, runs <paramref name="place"/>, and
    /// compensates on failure.
    /// </summary>
    /// <param name="stock">Shared era pool the cost is paid from.</param>
    /// <param name="currency">Cost currency (level build policy).</param>
    /// <param name="cost">Cost in resource points; ≤ 0 places for free.</param>
    /// <param name="place">The placement action — every subsystem mutation of the verb.</param>
    /// <returns>null on success; <c>insufficient-resources</c> when the pool
    /// cannot cover the cost (nothing withdrawn, nothing placed); otherwise
    /// <c>place-failed-cost-refunded:&lt;exception&gt;</c> — the placement
    /// threw, the cost was re-deposited in full.</returns>
    /// <exception cref="ArgumentNullException">stock, currency or place is null.</exception>
    public static string? TryPayThenPlace(
        ResourceStock stock,
        ResourceKey currency,
        double cost,
        Action place)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(place);

        if (cost > 0)
        {
            if (stock.AmountOf(currency) < cost)
            {
                return "insufficient-resources";
            }

            if (stock.TryWithdraw(currency, cost) < cost)
            {
                return "insufficient-resources";
            }
        }

        try
        {
            place();
            return null;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or KeyNotFoundException
                or ArgumentOutOfRangeException)
        {
            // Narrow-window compensation (DEBT ③): the subsystem refused
            // mid-verb — refund the exact withdrawal so the rejected
            // command leaves the stock untouched, and name the refund in
            // the rejection code the command-result stream records.
            if (cost > 0)
            {
                _ = stock.Deposit(currency, cost);
            }

            return $"{RefundedRejectionPrefix}:{ex.GetType().Name}";
        }
    }
}
