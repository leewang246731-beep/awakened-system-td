using EraCore.Contracts.Persistence;

namespace EraCore.Persistence;

/// <summary>
/// Concrete <see cref="ILevelSaveBlob"/> for the SQLite backend: one
/// wave-boundary checkpoint plus its command tail, both opaque compressed
/// byte arrays stored as BLOBs verbatim (T-041 / 07 D-11).
/// </summary>
public sealed record LevelSaveBlobDto(
    int WaveCheckpoint,
    byte[] SimSnapshotPayload,
    byte[] CommandTailPayload) : ILevelSaveBlob;
