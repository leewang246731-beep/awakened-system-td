namespace EraCore.Legacy;

/// <summary>
/// One 传承商店 redemption outcome (T-074): the redeemed decorative
/// blueprint and the remaining balance.
/// </summary>
/// <param name="ItemId">Shop item id redeemed.</param>
/// <param name="PricePaid">Fund units spent.</param>
/// <param name="RemainingBalance">Fund balance after the redemption.</param>
public sealed record HeritageRedemption(string ItemId, long PricePaid, long RemainingBalance);

/// <summary>
/// Civilization-fund ledger (T-074, F-011/07 #23): the 3:1 fold of
/// use-less old-era resources becomes fund units, spendable ONLY in the
/// 传承商店 on decorative blueprints — the ledger structurally cannot
/// touch core balance (it moves fund units against shelf rows and nothing
/// else; D21-7 family).
/// </summary>
public sealed class CivilizationFundLedger
{
    private readonly HeritageShopTable _shop;
    private readonly Dictionary<string, HeritageShopItem> _shelf;
    private readonly List<string> _redeemedInOrder = new();

    /// <param name="shop">Heritage-shop shelf content.</param>
    /// <param name="openingBalance">Opening fund units (the transition deposits the 3:1 fold here).</param>
    /// <exception cref="ArgumentNullException">shop is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">openingBalance is negative.</exception>
    public CivilizationFundLedger(HeritageShopTable shop, long openingBalance = 0)
    {
        ArgumentNullException.ThrowIfNull(shop);
        if (openingBalance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingBalance), openingBalance, "Fund balance cannot start negative.");
        }

        _shop = shop;
        _shelf = shop.Items.ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
        Balance = openingBalance;
    }

    /// <summary>The shelf this ledger redeems against.</summary>
    public HeritageShopTable Shop => _shop;

    /// <summary>Current fund-unit balance.</summary>
    public long Balance { get; private set; }

    /// <summary>Item ids redeemed, in redemption order.</summary>
    public IReadOnlyList<string> RedeemedIds => _redeemedInOrder;

    /// <summary>
    /// Deposits fund units (the era transition's 3:1 fold lands here; also
    /// the audit-friendly refund path of the transaction journal).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">units negative.</exception>
    public void DepositFund(long units)
    {
        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units), units, "Fund deposits must be non-negative.");
        }

        Balance += units;
    }

    /// <summary>
    /// Withdraws fund units (journal compensation path — never drives the
    /// balance negative; a refused withdrawal leaves the balance untouched).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">units negative.</exception>
    /// <exception cref="InvalidOperationException">The balance is short.</exception>
    public void WithdrawFund(long units)
    {
        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units), units, "Fund withdrawals must be non-negative.");
        }

        if (Balance - units < 0)
        {
            throw new InvalidOperationException(
                $"Fund withdrawal of {units} would drive the balance negative ({Balance}) — callers must journal the prior balance.");
        }

        Balance -= units;
    }

    /// <summary>
    /// Redeems one shelf row: decorative blueprints are unique — a second
    /// copy of the same row is refused, as is an unfunded basket.
    /// </summary>
    /// <exception cref="ArgumentNullException">itemId is null.</exception>
    /// <exception cref="ArgumentException">itemId blank or unknown.</exception>
    /// <exception cref="InvalidOperationException">Already redeemed, or the balance is short.</exception>
    public HeritageRedemption Redeem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("Item id must not be blank.", nameof(itemId));
        }

        string id = itemId.Trim();
        if (!_shelf.TryGetValue(id, out HeritageShopItem? item))
        {
            throw new ArgumentException($"Unknown heritage-shop item '{id}'.", nameof(itemId));
        }

        if (_redeemedInOrder.Contains(id))
        {
            throw new InvalidOperationException($"Decorative blueprint '{id}' is already redeemed (装饰唯一，不重复兑付).");
        }

        if (Balance < item.PriceFund)
        {
            throw new InvalidOperationException(
                $"Fund balance {Balance} cannot cover '{id}' (price {item.PriceFund}) — 传承商店不赊账.");
        }

        Balance -= item.PriceFund;
        _redeemedInOrder.Add(id);
        return new HeritageRedemption(id, item.PriceFund, Balance);
    }
}
