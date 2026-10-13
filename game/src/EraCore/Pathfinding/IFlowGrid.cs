using EraCore.Contracts.Foundation;

namespace EraCore.Pathfinding;

/// <summary>
/// Read-only grid seam consumed by the flow-field pathfinder (C-11, F-043).
/// Implemented by the grid world component (World.GridWorld, T-032, W1-A
/// lane) once it lands; until then tests provide their own implementations.
/// The interface is frozen early so C-05 and C-11 can proceed in parallel;
/// ownership overlaps at merge time are resolved by the supervisor.
/// </summary>
public interface IFlowGrid
{
    /// <summary>Grid width in cells (X axis, eastward).</summary>
    int Width { get; }

    /// <summary>Grid height in cells (Y axis, southward).</summary>
    int Height { get; }

    /// <summary>
    /// Terrain base cost of entering cell <paramref name="c"/>. Contract:
    /// values below <see cref="FlowField.MinimumCellCost"/> are clamped up to
    /// it (zero-cost edges would break gradient descent termination).
    /// </summary>
    int Cost(CellCoord c);

    /// <summary>
    /// Whether the cell can be walked on without tearing anything.
    /// Hard terrain (water/void) reports false with no blood cost and can
    /// never be crossed; a blocking building may report false while carrying
    /// a positive <see cref="BloodCost"/>, marking it tearable.
    /// </summary>
    bool Passable(CellCoord c);

    /// <summary>
    /// Hit points of the enemy building standing on this cell — the tear
    /// price the flow field charges for cutting through it (v3.2 §6:
    /// 建筑血量 ×5 计代价). Implementations without a building on the cell
    /// return 0; negative values are clamped to 0.
    /// </summary>
    int BloodCost(CellCoord c);
}
