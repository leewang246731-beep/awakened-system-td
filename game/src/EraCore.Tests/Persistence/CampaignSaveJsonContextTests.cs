using System.Text.Json;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [C-04] K-7 save backend: source-generated campaign JSON (AOT red line, T-041)
public class CampaignSaveJsonContextTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Campaign_roundtrips_through_the_generated_context()
    {
        var meta = SqliteSavePortTestBase.SampleCampaign(31337);

        var json = CampaignSaveJsonContext.SerializeCampaign(meta);
        var decoded = CampaignSaveJsonContext.DeserializeCampaign(json);

        Assert.Equal(31337, decoded.WorldSeed);
        Assert.Equal(2, decoded.EraIndex);
        Assert.Equal(150, decoded.ResourcePool["steel"]);
        Assert.Equal(9, decoded.ResourcePool["intel"]);
        Assert.Equal(new[] { "tech.agri", "tech.smith" }, decoded.UnlockedTechs);
        Assert.Equal(new[] { "relic.cheb", "relic.map" }, decoded.RelicRegistry);
        Assert.Equal(2, decoded.LevelLayoutSnapshots.Count);
        Assert.Equal("level.e1.m01", decoded.LevelLayoutSnapshots[0].LevelId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded.LevelLayoutSnapshots[0].LayoutPayload);
        Assert.Equal("level.e1.m02", decoded.LevelLayoutSnapshots[1].LevelId);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.LevelLayoutSnapshots[1].LayoutPayload);
    }

    [Fact]
    public void Serialized_document_uses_the_domain_member_names()
    {
        // The on-disk v1 document shape is part of the format contract:
        // PascalCase member names matching the K-7 fields, byte payloads as
        // base64 (STJ default for byte[]).
        var json = CampaignSaveJsonContext.SerializeCampaign(
            SqliteSavePortTestBase.SampleCampaign(5));

        Assert.Contains("\"WorldSeed\":5", json);
        Assert.Contains("\"EraIndex\":2", json);
        Assert.Contains("\"ResourcePool\"", json);
        Assert.Contains("\"UnlockedTechs\"", json);
        Assert.Contains("\"RelicRegistry\"", json);
        Assert.Contains("\"LevelLayoutSnapshots\"", json);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Empty_collections_roundtrip_to_empty_collections()
    {
        var meta = new CampaignMetaDto(0, 1, new Dictionary<string, long>(),
            new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>());

        var decoded = CampaignSaveJsonContext.DeserializeCampaign(
            CampaignSaveJsonContext.SerializeCampaign(meta));

        Assert.Empty(decoded.ResourcePool);
        Assert.Empty(decoded.UnlockedTechs);
        Assert.Empty(decoded.RelicRegistry);
        Assert.Empty(decoded.LevelLayoutSnapshots);
    }

    // ------------------------------------------------------------ illegal

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("{\"WorldSeed\":\"not-a-long\"}")]
    public void Damaged_documents_throw_JsonException(string damaged)
    {
        // The read side wraps this into SaveCorruptException at the port
        // boundary; the context itself must fail loudly, never return a
        // half-populated DTO.
        Assert.Throws<JsonException>(
            () => CampaignSaveJsonContext.DeserializeCampaign(damaged));
    }
}
