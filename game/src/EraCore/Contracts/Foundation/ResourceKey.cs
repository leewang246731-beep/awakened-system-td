// T2: pure contract — K-1 domain primitive, T-019 remainder, frozen (round 2 backfill).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Typed key for one economy resource (K-1 domain primitive,
/// 03-decomposition §6). Wraps the fully qualified content id
/// ("modid:contentid") of a K-2 ContentType.Resource entry so resource
/// ledgers (C-06) cannot mix resource ids with free strings.
/// </summary>
/// <param name="Value">Fully qualified resource content id.</param>
public sealed record ResourceKey(string Value);
