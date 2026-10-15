using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束：C-04 JSON 后备的 AOT 序列化上下文（T-100 / 07 D-32）
public sealed class JsonSaveFileContextTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Level_payload_roundtrips_with_byte_arrays_as_base64()
    {
        var blob = SqliteSavePortTestBase.MarkerBlob(0xA0, 9, 15);

        var readBack = JsonSaveFileContext.DeserializeLevelPayload(
            JsonSaveFileContext.SerializeLevelPayload(
                new LevelSaveBlobDto(blob.WaveCheckpoint, blob.SimSnapshotPayload, blob.CommandTailPayload)));

        Assert.Equal(15, readBack.WaveCheckpoint);
        Assert.Equal(blob.SimSnapshotPayload, readBack.SimSnapshotPayload);
        Assert.Equal(blob.CommandTailPayload, readBack.CommandTailPayload);
    }

    [Fact]
    public void Campaign_payload_passes_through_the_envelope_unchanged()
    {
        // Cross-backend equivalence starts at the serializer: the JSON file
        // backend stores the SAME campaign payload string the SQLite
        // payload_json column stores, so both domains share one payload
        // format and future migrations stay single-format.
        var meta = SqliteSavePortTestBase.SampleCampaign(8080);
        var payload = CampaignSaveJsonContext.SerializeCampaign(meta);

        var envelope = JsonSaveFileContext.DeserializeEnvelope(
            JsonSaveFileContext.SerializeEnvelope(
                new SaveFileDocument(SaveVersion.Current, payload)));

        Assert.Equal(payload, envelope.PayloadJson);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Envelope_serialization_is_compact_and_stable()
    {
        // Property casing stays PascalCase — the same default the SQLite
        // backend's campaign payload uses, so both stores share one style.
        var bytes = JsonSaveFileContext.SerializeEnvelope(
            new SaveFileDocument(1, "{}"));

        Assert.Equal("{\"FormatVersion\":1,\"PayloadJson\":\"{}\"}"u8.ToArray(), bytes);
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Damaged_level_payload_string_throws_json_exception()
    {
        Assert.Throws<System.Text.Json.JsonException>(
            () => JsonSaveFileContext.DeserializeLevelPayload("{ this is not json"));
    }

    [Fact]
    public void Level_payload_of_the_wrong_shape_throws_json_exception()
    {
        // A decodable JSON object that is not a level payload must fail as
        // damage, not come back as a default blob.
        Assert.Throws<System.Text.Json.JsonException>(
            () => JsonSaveFileContext.DeserializeLevelPayload("{\"unexpected\":true}"));
    }
}
