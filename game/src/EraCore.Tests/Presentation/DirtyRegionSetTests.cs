using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-21] chunk-grained dirty tracking behind the MultiMesh partial rebuild:
// cell→chunk mapping across 32-cell chunk boundaries, idempotency, drain
// semantics and out-of-grid rejection.
public class DirtyRegionSetTests
{
    private const int Width = 64;
    private const int Height = 48; // 1.5 chunks tall → 2×2 chunk grid, edge chunk partial

    private static DirtyRegionSet Make(int width = Width, int height = Height, int chunkCells = 32) =>
        new(width, height, chunkCells);

    // ---- legal — marking and draining --------------------------------------

    [Fact] // legal — a marked cell surfaces as its owning chunk on drain
    public void Marked_cell_drains_as_its_chunk()
    {
        var set = Make();
        set.MarkDirty(new CellCoord(5, 7));

        var drained = set.TakeDirtyChunks();

        var chunk = Assert.Single(drained);
        Assert.Equal((0, 0), chunk);
        Assert.Equal(0, set.DirtyChunkCount);
    }

    [Fact] // legal — two cells in the same chunk coalesce into one dirty chunk
    public void Cells_in_same_chunk_coalesce()
    {
        var set = Make();
        set.MarkDirty(0, 0);
        set.MarkDirty(31, 31);

        var drained = set.TakeDirtyChunks();

        var chunk = Assert.Single(drained);
        Assert.Equal((0, 0), chunk);
    }

    [Fact] // legal — repeated marks of the same chunk are idempotent
    public void Duplicate_marks_are_idempotent()
    {
        var set = Make();
        for (int i = 0; i < 5; i++)
        {
            set.MarkDirty(10, 10);
            set.MarkChunkDirty(0, 0);
        }

        Assert.Equal(1, set.DirtyChunkCount);
        Assert.Single(set.TakeDirtyChunks());
    }

    [Fact] // legal — drain hands each chunk to the renderer exactly once per frame
    public void Drain_clears_and_next_drain_is_empty()
    {
        var set = Make();
        set.MarkDirty(1, 1);
        set.MarkDirty(33, 33);
        Assert.Equal(2, set.DirtyChunkCount);

        var first = set.TakeDirtyChunks();
        set.MarkDirty(2, 2); // a frame-2 change lands in the next drain window

        Assert.Equal(2, first.Count);
        var second = set.TakeDirtyChunks();
        var chunk = Assert.Single(second);
        Assert.Equal((0, 0), chunk);
        Assert.Empty(set.TakeDirtyChunks()); // fully drained
    }

    [Fact] // legal — MarkAllDirty covers the whole chunk grid (initial full build)
    public void MarkAll_dirty_covers_chunk_grid()
    {
        var set = Make(); // 64/32 × ceil(48/32) = 2 × 2

        set.MarkAllDirty();

        Assert.Equal(4, set.DirtyChunkCount);
        Assert.Equal(4, set.TakeDirtyChunks().Count);
    }

    // ---- boundary — chunk edges and partial edge chunks --------------------

    [Theory] // boundary — the 32-cell chunk seam routes neighbors to different chunks
    [InlineData(31, 31, 0, 0)]
    [InlineData(32, 31, 1, 0)]
    [InlineData(31, 32, 0, 1)]
    [InlineData(32, 32, 1, 1)]
    [InlineData(63, 47, 1, 1)] // last cell of the map lives in the partial edge chunk
    [InlineData(0, 47, 0, 1)]  // partial edge chunk spans cells 32..47 only
    public void Chunk_seam_maps_boundary_cells(int cellX, int cellY, int chunkX, int chunkY)
    {
        var set = Make();

        Assert.Equal((chunkX, chunkY), set.ChunkOf(cellX, cellY));
        Assert.Equal((chunkX, chunkY), set.ChunkOf(new CellCoord(cellX, cellY)));
    }

    [Fact] // boundary — non-default chunk edge changes the chunk grid geometry
    public void Custom_chunk_edge_regrids()
    {
        var set = Make(chunkCells: 16); // 64/16 × 48/16 = 4 × 3

        Assert.Equal(4, set.ChunkCountX);
        Assert.Equal(3, set.ChunkCountY);
        Assert.Equal((3, 2), set.ChunkOf(63, 47));
        Assert.Equal((1, 0), set.ChunkOf(16, 15));
    }

    [Fact] // boundary — ceil division keeps a ragged height inside the last chunk row
    public void Ragged_height_uses_ceiling_chunk_count()
    {
        var set = Make(width: 33, height: 32); // 33 cells wide → 2 chunk columns

        Assert.Equal(2, set.ChunkCountX);
        Assert.Equal(1, set.ChunkCountY);
        set.MarkDirty(32, 31);
        Assert.Equal((1, 0), Assert.Single(set.TakeDirtyChunks()));
    }

    // ---- illegal — out-of-grid cells and bad geometry rejected --------------

    [Theory] // illegal — cells outside the tracked grid are refused
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(64, 0)]  // width is 64 → valid X is [0,63]
    [InlineData(0, 48)]  // height is 48 → valid Y is [0,47]
    public void Out_of_grid_cell_is_rejected(int cellX, int cellY)
    {
        var set = Make();
        Assert.Throws<ArgumentException>(() => set.MarkDirty(cellX, cellY));
    }

    [Theory] // illegal — chunks outside the chunk grid are refused
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    public void Out_of_grid_chunk_is_rejected(int chunkX, int chunkY)
    {
        var set = Make();
        Assert.Throws<ArgumentException>(() => set.MarkChunkDirty(chunkX, chunkY));
    }

    [Theory] // illegal — non-positive geometry is refused at construction
    [InlineData(0, 48, 32)]
    [InlineData(64, 0, 32)]
    [InlineData(-1, 48, 32)]
    [InlineData(64, 48, 0)]
    [InlineData(64, 48, -8)]
    public void Constructor_rejects_invalid_geometry(int width, int height, int chunkCells)
    {
        Assert.Throws<ArgumentException>(() => Make(width, height, chunkCells));
    }
}
