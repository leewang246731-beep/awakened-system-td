using System.Collections.Generic;
using EraCore.Contracts.Sim;
using Godot;

namespace EraTD;

/// <summary>
/// Chunked MultiMesh renderer (C-21): one node instance per render layer.
/// Instance slots are indexed by cell (ground layer) or by stable
/// per-entity slot (unit layer); every frame the renderer receives ONLY the
/// dirty chunks drained from <see cref="SimSnapshotBridge"/> and rewrites
/// the instance slots those chunks own — the untouched chunks keep their
/// previous buffer contents (dirty-mark partial update, 03-decomposition
/// §1.2-3 / R-E4 mitigation).
///
/// E1 art: quads are SOLID COLOR because no texture atlas exists yet —
/// T-045 swaps the QuadMesh for the CC0 atlas sprites; the chunk-rebuild
/// path below does not change with that swap.
/// </summary>
public sealed partial class MultiMeshRenderer : MultiMeshInstance2D
{
    private const float QuadCenterOffset = 0.5f; // QuadMesh is centered: +16 px at 32 px cells

    private LayerKind _kind;
    private int _mapWidthCells;
    private int _mapHeightCells;
    private int _chunkCells;
    private readonly Dictionary<long, int> _unitSlots = new(); // EntityId.Value → instance slot
    private int _unitSlotsUsed;
    private int _unitCapacity;

    /// <summary>What this node renders. Ground = one quad per cell; Units = one quad per entity.</summary>
    public enum LayerKind
    {
        GroundCells,
        Units,
    }

    /// <summary>Configures this node as the ground (cell) layer.</summary>
    public void SetupGround(int mapWidthCells, int mapHeightCells, int chunkCells)
    {
        _kind = LayerKind.GroundCells;
        _mapWidthCells = mapWidthCells;
        _mapHeightCells = mapHeightCells;
        _chunkCells = chunkCells;

        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            Mesh = MakeCellQuad(),
            UseColors = true,
            InstanceCount = mapWidthCells * mapHeightCells,
        };
    }

    /// <summary>
    /// Configures this node as the unit (entity) layer. Map/chunk geometry
    /// is required for chunk-membership tests during dirty rebuilds.
    /// <paramref name="capacity"/> fixes the MultiMesh instance budget; the
    /// E1 mock world has a constant unit population, so slot recycling is
    /// out of skeleton scope (full-rebuild path covers later growth).
    /// </summary>
    public void SetupUnits(int mapWidthCells, int mapHeightCells, int chunkCells, int capacity)
    {
        _kind = LayerKind.Units;
        _mapWidthCells = mapWidthCells;
        _mapHeightCells = mapHeightCells;
        _chunkCells = chunkCells;
        _unitCapacity = capacity;

        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            Mesh = MakeCellQuad(),
            UseColors = true,
            InstanceCount = capacity,
        };

        HideAllUnitSlots();
    }

    /// <summary>
    /// Rewrites the ground-cell instances owned by <paramref name="dirtyChunks"/>
    /// (slot = cellY*mapWidth + cellX; partial edge chunks clamp to map bounds).
    /// </summary>
    public void RebuildGroundChunks(
        IReadOnlyCollection<(int ChunkX, int ChunkY)> dirtyChunks, IFramebufferProvider framebuffer)
    {
        if (_kind != LayerKind.GroundCells)
        {
            throw new InvalidOperationException("RebuildGroundChunks on a non-ground layer.");
        }

        float cell = SimSnapshotBridge.CellSizePx;
        foreach ((int chunkX, int chunkY) in dirtyChunks)
        {
            int x0 = chunkX * _chunkCells;
            int y0 = chunkY * _chunkCells;
            int x1 = Math.Min(x0 + _chunkCells, _mapWidthCells);
            int y1 = Math.Min(y0 + _chunkCells, _mapHeightCells);

            for (int cellY = y0; cellY < y1; cellY++)
            {
                for (int cellX = x0; cellX < x1; cellX++)
                {
                    int slot = cellY * _mapWidthCells + cellX;
                    Multimesh.SetInstanceTransform2D(
                        slot, CellTransform(cellX * cell, cellY * cell, cell));
                    Multimesh.SetInstanceColor(slot, RgbColor(framebuffer.ColorAt(cellX, cellY)));
                }
            }
        }
    }

    /// <summary>
    /// Rewrites the unit instances that currently live inside
    /// <paramref name="dirtyChunks"/> (chunk membership via cell position;
    /// units outside the dirty set keep last frame's transform and color).
    /// </summary>
    public void RebuildUnitChunks(
        IReadOnlyCollection<(int ChunkX, int ChunkY)> dirtyChunks, SimSnapshot snapshot)
    {
        if (_kind != LayerKind.Units)
        {
            throw new InvalidOperationException("RebuildUnitChunks on a non-unit layer.");
        }

        var dirty = new HashSet<(int, int)>(dirtyChunks);
        float cell = SimSnapshotBridge.CellSizePx;
        foreach (IUnit unit in snapshot.Units)
        {
            int cellX = Math.Clamp((int)(unit.X / cell), 0, _mapWidthCells - 1);
            int cellY = Math.Clamp((int)(unit.Y / cell), 0, _mapHeightCells - 1);
            if (!dirty.Contains((cellX / _chunkCells, cellY / _chunkCells)))
            {
                continue;
            }

            int slot = UnitSlot(unit.Id.Value);
            Multimesh.SetInstanceTransform2D(slot, CellTransform(unit.X, unit.Y, cell));
            float hpRatio = unit.MaxHp > 0 ? (float)unit.Hp / unit.MaxHp : 0f;
            Multimesh.SetInstanceColor(slot, new Color(1f - hpRatio, 0.35f + 0.5f * hpRatio, 0.25f));
        }
    }

    private static QuadMesh MakeCellQuad() => new()
    {
        Size = new Vector2(SimSnapshotBridge.CellSizePx, SimSnapshotBridge.CellSizePx),
    };

    /// <summary>Quad at (<paramref name="x"/>, <paramref name="y"/>) sized like one cell (centered mesh ⇒ +cell/2).</summary>
    private static Transform2D CellTransform(float x, float y, float cell) =>
        new(
            0f,                                   // rotation
            new Vector2(x + cell * QuadCenterOffset, y + cell * QuadCenterOffset)); // translation

    private static Color RgbColor(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f);

    private int UnitSlot(long entityIdValue)
    {
        if (_unitSlots.TryGetValue(entityIdValue, out int slot))
        {
            return slot;
        }

        if (_unitSlotsUsed >= _unitCapacity)
        {
            // Fixed-budget skeleton: overflow would need slot recycling —
            // the mock population is constant, so this guards misuse loudly.
            throw new InvalidOperationException(
                $"unit layer capacity {_unitCapacity} exhausted; raise capacity or add slot recycling.");
        }

        slot = _unitSlotsUsed++;
        _unitSlots[entityIdValue] = slot;
        return slot;
    }

    private void HideAllUnitSlots()
    {
        Transform2D hidden = Transform2D.Identity.Scaled(Vector2.Zero);
        for (int i = 0; i < _unitCapacity; i++)
        {
            Multimesh.SetInstanceTransform2D(i, hidden);
        }
    }
}
