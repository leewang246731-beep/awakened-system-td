namespace EraCore.Energy;

/// <summary>
/// Stable identity of one energy node inside an <see cref="EnergyLedger"/>
/// (C-08, T-033). Producers (labor camps, beast pens) and consumers
/// (towers, facilities) are registered under this id; it is deliberately
/// independent from <see cref="Contracts.Foundation.EntityId"/> so the
/// ledger can also address virtual nodes that are not placed buildings
/// (e.g. an era-level manpower pool tap). Opaque — consumers must never
/// parse meaning out of the value.
/// </summary>
/// <param name="Value">Opaque caller-issued value, unique within one ledger.</param>
public sealed record EnergyNodeId(long Value);
