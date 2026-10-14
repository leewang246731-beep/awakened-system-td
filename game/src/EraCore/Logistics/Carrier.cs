using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>Carrier trip phase (porter tier, F-042).</summary>
public enum CarrierPhase
{
    /// <summary>At home: loading cargo from the supplier (or waiting for supply).</summary>
    Loading = 0,

    /// <summary>Walking the A* route toward the destination.</summary>
    Outbound = 1,

    /// <summary>
    /// At the destination: unloading service window, or queued because the
    /// destination refused the cargo (待卸队列).
    /// </summary>
    Unloading = 2,

    /// <summary>Walking the reverse route home.</summary>
    Inbound = 3,
}

/// <summary>
/// Source-side cargo port of a carrier trip (secondary type of Carrier.cs):
/// how much of at most <paramref name="maxAmount"/> the supplier actually
/// hands over (0 = nothing stocked — the carrier waits and retries).
/// </summary>
public interface ICargoSupplier
{
    /// <summary>Takes up to <paramref name="maxAmount"/> items; returns the amount actually given in [0, maxAmount].</summary>
    int TryTake(ResourceKey resource, int maxAmount);
}

/// <summary>
/// Destination-side unload port (待卸队列 consumer, secondary type of
/// Carrier.cs): how much of <paramref name="amount"/> the destination accepts
/// right now (0 = full/refusing — the carrier stays queued and retries next
/// window; unload only ever happens on a positive answer, accept 才卸).
/// </summary>
public interface ICargoReceiver
{
    /// <summary>Accepts up to <paramref name="amount"/> items; returns the amount actually accepted in [0, amount].</summary>
    int TryAccept(ResourceKey resource, int amount);
}

/// <summary>
/// Per-tick speed resolver of a moving carrier (secondary type of
/// Carrier.cs; C-07 T-064 [NEW 细节标注]): the one seam where a tier's speed
/// is DYNAMIC — queried once per movement tick, so the effective speed is a
/// pure function of (carrier state, world state) and determinism is
/// untouched (CS-1.4). Sole product implementation today: <see
/// cref="RailLine"/> (轨道格连续通行时矿轨全速、断轨降为马车速).
/// </summary>
public interface ITierSpeedPolicy
{
    /// <summary>
    /// Effective speed in px/tick for <paramref name="carrier"/> THIS tick;
    /// must be positive and must not mutate the carrier.
    /// </summary>
    double EffectiveSpeedPxPerTick(Carrier carrier);
}

/// <summary>
/// Formal "cached route may be stale" notification of one carrier departure
/// (T-059 DEBT ②, spike2-report §5 pit#5): fired exactly at the two phase
/// transitions INTO a movement phase — Loading→Outbound and
/// Unloading→Inbound — because <c>BeginOutbound</c> replays
/// <c>_lastHomeRoute</c> (the last actually-walked polyline), which a wall
/// event may have invalidated while the porter sat in a service window. The
/// C-12 orchestrator subscribes, recomputes the route against the CURRENT
/// world and calls <see cref="Carrier.Reroute"/> when the cached polyline
/// differs (相位过渡主动 Reroute). Purely observational: no rng, no clock,
/// no hashed state — subscription and handling never affect
/// <c>ComputeStateHash</c> or the T-040 determinism invariant.
/// </summary>
/// <param name="Carrier">The departing porter.</param>
/// <param name="Phase">Movement phase just entered (Outbound or Inbound).</param>
/// <param name="DepartureCell">Cell the porter departs from (the route origin it would replay).</param>
/// <param name="TargetCell">Phase target: destination when outbound, home when inbound.</param>
public sealed record RouteInvalidatedEvent(
    Carrier Carrier,
    CarrierPhase Phase,
    CellCoord DepartureCell,
    CellCoord TargetCell);

/// <summary>
/// Trip-based carrier of the logistics ladder (C-07 全量, T-064, F-040~F-042;
/// tiers 民夫/马车/矿轨/无人机): walks a route at the tier speed, loads at
/// most the tier capacity per trip and unloads only when the destination
/// accepts (待卸队列). Fully deterministic: movement is pure arithmetic on
/// the route polyline — no clock, no RNG (CS-1.4); one <see cref="Tick"/> =
/// one 1/30 s simulation step.
/// <para>
/// Tier parameterization (T-064 档位参数化): capacity / speed / load window /
/// unload window / movement form all come from the <see cref="TransportTier"/>
/// row (<see cref="TransportTiers"/>). The T-047 constructor defaults to
/// <see cref="TransportTiers.Porter"/> so every existing porter use is
/// byte-identical to before; the tier table's porter row itself reads the
/// frozen <see cref="Carrier"/> constants, so the two can never drift.
/// </para>
/// <para>
/// Tier anchor — v3.2 §5 logistics ladder. With the reference route
/// (one-way straight 5 cells = 5 × 32 px = 160 px) the porter round trip is
/// 2 × 160 ÷ 2.0 px/tick travel + 20 load + 20 unload = 200 ticks
/// (see <see cref="RoundTripTicks(double)"/>) = 6.67 s for
/// <see cref="CapacityItems"/> = 2 items ⇒ exactly 0.3 items/s; 马车
/// 0.6 / 矿轨 10 / 无人机 20 land the same way through
/// <see cref="RoundTripTicks(TransportTier, double)"/>. Caliper freeze
/// (batch2 review F5): the anchors hold ONLY at the reference distance of
/// 160 px one-way — throughput = capacity ÷ round-trip period, and the
/// period is distance-sensitive (travel legs scale with route length,
/// service windows do not). The six-tier logistics acceptance (T-064)
/// therefore compares all tiers at the SAME 160 px reference distance
/// (统一口径, <see cref="TransportTiers.ReferenceOneWayPx"/>).
/// </para>
/// <para>
/// Movement forms — <see cref="TransportForm.TripGrid"/> walks an 8-neighbour
/// A* route (adjacency-validated); <see cref="TransportForm.TripFlight"/>
/// ([NEW] 无人机) flies the direct segment between the route's two endpoints:
/// the route is exactly two cells (home, destination), adjacency validation
/// is skipped and terrain never gates the flight. <see
/// cref="TransportForm.TripRail"/> moves like a grid trip but resolves its
/// per-tick speed through the optional <see cref="ITierSpeedPolicy"/>
/// (矿轨断轨降级).
/// </para>
/// </summary>
public sealed class Carrier
{
    /// <summary>Porter capacity 2 件/趟 (T-047 task card; v3.2 §5).</summary>
    public const int CapacityItems = 2;

    /// <summary>1 件弹药 = 10 发 (v3.2 §5) — content-side conversion constant.</summary>
    public const int ShotsPerItem = 10;

    /// <summary>
    /// Porter walking speed. [NEW-任务卡口径] 2.0 px/tick on the fixed 30 Hz
    /// tick domain = 60 px/s = 1.875 cells/s.
    /// </summary>
    public const double SpeedPixelsPerTick = 2.0;

    /// <summary>Fixed simulation tick rate (K-1: 30 Hz tick domain).</summary>
    public const int TicksPerSecond = 30;

    /// <summary>
    /// Loading service window in ticks (0.67 s).
    /// [NEW-原型校准] 20 ticks so the reference route realizes 0.3 件/s.
    /// </summary>
    public const int LoadTicks = 20; // ci-allow: R7 T-047 [NEW-原型校准] 装载服务窗 20t=0.67s——0.3 件/s 吞吐锚点（160px 参考距口径）的构成常量，v3.2 未钉服务窗

    /// <summary>
    /// Unloading service window in ticks (0.67 s).
    /// [NEW-原型校准] 20 ticks so the reference route realizes 0.3 件/s.
    /// </summary>
    public const int UnloadTicks = 20; // ci-allow: R7 T-047 [NEW-原型校准] 卸载服务窗 20t=0.67s——与 LoadTicks 同口径的构成常量

    /// <summary>One grid cell = 32 px (CellCoord contract, K-1).</summary>
    public const double CellSizePx = 32.0;

    /// <summary>
    /// v3.2 §5 logistics ladder tier 1 (民夫 0.3 件/s) — design anchor the
    /// reference-route simulation must reproduce within ±5%.
    /// </summary>
    public const double PorterThroughputItemsPerSecond = 0.3;

    /// <summary>
    /// Reference-route one-way distance: straight 5 cells = 160 px — the
    /// anchor distance at which the tier numbers above realize exactly
    /// 0.3 件/s. [NEW-原型校准]
    /// </summary>
    public const double ReferenceRouteOneWayPx = 5.0 * CellSizePx;

    private readonly EntityId _id;
    private readonly ResourceKey _resource;
    private readonly ICargoSupplier _source;
    private readonly ICargoReceiver _destination;
    private readonly CellCoord _home;
    private readonly CellCoord _destinationCell;
    private readonly TransportTier _tier;
    private readonly ITierSpeedPolicy? _speedPolicy;

    private IReadOnlyList<CellCoord> _route;
    private IReadOnlyList<CellCoord> _lastHomeRoute;
    private List<CellCoord> _walkedOutbound;
    private CarrierPhase _phase;
    private int _segmentIndex;
    private double _progressPx;
    private int _cargoItems;
    private int _serviceTicksLeft;
    private long _deliveredItems;
    private long _ticksAlive;

    /// <summary>
    /// Creates a porter (<see cref="TransportTiers.Porter"/>) standing at
    /// home (route[0]) about to load for its first trip along
    /// <paramref name="homeToDestinationRoute"/> (an A* route; consecutive
    /// cells must be 8-neighbours). The T-047 constructor, kept verbatim.
    /// </summary>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">route has fewer than two cells or a non-adjacent step.</exception>
    public Carrier(
        EntityId id,
        ResourceKey resource,
        ICargoSupplier source,
        ICargoReceiver destination,
        IReadOnlyList<CellCoord> homeToDestinationRoute)
        : this(id, resource, source, destination, homeToDestinationRoute, TransportTiers.Porter)
    {
    }

    /// <summary>
    /// Creates a carrier of ANY ladder tier (T-064 档位参数化). Grid and rail
    /// tiers take an 8-neighbour A* route; flight tiers
    /// (<see cref="TransportForm.TripFlight"/>) take exactly the two
    /// endpoints (home, destination) and skip adjacency validation — the
    /// drone flies the direct segment regardless of terrain. Belt and
    /// teleport forms are not trip carriers and are rejected here (they live
    /// in <see cref="ConveyorBelt"/>/<see cref="TeleporterLink"/>).
    /// <paramref name="speedPolicy"/> optionally resolves the per-tick speed
    /// (rail degradation seam); null = fixed tier speed.
    /// </summary>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// route has fewer than two cells, a non-adjacent step on a ground tier,
    /// a flight route with more than two cells, a non-trip tier, or a tier
    /// with non-positive speed/capacity.
    /// </exception>
    public Carrier(
        EntityId id,
        ResourceKey resource,
        ICargoSupplier source,
        ICargoReceiver destination,
        IReadOnlyList<CellCoord> homeToDestinationRoute,
        TransportTier tier,
        ITierSpeedPolicy? speedPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(homeToDestinationRoute);
        ArgumentNullException.ThrowIfNull(tier);
        if (homeToDestinationRoute.Count < 2)
        {
            throw new ArgumentException("A carrier route needs at least two cells (home and destination).", nameof(homeToDestinationRoute));
        }

        if (tier.Form is not (TransportForm.TripGrid or TransportForm.TripRail or TransportForm.TripFlight))
        {
            throw new ArgumentException(
                $"Tier '{tier.Id}' (form {tier.Form}) is not a trip carrier; belt/teleport tiers use ConveyorBelt/TeleporterLink.",
                nameof(tier));
        }

        if (tier.CapacityItems < 1)
        {
            throw new ArgumentException($"Tier '{tier.Id}' capacity must be positive.", nameof(tier));
        }

        if (tier.SpeedPxPerTick <= 0)
        {
            throw new ArgumentException($"Tier '{tier.Id}' speed must be positive.", nameof(tier));
        }

        if (tier.Form == TransportForm.TripFlight)
        {
            if (homeToDestinationRoute.Count != 2)
            {
                throw new ArgumentException(
                    "A flight route is exactly two cells (home, destination) — the drone crosses the direct segment.",
                    nameof(homeToDestinationRoute));
            }
        }
        else
        {
            ValidateAdjacency(homeToDestinationRoute);
        }

        _id = id;
        _resource = resource;
        _source = source;
        _destination = destination;
        _tier = tier;
        _speedPolicy = speedPolicy;
        _home = homeToDestinationRoute[0];
        _destinationCell = homeToDestinationRoute[^1];
        _route = homeToDestinationRoute.ToArray();
        _lastHomeRoute = _route;
        _walkedOutbound = new List<CellCoord> { _home };
        _phase = CarrierPhase.Loading;
        _serviceTicksLeft = tier.LoadTicks;
    }

    /// <summary>Carrier identity (K-1).</summary>
    public EntityId Id => _id;

    /// <summary>The ladder tier this carrier runs (T-064).</summary>
    public TransportTier Tier => _tier;

    /// <summary>Resource carried (K-1 typed key).</summary>
    public ResourceKey Resource => _resource;

    /// <summary>Home cell = route origin (loading point).</summary>
    public CellCoord Home => _home;

    /// <summary>Destination cell = route end (unload point).</summary>
    public CellCoord Destination => _destinationCell;

    /// <summary>Current trip phase.</summary>
    public CarrierPhase Phase => _phase;

    /// <summary>
    /// The last cell whose centre has been reached: home while loading, the
    /// destination while unloading, otherwise the origin of the segment being
    /// walked.
    /// </summary>
    public CellCoord CurrentCell => _phase switch
    {
        CarrierPhase.Loading => _home,
        CarrierPhase.Unloading => _destinationCell,
        _ => _route[_segmentIndex],
    };

    /// <summary>Items aboard (0…<see cref="TransportTier.CapacityItems"/> of <see cref="Tier"/>).</summary>
    public int CargoItems => _cargoItems;

    /// <summary>Items cumulatively accepted by the destination.</summary>
    public long DeliveredItems => _deliveredItems;

    /// <summary>Whether the carrier sits at the destination in the unload queue (accepted-or-waiting).</summary>
    public bool IsUnloadQueued => _phase == CarrierPhase.Unloading;

    /// <summary>Ticks lived so far (one <see cref="Tick"/> = +1).</summary>
    public long TicksAlive => _ticksAlive;

    /// <summary>
    /// Formal route-invalidation event (T-059 DEBT ②, spike2 pit#5): raised
    /// at every departure into a movement phase, AFTER the phase is set, so a
    /// handler may legally call <see cref="Reroute"/> synchronously — that is
    /// the orchestrator's "route invalidated → recompute → reroute" seam.
    /// Handlers must not re-enter <see cref="Tick"/> (single-threaded tick
    /// domain, CS-1.5). Never raised in Loading/Unloading service windows —
    /// mid-window porters hold their queued cargo by design (待卸队列).
    /// </summary>
    public event Action<RouteInvalidatedEvent>? RouteInvalidated;

    /// <summary>
    /// Round-trip period in ticks of the PORTER tier over a one-way route of
    /// <paramref name="oneWayDistancePx"/> pixels: both travel legs plus one
    /// load and one unload window — the hand-calc the throughput test checks
    /// the simulation against (往返周期手算对照模拟). Kept from T-047;
    /// tier-generic form: <see cref="RoundTripTicks(TransportTier, double)"/>.
    /// </summary>
    public static double RoundTripTicks(double oneWayDistancePx) =>
        RoundTripTicks(TransportTiers.Porter, oneWayDistancePx);

    /// <summary>
    /// Tier-generic round-trip period in ticks: both travel legs at the tier
    /// speed plus the tier's load and unload windows (T-064 六档手算公式).
    /// </summary>
    /// <exception cref="ArgumentNullException">tier is null.</exception>
    public static double RoundTripTicks(TransportTier tier, double oneWayDistancePx)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return tier.RoundTripTicks(oneWayDistancePx);
    }

    /// <summary>
    /// Length of a cell route in pixels: cell-centre polyline, straight
    /// steps <see cref="CellSizePx"/>, diagonal steps 32×√2. A two-cell
    /// route is the direct segment between the endpoints (the
    /// <see cref="TransportForm.TripFlight"/> form) — adjacency is only
    /// required for routes of three or more cells.
    /// </summary>
    /// <exception cref="ArgumentNullException">route is null.</exception>
    /// <exception cref="ArgumentException">route has fewer than two cells or a non-adjacent step.</exception>
    public static double RouteLengthPx(IReadOnlyList<CellCoord> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count < 2)
        {
            throw new ArgumentException("A route needs at least two cells.", nameof(route));
        }

        if (route.Count > 2)
        {
            ValidateAdjacency(route);
        }
        double total = 0;
        for (int i = 1; i < route.Count; i++)
        {
            total += SegmentLengthPx(route[i - 1], route[i]);
        }

        return total;
    }

    /// <summary>
    /// Advances exactly one simulation tick: one phase action — a service
    /// countdown step, one load/unload transfer attempt at the end of a
    /// window, or one tier-speed movement slice (a tier with an <see
    /// cref="ITierSpeedPolicy"/> resolves its speed per tick). Phase
    /// transitions happen at the end of their tick, so a carrier that finishes
    /// loading on tick t first moves on tick t+1.
    /// </summary>
    public void Tick()
    {
        _ticksAlive++;
        switch (_phase)
        {
            case CarrierPhase.Loading:
                if (--_serviceTicksLeft > 0)
                {
                    break;
                }

                int room = _tier.CapacityItems - _cargoItems;
                int taken = Math.Min(_source.TryTake(_resource, room), room);
                if (taken > 0)
                {
                    _cargoItems += taken;
                    BeginOutbound();
                }
                else
                {
                    // Starved source: wait one more full window and retry
                    // (断供 hooks in here, T-048).
                    _serviceTicksLeft = _tier.LoadTicks;
                }

                break;

            case CarrierPhase.Outbound:
            case CarrierPhase.Inbound:
                Move();
                break;

            case CarrierPhase.Unloading:
                if (--_serviceTicksLeft > 0)
                {
                    break;
                }

                int accepted = Math.Min(_destination.TryAccept(_resource, _cargoItems), _cargoItems);
                if (accepted >= _cargoItems)
                {
                    _deliveredItems += _cargoItems;
                    _cargoItems = 0;
                    BeginInbound();
                }
                else
                {
                    // Refused (queued) or partial: deliver what was accepted,
                    // then serve another unload window for the remainder —
                    // 待卸队列 semantics, cargo only ever leaves on accept.
                    _deliveredItems += accepted;
                    _cargoItems -= accepted;
                    _serviceTicksLeft = _tier.UnloadTicks;
                }

                break;

            default:
                throw new InvalidOperationException($"Unknown carrier phase {_phase}.");
        }
    }

    /// <summary>
    /// Replaces the remaining movement route mid-trip — the orchestrator's
    /// answer to a path-cache invalidation (加墙→失效→重算→改道).
    /// <paramref name="newRoute"/>[0] must equal <see cref="CurrentCell"/>
    /// and its last cell the current phase target (destination outbound, home
    /// inbound). Progress inside the current segment carries over, so no
    /// distance is gained or lost by the swap. Ground tiers validate 8-neighbour
    /// adjacency; flight tiers reroute onto a fresh two-cell segment.
    /// </summary>
    /// <exception cref="ArgumentNullException">newRoute is null.</exception>
    /// <exception cref="ArgumentException">newRoute has fewer than two cells or a non-adjacent step (ground tier).</exception>
    /// <exception cref="InvalidOperationException">
    /// carrier not in a movement phase, newRoute does not start at
    /// <see cref="CurrentCell"/>, or does not end at the phase target.
    /// </exception>
    public void Reroute(IReadOnlyList<CellCoord> newRoute)
    {
        ArgumentNullException.ThrowIfNull(newRoute);
        if (_phase is not (CarrierPhase.Outbound or CarrierPhase.Inbound))
        {
            throw new InvalidOperationException($"Reroute is only valid while moving (phase={_phase}).");
        }

        if (newRoute.Count < 2)
        {
            throw new ArgumentException("A reroute needs at least two cells.", nameof(newRoute));
        }

        if (_tier.Form != TransportForm.TripFlight)
        {
            ValidateAdjacency(newRoute);
        }
        CellCoord current = CurrentCell;
        if (!newRoute[0].Equals(current))
        {
            throw new InvalidOperationException(
                $"Reroute must start at the carrier's current cell ({current.X},{current.Y}), got ({newRoute[0].X},{newRoute[0].Y}).");
        }

        CellCoord target = _phase == CarrierPhase.Outbound ? _destinationCell : _home;
        if (!newRoute[^1].Equals(target))
        {
            throw new InvalidOperationException(
                $"Reroute must end at the phase target ({target.X},{target.Y}), got ({newRoute[^1].X},{newRoute[^1].Y}).");
        }

        _route = newRoute.ToArray();
        _segmentIndex = 0;
    }

    private void BeginOutbound()
    {
        _route = _lastHomeRoute;
        _segmentIndex = 0;
        _progressPx = 0;
        _walkedOutbound = new List<CellCoord> { _route[0] };
        _phase = CarrierPhase.Outbound;
        // T-059 DEBT ②: the replayed cached route may be stale — hand the
        // orchestrator the recomputation seam at the moment of departure.
        RaiseRouteInvalidated(CarrierPhase.Outbound, _home, _destinationCell);
    }

    private void BeginInbound()
    {
        var reverse = new CellCoord[_walkedOutbound.Count];
        for (int i = 0; i < _walkedOutbound.Count; i++)
        {
            reverse[i] = _walkedOutbound[_walkedOutbound.Count - 1 - i];
        }

        _route = reverse;
        _segmentIndex = 0;
        _progressPx = 0;
        _phase = CarrierPhase.Inbound;
        RaiseRouteInvalidated(CarrierPhase.Inbound, _destinationCell, _home);
    }

    /// <summary>
    /// Fires <see cref="RouteInvalidated"/> with the phase already set, so
    /// handlers may reroute synchronously. Null-safe (no subscriber = no
    /// cost) and side-effect free beyond subscriber work.
    /// </summary>
    private void RaiseRouteInvalidated(CarrierPhase phase, CellCoord departure, CellCoord target) =>
        RouteInvalidated?.Invoke(new RouteInvalidatedEvent(this, phase, departure, target));

    private void Move()
    {
        // Tier speed — fixed, unless the tier carries a speed policy (rail
        // degradation seam): resolved once per tick as a pure function of
        // carrier + world state (CS-1.4 determinism untouched).
        double remaining = _speedPolicy?.EffectiveSpeedPxPerTick(this) ?? _tier.SpeedPxPerTick;
        while (remaining > 0 && _segmentIndex < _route.Count - 1)
        {
            double room = SegmentLengthPx(_route[_segmentIndex], _route[_segmentIndex + 1]) - _progressPx;
            if (remaining < room)
            {
                _progressPx += remaining;
                remaining = 0;
                break;
            }

            remaining -= room;
            _progressPx = 0;
            _segmentIndex++;
            if (_phase == CarrierPhase.Outbound)
            {
                _walkedOutbound.Add(_route[_segmentIndex]);
            }
        }

        if (_segmentIndex == _route.Count - 1 && _progressPx == 0d)
        {
            Arrived();
        }
    }

    private void Arrived()
    {
        if (_phase == CarrierPhase.Outbound)
        {
            // The road actually walked (reroutes included) becomes the
            // canonical route for the next departure and the return trip.
            _lastHomeRoute = _walkedOutbound.ToArray();
            _phase = CarrierPhase.Unloading;
            _serviceTicksLeft = _tier.UnloadTicks;
        }
        else
        {
            _phase = CarrierPhase.Loading;
            _serviceTicksLeft = _tier.LoadTicks;
        }
    }

    private static double SegmentLengthPx(CellCoord from, CellCoord to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        return CellSizePx * Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void ValidateAdjacency(IReadOnlyList<CellCoord> route)
    {
        for (int i = 1; i < route.Count; i++)
        {
            int dx = Math.Abs(route[i].X - route[i - 1].X);
            int dy = Math.Abs(route[i].Y - route[i - 1].Y);
            if (dx > 1 || dy > 1 || (dx == 0 && dy == 0))
            {
                throw new ArgumentException(
                    $"Route step {i - 1}→{i} ({route[i - 1].X},{route[i - 1].Y})→({route[i].X},{route[i].Y}) is not an 8-neighbour step.",
                    nameof(route));
            }
        }
    }
}
