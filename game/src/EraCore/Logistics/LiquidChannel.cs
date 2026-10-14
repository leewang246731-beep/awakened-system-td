using EraCore.Contracts.Foundation;

namespace EraCore.Logistics;

/// <summary>
/// Structured channel family of the liquid logistics (secondary type of
/// LiquidChannel.cs; C-07 T-064 [NEW], F-042): each era's water-work is a
/// typed channel with its own flow-rate constant. E1 scope carries exactly
/// one member — the aqueduct (水渠); further kinds (pipes, pressurized
/// mains) join this enum with their era content.
/// </summary>
public enum LiquidChannelKind
{
    /// <summary>
    /// 水渠 — gravity aqueduct of era E2 water logistics: open channel over
    /// structured cells, flow rate
    /// <see cref="LiquidChannel.AqueductFlowUnitsPerSecond"/>.
    /// </summary>
    Aqueduct = 0,
}

/// <summary>
/// Structured liquid channel of the logistics family (C-07 T-064 [NEW],
/// F-042; 任务卡 "水渠（E2 液体薄版：结构化通道类型+流速常量）"). This is the
/// E1-thin scope of the aqueduct: a typed, cell-anchored channel with the
/// flow-rate constants of its <see cref="LiquidChannelKind"/> — the same
/// "structured channel" shape as the supply corridor
/// (<see cref="CorridorRule"/>) and the mine rail (<see cref="RailLine"/>).
/// The liquid content itself (volume stock, consumption settlement,
/// resource keys of the carried liquid) lands with the E2 era content tasks
/// and will consume <see cref="FlowUnitsPerTick"/> through the same cargo
/// ports as the item tiers; nothing here models it yet, by design of the
/// thin cut.
/// </summary>
public sealed class LiquidChannel
{
    /// <summary>
    /// Aqueduct flow constant ([NEW] 流速常量, v3.2 §5 liquid ladder thin
    /// cut): 2.0 units/s on the fixed 30 Hz tick domain ⇒
    /// <see cref="FlowUnitsPerTick"/> = 1/15 per tick.
    /// </summary>
    public const double AqueductFlowUnitsPerSecond = 2.0;

    /// <summary>Simulation tick rate the per-second constants scale to (K-1).</summary>
    public const double TicksPerSecond = 30.0;

    private readonly LiquidChannelKind _kind;
    private readonly CellCoord[] _cells;

    /// <summary>
    /// Creates a channel of <paramref name="kind"/> over ordered,
    /// 8-neighbour-adjacent cells (contiguous duct — same validation shape
    /// as rail lines and belt chains).
    /// </summary>
    /// <exception cref="ArgumentNullException">cells is null.</exception>
    /// <exception cref="ArgumentException">
    /// fewer than two cells, a non-adjacent step, or a repeated cell.
    /// </exception>
    public LiquidChannel(LiquidChannelKind kind, IReadOnlyList<CellCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count < 2)
        {
            throw new ArgumentException("A liquid channel needs at least two cells.", nameof(cells));
        }

        ValidateAdjacency(cells);
        _cells = cells.ToArray();
        for (int i = 0; i < _cells.Length; i++)
        {
            for (int j = i + 1; j < _cells.Length; j++)
            {
                if (_cells[i].Equals(_cells[j]))
                {
                    throw new ArgumentException("A liquid channel must not repeat a cell.", nameof(cells));
                }
            }
        }

        _kind = kind;
    }

    /// <summary>The channel's family kind (flow-rate class).</summary>
    public LiquidChannelKind Kind => _kind;

    /// <summary>The channel cells, upstream → downstream.</summary>
    public IReadOnlyList<CellCoord> Cells => _cells;

    /// <summary>Flow-rate constant of the kind, units per second.</summary>
    public double FlowUnitsPerSecond => Kind switch
    {
        LiquidChannelKind.Aqueduct => AqueductFlowUnitsPerSecond,
        _ => throw new InvalidOperationException($"Unknown liquid channel kind {Kind}."),
    };

    /// <summary>Flow-rate constant scaled to the tick domain: units per tick.</summary>
    public double FlowUnitsPerTick => FlowUnitsPerSecond / TicksPerSecond;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Content hash of the channel structure (kind + every cell, FNV-1a 64):
    /// identical construction yields identical hashes — the determinism probe
    /// for replay checks once liquid volume content attaches in E2.
    /// </summary>
    public long ComputeStateHash()
    {
        unchecked
        {
            ulong h = FnvOffset;
            h = Mix(h, (ulong)(int)Kind);
            h = Mix(h, (ulong)_cells.Length);
            foreach (CellCoord cell in _cells)
            {
                h = Mix(h, (ulong)cell.X);
                h = Mix(h, (ulong)cell.Y);
            }

            return (long)h;
        }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (value >> shift) & 0xFFUL;
            hash *= FnvPrime;
        }

        return hash;
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
                    $"Channel step {i - 1}→{i} ({cells[i - 1].X},{cells[i - 1].Y})→({cells[i].X},{cells[i].Y}) is not an 8-neighbour step.",
                    nameof(cells));
            }
        }
    }
}
