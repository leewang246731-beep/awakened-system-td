using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束：C-04 JSON 后备的存档文件 envelope（T-100 / 07 D-32）
public sealed class SaveFileDocumentTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Properties_pass_through()
    {
        var document = new SaveFileDocument(7, "{\"seed\":42}");

        Assert.Equal(7, document.FormatVersion);
        Assert.Equal("{\"seed\":42}", document.PayloadJson);
    }

    [Fact]
    public void Envelope_roundtrips_through_the_source_generated_context()
    {
        var document = new SaveFileDocument(
            SaveVersion.Current,
            CampaignSaveJsonContext.SerializeCampaign(
                SqliteSavePortTestBase.SampleCampaign(123)));

        var readBack = JsonSaveFileContext.DeserializeEnvelope(
            JsonSaveFileContext.SerializeEnvelope(document));

        Assert.Equal(document.FormatVersion, readBack.FormatVersion);
        Assert.Equal(document.PayloadJson, readBack.PayloadJson);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Missing_fields_deserialize_as_absent_not_as_zero()
    {
        // "field absent" must stay distinguishable from "explicit 0": an
        // absent authority heals on write, an explicit one must resolve —
        // the nullable int is exactly that distinction.
        var document = JsonSaveFileContext.DeserializeEnvelope("{}"u8.ToArray());

        Assert.Null(document.FormatVersion);
        Assert.Null(document.PayloadJson);
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Undecodable_bytes_throw_json_exception_for_callers_to_wrap()
    {
        Assert.Throws<System.Text.Json.JsonException>(
            () => JsonSaveFileContext.DeserializeEnvelope(new byte[] { 0xDE, 0xAD }));
    }
}
