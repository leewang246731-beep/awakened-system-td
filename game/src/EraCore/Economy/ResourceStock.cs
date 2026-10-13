using EraCore.Contracts.Foundation;

namespace EraCore.Economy;

/// <summary>
/// Global multi-resource pool (C-06 E1 thin, T-034, F-024): one shared
/// store per player keyed by <see cref="ResourceKey"/> (wood/stone/food in
/// E1 — era1.json resources). Capacity is per-resource and comes from the
/// frozen formula <c>2000 × 仓库等级 × (1 + 金字塔 0.2)</c> (v3.2 附录 F.3);
/// the pyramid term applies only once the era wonder is built. Deposits
/// only ever count inside the cap (入池只计上限内): the portion above the
/// headroom is refused — nothing is destroyed that is already stored, and
/// each refusal surfaces as a structured <see cref="Overflow"/> record
/// (the UI tooltip seam of F-024; bus relay lands with C-12/K-8).
/// Warehouse level: E1 = 1, era jumps +1 automatically, never manually
/// upgradable (v3.2 §5 L79) — the E1 anchor is cross-checked against
/// era1.json warehouse_level by the tests. Time enters only via
/// <see cref="IClock"/> (CS-1.4); same operation sequence ⇒ identical
/// <see cref="ComputeStateHash"/>.
/// </summary>
public sealed class ResourceStock
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly IClock _clock;
    private readonly Dictionary<ResourceKey, double> _amounts = new();
    private readonly List<ResourceKey> _order = new();

    /// <summary>
    /// Creates an empty pool. <paramref name="warehouseLevel"/> must be ≥ 1
    /// (E1 = 1; jumps hand the era a fresh pool, v3.2 §5 L79 — there is no
    /// manual upgrade path, so the level is fixed for the pool's lifetime).
    /// </summary>
    /// <param name="clock">Deterministic time source (K-1, 30 Hz domain) — stamps overflow records.</param>
    /// <param name="warehouseLevel">Main-city warehouse building level, ≥ 1 (era1.json warehouse_level).</param>
    /// <param name="pyramidBuilt">Whether the era wonder (E1 金字塔) is built: +20% cap (F.3).</param>
    /// <exception cref="ArgumentNullException">clock is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">warehouseLevel &lt; 1.</exception>
    public ResourceStock(IClock clock, int warehouseLevel, bool pyramidBuilt = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (warehouseLevel < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(warehouseLevel), warehouseLevel, "Warehouse level must be at least 1 (E1=1, 跃迁自动+1).");
        }

        _clock = clock;
        WarehouseLevel = warehouseLevel;
        PyramidBuilt = pyramidBuilt;
    }

    /// <summary>
    /// Structured overflow record (F-024 observability): what a deposit
    /// refused, on which tick. The 0.5-tick stamp comes from the K-1 clock;
    /// a bus-facing event type with <c>(tick, seq)</c> lands with C-12.
    /// </summary>
    /// <param name="Tick">Simulation tick of the refusing deposit.</param>
    /// <param name="Resource">Resource whose cap was hit.</param>
    /// <param name="RefusedAmount">Portion above the headroom, &gt; 0.</param>
    public sealed record Overflow(long Tick, ResourceKey Resource, double RefusedAmount);

    /// <summary>
    /// Frozen cap base: 2000 per warehouse level (v3.2 附录 F.3
    /// 资源池上限 2000×仓库等级×(1+金字塔 0.2)).
    /// </summary>
    public const double BaseCapacityPerWarehouseLevel = 2000.0;

    /// <summary>Frozen pyramid bonus term: +20% when the wonder is built (v3.2 附录 F.3).</summary>
    public const double PyramidBonus = 0.2;

    /// <summary>
    /// E1 warehouse level anchor: 1 (v3.2 §5 L79; era1.json
    /// <c>warehouse_level</c> — equality enforced by ResourceStockTests).
    /// </summary>
    public const int E1WarehouseLevel = 1;

    /// <summary>Warehouse building level of this pool (fixed at construction).</summary>
    public int WarehouseLevel { get; }

    /// <summary>Whether the pyramid era wonder is built (+20% cap).</summary>
    public bool PyramidBuilt { get; }

    /// <summary>
    /// Per-resource capacity from the frozen formula:
    /// <c>2000 × level × (1 + 0.2 if pyramid)</c>. E1 without pyramid = 2000.
    /// </summary>
    public double Capacity =>
        BaseCapacityPerWarehouseLevel * WarehouseLevel * (1.0 + (PyramidBuilt ? PyramidBonus : 0.0));

    /// <summary>Total items stored across all resources.</summary>
    public double TotalAmount
    {
        get
        {
            double sum = 0;
            foreach (ResourceKey resource in _order)
            {
                sum += _amounts[resource];
            }

            return sum;
        }
    }

    /// <summary>Resources ever deposited, in first-deposit order.</summary>
    public IReadOnlyList<ResourceKey> Resources => _order;

    /// <summary>Last refusal record; null when no deposit has overflowed yet.</summary>
    public Overflow? LastOverflow { get; private set; }

    /// <summary>Total items refused above the cap since construction.</summary>
    public double CumulativeOverflow { get; private set; }

    /// <summary>Number of deposits that hit the cap since construction.</summary>
    public long OverflowCount { get; private set; }

    /// <summary>Stored amount of one resource; 0 for resources never deposited.</summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    public double AmountOf(ResourceKey resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _amounts.TryGetValue(resource, out double amount) ? amount : 0.0;
    }

    /// <summary>
    /// Deposits up to <paramref name="amount"/> items of
    /// <paramref name="resource"/> into the pool and returns the accepted
    /// part in [0, amount] (入池只计上限内). The refused remainder (if any)
    /// updates <see cref="LastOverflow"/>/<see cref="CumulativeOverflow"/>
    /// but never touches what is already stored. Amount 0 is a legal no-op.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentException">amount negative, NaN or infinite.</exception>
    public double Deposit(ResourceKey resource, double amount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!double.IsFinite(amount) || amount < 0)
        {
            throw new ArgumentException($"Deposit amount must be finite and non-negative, got {amount}.", nameof(amount));
        }

        double current = AmountOf(resource);
        double headroom = Math.Max(0.0, Capacity - current);
        double accepted = Math.Min(amount, headroom);
        if (accepted > 0)
        {
            if (!_amounts.ContainsKey(resource))
            {
                _order.Add(resource);
                _amounts[resource] = 0.0;
            }

            _amounts[resource] = Math.Min(Capacity, current + accepted);
        }

        double refused = amount - accepted;
        if (refused > 0)
        {
            LastOverflow = new Overflow(_clock.Tick, resource, refused);
            CumulativeOverflow += refused;
            OverflowCount++;
        }

        return accepted;
    }

    /// <summary>
    /// Withdraws up to <paramref name="amount"/> items and returns the
    /// taken part in [0, amount] — never drives a resource negative.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentException">amount negative, NaN or infinite.</exception>
    public double TryWithdraw(ResourceKey resource, double amount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!double.IsFinite(amount) || amount < 0)
        {
            throw new ArgumentException($"Withdraw amount must be finite and non-negative, got {amount}.", nameof(amount));
        }

        double taken = Math.Min(AmountOf(resource), amount);
        if (taken > 0)
        {
            _amounts[resource] -= taken;
        }

        return taken;
    }

    /// <summary>
    /// FNV-1a hash over the full observable state (resources in first-
    /// deposit order with raw double bits, capacity flags, overflow
    /// counters and the last record). Same operation sequence ⇒ equal
    /// hashes (03-decomposition §1.2 replay requirement).
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)WarehouseLevel);
        hash = Mix(hash, PyramidBuilt ? 1UL : 0UL);
        hash = Mix(hash, (ulong)_order.Count);
        foreach (ResourceKey resource in _order)
        {
            foreach (char c in resource.Value)
            {
                hash = Mix(hash, c);
            }

            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_amounts[resource]));
        }

        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(CumulativeOverflow));
        hash = Mix(hash, (ulong)OverflowCount);
        hash = Mix(hash, LastOverflow is null ? 0UL : 1UL);
        if (LastOverflow is not null)
        {
            hash = Mix(hash, (ulong)LastOverflow.Tick);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(LastOverflow.RefusedAmount));
            foreach (char c in LastOverflow.Resource.Value)
            {
                hash = Mix(hash, c);
            }
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }
}
