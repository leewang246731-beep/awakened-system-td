using EraCore.Contracts.Persistence;

namespace EraCore.Persistence;

/// <summary>
/// Serializable per-level layout facts (K-7, A5 constraint): the core-zone
/// cell set travels as opaque bytes and is never recomputed after load.
/// This record is the System.Text.Json source-generated shape used by the
/// campaign payload document (T-041).
/// </summary>
public sealed record LevelLayoutSnapshotDto(
    string LevelId,
    byte[] LayoutPayload) : ILevelLayoutSnapshot;
