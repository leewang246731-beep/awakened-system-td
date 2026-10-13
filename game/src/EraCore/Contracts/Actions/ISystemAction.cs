// T2: pure contract — K-3 system action schema, T-021, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Actions;

/// <summary>
/// Marker interface for typed system-action payloads (K-3). Intentionally
/// empty: actions travel as validated JSON through the pipeline; concrete
/// payload types are C-03/C-13 implementation details and must not leak into
/// the frozen contract. Every action goes through the closed 19-verb schema
/// plus registry and budget — there is no lawful bypass (RL6).
/// </summary>
public interface ISystemAction
{
}
