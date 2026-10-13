// T2: pure contract — K-1 domain primitive, T-019 remainder, frozen (round 2 backfill).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Grid cell coordinate (K-1 domain primitive, 03-decomposition §6). One
/// cell = 32 px; the world is partitioned into 32×32-cell chunks (C-05).
/// Pure value type with structural equality.
/// </summary>
/// <param name="X">Cell column, 0-based, eastward.</param>
/// <param name="Y">Cell row, 0-based, southward.</param>
public sealed record CellCoord(int X, int Y);
