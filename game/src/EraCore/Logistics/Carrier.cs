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
/// E1 porter-tier carrier (民夫档, C-07 precursor, T-047, F-040~F-042): walks
/// an <see cref="AStarPathfinder"/> route at a fixed per-tick speed, loads at
/// most <see cref="CapacityItems"/> per trip and unloads only when the
/// destination accepts (待卸队列). Fully deterministic: movement is pure
/// arithmetic on the route polyline — no clock, no RNG (CS-1.4); one
/// <see cref="Tick"/> = one 1/30 s simulation step.
/// <para>
/// Tier anchor — v3.2 §5 logistics ladder tier 1 民夫 0.3 件/s. With the
/// reference route (one-way straight 5 cells = 5 × 32 px = 160 px) the round
/// trip is 2 × 160 ÷ 2.0 px/tick travel + 20 load + 20 unload = 200 ticks
/// (see <see cref="RoundTripTicks"/>) = 6.67 s for <see cref="CapacityItems"/>
/// = 2 items ⇒ exactly 0.3 items/s. The throughput test replays this by
/// simulation; six-tier content (马车 0.6 … 传送阵 ∞) lands with T-064.
/// Caliper freeze (batch2 review F5): the 0.3 items/s anchor holds ONLY at
/// the reference distance of 160 px one-way — throughput = capacity ÷
/// round-trip period, and the period is distance-sensitive (travel legs
/// scale with route length, service windows do not). The six-tier logistics
/// acceptance (T-064) must therefore compare all tiers at the SAME 160 px
/// reference distance (统一口径).
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
    /// Creates a porter standing at home (route[0]) about to load for its
    /// first trip along <paramref name="homeToDestinationRoute"/> (an A*
    /// route; consecutive cells must be 8-neighbours).
    /// </summary>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">route has fewer than two cells or a non-adjacent step.</exception>
    public Carrier(
        EntityId id,
        ResourceKey resource,
        ICargoSupplier source,
        ICargoReceiver destination,
        IReadOnlyList<CellCoord> homeToDestinationRoute)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(homeToDestinationRoute);
        if (homeToDestinationRoute.Count < 2)
        {
            throw new ArgumentException("A carrier route needs at least two cells (home and destination).", nameof(homeToDestinationRoute));
        }

        ValidateAdjacency(homeToDestinationRoute);
        _id = id;
        _resource = resource;
        _source = source;
        _destination = destination;
        _home = homeToDestinationRoute[0];
        _destinationCell = homeToDestinationRoute[^1];
        _route = homeToDestinationRoute.ToArray();
        _lastHomeRoute = _route;
        _walkedOutbound = new List<CellCoord> { _home };
        _phase = CarrierPhase.Loading;
        _serviceTicksLeft = LoadTicks;
    }

    /// <summary>Carrier identity (K-1).</summary>
    public EntityId Id => _id;

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

    /// <summary>Items aboard (0…<see cref="CapacityItems"/>).</summary>
    public int CargoItems => _cargoItems;

    /// <summary>Items cumulatively accepted by the destination.</summary>
    public long DeliveredItems => _deliveredItems;

    /// <summary>Whether the carrier sits at the destination in the unload queue (accepted-or-waiting).</summary>
    public bool IsUnloadQueued => _phase == CarrierPhase.Unloading;

    /// <summary>Ticks lived so far (one <see cref="Tick"/> = +1).</summary>
    public long TicksAlive => _ticksAlive;

    /// <summary>
    /// Round-trip period in ticks for a one-way route of
    /// <paramref name="oneWayDistancePx"/> pixels: both travel legs plus one
    /// load and one unload window — the hand-calc the throughput test checks
    /// the simulation against (往返周期手算对照模拟).
    /// </summary>
    public static double RoundTripTicks(double oneWayDistancePx) =>
        (2.0 * oneWayDistancePx / SpeedPixelsPerTick) + LoadTicks + UnloadTicks;

    /// <summary>
    /// Length of a cell route in pixels: cell-centre polyline, straight
    /// steps <see cref="CellSizePx"/>, diagonal steps 32×√2.
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

        ValidateAdjacency(route);
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
    /// window, or one <see cref="SpeedPixelsPerTick"/> movement slice. Phase
    /// transitions happen at the end of their tick, so a porter that finishes
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

                int taken = Math.Min(_source.TryTake(_resource, CapacityItems - _cargoItems), CapacityItems - _cargoItems);
                if (taken > 0)
                {
                    _cargoItems += taken;
                    BeginOutbound();
                }
                else
                {
                    // Starved source: wait one more full window and retry
                    // (断供 hooks in here, T-048).
                    _serviceTicksLeft = LoadTicks;
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
                    _serviceTicksLeft = UnloadTicks;
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
    /// distance is gained or lost by the swap.
    /// </summary>
    /// <exception cref="ArgumentNullException">newRoute is null.</exception>
    /// <exception cref="ArgumentException">newRoute has fewer than two cells or a non-adjacent step.</exception>
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

        ValidateAdjacency(newRoute);
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
    }

    private void Move()
    {
        double remaining = SpeedPixelsPerTick;
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
            _serviceTicksLeft = UnloadTicks;
        }
        else
        {
            _phase = CarrierPhase.Loading;
            _serviceTicksLeft = LoadTicks;
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
