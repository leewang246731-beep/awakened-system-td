namespace EraCore.Endless;

/// <summary>
/// Spawn role of one endless pool entry (T-080): the endless generator
/// draws the regular line, the elite line (<c>1+⌊(w−8)/4⌋</c>, SP-7a) and
/// the boss cadence from SEPARATE sub-pools so the cross-era mix keeps its
/// role semantics while every role still mixes eras (混编跨时代).
/// </summary>
public enum EndlessEnemyRole
{
    /// <summary>Regular line enemy.</summary>
    Regular = 0,

    /// <summary>Elite-line enemy (SP-7a 精英线).</summary>
    Elite = 1,

    /// <summary>Boss-cadence enemy (NEW-原型校准: boss every 10 waves by default).</summary>
    Boss = 2,
}

/// <summary>
/// One row of the endless mixed-era enemy pool (T-080, F-006; SP-7 入口
/// 规则「混编敌人——跨时代敌人池按权重」): an enemy content id, the era it
/// belongs to (1=E1 … 6=终章, appendix A), its spawn role and its draw
/// weight. Pure data; ids and weights arrive from content at the call site
/// (RL7).
/// </summary>
/// <param name="EnemyId">Enemy content id (K-2 namespace, e.g. "era1:enemy-skirmisher").</param>
/// <param name="EraIndex">Era the enemy belongs to (1..6).</param>
/// <param name="Role">Spawn role (regular line / elite line / boss cadence).</param>
/// <param name="Weight">Draw weight (> 0; the mix share is weight/total WITHIN the role).</param>
public sealed record EndlessEnemyPoolEntry(string EnemyId, int EraIndex, EndlessEnemyRole Role, double Weight);

/// <summary>
/// The cross-era weighted enemy pool of one endless run (T-080 混编敌人):
/// entries are drawn one per spawned unit through the injected
/// <see cref="Contracts.Foundation.IRng"/> — same seed + same draw sequence
/// ⇒ same roster, which is what the run's determinism ledger hashes. The
/// cumulative walk runs in ORDINAL entry order, so a given (draw, pool)
/// pair always resolves to the same enemy regardless of dictionary layout.
/// Each role draws from its own sub-pool (weights normalise inside the
/// role), so 混编 happens per line while roles stay clean.
/// </summary>
public sealed class EndlessEnemyPool
{
    /// <summary>Lowest legal era index (appendix A: E1=1).</summary>
    public const int MinEraIndex = 1;

    /// <summary>Highest legal era index (appendix A: E6/终章=6).</summary>
    public const int MaxEraIndex = 6;

    private readonly List<EndlessEnemyPoolEntry> _entries;
    private readonly double _totalWeight;
    private readonly Dictionary<EndlessEnemyRole, double> _roleWeights;
    private readonly Dictionary<EndlessEnemyRole, List<EndlessEnemyPoolEntry>> _byRole;

    /// <summary>
    /// Validates and freezes the pool: at least one entry, non-blank and
    /// unique enemy ids, era indices inside 1..6, strictly positive
    /// weights, and at least one REGULAR entry (the endless regular line
    /// always spawns — SP-7a 数量外推 never reaches 0 for w&gt;40).
    /// </summary>
    /// <exception cref="ArgumentNullException">entries is null or contains a null row.</exception>
    /// <exception cref="ArgumentException">
    /// Empty pool, blank/duplicate id, era index outside 1..6, a weight
    /// that is not strictly positive, or no regular entry.
    /// </exception>
    public EndlessEnemyPool(IReadOnlyList<EndlessEnemyPoolEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("The endless enemy pool must not be empty.", nameof(entries));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var frozen = new List<EndlessEnemyPoolEntry>(entries.Count);
        foreach (EndlessEnemyPoolEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrWhiteSpace(entry.EnemyId))
            {
                throw new ArgumentException("Pool entries need non-blank enemy ids.", nameof(entries));
            }

            if (!seen.Add(entry.EnemyId))
            {
                throw new ArgumentException($"Duplicate pool enemy id '{entry.EnemyId}'.", nameof(entries));
            }

            if (entry.EraIndex < MinEraIndex || entry.EraIndex > MaxEraIndex)
            {
                throw new ArgumentException(
                    $"Era index of '{entry.EnemyId}' must lie in {MinEraIndex}..{MaxEraIndex} (got {entry.EraIndex}).",
                    nameof(entries));
            }

            if (!(entry.Weight > 0))
            {
                throw new ArgumentException(
                    $"Weight of '{entry.EnemyId}' must be strictly positive (got {entry.Weight}).",
                    nameof(entries));
            }

            frozen.Add(entry);
            _totalWeight += entry.Weight;
        }

        if (frozen.TrueForAll(entry => entry.Role != EndlessEnemyRole.Regular))
        {
            throw new ArgumentException("The endless pool needs at least one Regular entry.", nameof(entries));
        }

        _entries = frozen;
        _roleWeights = new Dictionary<EndlessEnemyRole, double>();
        _byRole = new Dictionary<EndlessEnemyRole, List<EndlessEnemyPoolEntry>>();
        foreach (EndlessEnemyPoolEntry entry in _entries)
        {
            if (!_byRole.TryGetValue(entry.Role, out List<EndlessEnemyPoolEntry>? bucket))
            {
                bucket = new List<EndlessEnemyPoolEntry>();
                _byRole[entry.Role] = bucket;
                _roleWeights[entry.Role] = 0.0;
            }

            bucket.Add(entry);
            _roleWeights[entry.Role] += entry.Weight;
        }
    }

    /// <summary>Frozen entries in constructor (ordinal) order.</summary>
    public IReadOnlyList<EndlessEnemyPoolEntry> Entries => _entries;

    /// <summary>Sum of all weights (the draw domain is [0, TotalWeight)).</summary>
    public double TotalWeight => _totalWeight;

    /// <summary>Sum of weights inside one role (0 when the role is absent).</summary>
    public double RoleWeight(EndlessEnemyRole role) =>
        _roleWeights.TryGetValue(role, out double weight) ? weight : 0.0;

    /// <summary>True when the role has at least one entry.</summary>
    public bool HasRole(EndlessEnemyRole role) => _byRole.ContainsKey(role);

    /// <summary>Distinct era indexes present, ascending — the 混编 proof figure.</summary>
    public IReadOnlyList<int> EraIndexes => _entries.Select(entry => entry.EraIndex)
        .Distinct()
        .OrderBy(era => era)
        .ToArray();

    /// <summary>
    /// Pure weighted pick over the WHOLE pool: a draw in
    /// [0, <see cref="TotalWeight"/>) walks the cumulative weights in
    /// ordinal order; the entry whose cumulative interval contains the draw
    /// wins. Deterministic for a fixed pool — no rng contact, so the mapping
    /// itself is table-testable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// draw is negative or ≥ <see cref="TotalWeight"/>.
    /// </exception>
    public EndlessEnemyPoolEntry Pick(double draw)
    {
        if (draw < 0 || draw >= _totalWeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draw), draw, $"Draw must lie in [0,{_totalWeight}).");
        }

        double cumulative = 0;
        foreach (EndlessEnemyPoolEntry entry in _entries)
        {
            cumulative += entry.Weight;
            if (draw < cumulative)
            {
                return entry;
            }
        }

        // Unreachable for finite positive weights (the guard above already
        // rejected draws at/above the total); the last entry is the honest
        // fallback for any floating residue at the domain's very top.
        return _entries[^1];
    }

    /// <summary>
    /// Pure weighted pick inside one ROLE sub-pool (weights normalise
    /// within the role). The cumulative walk runs in ordinal entry order —
    /// same determinism discipline as <see cref="Pick(double)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The role has no entries.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// draw is negative or ≥ the role's total weight.
    /// </exception>
    public EndlessEnemyPoolEntry Pick(double draw, EndlessEnemyRole role)
    {
        if (!_byRole.TryGetValue(role, out List<EndlessEnemyPoolEntry>? bucket))
        {
            throw new ArgumentException($"The pool carries no {role} entries.", nameof(role));
        }

        double total = _roleWeights[role];
        if (draw < 0 || draw >= total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draw), draw, $"Draw must lie in [0,{total}) for role {role}.");
        }

        double cumulative = 0;
        foreach (EndlessEnemyPoolEntry entry in bucket)
        {
            cumulative += entry.Weight;
            if (draw < cumulative)
            {
                return entry;
            }
        }

        return bucket[^1];
    }

    /// <summary>
    /// Seeded pick over the whole pool: one uniform double from the injected
    /// rng scaled onto the weight domain (CS-1.4 — randomness only through
    /// the port).
    /// </summary>
    /// <exception cref="ArgumentNullException">rng is null.</exception>
    public EndlessEnemyPoolEntry Pick(Contracts.Foundation.IRng rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Pick(rng.NextDouble() * _totalWeight);
    }

    /// <summary>
    /// Seeded pick inside one role sub-pool (CS-1.4): one uniform double
    /// from the injected rng scaled onto the role's weight domain.
    /// </summary>
    /// <exception cref="ArgumentNullException">rng is null.</exception>
    /// <exception cref="ArgumentException">The role has no entries.</exception>
    public EndlessEnemyPoolEntry Pick(Contracts.Foundation.IRng rng, EndlessEnemyRole role)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Pick(rng.NextDouble() * _roleWeights[role], role);
    }
}
