using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Persistence;

/// <summary>
/// System.Text.Json source-generation context for the Web JSON save backend
/// (T-100 / 07 D-32). AOT red line (07 D-05): serialization must go through
/// generated <see cref="JsonTypeInfo{T}"/> metadata — reflection-based
/// (de)serialization is forbidden in EraCore. The campaign payload keeps
/// going through <see cref="CampaignSaveJsonContext"/> (the SAME entry
/// point as the SQLite backend, so both backends write byte-identical
/// campaign payloads); this context owns the file envelope and the level
/// payload shape.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SaveFileDocument))]
[JsonSerializable(typeof(LevelSaveBlobDto))]
public sealed partial class JsonSaveFileContext : JsonSerializerContext
{
    /// <summary>Serializes one file envelope to UTF-8 bytes via generated metadata.</summary>
    public static byte[] SerializeEnvelope(SaveFileDocument document)
    {
        return JsonSerializer.SerializeToUtf8Bytes(document, Default.SaveFileDocument);
    }

    /// <summary>
    /// Deserializes one file envelope. Throws <see cref="JsonException"/> on
    /// an undecodable file — callers wrap it into <see cref="SaveCorruptException"/>
    /// as "no version authority" (stored version 0).
    /// </summary>
    public static SaveFileDocument DeserializeEnvelope(byte[] utf8Json)
    {
        return JsonSerializer.Deserialize(utf8Json, Default.SaveFileDocument)
            ?? throw new JsonException("Save file envelope deserialized to null.");
    }

    /// <summary>Serializes one level payload via generated metadata.</summary>
    public static string SerializeLevelPayload(LevelSaveBlobDto blob)
    {
        return JsonSerializer.Serialize(blob, Default.LevelSaveBlobDto);
    }

    /// <summary>
    /// Deserializes one level payload. Throws <see cref="JsonException"/> on
    /// a damaged payload string — callers wrap it into
    /// <see cref="SaveCorruptException"/> together with the stored version.
    /// </summary>
    public static LevelSaveBlobDto DeserializeLevelPayload(string payloadJson)
    {
        return JsonSerializer.Deserialize(payloadJson, Default.LevelSaveBlobDto)
            ?? throw new JsonException("Level payload deserialized to null.");
    }
}
