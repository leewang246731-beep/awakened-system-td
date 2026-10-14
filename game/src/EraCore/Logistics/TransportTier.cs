using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>
/// Movement form of a logistics tier (secondary type of TransportTier.cs,
/// C-07 T-064, F-041/F-042): how a tier's transport physically crosses the
/// map — the pair (form, tier parameters) is the whole data contract the
/// engines consume.
/// </summary>
public enum TransportForm
{
    /// <summary>
    /// Grid walker (民夫/马车): follows an 8-neighbour <see cref="AStarPathfinder"/>
    /// route at the tier speed; terrain passability gates the route at query
    /// time.
    /// </summary>
    TripGrid = 0,

    /// <summary>
    /// Guided rail trip (矿轨): walks a fixed <see cref="RailLine"/> route;
    /// contiguous passable rail cells grant the tier speed, a broken rail
    /// degrades to the cart tier speed (断轨降级, T-064 task card).
    /// </summary>
    TripRail = 1,

    /// <summary>
    /// Straight flight (无人机): moves along the direct home→destination
    /// segment and ignores terrain entirely — the two-cell route needs no
    /// adjacency and no passability ([NEW] v3.2 §5 tier 5).
    /// </summary>
    TripFlight = 2,

    /// <summary>
    /// Continuous belt flow (传送带): no trips — items advance cell-to-cell
    /// every fixed number of ticks through a <see cref="ConveyorBelt"/>; the
    /// tier speed expresses the belt surface speed (px/tick).
    /// </summary>
    BeltFlow = 3,

    /// <summary>
    /// Instant teleport (传送阵): no travel time at all — throughput is
    /// unbounded and the only limiter is the energy cost hook
    /// (<see cref="TeleporterLink"/>, v3.2 §5 "∞耗能").
    /// </summary>
    Teleport = 4,
}

/// <summary>
/// One rung of the v3.2 §5 logistics ladder (C-07 全量, T-064, F-041/F-042):
/// the data row every transport engine reads — capacity, speed, service
/// windows, movement form and the design throughput anchor. Trip parameters
/// (capacity/speed/windows) feed <see cref="Carrier"/>; belt parameters
/// (speed as surface speed, zero windows) feed <see cref="ConveyorBelt"/>;
/// teleport rows carry no trip parameters (anchor = ∞, limiter = energy).
/// <para>
/// Caliper freeze (batch2 review F5, 08-task-plan v1.2): the ladder anchors
/// are compared at ONE frozen reference distance — one-way 160 px = 5 cells
/// — because throughput = capacity ÷ round-trip period and the period is
/// distance-sensitive (travel legs scale with route length, service windows
/// do not). <see cref="ThroughputItemsPerSecondAt"/> realizes the formula;
/// the six-tier acceptance test (T-064) asserts every anchor at exactly
/// this distance.
/// </para>
/// </summary>
/// <param name="Id">Stable content id of the tier ("porter" … "teleporter").</param>
/// <param name="Form">Movement form (see <see cref="TransportForm"/>).</param>
/// <param name="CapacityItems">Items per trip (trip forms) or per belt cell (belt form).</param>
/// <param name="SpeedPxPerTick">Movement speed; belt form: surface speed (cells per tick × 32).</param>
/// <param name="LoadTicks">Loading service window in ticks (0 for belt/teleport forms).</param>
/// <param name="UnloadTicks">Unloading service window in ticks (0 for belt/teleport forms).</param>
/// <param name="ThroughputAnchorItemsPerSecond">
/// Design anchor at the 160 px reference distance (v3.2 §5 ladder
/// 0.3→0.6→10→15→20→∞); <see cref="double.PositiveInfinity"/> for the
/// teleport tier.
/// </param>
public sealed record TransportTier(
    string Id,
    TransportForm Form,
    int CapacityItems,
    double SpeedPxPerTick,
    int LoadTicks,
    int UnloadTicks,
    double ThroughputAnchorItemsPerSecond)
{
    /// <summary>
    /// Round-trip period in ticks over a one-way distance of
    /// <paramref name="oneWayDistancePx"/> pixels: both travel legs plus one
    /// load and one unload window (the hand-calc the throughput tests check
    /// simulations against, 往返周期手算对照模拟).
    /// </summary>
    public double RoundTripTicks(double oneWayDistancePx) =>
        (2.0 * oneWayDistancePx / SpeedPxPerTick) + LoadTicks + UnloadTicks;

    /// <summary>
    /// Formula throughput at <paramref name="oneWayDistancePx"/>: capacity
    /// ÷ (round-trip period ÷ 30). Valid for trip forms; the belt form
    /// realizes it as surface speed ÷ cell size × per-cell items (no
    /// windows); the teleport form is unbounded (∞).
    /// </summary>
    public double ThroughputItemsPerSecondAt(double oneWayDistancePx) =>
        Form == TransportForm.Teleport
            ? double.PositiveInfinity
            : CapacityItems / (RoundTripTicks(oneWayDistancePx) / Carrier.TicksPerSecond);
}

/// <summary>
/// The six-tier logistics ladder table (v3.2 §5 "物流阶梯
/// 0.3→0.6→10→15→20→∞ 件/s"; C-07 全量, T-064, F-041/F-042). Tier 1 民夫
/// re-uses the frozen T-047 anchors (<see cref="Carrier"/> constants) so the
/// ladder and the porter class can never drift apart; tiers 2~6 are new with
/// this task. All numbers are engineering constants of the tier table
/// (R7-annotated lines), each justified in place.
/// </summary>
public static class TransportTiers
{
    /// <summary>
    /// Tier 1 民夫 (0.3 件/s @160 px): the frozen T-047 anchors — capacity 2,
    /// speed 2.0 px/tick, 20+20 service windows, round trip 200 ticks.
    /// </summary>
    public static TransportTier Porter { get; } = new(
        "porter", TransportForm.TripGrid,
        Carrier.CapacityItems, Carrier.SpeedPixelsPerTick, Carrier.LoadTicks, Carrier.UnloadTicks,
        Carrier.PorterThroughputItemsPerSecond);

    /// <summary>
    /// Tier 2 马车 (0.6 件/s @160 px): capacity 4, speed 3.5 px/tick
    /// ([NEW-任务卡口径] T-064 task card). [NEW-原型校准] symmetric 54+54
    /// tick windows: exact 0.6 is unreachable on integer windows
    /// (2×160÷3.5 = 91.43 travel ticks), 54+54 lands 0.6017 件/s — inside
    /// the ±5% acceptance band around the 0.6 anchor.
    /// </summary>
    public static TransportTier Cart { get; } = new(
        "cart", TransportForm.TripGrid,
        4, 3.5, 54, 54, // ci-allow: R7 T-064 马车档 v3.2 §5 阶梯第 2 档参数（容量 4 件/3.5 px/tick，任务卡口径；54t 服务窗为 0.6 件/s 锚点的原型校准）
        0.6);

    /// <summary>
    /// Tier 3 矿轨 (10 件/s @160 px): capacity 20, rail speed 16 px/tick,
    /// 20+20 windows — round trip 60 ticks ⇒ exactly 10 件/s. A broken rail
    /// degrades the speed to the cart tier's 3.5 px/tick (断轨降为马车速,
    /// <see cref="RailLine"/>), which drops the 160 px throughput to
    /// 20 ÷ ((2×160÷3.5 + 40) ÷ 30) ≈ 4.57 件/s.
    /// </summary>
    public static TransportTier MineRail { get; } = new(
        "mine-rail", TransportForm.TripRail,
        20, 16.0, 20, 20, // ci-allow: R7 T-064 矿轨档 v3.2 §5 阶梯第 3 档参数（10 件/s 锚点@160px：RT=2×160/16+40=60t）
        10.0);

    /// <summary>
    /// Tier 4 传送带 (15 件/s): surface speed 16 px/tick = half a cell per
    /// tick (<see cref="ConveyorBelt.TicksPerCell"/> = 2), one item per cell
    /// ⇒ 0.5 items/tick = 15 件/s — length-independent steady-state flow.
    /// </summary>
    public static TransportTier Belt { get; } = new(
        "conveyor-belt", TransportForm.BeltFlow,
        1, 16.0, 0, 0,
        15.0); // ci-allow: R7 T-064 传送带档 v3.2 §5 阶梯第 4 档 15 件/s 锚点（浮点常量表面化锚点数值，供档位表断言直接对照）

    /// <summary>
    /// Tier 5 无人机 (20 件/s @160 px): capacity 20, flight speed 20 px/tick,
    /// 7+7 windows — round trip 30 ticks ⇒ exactly 20 件/s; flies the direct
    /// segment and ignores terrain ([NEW] v3.2 §5 tier 5).
    /// </summary>
    public static TransportTier Drone { get; } = new(
        "drone", TransportForm.TripFlight,
        20, 20.0, 7, 7, // ci-allow: R7 T-064 无人机档 v3.2 §5 阶梯第 5 档参数（20 件/s 锚点@160px：RT=2×160/20+14=30t）
        20.0);

    /// <summary>
    /// Tier 6 传送阵 (∞ 件/s, 耗能): instantaneous transfer — no travel time
    /// and no service window; the ONLY limiter is the per-item energy price
    /// drawn through the <see cref="ITeleportEnergyPort"/> hook
    /// (<see cref="TeleporterLink.EnergyCostPerItem"/>).
    /// </summary>
    public static TransportTier Teleporter { get; } = new(
        "teleporter", TransportForm.Teleport,
        0, 0.0, 0, 0,
        double.PositiveInfinity);

    /// <summary>The ladder in design order (tier 1 → 6).</summary>
    public static IReadOnlyList<TransportTier> Ladder { get; } =
        new[] { Porter, Cart, MineRail, Belt, Drone, Teleporter };

    /// <summary>Looks a tier up by id; null when unknown.</summary>
    public static TransportTier? ById(string id)
    {
        foreach (TransportTier tier in Ladder)
        {
            if (tier.Id == id)
            {
                return tier;
            }
        }

        return null;
    }

    /// <summary>
    /// One-way reference distance of the frozen throughput caliper (batch2
    /// review F5 / 08-task-plan v1.2): 5 straight cells = 160 px — the single
    /// distance at which the six-tier ladder comparison is legal.
    /// </summary>
    public const double ReferenceOneWayPx = Carrier.ReferenceRouteOneWayPx;

    /// <summary>
    /// Acceptance tolerance of a tier anchor against a measured throughput
    /// (T-047 task card precedent, kept for all six tiers): ±5%.
    /// </summary>
    public const double AnchorTolerance = 0.05;
}
