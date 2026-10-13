using EraCore.Contracts.Foundation;

namespace EraCore.Energy;

/// <summary>
/// E1 manpower energy ledger (人力账本, C-08 T-033, F-009). Producers (labor
/// camps, beast pens — outputs pinned by <see cref="E1EnergyAnchors"/>) and
/// consumers (towers, facilities — per-node demand) feed one shared
/// per-era pool (v3.2 §4 L65: 能源单位"能量点/秒"，各代独立). Each
/// <see cref="Settle"/> computes the pool satisfaction
/// <c>clamp(产出/需求, 0, 1)</c> — demand ≤ 0 yields 1 (v3.2 §5 L75) — and
/// every consumer reads the SAME value back as its efficiency, so a
/// shortage slows everyone down proportionally instead of stalling anyone
/// (短缺=同比降速不停摆). That value is the energy dimension of the §5.3
/// efficiency chain <c>效率 = min(供给率)</c> (C-06/T-034 will take the min
/// over the other dimensions). Time enters only via <see cref="IClock"/>
/// and randomness only via the <see cref="IRng"/> held by the optional
/// <see cref="GranaryOverflowEvent"/> (CS-1.4); same seed + same tick
/// sequence ⇒ identical <see cref="ComputeStateHash"/> (03-decomposition
/// §1.2 replay requirement).
/// </summary>
public sealed class EnergyLedger
{
    /// <summary>
    /// Simulation tick rate of the K-1 clock domain (03-decomposition
    /// §1.2): 30 ticks = 1 second of simulation time. Rates in this ledger
    /// are per second; per-tick amounts scale by <c>dt / TicksPerSecond</c>.
    /// </summary>
    public const double TicksPerSecond = 30.0;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly IClock _clock;
    private readonly GranaryOverflowEvent? _granary;
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<EnergyNodeId, Node> _byId = new();

    private ResourceKey? _granaryResource;
    private double _granaryCapacity;
    private double _granaryStock;
    private double _cumulativeGranaryLoss;
    private GranaryOverflowEvent.Loss? _lastGranaryLoss;

    private bool _hasSettled;
    private long _lastTick = -1;
    private long _settledTicks;
    private double _satisfaction = 1.0;
    private double _cumulativeDelivered;
    private double _cumulativeSurplus;

    /// <summary>
    /// Creates an empty ledger. The E1 ladder band [3,5] is asserted here
    /// so an anchor regression fails at construction (T-033 验收).
    /// </summary>
    /// <param name="clock">Deterministic time source (K-1, 30 Hz domain).</param>
    /// <param name="granary">
    /// Optional granary overflow policy; when attached via
    /// <see cref="AttachGranary"/> the ledger runs one spoilage check per
    /// settle. Its seeded <see cref="IRng"/> is the only randomness the
    /// ledger ever touches.
    /// </param>
    public EnergyLedger(IClock clock, GranaryOverflowEvent? granary = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        E1EnergyAnchors.EnsureLadderInBand();

        _clock = clock;
        _granary = granary;
    }

    private sealed record Node(
        EnergyNodeId Id,
        EnergyNodeKind Kind,
        bool IsProducer,
        double RatePerSecond,
        ResourceKey? Supplies);

    /// <summary>Last settle's pool satisfaction in [0,1]; 1 before the first settle.</summary>
    public double Satisfaction => _satisfaction;

    /// <summary>Total producer output in energy points per second (registration order sum).</summary>
    public double TotalOutputPerSecond
    {
        get
        {
            double sum = 0;
            foreach (Node node in _nodes)
            {
                if (node.IsProducer)
                {
                    sum += node.RatePerSecond;
                }
            }

            return sum;
        }
    }

    /// <summary>Total consumer demand in energy points per second (registration order sum).</summary>
    public double TotalDemandPerSecond
    {
        get
        {
            double sum = 0;
            foreach (Node node in _nodes)
            {
                if (!node.IsProducer)
                {
                    sum += node.RatePerSecond;
                }
            }

            return sum;
        }
    }

    /// <summary>Number of settles performed so far.</summary>
    public long SettledTicks => _settledTicks;

    /// <summary>Tick of the last settle; −1 before the first settle.</summary>
    public long LastTick => _lastTick;

    /// <summary>
    /// Energy points actually delivered to consumers since construction
    /// (<c>min(产出,需求) × dt</c> accumulated per settle).
    /// </summary>
    public double CumulativeDelivered => _cumulativeDelivered;

    /// <summary>Energy points produced above demand since construction (wasted surplus).</summary>
    public double CumulativeSurplus => _cumulativeSurplus;

    /// <summary>Number of registered producer nodes.</summary>
    public int ProducerCount => _nodes.Count(static n => n.IsProducer);

    /// <summary>Number of registered consumer nodes.</summary>
    public int ConsumerCount => _nodes.Count(static n => !n.IsProducer);

    /// <summary>Food stock of the attached granary; 0 when no granary is attached.</summary>
    public double GranaryStock => _granaryStock;

    /// <summary>Capacity of the attached granary; 0 when no granary is attached.</summary>
    public double GranaryCapacity => _granaryCapacity;

    /// <summary>Resource the attached granary stores (typically era1:res-food); null when none.</summary>
    public ResourceKey? GranaryResource => _granaryResource;

    /// <summary>Last applied spoilage loss, null when none fired yet or no granary attached.</summary>
    public GranaryOverflowEvent.Loss? LastGranaryLoss => _lastGranaryLoss;

    /// <summary>Total food points destroyed by granary overflow since attachment.</summary>
    public double CumulativeGranaryLoss => _cumulativeGranaryLoss;

    /// <summary>
    /// Registers a producer node. The output is pinned by the era anchor
    /// table (<see cref="E1EnergyAnchors.OutputPerSecond"/>) — tier outputs
    /// are content values, not caller input. Rejects null ids, undefined or
    /// non-producer kinds (<see cref="ArgumentException"/>) and duplicate
    /// ids (<see cref="InvalidOperationException"/>, across producers and
    /// consumers alike — one id, one node).
    /// </summary>
    public void RegisterProducer(EnergyNodeId id, EnergyNodeKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Undefined energy node value {(int)kind}.", nameof(kind));
        }

        if (!E1EnergyAnchors.IsProducer(kind))
        {
            throw new ArgumentException(
                $"Kind {kind} is not a producer; use {nameof(RegisterConsumer)} for facilities.", nameof(kind));
        }

        Register(new Node(id, kind, IsProducer: true, E1EnergyAnchors.OutputPerSecond(kind), Supplies: null));
    }

    /// <summary>
    /// Registers a consumer node (tower/facility) with its per-node demand
    /// in energy points per second. <paramref name="supplies"/> optionally
    /// tags the economy resource this consumer's efficiency feeds — the
    /// §5.3 min-chain seam C-06 (T-034) consumes via
    /// <see cref="SatisfactionOf"/>. Demand 0 is legal (a zero-demand
    /// consumer is always fully satisfied); negative or non-finite demand
    /// is rejected with <see cref="ArgumentException"/>, duplicate ids with
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public void RegisterConsumer(EnergyNodeId id, double demandPerSecond, ResourceKey? supplies = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!double.IsFinite(demandPerSecond) || demandPerSecond < 0)
        {
            throw new ArgumentException(
                $"Demand must be finite and non-negative, got {demandPerSecond}.", nameof(demandPerSecond));
        }

        Register(new Node(id, EnergyNodeKind.Facility, IsProducer: false, demandPerSecond, supplies));
    }

    /// <summary>
    /// Removes a node (any kind). Idempotent: unknown ids return false and
    /// change nothing. The next settle settles without the node — selling a
    /// tower or a labor camp takes effect on the following tick.
    /// </summary>
    public bool Unregister(EnergyNodeId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_byId.Remove(id, out Node? node))
        {
            return false;
        }

        _nodes.Remove(node!);
        return true;
    }

    /// <summary>
    /// Attaches the granary (resource-addressed side of the ledger — the
    /// food pool key, e.g. <c>era1:res-food</c>). Only one granary per
    /// ledger in E1; a second attach is rejected with
    /// <see cref="InvalidOperationException"/>. Capacity must be finite and
    /// non-negative. Stock is synced by the economy layer (C-06, T-034)
    /// through <see cref="SetGranaryStock"/>.
    /// </summary>
    public void AttachGranary(ResourceKey resource, double capacity)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_granary is null)
        {
            throw new InvalidOperationException(
                "No granary overflow policy was given to this ledger; pass one to the constructor first.");
        }

        if (_granaryResource is not null)
        {
            throw new InvalidOperationException(
                $"A granary for {_granaryResource.Value} is already attached; one per E1 ledger.");
        }

        if (!double.IsFinite(capacity) || capacity < 0)
        {
            throw new ArgumentException($"Capacity must be finite and non-negative, got {capacity}.", nameof(capacity));
        }

        _granaryResource = resource;
        _granaryCapacity = capacity;
        _granaryStock = 0;
    }

    /// <summary>
    /// Syncs the granary food stock (authoritative owner is the C-06
    /// economy pool; the ledger applies spoilage losses on top and reports
    /// them back via <see cref="LastGranaryLoss"/>). Rejects negative stock
    /// and calls without an attached granary.
    /// </summary>
    public void SetGranaryStock(double stock)
    {
        if (_granaryResource is null)
        {
            throw new InvalidOperationException("No granary attached; call AttachGranary first.");
        }

        if (!double.IsFinite(stock) || stock < 0)
        {
            throw new ArgumentException($"Stock must be finite and non-negative, got {stock}.", nameof(stock));
        }

        _granaryStock = stock;
    }

    /// <summary>
    /// Efficiency of one consumer for the last settle — the energy
    /// dimension of the §5.3 chain. Zero-demand consumers report 1
    /// (需求≤0 恒 1, v3.2 §5 L75); everyone else reports the shared pool
    /// satisfaction, so all demanding consumers slow down by the SAME
    /// factor under shortage (同比降速). Unknown ids and producer ids are
    /// rejected with <see cref="ArgumentException"/>.
    /// </summary>
    public double SatisfactionOf(EnergyNodeId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_byId.TryGetValue(id, out Node? node))
        {
            throw new ArgumentException($"No energy node {id.Value} is registered.", nameof(id));
        }

        if (node.IsProducer)
        {
            throw new ArgumentException($"Node {id.Value} is a producer; efficiency is a consumer concept.", nameof(id));
        }

        return node.RatePerSecond <= 0 ? 1.0 : _satisfaction;
    }

    /// <summary>
    /// Runs one settlement for the current <see cref="IClock.Tick"/>:
    /// recomputes the pool satisfaction from the registered rates,
    /// accumulates delivered/surplus energy over the elapsed ticks, and —
    /// with an attached granary — runs one spoilage check and applies any
    /// loss to the tracked stock. Rejects re-settling a tick already
    /// settled and non-monotonic clocks with
    /// <see cref="InvalidOperationException"/> (each tick settles exactly
    /// once; that is the determinism contract).
    /// </summary>
    public void Settle()
    {
        long tick = _clock.Tick;
        if (_hasSettled)
        {
            if (tick == _lastTick)
            {
                throw new InvalidOperationException(
                    $"Tick {tick} was already settled; settle each tick exactly once.");
            }

            if (tick < _lastTick)
            {
                throw new InvalidOperationException(
                    $"Clock went backwards: {tick} after {_lastTick}.");
            }
        }

        long dt = _hasSettled ? tick - _lastTick : 1;

        double output = 0;
        double demand = 0;
        foreach (Node node in _nodes)
        {
            if (node.IsProducer)
            {
                output += node.RatePerSecond;
            }
            else
            {
                demand += node.RatePerSecond;
            }
        }

        // v3.2 §5 L75: 效率=min(供给率)，需求≤0 恒 1 — the pool has no
        // demand to starve, so it is fully satisfied.
        _satisfaction = demand <= 0 ? 1.0 : Math.Clamp(output / demand, 0.0, 1.0);

        double seconds = dt / TicksPerSecond;
        _cumulativeDelivered += Math.Min(output, demand) * seconds;
        _cumulativeSurplus += Math.Max(0, output - demand) * seconds;

        if (_granary is not null && _granaryResource is not null)
        {
            GranaryOverflowEvent.Loss? loss = _granary.Evaluate(tick, _granaryStock, _granaryCapacity);
            if (loss is not null)
            {
                _granaryStock = loss.StockAfter;
                _lastGranaryLoss = loss;
                _cumulativeGranaryLoss += loss.Amount;
            }
        }

        _lastTick = tick;
        _settledTicks++;
        _hasSettled = true;
    }

    /// <summary>
    /// FNV-1a hash over the full observable ledger state (settled ticks,
    /// satisfaction and counters as raw double bits, nodes in registration
    /// order, granary state). Same seed + same tick sequence ⇒ equal hashes;
    /// used by the determinism/replay tests (03-decomposition §1.2).
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_lastTick);
        hash = Mix(hash, (ulong)_settledTicks);
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_satisfaction));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_cumulativeDelivered));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_cumulativeSurplus));
        hash = Mix(hash, (ulong)_nodes.Count);
        foreach (Node node in _nodes)
        {
            hash = Mix(hash, (ulong)node.Id.Value);
            hash = Mix(hash, (ulong)node.Kind);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(node.RatePerSecond));
            hash = Mix(hash, node.Supplies is null ? 0UL : 1UL);
            if (node.Supplies is not null)
            {
                foreach (char c in node.Supplies.Value)
                {
                    hash = Mix(hash, c);
                }
            }
        }

        hash = Mix(hash, _granaryResource is null ? 0UL : 1UL);
        if (_granaryResource is not null)
        {
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_granaryCapacity));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_granaryStock));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_cumulativeGranaryLoss));
            hash = Mix(hash, (ulong)(_granary?.TriggerCount ?? 0));
            if (_lastGranaryLoss is not null)
            {
                hash = Mix(hash, (ulong)_lastGranaryLoss.Tick);
                hash = Mix(hash, (ulong)_lastGranaryLoss.LossKind);
                hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_lastGranaryLoss.Amount));
            }
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    private void Register(Node node)
    {
        if (_byId.ContainsKey(node.Id))
        {
            throw new InvalidOperationException(
                $"Energy node {node.Id.Value} is already registered; unregister it first.");
        }

        _byId.Add(node.Id, node);
        _nodes.Add(node);
    }
}
