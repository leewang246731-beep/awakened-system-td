// T2: pure contract — K-7 save format v1, T-024, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Persistence;

/// <summary>
/// Per-level persisted layout facts (K-7). The core-zone cell set of the
/// relic city is persisted EXPLICITLY — recomputing it geometrically after
/// load is forbidden (07 A5 constraint). The payload itself is opaque
/// compressed bytes owned by C-04/C-20.
/// </summary>
public interface ILevelLayoutSnapshot
{
    /// <summary>Level slot this snapshot belongs to (content-registry id).</summary>
    string LevelId { get; }

    /// <summary>Opaque serialized layout facts; compression and encoding owned by C-04.</summary>
    byte[] LayoutPayload { get; }
}
