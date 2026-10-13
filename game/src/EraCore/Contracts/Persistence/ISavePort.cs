// T2: pure contract — K-7 save format v1, T-024, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Persistence;

/// <summary>
/// Save backend port (K-7). Implementations: desktop SQLite, Web JSON,
/// in-memory for tests — the same test suite runs parameterized over all of
/// them (D-32). Atomic-write discipline is part of the contract: temp file
/// plus rename, verified by crash injection (T-041).
/// </summary>
public interface ISavePort
{
    /// <summary>Writes campaign-global state at format version <paramref name="formatVersion"/>.</summary>
    void WriteCampaign(ICampaignMeta meta, int formatVersion);

    /// <summary>
    /// Reads campaign state, migrating the stored version forward to
    /// <paramref name="formatVersion"/> chain-style.
    /// </summary>
    ICampaignMeta ReadCampaign(int formatVersion);

    /// <summary>
    /// Writes one level slot at <paramref name="formatVersion"/>, keeping
    /// the two rotating payload backups per slot.
    /// </summary>
    void WriteLevel(string levelSlotId, ILevelSaveBlob blob, int formatVersion);

    /// <summary>Reads one level slot, migrating forward to <paramref name="formatVersion"/>.</summary>
    ILevelSaveBlob ReadLevel(string levelSlotId, int formatVersion);
}
