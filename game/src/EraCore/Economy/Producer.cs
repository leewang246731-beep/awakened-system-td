using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Logistics;

namespace EraCore.Economy;

/// <summary>
/// E1 harvest building (采集建筑, C-06 thin, T-034, F-019): turns manpower
/// into whole resource items at the <see cref="HarvestLaw"/> rate —
/// tier-1 anchor 0.5 件/s/坐席 scaled by seats, tier ladder ×1.8
/// output / ×2.7 energy per level (E1 ships tier 1 only; the ladder lives
/// in the law validator for later tasks). Harvest speed follows the energy
/// dimension of the §5.3 chain: each tick the producer reads
/// <see cref="EnergyLedger.SatisfactionOf"/> of its own consumer node, so
/// a manpower shortage slows harvesting proportionally (同比降速不停摆).
/// Finished items land in a local output buffer (the yard pile) that the
/// Logistics porters drain through <see cref="ICargoSupplier"/>; a FULL
/// buffer is backpressure, not a stall (满仓反压): efficiency reads 0 and
/// both the buffer and the fractional progress are preserved untouched —
/// nothing is destroyed, and draining the buffer resumes production where
/// it stopped (v3.2 §5 L75 降速不停摆 semantics, same standing as the
/// converter's 满仓停产 note). Deterministic: pure per-tick arithmetic on
/// the clock domain, no RNG (CS-1.4); same seed + tick sequence ⇒ equal
/// <see cref="ComputeStateHash"/>.
/// </summary>
public sealed class Producer : ICargoSupplier
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly EnergyLedger _ledger;
    private readonly EnergyNodeId _energyNode;
    private double _progressItems;

    /// <summary>
    /// Places the harvester and registers its energy demand on
    /// <paramref name="ledger"/> (demand = tier energy anchor × seats,
    /// tagged with the resource it supplies — the §5.3 seam). Selling the
    /// building later means unregistering the node on the ledger side
    /// (orchestrator discipline, C-12: a live producer reading an
    /// unregistered node is a wiring bug).
    /// </summary>
    /// <param name="id">Building identity (K-1).</param>
    /// <param name="resource">Resource harvested (K-2 typed key, e.g. era1:res-wood).</param>
    /// <param name="seats">Work seats, ≥ 1 (坐席; rate scales per seat).</param>
    /// <param name="tier">Harvest tier, 1..<see cref="HarvestLaw.MaxTier"/> (E1 = 1).</param>
    /// <param name="ledger">Shared era energy ledger (C-08, read-only reuse).</param>
    /// <param name="energyNode">Consumer node id for this building on the ledger.</param>
    /// <exception cref="ArgumentNullException">any reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">seats &lt; 1 or tier outside the band.</exception>
    public Producer(
        EntityId id,
        ResourceKey resource,
        int seats,
        int tier,
        EnergyLedger ledger,
        EnergyNodeId energyNode)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(energyNode);
        if (seats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seats), seats, "A harvester needs at least one seat (坐席).");
        }

        if (tier < 1 || tier > HarvestLaw.MaxTier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tier), tier, $"Harvest tier must be inside [1,{HarvestLaw.MaxTier}].");
        }

        Id = id;
        Resource = resource;
        Seats = seats;
        Tier = tier;
        OutputItemsPerSecond = HarvestLaw.ProjectedOutputPerSeatPerSecond(tier) * seats;
        EnergyCostPerSecond = HarvestLaw.ProjectedEnergyPerSeatPerSecond(tier) * seats;
        _ledger = ledger;
        _energyNode = energyNode;
        ledger.RegisterConsumer(energyNode, EnergyCostPerSecond, supplies: resource);
    }

    /// <summary>
    /// Output buffer size (the yard pile a porter drains).
    /// [NEW-T-034] v3.2 pins no buffer number; 10 items = 20 s of one-seat
    /// tier-1 output, one porter round trip's worth of headroom.
    /// </summary>
    public const int OutputBufferCapacity = 10;

    /// <summary>Building identity (K-1).</summary>
    public EntityId Id { get; }

    /// <summary>Resource harvested (K-1 typed key).</summary>
    public ResourceKey Resource { get; }

    /// <summary>Work seats (坐席) — rate and energy demand scale per seat.</summary>
    public int Seats { get; }

    /// <summary>Harvest tier, 1..<see cref="HarvestLaw.MaxTier"/> (E1 = 1).</summary>
    public int Tier { get; }

    /// <summary>Total output in 件/s from the <see cref="HarvestLaw"/> projections.</summary>
    public double OutputItemsPerSecond { get; }

    /// <summary>Total energy demand in points/s registered on the ledger.</summary>
    public double EnergyCostPerSecond { get; }

    /// <summary>Whole items waiting in the yard pile, 0..<see cref="OutputBufferCapacity"/>.</summary>
    public int OutputBuffer { get; private set; }

    /// <summary>Whole items emitted into the buffer since construction.</summary>
    public long CumulativeHarvested { get; private set; }

    /// <summary>Fractional progress toward the next whole item, in [0, ∞) — frozen under backpressure.</summary>
    public double ProgressItems => _progressItems;

    /// <summary>True when the yard pile is full — production is paused (满仓反压), not stalled.</summary>
    public bool IsBackpressured => OutputBuffer >= OutputBufferCapacity;

    /// <summary>
    /// Current harvest efficiency: 0 while backpressured (输出满→停产，效率降
    /// 0 但不销毁任何积压), otherwise the energy satisfaction of this
    /// building's ledger node (the §5.3 energy dimension; a harvester's
    /// supply dimension is its own buffer room, covered by the gate).
    /// </summary>
    public double Efficiency => IsBackpressured ? 0.0 : _ledger.SatisfactionOf(_energyNode);

    /// <summary>
    /// Advances exactly one simulation tick (1/30 s): adds
    /// <c>rate × efficiency / 30</c> to the fractional progress and moves
    /// every whole item it completes into the output buffer, stopping at
    /// <see cref="OutputBufferCapacity"/>. While backpressured the tick is
    /// a no-op — progress and buffer are preserved bit-for-bit.
    /// </summary>
    public void Tick()
    {
        if (IsBackpressured)
        {
            return;
        }

        double progress = _progressItems + (OutputItemsPerSecond * Efficiency / EnergyLedger.TicksPerSecond);
        int buffer = OutputBuffer;
        long harvested = CumulativeHarvested;
        while (progress >= 1.0 && buffer < OutputBufferCapacity)
        {
            progress -= 1.0;
            buffer++;
            harvested++;
        }

        _progressItems = progress;
        OutputBuffer = buffer;
        CumulativeHarvested = harvested;
    }

    /// <summary>
    /// <see cref="ICargoSupplier"/> seam for the Logistics porters: hands
    /// over up to <paramref name="maxAmount"/> whole items of THIS
    /// producer's resource from the yard pile and returns the amount given
    /// in [0, maxAmount] (0 = pile empty — the porter waits and retries).
    /// Frees backpressure the moment room opens.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxAmount &lt; 0.</exception>
    public int TryTake(ResourceKey resource, int maxAmount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (maxAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAmount), maxAmount, "Take amount must be non-negative.");
        }

        if (!resource.Equals(Resource))
        {
            return 0;
        }

        int taken = Math.Min(maxAmount, OutputBuffer);
        OutputBuffer -= taken;
        return taken;
    }

    /// <summary>
    /// FNV-1a hash over the full observable state (identity, tier/seats,
    /// buffer, fractional progress as raw double bits, cumulative
    /// harvest). Same seed + tick sequence ⇒ equal hashes.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)Id.Value);
        hash = Mix(hash, (ulong)Tier);
        hash = Mix(hash, (ulong)Seats);
        hash = Mix(hash, (uint)OutputBuffer);
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_progressItems));
        hash = Mix(hash, (ulong)CumulativeHarvested);
        foreach (char c in Resource.Value)
        {
            hash = Mix(hash, c);
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }
}
