// T2: pure contract — K-1 domain primitive, T-019 remainder, frozen (round 2 backfill).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Stable identity of one simulated entity within the current level run
/// (K-1 domain primitive, 03-decomposition §6). Issued by C-12 for units,
/// projectiles and buildings; carried into wave-boundary snapshots by C-04.
/// Opaque — consumers must never parse meaning out of the value.
/// </summary>
/// <param name="Value">Opaque monotonically issued value.</param>
public sealed record EntityId(long Value);
