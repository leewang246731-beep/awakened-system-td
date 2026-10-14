using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.World;

namespace EraCore.Logistics;

/// <summary>
/// Productized supply-corridor traffic rule (C-07 全量, T-064, F-045; design
/// v3.2 §6 "物流交规（补给走廊/城门）"; spike2-report §5 pit#2 清偿).
/// The spike proved that a corridor holds ONLY when terrain structure and
/// dual cost act TOGETHER — 缺一即漏:
/// <list type="bullet">
/// <item><b>Structure (衬墙)</b>: the 2-row paved lane is flanked by
/// untearable Wall-terrain lining (two rows each side, spanning the lane
/// length) with 2-wide underpass holes where overland traffic legitimately
/// crosses. The lining kills the two leaks a price alone cannot close —
/// "shadowing" the lane from the outside and cutting between crossings
/// inside the tube.</item>
/// <item><b>Dual cost (双代价)</b>: <see cref="CreateEnemyView"/> reprices
/// every lane cell at <see cref="EnemyPricePerCell"/> for the ENEMY flow
/// field only (registered per lane×costType bucket through <see
/// cref="FlowFieldManager.RegisterGridView"/>, pit#3), while porters keep
/// reading the composed grid directly — the Road lane costs them 1. The
/// price is auto-derived from the map itself via <see cref="CorridorPricing"/>
/// (pit#4), replacing the spike's hand-tuned 350.</item>
/// </list>
/// Determinism (CS-1.4): pure structure application over caller-owned
/// <see cref="GridWorld"/> state and pure view wrappers — no clock, no RNG.
/// </summary>
public sealed class CorridorRule
{
    /// <summary>
    /// Lining thickness on each side of the lane (城墙夹廊口径: two Wall rows
    /// north and south — spike② geometry).
    /// </summary>
    public const int LiningThickness = 2;

    /// <summary>
    /// Width of one underpass hole (2-wide hole per crossing — spike②
    /// geometry; also the width of the enemy toll band when the overland
    /// route crosses the lane).
    /// </summary>
    public const int UnderpassWidth = 2;

    private readonly CellRegion _lane;
    private readonly int[] _underpassColumns;
    private readonly List<CellCoord> _laneCells;
    private readonly List<CellCoord> _liningCells;
    private readonly HashSet<CellCoord> _liningSet = new();

    /// <summary>
    /// Defines the corridor geometry: a 2-row lane band plus the west
    /// columns of its underpass holes. Nothing is written to any world until
    /// <see cref="ApplyStructure"/>; pricing is derived on demand via
    /// <see cref="DerivePricing"/> (before that the enemy price is
    /// <see cref="CorridorPricing.MinimumEnemyPrice"/>).
    /// </summary>
    /// <param name="lane">Paved lane band; exactly 2 rows.</param>
    /// <param name="underpassColumns">
    /// West column of each 2-wide lining hole, strictly inside the lane,
    /// ascending (may be empty — mouths remain the only crossings).
    /// </param>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// lane height ≠ 2 or an underpass column outside the lane interior /
    /// not ascending (re-thrown from <see cref="CorridorPricing.Derive"/>
    /// validation, shared with the pricing ground).
    /// </exception>
    public CorridorRule(CellRegion lane, IReadOnlyList<int> underpassColumns)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(underpassColumns);
        if (lane.Height != 2)
        {
            throw new ArgumentException($"The corridor lane is a 2-row band (got height {lane.Height}).", nameof(lane));
        }

        if (lane.Width < 3)
        {
            throw new ArgumentException("The corridor lane needs at least three columns (two mouths plus a tube).", nameof(lane));
        }

        _lane = lane;
        _underpassColumns = underpassColumns.ToArray();
        ValidateUnderpasses();
        _laneCells = new List<CellCoord>(lane.Width * 2);
        for (int x = lane.X; x < lane.X + lane.Width; x++)
        {
            _laneCells.Add(new CellCoord(x, lane.Y));
            _laneCells.Add(new CellCoord(x, lane.Y + 1));
        }

        _liningCells = BuildLiningCells();
    }

    /// <summary>The paved lane band (2 rows).</summary>
    public CellRegion Lane => _lane;

    /// <summary>West columns of the underpass holes, ascending.</summary>
    public IReadOnlyList<int> UnderpassColumns => _underpassColumns;

    /// <summary>Every lane cell (both rows, west → east).</summary>
    public IReadOnlyList<CellCoord> LaneCells => _laneCells;

    /// <summary>
    /// Every lining cell this rule places (both sides, holes excluded) —
    /// the structural half of the rule.
    /// </summary>
    public IReadOnlyList<CellCoord> LiningCells => _liningCells;

    /// <summary>
    /// Enemy price of one lane cell — <see cref="CorridorPricing.MinimumEnemyPrice"/>
    /// until <see cref="DerivePricing"/> runs, then the auto-derived value
    /// (pit#4: 最短替代路径 × 1.5~2.0).
    /// </summary>
    public int EnemyPricePerCell { get; private set; } = CorridorPricing.MinimumEnemyPrice;

    /// <summary>The last derivation's audit result, or null before the first.</summary>
    public CorridorPricingResult? LastPricing { get; private set; }

    /// <summary>Whether the cell is a lane cell of this corridor.</summary>
    public bool IsLaneCell(CellCoord cell) =>
        cell.Y >= _lane.Y && cell.Y <= _lane.Y + 1
        && cell.X >= _lane.X && cell.X < _lane.X + _lane.Width;

    /// <summary>Whether the cell is a lining cell of this corridor (holes excluded).</summary>
    public bool IsLiningCell(CellCoord cell) => _liningSet.Contains(cell);

    /// <summary>
    /// Applies the structural half of the rule to a <see cref="GridWorld"/>:
    /// Road on every lane cell, untearable Wall on every lining cell that is
    /// not already Wall (serpentine/fortress walls crossing the corridor are
    /// kept and simply count as lining). Returns how many lining cells were
    /// actually written (idempotent second run returns 0).
    /// </summary>
    /// <exception cref="ArgumentNullException">world is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">re-thrown from the world for out-of-bounds cells.</exception>
    public int ApplyStructure(GridWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (CellCoord cell in _laneCells)
        {
            world.SetTerrain(cell, TerrainKind.Road);
        }

        int written = 0;
        foreach (CellCoord cell in _liningCells)
        {
            if (world.TerrainAt(cell) != TerrainKind.Wall)
            {
                world.SetTerrain(cell, TerrainKind.Wall);
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Derives the enemy price per lane cell from the CURRENT world state
    /// (pit#4 auto-pricing; basis and formula: <see cref="CorridorPricing"/>)
    /// and stores it as <see cref="EnemyPricePerCell"/>. Re-run after
    /// structural changes that reroute the overland alternatives (walls,
    /// buildings); the returned evidence makes the derivation auditable.
    /// </summary>
    /// <param name="composed">Composed world view (terrain + occupancy) both costs read.</param>
    /// <param name="multiplier">Calibration multiplier, clamped into [1.5, 2.0].</param>
    /// <exception cref="ArgumentNullException">composed is null.</exception>
    /// <exception cref="ArgumentException">re-thrown from the derivation's geometry validation.</exception>
    public CorridorPricingResult DerivePricing(IFlowGrid composed, double multiplier = CorridorPricing.DefaultMultiplier)
    {
        ArgumentNullException.ThrowIfNull(composed);
        CorridorPricingResult result = CorridorPricing.Derive(composed, _lane, _underpassColumns, multiplier);
        EnemyPricePerCell = result.PricePerCell;
        LastPricing = result;
        return result;
    }

    /// <summary>
    /// The dual-cost half of the rule: an <see cref="IFlowGrid"/> view of
    /// <paramref name="composed"/> where every lane cell costs
    /// <see cref="EnemyPricePerCell"/> for the enemy flow field — passability
    /// and blood cost forward unchanged, so tearable blockers keep their
    /// semantics. Register it on the enemy lane's bucket via <see
    /// cref="FlowFieldManager.RegisterGridView"/> (pit#3 落点: 每 lane 一视图,
    /// not a hand-built manager per view).
    /// </summary>
    /// <exception cref="ArgumentNullException">composed is null.</exception>
    public IFlowGrid CreateEnemyView(IFlowGrid composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        return new EnemyCorridorView(composed, this);
    }

    /// <summary>
    /// One-call wiring of the dual-cost half onto a
    /// <see cref="FlowFieldManager"/> bucket: registers
    /// <see cref="CreateEnemyView"/> under <paramref name="key"/> so every
    /// (re)build of that bucket — including rebuilds after LRU eviction —
    /// floods the enemy view (pit#3: lane×costType 分桶注册, 视图数随桶数封顶).
    /// </summary>
    /// <exception cref="ArgumentNullException">manager, key or composed is null.</exception>
    public void RegisterEnemyLane(FlowFieldManager manager, FlowFieldKey key, IFlowGrid composed)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(key);
        manager.RegisterGridView(key, CreateEnemyView(composed));
    }

    private List<CellCoord> BuildLiningCells()
    {
        var cells = new List<CellCoord>();
        int yNorthA = _lane.Y - 1;
        int yNorthB = _lane.Y - LiningThickness;
        int ySouthA = _lane.Y + 2;
        int ySouthB = _lane.Y + 1 + LiningThickness;
        for (int x = _lane.X; x < _lane.X + _lane.Width; x++)
        {
            if (IsUnderpassColumn(x))
            {
                continue;
            }

            foreach (int y in new[] { yNorthB, yNorthA, ySouthA, ySouthB })
            {
                var cell = new CellCoord(x, y);
                cells.Add(cell);
                _liningSet.Add(cell);
            }
        }

        return cells;
    }

    private bool IsUnderpassColumn(int x)
    {
        foreach (int u in _underpassColumns)
        {
            if (x >= u && x < u + UnderpassWidth)
            {
                return true;
            }
        }

        return false;
    }

    private void ValidateUnderpasses()
    {
        int x0 = _lane.X;
        int x1 = _lane.X + _lane.Width - 1;
        int previous = int.MinValue;
        foreach (int u in _underpassColumns)
        {
            if (u <= x0 || u + UnderpassWidth - 1 >= x1)
            {
                throw new ArgumentException(
                    $"Underpass column {u} must sit strictly inside the lane ({x0 + 1}..{x1 - UnderpassWidth} as a west column).",
                    "underpassColumns");
            }

            if (u <= previous)
            {
                throw new ArgumentException("Underpass columns must be ascending and distinct.", "underpassColumns");
            }

            previous = u;
        }
    }

    /// <summary>
    /// Enemy-side dual-cost view (secondary type of CorridorRule.cs): lane
    /// cells repriced at the corridor's derived enemy price, everything else
    /// forwarded from the composed grid. Same shape as the spike's
    /// hand-built <c>Spike2EnemyCorridorView</c>, but priced by
    /// <see cref="CorridorPricing"/> and registered through the manager's
    /// formal per-bucket view interface instead of a second manager instance.
    /// </summary>
    private sealed class EnemyCorridorView : IFlowGrid
    {
        private readonly IFlowGrid _composed;
        private readonly CorridorRule _rule;

        public EnemyCorridorView(IFlowGrid composed, CorridorRule rule)
        {
            _composed = composed;
            _rule = rule;
        }

        public int Width => _composed.Width;

        public int Height => _composed.Height;

        public int Cost(CellCoord c) =>
            _rule.IsLaneCell(c) ? _rule.EnemyPricePerCell : _composed.Cost(c);

        public bool Passable(CellCoord c) => _composed.Passable(c);

        public int BloodCost(CellCoord c) => _composed.BloodCost(c);
    }
}
