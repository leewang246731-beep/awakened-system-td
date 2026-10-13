using EraCore.Contracts.Foundation;

namespace EraCore.Presentation;

/// <summary>
/// Chunk-grained dirty tracking that drives MultiMesh partial rebuilds
/// (C-21). The cell grid is partitioned into 32×32-cell chunks (matching
/// the C-05 chunking, 1 cell = 32 px): <see cref="MarkDirty(CellCoord)"/>
/// maps a changed cell to its owning chunk, and the renderer drains the set
/// at end of frame via <see cref="TakeDirtyChunks"/> so each chunk is
/// rebuilt at most once per frame. Pure collection logic, zero engine types
/// (D-01) — the Godot side owns the actual MultiMesh resources.
/// </summary>
public sealed class DirtyRegionSet
{
    /// <summary>Chunk edge in cells (32×32 cells = 1024×1024 px per chunk).</summary>
    public const int DefaultChunkCells = 32;

    private readonly HashSet<(int ChunkX, int ChunkY)> _dirty = new();

    /// <summary>Tracked grid width in cells.</summary>
    public int WidthCells { get; }

    /// <summary>Tracked grid height in cells.</summary>
    public int HeightCells { get; }

    /// <summary>Chunk edge in cells.</summary>
    public int ChunkCells { get; }

    /// <summary>Number of chunks along X (ceil division; edge chunk may be partial).</summary>
    public int ChunkCountX { get; }

    /// <summary>Number of chunks along Y (ceil division; edge chunk may be partial).</summary>
    public int ChunkCountY { get; }

    /// <summary>Chunks currently marked dirty.</summary>
    public int DirtyChunkCount => _dirty.Count;

    public DirtyRegionSet(int widthCells, int heightCells, int chunkCells = DefaultChunkCells)
    {
        if (widthCells <= 0)
        {
            throw new ArgumentException($"width must be positive, got {widthCells}.", nameof(widthCells));
        }

        if (heightCells <= 0)
        {
            throw new ArgumentException($"height must be positive, got {heightCells}.", nameof(heightCells));
        }

        if (chunkCells <= 0)
        {
            throw new ArgumentException($"chunk edge must be positive, got {chunkCells}.", nameof(chunkCells));
        }

        WidthCells = widthCells;
        HeightCells = heightCells;
        ChunkCells = chunkCells;
        ChunkCountX = DivideCeiling(widthCells, chunkCells);
        ChunkCountY = DivideCeiling(heightCells, chunkCells);
    }

    /// <summary>Owning chunk of a cell (the only mapping rule the renderer and the bridge must agree on).</summary>
    public (int ChunkX, int ChunkY) ChunkOf(CellCoord cell) => ChunkOf(cell.X, cell.Y);

    /// <summary>Owning chunk of a cell (the only mapping rule the renderer and the bridge must agree on).</summary>
    public (int ChunkX, int ChunkY) ChunkOf(int cellX, int cellY)
    {
        if (cellX < 0 || cellX >= WidthCells)
        {
            throw new ArgumentException($"cell X {cellX} outside tracked grid [0,{WidthCells}).", nameof(cellX));
        }

        if (cellY < 0 || cellY >= HeightCells)
        {
            throw new ArgumentException($"cell Y {cellY} outside tracked grid [0,{HeightCells}).", nameof(cellY));
        }

        return (cellX / ChunkCells, cellY / ChunkCells);
    }

    /// <summary>Marks the chunk owning <paramref name="cell"/> dirty (idempotent).</summary>
    public void MarkDirty(CellCoord cell) => MarkDirty(cell.X, cell.Y);

    /// <summary>Marks the chunk owning cell (<paramref name="cellX"/>, <paramref name="cellY"/>) dirty (idempotent).</summary>
    public void MarkDirty(int cellX, int cellY)
    {
        _dirty.Add(ChunkOf(cellX, cellY));
    }

    /// <summary>Marks the chunk (<paramref name="chunkX"/>, <paramref name="chunkY"/>) dirty directly (idempotent).</summary>
    public void MarkChunkDirty(int chunkX, int chunkY)
    {
        if (chunkX < 0 || chunkX >= ChunkCountX)
        {
            throw new ArgumentException($"chunk X {chunkX} outside chunk grid [0,{ChunkCountX}).", nameof(chunkX));
        }

        if (chunkY < 0 || chunkY >= ChunkCountY)
        {
            throw new ArgumentException($"chunk Y {chunkY} outside chunk grid [0,{ChunkCountY}).", nameof(chunkY));
        }

        _dirty.Add((chunkX, chunkY));
    }

    /// <summary>Marks every chunk dirty (initial full build, resize, mode switch).</summary>
    public void MarkAllDirty()
    {
        _dirty.Clear();
        for (int y = 0; y < ChunkCountY; y++)
        {
            for (int x = 0; x < ChunkCountX; x++)
            {
                _dirty.Add((x, y));
            }
        }
    }

    /// <summary>
    /// Drains the dirty set: returns the chunks marked since the previous
    /// drain and clears the set. End-of-frame hand-off — the caller rebuilds
    /// exactly the returned chunks.
    /// </summary>
    public IReadOnlyCollection<(int ChunkX, int ChunkY)> TakeDirtyChunks()
    {
        if (_dirty.Count == 0)
        {
            return Array.Empty<(int, int)>();
        }

        var taken = _dirty.ToArray();
        _dirty.Clear();
        return taken;
    }

    private static int DivideCeiling(int value, int divisor) => (value + divisor - 1) / divisor;
}
