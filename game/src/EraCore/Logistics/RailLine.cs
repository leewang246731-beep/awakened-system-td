using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.Logistics;

/// <summary>
/// Guided rail transport of ladder tier 3 矿轨 (C-07 全量, T-064, F-042;
/// [NEW 细节标注] 任务卡口径 "沿轨道直线高速——薄版先做『轨道格连续通行时
/// 10 件/s、断轨降为马车速』规则"). A rail line owns its ordered track cells
/// (typically a paved <see cref="EraCore.World.TerrainKind.Road"/> row or the
/// supply-corridor lane) and doubles as the carrier's <see
/// cref="ITierSpeedPolicy"/>: while EVERY remaining track cell between the
/// carrier and its phase target is passable in the composed grid view, the
/// carrier runs at the rail tier speed (16 px/tick ⇒ 10 件/s @160 px); the
/// moment any remaining track cell is blocked (occupancy, wall — 断轨), the
/// speed degrades to the cart tier's 3.5 px/tick for as long as the break
/// stands. The movement layer itself never checks passability (same logical
/// layer as spike2 pit#6: the carrier crawls across the broken section at
/// cart speed instead of rerouting; product rerouting stays the C-12
/// orchestrator's job via <see cref="Carrier.RouteInvalidated"/>).
/// <para>
/// Determinism (CS-1.4): the speed answer is a pure function of (carrier
/// phase/cell, track passability) — no clock, no RNG; the same world state
/// always yields the same speed, so replays are bit-identical.
/// </para>
/// </summary>
public sealed class RailLine : ITierSpeedPolicy
{
    private readonly IFlowGrid _passability;
    private readonly TransportTier _railTier;
    private readonly TransportTier _degradedTier;
    private readonly Dictionary<CellCoord, int> _firstIndex;
    private readonly CellCoord[] _cells;

    /// <summary>
    /// Creates a rail line over ordered, 8-neighbour-adjacent track cells.
    /// <paramref name="passability"/> is the composed grid view the integrity
    /// rule reads (a cell counts as broken when the view reports it
    /// impassable); <paramref name="railTier"/> defaults to
    /// <see cref="TransportTiers.MineRail"/> and
    /// <paramref name="degradedTier"/> to <see cref="TransportTiers.Cart"/>
    /// (断轨降为马车速).
    /// </summary>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// fewer than two cells, a non-adjacent step, or a repeated cell.
    /// </exception>
    public RailLine(
        IReadOnlyList<CellCoord> railCells,
        IFlowGrid passability,
        TransportTier? railTier = null,
        TransportTier? degradedTier = null)
    {
        ArgumentNullException.ThrowIfNull(railCells);
        ArgumentNullException.ThrowIfNull(passability);
        if (railCells.Count < 2)
        {
            throw new ArgumentException("A rail line needs at least two cells.", nameof(railCells));
        }

        ValidateAdjacency(railCells);
        _cells = railCells.ToArray();
        _firstIndex = new Dictionary<CellCoord, int>(_cells.Length);
        for (int i = 0; i < _cells.Length; i++)
        {
            if (!_firstIndex.ContainsKey(_cells[i]))
            {
                _firstIndex.Add(_cells[i], i);
            }
        }

        if (_firstIndex.Count != _cells.Length)
        {
            throw new ArgumentException("A rail line must not repeat a cell.", nameof(railCells));
        }

        _passability = passability;
        _railTier = railTier ?? TransportTiers.MineRail;
        _degradedTier = degradedTier ?? TransportTiers.Cart;
    }

    /// <summary>The ordered track cells (start → end).</summary>
    public IReadOnlyList<CellCoord> Cells => _cells;

    /// <summary>The tier speed granted while the remaining track is intact.</summary>
    public double RailSpeedPxPerTick => _railTier.SpeedPxPerTick;

    /// <summary>The degraded tier speed while a break stands ahead.</summary>
    public double DegradedSpeedPxPerTick => _degradedTier.SpeedPxPerTick;

    /// <summary>Whether the cell belongs to this track.</summary>
    public bool IsRailCell(CellCoord cell) => _firstIndex.ContainsKey(cell);

    /// <summary>
    /// Whether every track cell between <paramref name="fromIndex"/> and
    /// <paramref name="toIndex"/> (inclusive, either order) is passable in
    /// the composed view — the integrity probe the speed rule reads.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">an index is outside [0, Cells.Count).</exception>
    public bool IsIntact(int fromIndex, int toIndex)
    {
        Guard.EnsureInRange(fromIndex, 0, _cells.Length - 1, nameof(fromIndex));
        Guard.EnsureInRange(toIndex, 0, _cells.Length - 1, nameof(toIndex));
        int step = toIndex >= fromIndex ? 1 : -1;
        for (int i = fromIndex; i != toIndex + step; i += step)
        {
            if (!_passability.Passable(_cells[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="ITierSpeedPolicy"/> realization (queried once per movement
    /// tick by <see cref="Carrier"/>): rail speed while the remaining track
    /// from the carrier's current cell to its phase target is intact; cart
    /// speed otherwise — including the safe fallbacks (carrier not in a
    /// movement phase, current cell or target off the track, e.g. after an
    /// orchestrator reroute took it overland), which count as "broken ahead".
    /// </summary>
    public double EffectiveSpeedPxPerTick(Carrier carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        CellCoord target = carrier.Phase == CarrierPhase.Outbound ? carrier.Destination : carrier.Home;
        if (!_firstIndex.TryGetValue(carrier.CurrentCell, out int from) ||
            !_firstIndex.TryGetValue(target, out int to))
        {
            return DegradedSpeedPxPerTick;
        }

        return IsIntact(from, to) ? RailSpeedPxPerTick : DegradedSpeedPxPerTick;
    }

    private static void ValidateAdjacency(IReadOnlyList<CellCoord> cells)
    {
        for (int i = 1; i < cells.Count; i++)
        {
            int dx = Math.Abs(cells[i].X - cells[i - 1].X);
            int dy = Math.Abs(cells[i].Y - cells[i - 1].Y);
            if (dx > 1 || dy > 1 || (dx == 0 && dy == 0))
            {
                throw new ArgumentException(
                    $"Rail step {i - 1}→{i} ({cells[i - 1].X},{cells[i - 1].Y})→({cells[i].X},{cells[i].Y}) is not an 8-neighbour step.",
                    nameof(cells));
            }
        }
    }
}
