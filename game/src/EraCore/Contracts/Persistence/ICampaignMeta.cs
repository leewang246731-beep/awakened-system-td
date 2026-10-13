// T2: pure contract — K-7 save format v1, T-024, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Persistence;

/// <summary>
/// Campaign-global save domain (K-7, "profile" family of the single
/// dual-domain store): world seed, era progress, pooled resources, tech,
/// relic registry and per-level layout snapshots. Backends: desktop SQLite
/// (profile.db, WAL, atomic temp+rename, two rotating payload backups per
/// slot) and Web JSON — both pass the same parameterized test suite
/// (07 D-11/D-32).
/// </summary>
public interface ICampaignMeta
{
    /// <summary>World seed — the root of deterministic replay.</summary>
    long WorldSeed { get; }

    /// <summary>Era index: E1=1 … E6=6, finale=6 (appendix A symbol binding).</summary>
    int EraIndex { get; }

    /// <summary>Cross-era resource pool keyed by resource id (quantities in resource units).</summary>
    IReadOnlyDictionary<string, long> ResourcePool { get; }

    /// <summary>Unlocked technology node ids (content-registry ids).</summary>
    IReadOnlyList<string> UnlockedTechs { get; }

    /// <summary>Relic registry entries (id-addressed; output of the appendix F.7 mapping).</summary>
    IReadOnlyList<string> RelicRegistry { get; }

    /// <summary>
    /// Layout snapshots for every level touched this campaign — persisted
    /// facts, never recomputed after load (A5).
    /// </summary>
    IReadOnlyList<ILevelLayoutSnapshot> LevelLayoutSnapshots { get; }
}
