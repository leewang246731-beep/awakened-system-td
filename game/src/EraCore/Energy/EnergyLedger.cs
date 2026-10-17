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
/// <para>
/// T-113 (F-010): an optional <see cref="EnergyRiskEngine"/> attaches the
/// SP-4 load-rate ladder risks. Each settle then ALSO feeds the raw load
/// ratio (需求÷有效发电, SP-4 L142 — effective output already reflects E3
/// explosion downtime/derate windows) into the engine and applies its
/// in-domain outcomes: an E3 overheat explosion parks the culprit producer
/// at 0 output for the downtime and at the derate fraction afterwards
/// (肇事发电机停机→降功率), an E4 meltdown unregisters the culprit
/// (永久损毁成残骸 — the wreck itself is world-layer payload). The engine
/// draws no randomness, so the determinism contract is unchanged; E1/E2
/// ledgers (risk_events=false, era.json) attach no engine and hash
/// bit-identically to the pre-T-113 ledger.
/// </para>
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
    private readonly EnergyRiskEngine? _risk;
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<EnergyNodeId, Node> _byId = new();
    private readonly Dictionary<EnergyNodeId, DerateWindow> _derates = new();
    private readonly List<EnergyRiskOutcome> _riskEvents = new();

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
    private double _loadRatio;
    private bool _coolantSupplied = true;
    private bool _fusionHpZero; // E5 湮灭通道输入位（T-134）：无湮灭行的账本恒 false——哈希只在有行时折入
    private double _outputMultiplier = 1.0;

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
    /// <param name="risk">
    /// Optional SP-4 energy risk engine (T-113, F-010); null = this era has
    /// no load-rate ladder risks (E1/E2: era.json risk_events=false) and
    /// the ledger behaves exactly like the pre-T-113 version.
    /// </param>
    public EnergyLedger(IClock clock, GranaryOverflowEvent? granary = null, EnergyRiskEngine? risk = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        E1EnergyAnchors.EnsureLadderInBand();

        _clock = clock;
        _granary = granary;
        _risk = risk;
    }

    private sealed record Node(
        EnergyNodeId Id,
        EnergyNodeKind Kind,
        bool IsProducer,
        double RatePerSecond,
        ResourceKey? Supplies);

    /// <summary>
    /// One culprit producer's post-explosion output window (E3 consequence,
    /// SP-4 L146「肇事发电机停机 60 s 后以 50% 功率运行 30 s」): output 0 for
    /// <c>DowntimeTicks</c> after the fire tick, then
    /// <c>Rate × DerateFraction</c> for <c>DerateTicks</c>, then full again.
    /// The fire tick itself still produces (consequences begin with the
    /// NEXT settle — the explosion happens after that tick's satisfaction
    /// was computed).
    /// </summary>
    private sealed record DerateWindow(long FireTick, long DowntimeTicks, long DerateTicks, double DerateFraction);

    /// <summary>Last settle's pool satisfaction in [0,1]; 1 before the first settle.</summary>
    public double Satisfaction => _satisfaction;

    /// <summary>
    /// Last settle's raw grid load ratio 需求功率÷发电功率 (SP-4 L142) — the
    /// quantity the SP-4 risk thresholds compare against, unclamped and
    /// above 1 under overload. 0 before the first settle and whenever the
    /// grid has no producers (an empty grid cannot overheat).
    /// </summary>
    public double LoadRatio => _loadRatio;

    /// <summary>
    /// The attached SP-4 risk engine, null when this era has no load-rate
    /// ladder risks (T-113; era.json risk_events=false).
    /// </summary>
    public EnergyRiskEngine? Risk => _risk;

    /// <summary>
    /// Every fired energy risk event so far, in fire order (T-113
    /// observability; folded into the state hash). World-layer AOE/radiation
    /// payloads are consumed from here by the host.
    /// </summary>
    public IReadOnlyList<EnergyRiskOutcome> RiskEvents => _riskEvents;

    /// <summary>
    /// Total EFFECTIVE producer output in energy points per second — base
    /// rates minus live downtime/derate windows of explosion culprits
    /// (registration order sum). Equals the pre-T-113 sum when no risk
    /// engine is attached.
    /// </summary>
    public double TotalOutputPerSecond
    {
        get
        {
            double sum = 0;
            long tick = _clock.Tick;
            foreach (Node node in _nodes)
            {
                if (node.IsProducer)
                {
                    sum += EffectiveOutput(node, tick);
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
    /// Sets the coolant supply state of the era's generators (E4 断冷
    /// channel, T-113 task card「E4 核电[断冷→熔毁]」). Default true; read by
    /// the risk engine at each settle when a meltdown row exists — losing
    /// the supply starts the countdown, restoring it interrupts a
    /// coolant-started countdown (对策：冷却液供给可避免).
    /// </summary>
    public void SetCoolantSupplied(bool supplied) => _coolantSupplied = supplied;

    /// <summary>
    /// Sets the GLOBAL energy-output multiplier channel (T-134, 附录 F.3
    /// 奇观链第五个：戴森云=E5 奇观, 能源 ×2). Every producer's effective
    /// output — the settle sums, the satisfaction numerator and the load
    /// ratio denominator alike — scales by <paramref name="multiplier"/>
    /// (1.0 = no wonder; the value is derived from content by
    /// <c>Legacy.WonderEnergyOutputBoard</c> — the ledger itself stays
    /// wonder-agnostic, T-117 throughput-channel discipline mirrored on the
    /// energy side). Doubled output halves the load ratio at unchanged
    /// demand — the E5 annihilation-risk counterplay surface.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// multiplier is not finite or below 1 (F.3: the channel only boosts).
    /// </exception>
    public void SetOutputMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier < 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiplier), multiplier,
                "The global energy-output multiplier must be finite and >= 1 (F.3: only boosts).");
        }

        _outputMultiplier = multiplier;
    }

    /// <summary>The current global output multiplier (1.0 = no energy wonder built).</summary>
    public double OutputMultiplier => _outputMultiplier;

    /// <summary>
    /// Reports the fusion-building HP-zero fact of the era's grid (T-134, E5
    /// 湮灭通道, SP-4 L148「聚变建筑 HP 归零时」): the world layer — the only
    /// side that sees building HP — raises this on the settle a fusion plant
    /// dies on; an attached engine with an annihilation row detonates
    /// IMMEDIATELY (无倒计时). Grids without the row ignore the flag
    /// (E3/E4 behaviour and hashes unchanged). Default false; the flag is
    /// level-triggered per settle (the detonation consumes it).
    /// </summary>
    public void SetFusionHpZero(bool hpZero) => _fusionHpZero = hpZero;

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
    /// recomputes the pool satisfaction from the registered (effective)
    /// rates, accumulates delivered/surplus energy over the elapsed ticks,
    /// runs one granary spoilage check when attached, and — with an
    /// attached risk engine — feeds the load ratio into the SP-4 channels
    /// and applies any fired outcome (E3: culprit derate window; E4:
    /// culprit unregister). Rejects re-settling a tick already settled and
    /// non-monotonic clocks with <see cref="InvalidOperationException"/>
    /// (each tick settles exactly once; that is the determinism contract).
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
                output += EffectiveOutput(node, tick);
            }
            else
            {
                demand += node.RatePerSecond;
            }
        }

        // v3.2 §5 L75: 效率=min(供给率)，需求≤0 恒 1 — the pool has no
        // demand to starve, so it is fully satisfied.
        _satisfaction = demand <= 0 ? 1.0 : Math.Clamp(output / demand, 0.0, 1.0);

        // SP-4 L142: 电网负载率=当前需求功率÷当前发电功率 — raw and unclamped;
        // a producer-less grid has no generator to overheat, so it reads 0.
        _loadRatio = output > 0 ? demand / output : 0.0;

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

        if (_risk is not null)
        {
            foreach (EnergyRiskOutcome outcome in _risk.OnSettle(
                         tick, dt, _loadRatio, _coolantSupplied, _fusionHpZero, LargestProducerId()))
            {
                _riskEvents.Add(outcome);
                ApplyRiskOutcome(tick, outcome);
            }

            PruneExpiredDerates(tick);
        }

        // T-134: the fusion HP-zero fact is level-triggered per settle —
        // the detonation (or a row-less grid) consumes it; the world layer
        // re-raises it on the next death it observes. Reset outside the
        // risk-engine branch too: a risk-less ledger must never keep a
        // stale flag (hash only folds it when rows exist, but the bit
        // itself should not outlive the settle that consumed it).
        _fusionHpZero = false;

        _lastTick = tick;
        _settledTicks++;
        _hasSettled = true;
    }

    /// <summary>
    /// Applies one fired outcome's in-domain consequence: the E3 culprit
    /// producer gets its downtime/derate window (consequences visible from
    /// the NEXT settle); the E4 meltdown and E5 annihilation culprits are
    /// unregistered outright (永久损毁 — the wreck, its repair-recovery cost,
    /// the radiation zone and the E5 chain detonation are world-layer
    /// payloads carried by the outcome record).
    /// </summary>
    private void ApplyRiskOutcome(long tick, EnergyRiskOutcome outcome)
    {
        if (outcome.Kind == EnergyRiskEventKind.OverheatExplosion)
        {
            _derates[outcome.Culprit] = new DerateWindow(
                tick,
                SecondsToTicks(outcome.GeneratorDowntimeSeconds),
                SecondsToTicks(outcome.GeneratorDerateSeconds),
                outcome.GeneratorDerateFraction);
        }
        else
        {
            _ = Unregister(outcome.Culprit); // 熔毁（E4）/湮灭（E5）：肇事机永久损毁
        }
    }

    /// <summary>
    /// Effective output of one node at <paramref name="tick"/>: producers
    /// inside a live derate window read 0 (downtime) or
    /// <c>rate × fraction</c> (derate); everything else reads its base rate.
    /// The global output multiplier (<see cref="SetOutputMultiplier"/>,
    /// 戴森云 ×2 channel) scales every producer's reading — the same channel
    /// therefore flows into the settle sums, the satisfaction numerator and
    /// the load-ratio denominator. Pure — expired windows are pruned once
    /// per settle by <see cref="PruneExpiredDerates"/>, never here.
    /// </summary>
    private double EffectiveOutput(Node node, long tick)
    {
        if (!node.IsProducer)
        {
            return node.RatePerSecond;
        }

        double rate = node.RatePerSecond;
        if (_derates.TryGetValue(node.Id, out DerateWindow? window))
        {
            long elapsed = tick - window.FireTick;
            if (elapsed > 0)
            {
                if (elapsed <= window.DowntimeTicks)
                {
                    return 0.0;
                }

                if (elapsed <= window.DowntimeTicks + window.DerateTicks)
                {
                    rate *= window.DerateFraction;
                }
            }
        }

        return rate * _outputMultiplier;
    }

    /// <summary>
    /// The deterministic culprit candidate handed to the risk engine: the
    /// largest-output producer (ties resolved by registration order —
    /// [NEW-T-113]: SP-4 names only「肇事发电机」; the biggest boiler bears
    /// the risk). Null when the grid has no producers.
    /// </summary>
    private EnergyNodeId? LargestProducerId()
    {
        Node? largest = null;
        foreach (Node node in _nodes)
        {
            if (node.IsProducer && (largest is null || node.RatePerSecond > largest.RatePerSecond))
            {
                largest = node;
            }
        }

        return largest?.Id;
    }

    private void PruneExpiredDerates(long tick)
    {
        if (_derates.Count == 0)
        {
            return;
        }

        List<EnergyNodeId>? expired = null;
        foreach (KeyValuePair<EnergyNodeId, DerateWindow> pair in _derates)
        {
            long elapsed = tick - pair.Value.FireTick;
            if (elapsed > pair.Value.DowntimeTicks + pair.Value.DerateTicks)
            {
                (expired ??= new List<EnergyNodeId>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (EnergyNodeId id in expired)
            {
                _derates.Remove(id);
            }
        }
    }

    private static long SecondsToTicks(double seconds) => (long)Math.Ceiling(seconds * TicksPerSecond);

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

        // T-113: risk state folds in ONLY when an engine is attached — an
        // E1/E2 ledger (risk_events=false) contributes nothing and keeps
        // its pre-T-113 hash bit-for-bit (golden stability). T-134: the
        // fusion HP-zero input folds in only when the engine actually has
        // an annihilation row (E5) — E3/E4 hashes stay bit-identical.
        if (_risk is not null)
        {
            hash = Mix(hash, (ulong)_risk.ComputeStateHash());
            hash = Mix(hash, _coolantSupplied ? 1UL : 0UL);
            if (_risk.HasAnnihilation)
            {
                hash = Mix(hash, _fusionHpZero ? 1UL : 0UL);
            }

            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_loadRatio));
            hash = Mix(hash, (ulong)_derates.Count);
            foreach (Node node in _nodes)
            {
                if (_derates.TryGetValue(node.Id, out DerateWindow? window))
                {
                    hash = Mix(hash, (ulong)node.Id.Value);
                    hash = Mix(hash, (ulong)window.FireTick);
                    hash = Mix(hash, (ulong)window.DowntimeTicks);
                    hash = Mix(hash, (ulong)window.DerateTicks);
                    hash = Mix(hash, BitConverter.DoubleToUInt64Bits(window.DerateFraction));
                }
            }

            hash = Mix(hash, (ulong)_riskEvents.Count);
            foreach (EnergyRiskOutcome outcome in _riskEvents)
            {
                hash = Mix(hash, (ulong)outcome.Tick);
                hash = Mix(hash, (ulong)outcome.Kind);
                hash = Mix(hash, (ulong)outcome.Culprit.Value);
            }
        }

        // T-134: the dyson-cloud output multiplier folds in ONLY when it is
        // actually engaged (≠1) — every existing ledger (E1~E4, no energy
        // wonder) contributes nothing and keeps its pre-T-134 hash
        // bit-for-bit (golden stability, coolant/risk fold precedent).
        if (_outputMultiplier != 1.0)
        {
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_outputMultiplier));
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
