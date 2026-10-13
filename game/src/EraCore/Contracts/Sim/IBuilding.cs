// T2: pure contract — K-6 sim-side draft (T-023 remainder).

using EraCore.Contracts.Foundation;

namespace EraCore.Contracts.Sim;

/// <summary>
/// A placed building — tower, production, storage or wall (K-6 draft).
/// Occupancy and HP are registered with the grid world (C-05); building HP
/// feeds the flow-field path cost at ×5 (v3.2 §6). Tower classification
/// follows the six classes frozen by T-007 (physical/pierce/splash/energy/
/// slow/support).
/// </summary>
public interface IBuilding
{
    /// <summary>Stable identity within the current level run.</summary>
    EntityId Id { get; }

    /// <summary>Content id of the building definition (K-2 registry).</summary>
    string ContentId { get; }

    /// <summary>Occupied grid cell, 1 cell = 32 px (32×32-cell chunks, C-05).</summary>
    CellCoord Cell { get; }

    /// <summary>Current hit points.</summary>
    int Hp { get; }

    /// <summary>Maximum hit points.</summary>
    int MaxHp { get; }

    /// <summary>Tier 1..4 (DPS ×2 per tier ±15%, total cost ×3 per tier — v3.2 §6).</summary>
    int Tier { get; }
}
