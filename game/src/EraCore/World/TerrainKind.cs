namespace EraCore.World;

/// <summary>
/// Grid cell terrain (C-05, T-032). E1 terrain set per the task card:
/// plain, forest, rock, water, wall, road. Wall/Road are not generated
/// procedurally — they are placed by gameplay (player walls, roads) via
/// <see cref="GridWorld.SetTerrain"/>. Procedural generation only ever
/// emits Plain/Forest/Rock/Water (see <see cref="GridWorld"/> constructor).
/// </summary>
public enum TerrainKind
{
    /// <summary>Open grassland — default passable terrain, move cost 1.</summary>
    Plain = 0,

    /// <summary>Wooded cell — passable but slower (cost 2, [NEW-原型校准]).</summary>
    Forest = 1,

    /// <summary>Rock formation — hard blocker, never walkable, no tear cost.</summary>
    Rock = 2,

    /// <summary>Water — hard blocker (F-044 tear mode never crosses it).</summary>
    Water = 3,

    /// <summary>Built wall — placed by gameplay, hard blocker for the terrain layer.</summary>
    Wall = 4,

    /// <summary>Paved road — passable, cheapest movement (cost 1).</summary>
    Road = 5,
}

/// <summary>
/// Terrain rules companion of <see cref="TerrainKind"/>: passability and
/// base movement cost. Costs are integers on the <c>EraCore.Pathfinding</c>
/// cost scale (<see cref="EraCore.Pathfinding.FlowField.MinimumCellCost"/> = 1);
/// the flow field clamps anything below 1 back up. Passability here is the
/// TERRAIN layer only — occupancy (buildings) is composed on top by C-09/C-11.
/// </summary>
public static class TerrainInfo
{
    /// <summary>
    /// Whether a unit can walk on this terrain ignoring occupancy.
    /// Rock/Water/Wall are hard blockers; Plain/Forest/Road are walkable.
    /// </summary>
    public static bool IsPassable(TerrainKind kind) =>
        kind is TerrainKind.Plain or TerrainKind.Forest or TerrainKind.Road;

    /// <summary>
    /// Base move cost of entering a cell of this terrain. Impassable kinds
    /// report 1 — the value is meaningless for them (the flow field gates on
    /// <see cref="IsPassable"/> first) and mirrors the C-11 test grid
    /// convention of "cost 1 unless overridden" (MapGrid, T-037).
    /// Forest = 2 is [NEW-原型校准] (slowed movement through woods).
    /// </summary>
    public static int Cost(TerrainKind kind) => kind switch
    {
        TerrainKind.Forest => 2,
        _ => 1,
    };
}
