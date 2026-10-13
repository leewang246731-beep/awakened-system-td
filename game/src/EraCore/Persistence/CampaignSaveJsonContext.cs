using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Persistence;

/// <summary>
/// System.Text.Json source-generation context for the K-7 campaign payload
/// document (T-041). AOT red line (07 D-05): serialization must go through
/// generated <see cref="JsonTypeInfo{T}"/> metadata — reflection-based
/// (de)serialization is forbidden in EraCore, so this context is the ONLY
/// entry point for campaign JSON in the SQLite backend.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CampaignMetaDto))]
public sealed partial class CampaignSaveJsonContext : JsonSerializerContext
{
    /// <summary>Serializes one campaign document via generated metadata.</summary>
    public static string SerializeCampaign(CampaignMetaDto meta)
    {
        return JsonSerializer.Serialize(meta, Default.CampaignMetaDto);
    }

    /// <summary>
    /// Deserializes one campaign document via generated metadata. Throws
    /// <see cref="JsonException"/> on damaged payloads — callers wrap it into
    /// <see cref="SaveCorruptException"/> together with the stored version.
    /// </summary>
    public static CampaignMetaDto DeserializeCampaign(string json)
    {
        return JsonSerializer.Deserialize(json, Default.CampaignMetaDto)
            ?? throw new JsonException("Campaign payload deserialized to null.");
    }
}
