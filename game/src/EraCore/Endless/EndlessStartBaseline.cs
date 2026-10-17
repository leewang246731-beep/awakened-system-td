namespace EraCore.Endless;

/// <summary>
/// One initial-resource row of an endless opening (T-143 任选时代起始基准):
/// a resource id plus its starting amount. Amounts arrive from the host's
/// content linkage (the starting era's campaign tables / entry policy) —
/// the engine only freezes and exposes them (RL7).
/// </summary>
/// <param name="ResourceId">Resource content id (K-2 namespace, e.g. "era1:res-wood").</param>
/// <param name="Amount">Starting amount in resource units (≥ 0).</param>
public sealed record EndlessResourceStock(string ResourceId, double Amount);

/// <summary>
/// The starting-era baseline of one endless run (T-143 任选时代: 起始时代影响
/// 初始资源/解锁面): the era's opening resource stock and its unlocked tower
/// surface, frozen onto the run config so the host can apply them when it
/// builds the endless battlefield. Pure validated data; the era ENUMERATION
/// itself is content-driven (<see cref="EndlessEntry.AvailableStartEras"/> —
/// 已装载内容集内任一时代皆可开局，时代数不落码面).
/// </summary>
public sealed record EndlessStartEraBaseline
{
    /// <summary>
    /// Validates and freezes the baseline: non-null rows, unique non-blank
    /// resource ids with non-negative amounts, unique non-blank tower ids.
    /// The era-index match against the run config is checked by
    /// <see cref="EndlessRunConfig"/> (the owner of StartEraIndex).
    /// </summary>
    /// <param name="eraIndex">The starting era (must equal the config's StartEraIndex).</param>
    /// <param name="initialResources">初始资源 rows of the starting era (unique ids, amounts ≥ 0; may be empty = host starts bare).</param>
    /// <param name="unlockedTowers">解锁面: tower ids the starting era unlocks for the run (unique, non-blank; may be empty = host owns the surface).</param>
    /// <exception cref="ArgumentNullException">A member is null.</exception>
    /// <exception cref="ArgumentException">
    /// Blank/duplicate resource id, negative amount, or blank/duplicate
    /// tower id.
    /// </exception>
    public EndlessStartEraBaseline(
        int eraIndex,
        IReadOnlyList<EndlessResourceStock> initialResources,
        IReadOnlyList<string> unlockedTowers)
    {
        ArgumentNullException.ThrowIfNull(initialResources);
        ArgumentNullException.ThrowIfNull(unlockedTowers);

        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        foreach (EndlessResourceStock row in initialResources)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (string.IsNullOrWhiteSpace(row.ResourceId))
            {
                throw new ArgumentException("Baseline resource ids must not be blank.", nameof(initialResources));
            }

            if (!seenResources.Add(row.ResourceId.Trim()))
            {
                throw new ArgumentException($"Duplicate baseline resource id '{row.ResourceId}'.", nameof(initialResources));
            }

            if (!(row.Amount >= 0))
            {
                throw new ArgumentException(
                    $"Amount of '{row.ResourceId}' must be non-negative (got {row.Amount}).", nameof(initialResources));
            }
        }

        var seenTowers = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tower in unlockedTowers)
        {
            if (string.IsNullOrWhiteSpace(tower))
            {
                throw new ArgumentException("Baseline tower ids must not be blank.", nameof(unlockedTowers));
            }

            if (!seenTowers.Add(tower.Trim()))
            {
                throw new ArgumentException($"Duplicate baseline tower id '{tower}'.", nameof(unlockedTowers));
            }
        }

        EraIndex = eraIndex;
        InitialResources = initialResources.ToList();
        UnlockedTowers = unlockedTowers.ToList();
    }

    /// <summary>The starting era (equals the config's StartEraIndex — enforced there).</summary>
    public int EraIndex { get; }

    /// <summary>初始资源 rows of the starting era (frozen copy, constructor order).</summary>
    public IReadOnlyList<EndlessResourceStock> InitialResources { get; }

    /// <summary>解锁面: tower ids the starting era unlocks for the run (frozen copy, constructor order).</summary>
    public IReadOnlyList<string> UnlockedTowers { get; }
}
