using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.Logistics;

/// <summary>
/// Evidence row of one corridor hop (secondary type of CorridorPricing.cs):
/// the overland alternative the hop's tube transit would replace, as
/// measured by the derivation — anchors used, tube length in lane cells,
/// integrated alternative cost on the shared 10/14 metric (or −1 with
/// <see cref="Sealed"/> when the overland side has no route at all).
/// </summary>
/// <param name="HopIndex">Hop ordinal along the lane (0 = first crossing pair).</param>
/// <param name="FromAnchor">Measured anchor of the western crossing (north or south side).</param>
/// <param name="ToAnchor">Measured anchor of the eastern crossing.</param>
/// <param name="HopCells">Lane steps between the two crossings.</param>
/// <param name="AlternativeCost">
/// Cheapest overland cost between the anchors with lane travel disabled
/// (−1 when <see cref="Sealed"/>).
/// </param>
/// <param name="Sealed">No overland route connects the crossings (tube transit replaces nothing).</param>
public sealed record CorridorHopEvidence(
    int HopIndex,
    CellCoord FromAnchor,
    CellCoord ToAnchor,
    int HopCells,
    long AlternativeCost,
    bool Sealed);

/// <summary>
/// Outcome of the enemy corridor price derivation (secondary type of
/// CorridorPricing.cs): the price per lane cell plus every hop's evidence —
/// the audit trail the pricing tests compare against hand calculations
/// (手算对照) and reviewers can replay.
/// </summary>
/// <param name="PricePerCell">Derived enemy price of one lane cell.</param>
/// <param name="EffectiveMultiplier">Multiplier actually applied (input clamped into the band).</param>
/// <param name="Hops">Per-hop evidence, ordered along the lane.</param>
public sealed record CorridorPricingResult(
    int PricePerCell,
    double EffectiveMultiplier,
    IReadOnlyList<CorridorHopEvidence> Hops);

/// <summary>
/// Automatic enemy-side corridor pricing (C-07 T-064; spike2-report §5 pit#4
/// 清偿：产品化改按关键路径长度自动推导，替代 spike 手工标定的 350).
/// <para>
/// Formula — the enemy price of one lane cell is derived from the detour the
/// lane would let traffic skip: for every pair of ADJACENT crossings (tube
/// mouths and lining underpasses) the cheapest overland alternative cost
/// A_i is measured with lane travel disabled, against the hop's tube length
/// h_i lane cells, and
/// <c>PricePerCell = ceil(multiplier × max_i(A_i / h_i))</c> — i.e. the tube
/// hop costs at least <paramref name="multiplier"/>× the shortest
/// alternative path length it would replace ("最短替代路径 × 1.5~2.0 倍").
/// The binding hop is the one with the best per-cell detour ratio, because
/// that is the replacement the enemy flow field would actually take.
/// </para>
/// <para>
/// Measurement basis — the alternative search runs on an
/// <see cref="IFlowGrid"/> view of the composed world where lane cells are
/// impassable EXCEPT the crossing cells themselves (underpass holes): both
/// compared routes — tube hop and overland leg — cross the lane at their
/// endpoints' crossings and pay the same toll there, so the crossing toll
/// drops out of the comparison (symmetric-toll basis). Anchors sit just
/// outside the lane (lining-hole cells at underpasses, open cells outside
/// the mouths); the cheaper of the north-side and south-side measurement
/// wins. Hop pairs with NO overland route (sealed overland) replace
/// nothing and are skipped; if every pair is sealed the price falls back to
/// <see cref="MinimumEnemyPrice"/> — the traffic rule then rests on the
/// lining structure alone (spike2 pit#2: 衬墙+双代价双管齐下).
/// </para>
/// <para>
/// Determinism (CS-1.4): pure <see cref="AStarPathfinder"/> measurement
/// over caller-supplied grid state — same world, same result; no clock, no
/// RNG.
/// </para>
/// </summary>
public static class CorridorPricing
{
    /// <summary>
    /// Default calibration multiplier (midpoint of the task-card band
    /// 1.5~2.0; spike②'s hand-tuned 350 corresponded to ≈1.82 on its
    /// binding hop — inside this band).
    /// </summary>
    public const double DefaultMultiplier = 1.75;

    /// <summary>Lower edge of the calibration band (任务卡 "1.5~2.0 倍").</summary>
    public const double MinMultiplier = 1.5;

    /// <summary>Upper edge of the calibration band (任务卡 "1.5~2.0 倍").</summary>
    public const double MaxMultiplier = 2.0;

    /// <summary>
    /// Lower clamp of a derived price: one above plain parity so the enemy
    /// view never treats the lane as cheap ground even on degenerate maps.
    /// </summary>
    public const int MinimumEnemyPrice = 2;

    /// <summary>Clamps a multiplier into the calibration band [1.5, 2.0].</summary>
    public static double ClampMultiplier(double multiplier) =>
        Math.Clamp(multiplier, MinMultiplier, MaxMultiplier);

    /// <summary>
    /// Derives the enemy price per lane cell (pit#4 formula; see the class
    /// summary for the full derivation basis). <paramref name="lane"/> is
    /// the 2-row paved band; <paramref name="underpassColumns"/> are the
    /// WEST columns of the 2-wide lining holes; the two lane ends count as
    /// mouth crossings automatically.
    /// </summary>
    /// <param name="composed">Composed world view (terrain+occupancy) both sides read.</param>
    /// <param name="lane">Corridor lane band; exactly 2 rows, with one cell of margin around the map edges for anchors.</param>
    /// <param name="underpassColumns">West column of each 2-wide underpass hole, strictly inside the lane, ascending.</param>
    /// <param name="multiplier">Calibration multiplier (clamped into [1.5, 2.0]).</param>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// lane height ≠ 2, an underpass column outside the lane interior,
    /// underpasses not ascending/duplicated, or an anchor cell outside the grid.
    /// </exception>
    public static CorridorPricingResult Derive(
        IFlowGrid composed,
        CellRegion lane,
        IReadOnlyList<int> underpassColumns,
        double multiplier = DefaultMultiplier)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(underpassColumns);
        if (lane.Height != 2)
        {
            throw new ArgumentException($"The corridor lane is a 2-row band (got height {lane.Height}).", nameof(lane));
        }

        int x0 = lane.X;
        int x1 = lane.X + lane.Width - 1;
        int y0 = lane.Y;
        int y1 = lane.Y + 1;
        if (x1 - x0 < 2)
        {
            throw new ArgumentException("The corridor lane needs at least three columns (two crossings to price between).", nameof(lane));
        }

        int previous = int.MinValue;
        foreach (int u in underpassColumns)
        {
            if (u <= x0 || u + 1 >= x1)
            {
                throw new ArgumentException($"Underpass column {u} must sit strictly inside the lane ({x0 + 1}..{x1 - 2} as a west column).", nameof(underpassColumns));
            }

            if (u <= previous)
            {
                throw new ArgumentException("Underpass columns must be ascending and distinct.", nameof(underpassColumns));
            }

            previous = u;
        }

        if (x0 - 1 < 0 || x1 + 1 >= composed.Width || y0 - 1 < 0 || y1 + 1 >= composed.Height)
        {
            throw new ArgumentException("The lane needs one cell of margin on all sides for the derivation anchors.", nameof(lane));
        }

        double effectiveMultiplier = ClampMultiplier(multiplier);

        // Crossings along the lane: west mouth, every underpass, east mouth.
        // col = the lane column the hop starts/ends at; anchors sit just
        // outside the lane (lining-hole cells at underpasses, open cells
        // outside the mouths), north and south.
        var crossings = new List<(int Col, CellCoord North, CellCoord South)>(underpassColumns.Count + 2);
        crossings.Add((x0, new CellCoord(x0 - 1, y0), new CellCoord(x0 - 1, y1)));
        foreach (int u in underpassColumns)
        {
            crossings.Add((u, new CellCoord(u, y0 - 1), new CellCoord(u, y1 + 1)));
        }

        crossings.Add((x1, new CellCoord(x1 + 1, y0), new CellCoord(x1 + 1, y1)));

        // Alternative world: lane travel disabled except the underpass
        // holes (crossing cells) — the overland route may cross there, at
        // base cost, because both compared routes pay that toll.
        var openColumns = new HashSet<int>(underpassColumns.Count * 2);
        foreach (int u in underpassColumns)
        {
            openColumns.Add(u);
            openColumns.Add(u + 1);
        }

        var alternative = new AlternativeOverlandView(composed, lane, openColumns);
        var pathfinder = new AStarPathfinder(alternative);

        var hops = new List<CorridorHopEvidence>(crossings.Count - 1);
        double bindingRatio = 0;
        for (int i = 0; i + 1 < crossings.Count; i++)
        {
            (int colA, CellCoord northA, CellCoord southA) = crossings[i];
            (int colB, CellCoord northB, CellCoord southB) = crossings[i + 1];
            int hopCells = colB - colA;

            long best = -1;
            CellCoord fromAnchor = northA;
            CellCoord toAnchor = northB;
            foreach ((CellCoord from, CellCoord to) in new[] { (northA, northB), (southA, southB) })
            {
                PathQueryResult route = pathfinder.FindPath(from, to);
                if (route.Status == PathQueryStatus.Found && (best < 0 || route.Cost < best))
                {
                    best = route.Cost;
                    fromAnchor = from;
                    toAnchor = to;
                }
            }

            bool sealedHop = best < 0;
            hops.Add(new CorridorHopEvidence(i, fromAnchor, toAnchor, hopCells, best, sealedHop));
            if (!sealedHop)
            {
                bindingRatio = Math.Max(bindingRatio, (double)best / hopCells);
            }
        }

        // All hops sealed ⇒ no overland alternative exists to be replaced:
        // the price falls back to the floor and the traffic rule rests on
        // the lining structure (documented in the class summary).
        int price = Math.Max(MinimumEnemyPrice, (int)Math.Ceiling(effectiveMultiplier * bindingRatio));
        return new CorridorPricingResult(price, effectiveMultiplier, hops);
    }

    /// <summary>
    /// Lane-travel-disabled view of the composed world (secondary type of
    /// CorridorPricing.cs): lane cells are impassable except the open
    /// (crossing) columns, which forward base cost — the measurement ground
    /// for the overland alternative (see class summary, symmetric-toll
    /// basis).
    /// </summary>
    private sealed class AlternativeOverlandView : IFlowGrid
    {
        private readonly IFlowGrid _composed;
        private readonly CellRegion _lane;
        private readonly HashSet<int> _openColumns;

        public AlternativeOverlandView(IFlowGrid composed, CellRegion lane, HashSet<int> openColumns)
        {
            _composed = composed;
            _lane = lane;
            _openColumns = openColumns;
        }

        public int Width => _composed.Width;

        public int Height => _composed.Height;

        public int Cost(CellCoord c) => _composed.Cost(c);

        public bool Passable(CellCoord c) =>
            !IsClosedLane(c) && _composed.Passable(c);

        public int BloodCost(CellCoord c) => _composed.BloodCost(c);

        private bool IsClosedLane(CellCoord c) =>
            c.Y >= _lane.Y && c.Y <= _lane.Y + 1
            && c.X >= _lane.X && c.X < _lane.X + _lane.Width
            && !_openColumns.Contains(c.X);
    }
}
