// T2: pure contract — K-7 save format v1, T-024, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Persistence;

/// <summary>
/// Per-level save domain (K-7, "level_slot" family): wave-boundary full
/// snapshot plus command tail. This is the SAME code path as the replay
/// harness; wave boundaries are the checkpoint granularity (07 D-11).
/// </summary>
public interface ILevelSaveBlob
{
    /// <summary>Wave index of the stored checkpoint: ⌊win waves × 0.75⌋ and ≥ 10 (N7).</summary>
    int WaveCheckpoint { get; }

    /// <summary>Full simulation snapshot at the wave boundary (opaque compressed bytes).</summary>
    byte[] SimSnapshotPayload { get; }

    /// <summary>Command tail since the boundary (opaque compressed bytes; replay input).</summary>
    byte[] CommandTailPayload { get; }
}
