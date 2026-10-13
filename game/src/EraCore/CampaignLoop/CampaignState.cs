using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Economy;
using EraCore.Persistence;

namespace EraCore.CampaignLoop;

/// <summary>
/// Thin campaign shell (T-059, F-005): owns the 选图 continent grid and the
/// campaign-global resource pool (入库目标), and round-trips the pool through
/// the K-7 CAMPAIGN domain of an <see cref="ISavePort"/> so a mid-level exit
/// can save both domains and a re-entry can restore the pool
/// field-for-field. The pool amounts are whole items (the K-7 contract
/// stores <c>long</c> quantities): deposits must be integral, which the
/// victory/occupy settlements guarantee by flooring their grants.
/// </summary>
public sealed class CampaignState
{
    /// <summary>E1 era index (appendix A symbol binding: E1=1).</summary>
    public const int E1EraIndex = 1;

    private readonly CampaignGrid _grid;

    /// <param name="worldSeed">Deterministic grid-layout key (IRng replay contract).</param>
    /// <param name="rng">Random port (reseeded with the world seed inside the grid).</param>
    /// <param name="gridWidth">Continent grid width.</param>
    /// <param name="gridHeight">Continent grid height.</param>
    /// <param name="options">Grid generation knobs (may be null for defaults).</param>
    /// <exception cref="ArgumentNullException">rng is null.</exception>
    /// <exception cref="ArgumentException">via <see cref="CampaignGrid"/> construction.</exception>
    public CampaignState(long worldSeed, IRng rng, int gridWidth, int gridHeight, CampaignGridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rng);
        WorldSeed = worldSeed;
        Pool = new ResourceStock(new FrozenClock(), ResourceStock.E1WarehouseLevel);
        _grid = new CampaignGrid(gridWidth, gridHeight, rng, worldSeed, Pool, options);
    }

    /// <summary>Layout seed of this campaign (K-7 WorldSeed payload).</summary>
    public long WorldSeed { get; }

    /// <summary>The 选图 continent grid (F-005).</summary>
    public CampaignGrid Grid => _grid;

    /// <summary>Campaign-global resource pool (仓库等级 E1=1, cap enforced inside).</summary>
    public ResourceStock Pool { get; }

    /// <summary>
    /// Persists the campaign domain: pool amounts (quantized whole items,
    /// integral by banking contract) into the single campaign-meta document.
    /// Tech/relic/layout families persist as their current (thin: empty)
    /// state — the level exit snapshot of <see cref="LevelSession"/> owns
    /// the LEVEL domain of the same port.
    /// </summary>
    /// <exception cref="ArgumentException">A pool amount is not a whole number (K-7 stores long).</exception>
    /// <exception cref="InvalidOperationException">via the port when writing fails.</exception>
    public void SavePoolTo(ISavePort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var pool = new Dictionary<string, long>();
        foreach (ResourceKey resource in Pool.Resources)
        {
            double amount = Pool.AmountOf(resource);
            long whole = (long)Math.Floor(amount);
            if (whole != amount)
            {
                throw new ArgumentException(
                    $"Campaign pool amount of {resource.Value} is {amount} — banking must deposit whole items (K-7 long).");
            }

            pool[resource.Value] = whole;
        }

        port.WriteCampaign(
            new CampaignMetaDto(WorldSeed, E1EraIndex, pool, new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
            SaveVersion.Current);
    }

    /// <summary>
    /// Restores the pool from the campaign domain into THIS state's pool
    /// (same warehouse level ⇒ same capacity ⇒ every saved amount is under
    /// the headroom, so the reload is lossless). Returns the per-resource
    /// deposits so the caller can assert field-for-field equality.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No campaign document stored yet, or the stored era differs from E1.
    /// </exception>
    public IReadOnlyList<ResourceGrant> LoadPoolFrom(ISavePort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ICampaignMeta meta = port.ReadCampaign(SaveVersion.Current);
        if (meta.EraIndex != E1EraIndex)
        {
            throw new InvalidOperationException(
                $"Stored campaign is era {meta.EraIndex}; this thin shell only restores E1 ({E1EraIndex}).");
        }

        if (meta.WorldSeed != WorldSeed)
        {
            throw new InvalidOperationException(
                $"Stored campaign seed {meta.WorldSeed} does not match this state's seed {WorldSeed}.");
        }

        var deposits = new List<ResourceGrant>();
        foreach (KeyValuePair<string, long> entry in meta.ResourcePool)
        {
            var resource = new ResourceKey(entry.Key);
            double accepted = Pool.Deposit(resource, entry.Value);
            deposits.Add(new ResourceGrant(resource, entry.Value, accepted, entry.Value - accepted));
        }

        return deposits;
    }

    /// <summary>
    /// Standstill clock for the campaign pool: overflow records need a
    /// stamping tick, and the campaign layer owns no simulation clock of its
    /// own — tick 0 is the honest value (campaign banking is not
    /// tick-quantized). Deterministic, CS-1.4-clean (no wall time).
    /// </summary>
    private sealed class FrozenClock : Contracts.Foundation.IClock
    {
        public long Tick => 0;

        public long NowMs => 0;
    }
}
