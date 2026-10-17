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
/// <para>
/// T-134 (REV-20261017-v10-fullclear §八3 DEBT 清账): the shell hosts the
/// FULL campaign era domain E1~E5 — <see cref="EraIndex"/> tracks the
/// current era (starts at E1, advances one jump at a time via
/// <see cref="AdvanceEra"/>), <see cref="SavePoolTo"/> writes the CURRENT
/// era into the campaign document (no longer hard-coded 1) and
/// <see cref="LoadPoolFrom"/> restores a document saved at ANY era 1..
/// <see cref="MaxHostedEraIndex"/> (the pool is rebuilt at that era's
/// warehouse level — era N ⇒ level N, v3.2 §5). 终章（E=6）=T-135 域，不在
/// 本宿主面（its document era is refused until that task lands）.
/// </para>
/// </summary>
public sealed class CampaignState
{
    /// <summary>E1 era index (appendix A symbol binding: E1=1).</summary>
    public const int E1EraIndex = 1;

    /// <summary>
    /// Finale (终章 深空) era index — the highest era this shell hosts
    /// (T-135 宿主面小扩：E1~E5 清账后的终章入面；附录 F.3 奇观链第六环=
    /// 纪元之门通关之门，无第七跳). The appendix A binding E1=1…终章=6 makes
    /// this BOTH the era ordinal of the finale and the host ceiling.
    /// </summary>
    public const int MaxHostedEraIndex = 6; // ci-allow: R7 T-135 v3.2 §4 L63/L68 时代序号=终章 6（六时代终点量纲，非平衡数值）

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
        EraIndex = E1EraIndex;
        Pool = new ResourceStock(new FrozenClock(), ResourceStock.E1WarehouseLevel);
        _grid = new CampaignGrid(gridWidth, gridHeight, rng, worldSeed, Pool, options);
    }

    /// <summary>Layout seed of this campaign (K-7 WorldSeed payload).</summary>
    public long WorldSeed { get; }

    /// <summary>
    /// Current campaign era (1..6, appendix A binding E1=1…终章=6). Starts at
    /// E1; <see cref="AdvanceEra"/> walks it one jump at a time and a
    /// <see cref="LoadPoolFrom"/> of a saved document adopts the saved era.
    /// </summary>
    public int EraIndex { get; private set; }

    /// <summary>The 选图 continent grid (F-005).</summary>
    public CampaignGrid Grid => _grid;

    /// <summary>
    /// Campaign-global resource pool. Warehouse level follows the era
    /// (E1=1, +1 per transition — v3.2 §5); the instance is REPLACED on
    /// <see cref="AdvanceEra"/> (the transition's NewPool carries the
    /// carried 30% into the new-era level) and on
    /// <see cref="LoadPoolFrom"/> (a document saved at era N restores into a
    /// level-N pool, same capacity ⇒ lossless).
    /// </summary>
    public ResourceStock Pool { get; private set; }

    /// <summary>
    /// Advances the shell into the next era (T-134 宿主面): adopts the era
    /// transition's new pool (warehouse level +1, v3.2 §5) and rebuilds the
    /// 选图 grid from the same seed — a jump moves the campaign to a NEW
    /// continent, so the old grid's occupancy does not carry (the fresh grid
    /// starts at its home cell, exactly like a new campaign on that seed).
    /// One jump at a time, capped at <see cref="MaxHostedEraIndex"/>.
    /// </summary>
    /// <param name="toEraIndex">Target era (must be exactly EraIndex+1 and ≤6).</param>
    /// <param name="newPool">The transition's new-era pool (warehouse level must equal the target era).</param>
    /// <exception cref="ArgumentOutOfRangeException">toEraIndex is not EraIndex+1 or exceeds the host face.</exception>
    /// <exception cref="ArgumentException">newPool warehouse level does not match the target era.</exception>
    public void AdvanceEra(int toEraIndex, ResourceStock newPool)
    {
        ArgumentNullException.ThrowIfNull(newPool);
        if (toEraIndex != EraIndex + 1 || toEraIndex > MaxHostedEraIndex || toEraIndex < E1EraIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toEraIndex), toEraIndex,
                $"Era advance must walk one jump at a time from era {EraIndex} and stay within 1..{MaxHostedEraIndex} (E1~终章 host face; T-135 已入面，6=六时代终点无第七跳).");
        }

        if (newPool.WarehouseLevel != toEraIndex)
        {
            throw new ArgumentException(
                $"New-era pool warehouse level {newPool.WarehouseLevel} does not match target era {toEraIndex} (era N ⇒ level N, v3.2 §5 跃迁自动 +1).",
                nameof(newPool));
        }

        EraIndex = toEraIndex;
        Pool = newPool;
        GridReset(newPool);
    }

    /// <summary>
    /// Advances the shell with a committed era-transition result (convenience
    /// overload of <see cref="AdvanceEra(int, ResourceStock)"/> — the result's
    /// <see cref="Legacy.EraTransitionResult.ToEraIndex"/> and
    /// <see cref="Legacy.EraTransitionResult.NewPool"/> are the two facts the
    /// shell adopts; the transition service itself already wrote the campaign
    /// document through its own port handle).
    /// </summary>
    public void AdvanceEra(Legacy.EraTransitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        AdvanceEra(result.ToEraIndex, result.NewPool);
    }

    /// <summary>Rebuilds the selection grid on the same seed (fresh continent, home occupied).</summary>
    private void GridReset(ResourceStock pool) => _grid.ResetToHome(pool);

    /// <summary>
    /// Persists the campaign domain: pool amounts (quantized whole items,
    /// integral by banking contract) into the single campaign-meta document
    /// — written at the CURRENT era (<see cref="EraIndex"/>), T-134 清账：
    /// 不再硬写 E1). Tech/relic/layout families persist as their current
    /// (thin: empty) state — the level exit snapshot of
    /// <see cref="LevelSession"/> owns the LEVEL domain of the same port.
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
            new CampaignMetaDto(WorldSeed, EraIndex, pool, new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()),
            SaveVersion.Current);
    }

    /// <summary>
    /// Restores the pool from the campaign domain into THIS state (T-134 清账
    /// +T-135 终章入面：
    /// ANY era 1..<see cref="MaxHostedEraIndex"/> document is restorable — the
    /// pool is rebuilt at the saved era's warehouse level ⇒ same capacity ⇒
    /// every saved amount is under the headroom, so the reload is lossless).
    /// The shell adopts the saved era (<see cref="EraIndex"/>). Returns the
    /// per-resource deposits so the caller can assert field-for-field
    /// equality.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No campaign document stored yet, the stored era is outside the E1~终章
    /// host face (0/7+ 越界档), or the stored seed differs.
    /// </exception>
    public IReadOnlyList<ResourceGrant> LoadPoolFrom(ISavePort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ICampaignMeta meta = port.ReadCampaign(SaveVersion.Current);
        if (meta.EraIndex is < E1EraIndex or > MaxHostedEraIndex)
        {
            throw new InvalidOperationException(
                $"Stored campaign is era {meta.EraIndex}; this shell hosts eras {E1EraIndex}..{MaxHostedEraIndex} only (E1~终章 host face).");
        }

        if (meta.WorldSeed != WorldSeed)
        {
            throw new InvalidOperationException(
                $"Stored campaign seed {meta.WorldSeed} does not match this state's seed {WorldSeed}.");
        }

        // Pool swap at the saved era's warehouse level (era N ⇒ level N); the
        // grid re-draws on the same seed (fresh home-only continent — the
        // thin campaign document carries no 选图 occupancy, same as a fresh
        // CampaignState on this seed).
        EraIndex = meta.EraIndex;
        Pool = new ResourceStock(new FrozenClock(), meta.EraIndex);
        GridReset(Pool);

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
