using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [C-04] K-7 save backend: campaign DTO shaping and contract view (T-041)
public class CampaignMetaDtoTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void FromMeta_copies_every_field_from_the_contract_implementation()
    {
        var source = new CampaignMetaDto(
            777,
            4,
            new Dictionary<string, long> { ["wood"] = 12 },
            new List<string> { "tech.a" },
            new List<string> { "relic.b" },
            new List<LevelLayoutSnapshotDto>
            {
                new("level.x", new byte[] { 3, 1, 4 }),
            });

        var copy = CampaignMetaDto.FromMeta(source);

        Assert.Equal(777, copy.WorldSeed);
        Assert.Equal(4, copy.EraIndex);
        Assert.Equal(12, copy.ResourcePool["wood"]);
        Assert.Equal("tech.a", copy.UnlockedTechs.Single());
        Assert.Equal("relic.b", copy.RelicRegistry.Single());
        Assert.Equal("level.x", copy.LevelLayoutSnapshots.Single().LevelId);
        Assert.Equal(new byte[] { 3, 1, 4 }, copy.LevelLayoutSnapshots.Single().LayoutPayload);
    }

    [Fact]
    public void Dto_satisfies_the_campaign_contract_view()
    {
        ICampaignMeta meta = CampaignMetaDto.FromMeta(new CampaignMetaDto(
            42, 1,
            new Dictionary<string, long> { ["gold"] = 3 },
            new List<string> { "t" },
            new List<string> { "r" },
            new List<LevelLayoutSnapshotDto> { new("L", new byte[] { 0 }) }));

        Assert.Equal(42, meta.WorldSeed);
        Assert.Equal(1, meta.EraIndex);
        Assert.Equal(3, meta.ResourcePool["gold"]);
        Assert.Equal("t", meta.UnlockedTechs.Single());
        Assert.Equal("r", meta.RelicRegistry.Single());
        Assert.Equal("L", meta.LevelLayoutSnapshots.Single().LevelId);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void FromMeta_deep_copies_collections_so_later_source_mutation_is_invisible()
    {
        var pool = new Dictionary<string, long> { ["iron"] = 5 };
        var techs = new List<string> { "tech.a" };
        var layouts = new List<ILevelLayoutSnapshot>
        {
            new LevelLayoutSnapshotDto("L", new byte[] { 1 }),
        };
        ICampaignMeta source = new CapturingMeta(
            1, 1, pool, techs, new List<string> { "relic.r" }, layouts);

        var dto = CampaignMetaDto.FromMeta(source);
        pool["iron"] = 999;
        techs.Add("tech.late");
        layouts.Clear();

        Assert.Equal(5, dto.ResourcePool["iron"]);
        Assert.Equal("tech.a", dto.UnlockedTechs.Single());
        Assert.Equal("relic.r", dto.RelicRegistry.Single());
        Assert.Equal("L", dto.LevelLayoutSnapshots.Single().LevelId);
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void FromMeta_rejects_null_meta()
    {
        Assert.Throws<ArgumentNullException>(() => CampaignMetaDto.FromMeta(null!));
    }

    /// <summary>Minimal contract implementation for copy-semantics probing.</summary>
    private sealed record CapturingMeta(
        long WorldSeed,
        int EraIndex,
        IReadOnlyDictionary<string, long> ResourcePool,
        IReadOnlyList<string> UnlockedTechs,
        IReadOnlyList<string> RelicRegistry,
        IReadOnlyList<ILevelLayoutSnapshot> LevelLayoutSnapshots) : ICampaignMeta;
}
