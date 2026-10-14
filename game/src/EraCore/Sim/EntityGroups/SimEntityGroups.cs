using EraCore.Economy;
using EraCore.Logistics;
using EraCore.Units;

namespace EraCore.Sim.EntityGroups;

/// <summary>
/// The simulation's typed entity-group registry (C-12 T-067, F-155; G.1 条 2
/// 类型化分组): every orchestrator-owned entity collection is an
/// <see cref="EntityGroup{T}"/> — dense array, append-on-register,
/// swap-remove on retire — instead of bare <c>List&lt;T&gt;</c> fields.
/// <para>
/// The T-067 card names the four cross-cutting families: <b>Units</b> (the
/// unit layer — enemies here, heroes in the same family's typed
/// <see cref="Heroes"/> slot, since <see cref="Unit"/> and <see cref="Hero"/>
/// are distinct handwritten classes and typed grouping groups BY type),
/// <b>Projectiles</b> (the ballistic shadow layer,
/// <see cref="SimProjectile"/>), <b>Buildings</b> (placement-order
/// bookkeeping rows, <see cref="SimBuilding"/>) and <b>Carriers</b> (porter
/// trips of every ladder tier). The per-subsystem building views
/// (<see cref="Towers"/>/<see cref="Producers"/>/<see cref="Converters"/>)
/// are typed groups of the same discipline holding the engine objects the
/// tick loops advance.
/// </para>
/// <para>
/// Registration order is deterministic (commands apply in stream order,
/// waves spawn in plan order), so every group iterates in a replay-stable
/// order and <see cref="Simulation.ComputeStateHash"/> may fold groups
/// directly. The optional quadtree seam (<see cref="ISpatialIndex{T}"/>) is
/// attached per family when a spatial query consumer appears.
/// </para>
/// </summary>
public sealed class SimEntityGroups
{
    /// <summary>Unit layer — wave enemies in spawn order (Units family, enemies).</summary>
    public EntityGroup<Unit> Units { get; } = new();

    /// <summary>Unit layer — deployed heroes (Units family, hero slot; at most one per level in SP-6).</summary>
    public EntityGroup<Hero> Heroes { get; } = new();

    /// <summary>Ballistic shadow layer — in-flight projectile twins in fire order (K-6 载体槽).</summary>
    public EntityGroup<SimProjectile> Projectiles { get; } = new();

    /// <summary>Buildings — placement-order bookkeeping rows of every placed building.</summary>
    public EntityGroup<SimBuilding> Buildings { get; } = new();

    /// <summary>Weapon towers — engine objects of the combat tick, placement order.</summary>
    public EntityGroup<SimTower> Towers { get; } = new();

    /// <summary>Harvest buildings — engine objects of the economy tick, placement order.</summary>
    public EntityGroup<Producer> Producers { get; } = new();

    /// <summary>Ammo converters — engine objects of the economy tick, placement order.</summary>
    public EntityGroup<AmmoConverter> Converters { get; } = new();

    /// <summary>Porters/carts/rails/drones — trip carriers of the logistics tick, assignment order.</summary>
    public EntityGroup<Carrier> Carriers { get; } = new();

    /// <summary>Belt lines — tier-4 continuous flows of the logistics tick, build order.</summary>
    public EntityGroup<ConveyorBelt> Belts { get; } = new();

    /// <summary>Teleporter lanes — tier-6 instant transfers with their energy banks, build order.</summary>
    public EntityGroup<SimTeleporterLane> Teleporters { get; } = new();
}
