using EraCore.Contracts.Sim;
using EraCore.Presentation;

namespace EraTD;

// T-042 (C-21) adapter layer + the Wave-2 mock→real swap (T-055).
//
// REPLACEMENT RECORD (the mock producer is gone): through Wave 1 this file
// also carried DeterministicMockSnapshotSource, a presentation development
// fixture granted by 03-decomposition ("mock 快照可提前动工"). T-055 (C-22)
// replaced it with the REAL producer: SimHost (scripts/SimHost.cs) builds a
// live C-12 Simulation and hands the bridge its two adapters —
// ISimSnapshotSource → Simulation.Snapshots.Latest (K-6 double buffer) and
// IFramebufferProvider → the terrain map. The two ports below are the only
// seam that survived; the bridge and the dirty-chunk render path are
// untouched. Main.cs carries the second replacement note at the call site.

/// <summary>
/// Port: read-only view of the latest double-buffered <see cref="SimSnapshot"/>
/// (K-6 producer seam). The Godot layer polls this every render frame and
/// never mutates simulation state through it (03-decomposition §1.2-3
/// snapshot principle).
/// </summary>
public interface ISimSnapshotSource
{
    /// <summary>Latest published snapshot, or null before the first tick.</summary>
    SimSnapshot? Latest { get; }
}

/// <summary>
/// Port: ground-layer cell color view (terrain + floor tint) feeding the
/// chunked MultiMesh ground renderer. Cell colors change rarely (terrain
/// edit, building placement), which is why the ground layer is driven by a
/// coarse <see cref="IFramebufferProvider.FramebufferVersion"/> bump rather
/// than per-cell diffs.
/// </summary>
public interface IFramebufferProvider
{
    /// <summary>Ground grid width in cells.</summary>
    int WidthCells { get; }

    /// <summary>Ground grid height in cells.</summary>
    int HeightCells { get; }

    /// <summary>Monotonic content version; bump ⇒ renderer rebuilds all chunks.</summary>
    long FramebufferVersion { get; }

    /// <summary>Cell color as 0xRRGGBB at cell (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">Cell outside the grid.</exception>
    int ColorAt(int x, int y);
}

/// <summary>
/// C# adapter between the simulation seam and the Godot render skeleton
/// (03-decomposition §1.2 layer-3 adapters). Owns the two dirty trackers and
/// the reverse command queue:
/// <list type="bullet">
/// <item>forward: snapshot + framebuffer → chunk-level dirty sets (the
/// MultiMesh renderers drain them once per frame and rebuild only dirty
/// chunks);</item>
/// <item>reverse: every presentation mutation is queued as a
/// <see cref="SimCommand"/> — the only path into the deterministic core
/// (§1.2-3); the C-12 orchestrator consumes this queue from Wave 2 on
/// (T-053/T-056 wire the real handlers).</item>
/// </list>
/// </summary>
public sealed class SimSnapshotBridge
{
    /// <summary>1 grid cell = 32 px (C-05 world convention).</summary>
    public const int CellSizePx = 32;

    private readonly ISimSnapshotSource _source;
    private readonly IFramebufferProvider _framebuffer;
    private readonly Queue<SimCommand> _commands = new();
    private SimSnapshot? _previous;
    private long _lastFramebufferVersion = -1;

    public SimSnapshotBridge(
        ISimSnapshotSource source,
        IFramebufferProvider framebuffer,
        int widthCells,
        int heightCells,
        int chunkCells = DirtyRegionSet.DefaultChunkCells)
    {
        _source = source;
        _framebuffer = framebuffer;
        GroundDirty = new DirtyRegionSet(widthCells, heightCells, chunkCells);
        UnitDirty = new DirtyRegionSet(widthCells, heightCells, chunkCells);
    }

    /// <summary>Ground-layer chunk dirty tracker (version-driven).</summary>
    public DirtyRegionSet GroundDirty { get; }

    /// <summary>Unit-layer chunk dirty tracker (position-diff-driven).</summary>
    public DirtyRegionSet UnitDirty { get; }

    /// <summary>Snapshot the renderers should currently show (null before the first tick).</summary>
    public SimSnapshot? Current { get; private set; }

    /// <summary>Commands queued by the presentation layer, not yet consumed by the core.</summary>
    public int PendingCommandCount => _commands.Count;

    /// <summary>
    /// One render frame of adapter work: reads the latest snapshot, diffs it
    /// against the previously observed one and marks the owning chunks dirty.
    /// Call once per frame, BEFORE the renderers drain the dirty sets.
    /// </summary>
    public void SyncFrame()
    {
        // Ground: coarse version bump ⇒ full chunk rebuild (cell colors are
        // chunk-static between bumps, so per-cell diffing would be waste).
        if (_framebuffer.FramebufferVersion != _lastFramebufferVersion)
        {
            GroundDirty.MarkAllDirty();
            _lastFramebufferVersion = _framebuffer.FramebufferVersion;
        }

        SimSnapshot? snapshot = _source.Latest;
        if (snapshot is not null)
        {
            if (_previous is null)
            {
                UnitDirty.MarkAllDirty(); // first frame: everything is new
            }
            else
            {
                foreach (IUnit unit in _previous.Units)
                {
                    MarkUnitChunk(unit);
                }
            }

            foreach (IUnit unit in snapshot.Units)
            {
                MarkUnitChunk(unit);
            }

            _previous = snapshot;
        }

        Current = snapshot;
    }

    /// <summary>Drains the ground-layer dirty chunks for this frame's rebuild.</summary>
    public IReadOnlyCollection<(int ChunkX, int ChunkY)> TakeDirtyGroundChunks() =>
        GroundDirty.TakeDirtyChunks();

    /// <summary>Drains the unit-layer dirty chunks for this frame's rebuild.</summary>
    public IReadOnlyCollection<(int ChunkX, int ChunkY)> TakeDirtyUnitChunks() =>
        UnitDirty.TakeDirtyChunks();

    /// <summary>Reverse seam: queues the only legal way back into the core (§1.2-3).</summary>
    public void EnqueueCommand(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Enqueue(command);
    }

    /// <summary>Hands the queued commands to the simulation side (C-12 consumer, Wave 2+).</summary>
    public IReadOnlyList<SimCommand> DrainCommands()
    {
        if (_commands.Count == 0)
        {
            return Array.Empty<SimCommand>();
        }

        var drained = _commands.ToArray();
        _commands.Clear();
        return drained;
    }

    private void MarkUnitChunk(IUnit unit)
    {
        // Pixel→cell→chunk; clamped at the seam so a float position resting
        // exactly on the border pixel cannot address a cell outside the grid.
        int cellX = Math.Clamp((int)(unit.X / CellSizePx), 0, GroundDirty.WidthCells - 1);
        int cellY = Math.Clamp((int)(unit.Y / CellSizePx), 0, GroundDirty.HeightCells - 1);
        UnitDirty.MarkDirty(new EraCore.Contracts.Foundation.CellCoord(cellX, cellY));
    }
}
