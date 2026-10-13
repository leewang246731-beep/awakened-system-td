using EraCore.Contracts.Persistence;

namespace EraCore.Persistence;

/// <summary>
/// Serializable <see cref="ICampaignMeta"/> shape (K-7). One instance is one
/// campaign-global document; the SQLite backend stores it as a single JSON
/// payload row written by the source-generated
/// <see cref="CampaignSaveJsonContext"/> (AOT red line: no reflection
/// serialization, 07 D-05). Properties are concrete collections so the
/// source generator always has an instantiable type; the contract view is
/// provided by explicit interface implementations. Writing copies any
/// <see cref="ICampaignMeta"/> through <see cref="FromMeta"/>, reading
/// returns the DTO directly (it implements the contract).
/// </summary>
public sealed record CampaignMetaDto(
    long WorldSeed,
    int EraIndex,
    Dictionary<string, long> ResourcePool,
    List<string> UnlockedTechs,
    List<string> RelicRegistry,
    List<LevelLayoutSnapshotDto> LevelLayoutSnapshots) : ICampaignMeta
{
    IReadOnlyDictionary<string, long> ICampaignMeta.ResourcePool => ResourcePool;

    IReadOnlyList<string> ICampaignMeta.UnlockedTechs => UnlockedTechs;

    IReadOnlyList<string> ICampaignMeta.RelicRegistry => RelicRegistry;

    IReadOnlyList<ILevelLayoutSnapshot> ICampaignMeta.LevelLayoutSnapshots => LevelLayoutSnapshots;

    /// <summary>Copies any contract implementation into the serializable shape.</summary>
    public static CampaignMetaDto FromMeta(ICampaignMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return new CampaignMetaDto(
            meta.WorldSeed,
            meta.EraIndex,
            new Dictionary<string, long>(meta.ResourcePool),
            new List<string>(meta.UnlockedTechs),
            new List<string>(meta.RelicRegistry),
            meta.LevelLayoutSnapshots
                .Select(s => new LevelLayoutSnapshotDto(s.LevelId, s.LayoutPayload))
                .ToList());
    }
}
