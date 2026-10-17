using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Llm;

/// <summary>
/// Audio container selector of the reserved TTS channel (T-138, F-140):
/// the <c>response_format</c> wire field of the OpenAI-compatible
/// audio/speech endpoint the mimo-v2.5-tts preset targets. Wire names are
/// lowercase and exact — only these three strings parse.
/// </summary>
[JsonConverter(typeof(TtsAudioFormatJsonConverter))]
public enum TtsAudioFormat
{
    /// <summary>RIFF/WAVE container — the game audio pipeline's native input.</summary>
    Wav,

    /// <summary>MPEG audio (compact transfer; decoded before playback).</summary>
    Mp3,

    /// <summary>Headerless little-endian PCM sample bytes.</summary>
    Pcm,
}

/// <summary>
/// Wire-exact enum codec for <see cref="TtsAudioFormat"/>: only "wav",
/// "mp3" and "pcm" parse; anything else raises <see cref="JsonException"/>
/// so a future caller surfaces a precise error instead of silently
/// defaulting (same discipline as <see cref="LlmProtocolJsonConverter"/>).
/// </summary>
public sealed class TtsAudioFormatJsonConverter : JsonConverter<TtsAudioFormat>
{
    /// <inheritdoc />
    public override TtsAudioFormat Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "wav" => TtsAudioFormat.Wav,
            "mp3" => TtsAudioFormat.Mp3,
            "pcm" => TtsAudioFormat.Pcm,
            _ => throw new JsonException(
                $"response_format 必须是 wav/mp3/pcm，收到 '{value}'"),
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, TtsAudioFormat value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            TtsAudioFormat.Wav => "wav",
            TtsAudioFormat.Mp3 => "mp3",
            TtsAudioFormat.Pcm => "pcm",
            _ => throw new JsonException($"unserializable response_format {value}"),
        });
    }
}

/// <summary>
/// One TTS speech wire request (T-138, F-140): the JSON body the reserved
/// mimo-v2.5-tts endpoint expects (OpenAI-compatible audio/speech shape —
/// model/input/voice/response_format). This build defines and round-trips
/// the model only; nothing ever sends it (预留边界, REV-20261018-T138).
/// </summary>
public sealed record TtsSpeechRequest
{
    /// <summary>TTS model id, e.g. mimo-v2.5-tts (MiMo ids are lowercase).</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>The text to synthesize.</summary>
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    /// <summary>Voice selector; concrete voice ids arrive with the endpoint's launch.</summary>
    [JsonPropertyName("voice")]
    public string Voice { get; init; } = string.Empty;

    /// <summary>Audio container of the reply bytes.</summary>
    [JsonPropertyName("response_format")]
    public TtsAudioFormat ResponseFormat { get; init; } = TtsAudioFormat.Wav;
}

/// <summary>
/// One TTS speech reply: synthesized audio bytes plus their container.
/// Binary payload — never serialized into config/log surfaces (RL5 applies
/// to any future metadata logging; audio bytes are content, not secrets).
/// </summary>
/// <param name="Format">Container of <paramref name="Audio"/>.</param>
/// <param name="Audio">Synthesized audio bytes (empty array = no audio).</param>
public sealed record TtsSpeechResponse(TtsAudioFormat Format, byte[] Audio)
{
    /// <summary>True when actual audio bytes arrived (false = nothing synthesized).</summary>
    public bool HasAudio => Audio is { Length: > 0 };
}

/// <summary>
/// System.Text.Json source-generation context for the TTS wire contract
/// (D-05: reflection-free serialization in EraCore). Config
/// (de)serialization stays with <see cref="LlmJsonContext"/>; this context
/// owns the audio/speech request body only.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TtsSpeechRequest))]
[JsonSerializable(typeof(TtsAudioFormat))]
public sealed partial class TtsJsonContext : JsonSerializerContext
{
}
