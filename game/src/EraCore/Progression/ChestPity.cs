namespace EraCore.Progression;

/// <summary>
/// Pity counter of the chest stream (T-088, F-177, [NEW-原型校准]): per tier,
/// the count of consecutive granted items that carried NO Legendary — once a
/// tier's counter reaches the table's threshold
/// (<see cref="ChestTable.PityMaxMisses"/>, 17 in chests.json), the NEXT item
/// of that tier is forced Legendary and the counter resets. Any Legendary
/// (natural or forced) also resets its tier's counter.
/// <para>
/// 定案语义（SP-8c 登记，27-t088 报告推导）：计数器按档位独立、跨开箱持续、
/// 随存档持久——重放等价条件从"同 seed"扩为"同 seed+同计数器"，
/// <see cref="MissesByTier"/> 即宿主须随检查点保存的最小状态。
/// 阈值 17 是满足"保底后传说占比 ≤12%"的最小整数（稳态 10%→11.77%），
/// 阈值本身活在 chests.json（RL7），本类型只携带逻辑。
/// </para>
/// </summary>
public sealed class ChestPity
{
    private readonly int _maxMissesWithoutLegendary;
    private readonly Dictionary<string, int> _missesByTier;

    /// <param name="maxMissesWithoutLegendary">
    /// Pity threshold from the chest table (<see cref="ChestTable.PityMaxMisses"/>).
    /// </param>
    /// <param name="savedCounters">
    /// Restored per-tier miss counters (checkpoint replay); null/omitted = a
    /// fresh player state (every tier starts at 0).
    /// </param>
    /// <exception cref="ArgumentException">maxMissesWithoutLegendary below 1.</exception>
    public ChestPity(int maxMissesWithoutLegendary, IReadOnlyDictionary<string, int>? savedCounters = null)
    {
        if (maxMissesWithoutLegendary < 1)
        {
            throw new ArgumentException(
                $"Pity threshold must be at least 1 (got {maxMissesWithoutLegendary}).", nameof(maxMissesWithoutLegendary));
        }

        _maxMissesWithoutLegendary = maxMissesWithoutLegendary;
        _missesByTier = new Dictionary<string, int>(savedCounters ?? new Dictionary<string, int>(), StringComparer.Ordinal);
    }

    /// <summary>The threshold this counter fires at (echo of the table value).</summary>
    public int MaxMissesWithoutLegendary => _maxMissesWithoutLegendary;

    /// <summary>
    /// How many items this counter has forced Legendary so far — telemetry
    /// only (host UI 保底提示/模拟报告)；NOT part of the replay state (the
    /// per-tier counters below fully determine future outcomes).
    /// </summary>
    public int ForcedLegendaryCount { get; private set; }

    /// <summary>
    /// The per-tier miss counters — the exact state a checkpoint must persist
    /// for settlement replay to reproduce pity-forced boxes.
    /// </summary>
    public IReadOnlyDictionary<string, int> MissesByTier => _missesByTier;

    /// <summary>
    /// True when the tier's consecutive-miss counter is full — the next roll
    /// of this tier must be forced Legendary.
    /// </summary>
    /// <exception cref="ArgumentNullException">tierId is null.</exception>
    public bool ForcesLegendary(string tierId)
    {
        ArgumentNullException.ThrowIfNull(tierId);
        return _missesByTier.TryGetValue(tierId, out int misses) && misses >= _maxMissesWithoutLegendary;
    }

    /// <summary>
    /// Feeds one settled rarity back into the counter: Legendary (natural or
    /// forced) resets the tier to 0, every other rarity increments it.
    /// </summary>
    /// <param name="forced">Whether this item was pity-forced (telemetry only).</param>
    /// <exception cref="ArgumentNullException">tierId is null.</exception>
    public void Record(string tierId, RewardRarity rolled, bool forced = false)
    {
        ArgumentNullException.ThrowIfNull(tierId);
        if (forced)
        {
            ForcedLegendaryCount++;
        }

        if (rolled == RewardRarity.Legendary)
        {
            _missesByTier[tierId] = 0;
            return;
        }

        _missesByTier.TryGetValue(tierId, out int misses);
        _missesByTier[tierId] = misses + 1;
    }
}
