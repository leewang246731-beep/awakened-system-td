using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Logistics;

namespace EraCore.Sim;

/// <summary>
/// Energy port of one teleporter lane (C-12 T-067; C-07 T-064 能量钩子接
/// EnergyLedger): adapts the ledger's per-second satisfaction domain to the
/// <see cref="ITeleportEnergyPort"/> integer budget domain. The lane's bank
/// ACCUMULATES the satisfied share of its demand every tick
/// (<c>bank += floor(satisfaction × demand ÷ tick rate)</c>, exact integer
/// math — CS-1.4 no clock/no RNG inside the port) and every delivered item
/// draws <see cref="TeleporterLink.EnergyCostPerItem"/> from it. Unspent
/// budget carries over to later ticks, so a lane whose demand rounds below
/// one unit per tick still accumulates toward whole items — deterministic,
/// and a starved lane (satisfaction 0) moves nothing at all.
/// </summary>
public sealed class SimTeleporterEnergyPort : ITeleportEnergyPort
{
    private int _bank;

    /// <summary>Energy units currently drawable (never negative).</summary>
    public int Available => _bank;

    /// <summary>
    /// Credits exactly <paramref name="units"/> produced this tick
    /// (call once per tick after <see cref="EnergyLedger.Settle"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">units is negative.</exception>
    public void Produce(int units)
    {
        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units), units, "Produced energy must be non-negative.");
        }

        unchecked
        {
            _bank += units; // saturation beyond int.MaxValue is unreachable on the tick domain
        }
    }

    /// <summary>Draws exactly <paramref name="units"/> (callers only ever draw ≤ Available).</summary>
    /// <exception cref="ArgumentOutOfRangeException">units negative or above the bank.</exception>
    public void Draw(int units)
    {
        if (units < 0 || units > _bank)
        {
            throw new ArgumentOutOfRangeException(nameof(units), units, $"Draw must lie in [0,{_bank}].");
        }

        _bank -= units;
    }
}

/// <summary>
/// One tier-6 logistics lane (C-12 T-067; v3.2 §5 传送阵 ∞ 件/s，耗能):
/// a <see cref="TeleporterLink"/> between an existing supplier and receiver,
/// PLUS its C-08 energy presence — the lane registers one consumer node on
/// the shared <see cref="EnergyLedger"/> (demand in points/s, wiring data
/// from the level/command stream) and converts the settled satisfaction
/// share into the integer energy bank the link draws per delivered item.
/// Per tick: <see cref="SettleEnergy"/> (after the ledger settle) refreshes
/// the bank, then <see cref="Transfer"/> moves exactly the affordable amount
/// — the pull is capped at the bank's item capacity so no stock is ever
/// stranded in the link buffer by an unaffordable offer.
/// </summary>
public sealed class SimTeleporterLane
{
    /// <summary>Energy node id base of teleporter lanes (opaque numbering, unique per ledger).</summary>
    public const long NodeIdBase = 4_000;

    private readonly ICargoSupplier _source;
    private readonly ICargoReceiver _destination;
    private readonly double _demandPerSecond;

    /// <summary>Creates the lane and registers its consumer node on the ledger.</summary>
    /// <param name="id">Lane entity id (orchestrator-issued).</param>
    /// <param name="nodeId">Ledger consumer node id of this lane.</param>
    /// <param name="resource">Resource moved (implied by the supplier).</param>
    /// <param name="source">Supplier side (producer/converter).</param>
    /// <param name="destination">Receiver side (converter/magazine/warehouse pool).</param>
    /// <param name="demandPerSecond">Energy demand in points/s (level wiring data, RL7).</param>
    /// <param name="ledger">Shared era energy ledger.</param>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">demandPerSecond is not finite or negative.</exception>
    public SimTeleporterLane(
        EntityId id,
        EnergyNodeId nodeId,
        ResourceKey resource,
        ICargoSupplier source,
        ICargoReceiver destination,
        double demandPerSecond,
        EnergyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(ledger);
        if (!double.IsFinite(demandPerSecond) || demandPerSecond < 0)
        {
            throw new ArgumentException($"Demand must be finite and non-negative (got {demandPerSecond}).", nameof(demandPerSecond));
        }

        Id = id;
        NodeId = nodeId;
        Resource = resource;
        _source = source;
        _destination = destination;
        _demandPerSecond = demandPerSecond;
        Port = new SimTeleporterEnergyPort();
        Link = new TeleporterLink(Port);
        ledger.RegisterConsumer(nodeId, demandPerSecond);
    }

    /// <summary>Lane entity id (K-1).</summary>
    public EntityId Id { get; }

    /// <summary>Ledger consumer node id of this lane.</summary>
    public EnergyNodeId NodeId { get; }

    /// <summary>Resource moved by the lane.</summary>
    public ResourceKey Resource { get; }

    /// <summary>The instant-transport engine (∞ 件/s, energy-priced).</summary>
    public TeleporterLink Link { get; }

    /// <summary>The lane's integer energy bank (satisfaction → units).</summary>
    public SimTeleporterEnergyPort Port { get; }

    /// <summary>
    /// Credits this tick's satisfied share of the demand into the bank —
    /// call once per tick after <see cref="EnergyLedger.Settle"/>, before
    /// <see cref="Transfer"/>. Exact integer math: the whole tick share
    /// <c>floor(satisfaction × demand ÷ 30)</c> is produced, remainders
    /// re-earn next tick.
    /// </summary>
    public void SettleEnergy(EnergyLedger ledger, int tickRateHz)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (tickRateHz <= 0)
        {
            throw new ArgumentException($"Tick rate must be positive (got {tickRateHz}).", nameof(tickRateHz));
        }

        double satisfaction = ledger.SatisfactionOf(NodeId);
        Port.Produce((int)Math.Floor(satisfaction * _demandPerSecond / tickRateHz));
    }

    /// <summary>
    /// One instant transfer cycle: moves at most the affordable amount
    /// (bank ÷ per-item price) from supplier to receiver. The cap keeps the
    /// link buffer free of stock the energy line cannot pay for this tick.
    /// </summary>
    /// <returns>Items delivered this call.</returns>
    public int Transfer() => Link.Transfer(
        _source, _destination, Resource, Port.Available / TeleporterLink.EnergyCostPerItem);
}
