namespace EraCore.Llm;

/// <summary>
/// One reserved TTS endpoint descriptor (T-138, F-140): where the future
/// speech call would go and under which model id. Pure data — this build
/// never contacts the endpoint (预留边界, REV-20261018-T138).
/// </summary>
public sealed record TtsProviderEndpoint
{
    /// <summary>Stable endpoint id (UI/diagnostics only; keys stay keyed by the chat provider row id).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (UI only).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Provider base URL (OpenAI-compatible root; same host as the chat preset).</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>TTS model id (lowercase, MiMo convention).</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Endpoint path under the base URL (OpenAI-compatible audio/speech).</summary>
    public string EndpointPath { get; init; } = string.Empty;

    /// <summary>
    /// Voice selector; concrete voice ids ship with the endpoint's launch —
    /// the reserved preset deliberately leaves it empty rather than
    /// inventing unverified voice names.
    /// </summary>
    public string DefaultVoice { get; init; } = string.Empty;

    /// <summary>
    /// Full speech URL (base + path). Throws <see cref="UriFormatException"/>
    /// when BaseUrl is blank or not an absolute URI — a future caller is
    /// expected to validate configuration before composing the call.
    /// </summary>
    public Uri SpeechUrl => new(
        $"{BaseUrl.TrimEnd('/')}/{EndpointPath.TrimStart('/')}", UriKind.Absolute);
}

/// <summary>
/// The provider template of the reserved voice channel (T-138, F-140; 08
/// 行：mimo-v2.5-tts 接口预留，不进首发验收). CODE constants in the spirit
/// of <see cref="LlmProviderPresets"/>: the mimo speech endpoint follows
/// the OpenAI-compatible audio/speech convention on the same base URL as
/// the live-verified chat preset. The TTS endpoint itself is NOT open yet
/// — nothing here performs or claims a working call.
/// </summary>
public static class TtsProviderPresets
{
    /// <summary>Reserved TTS model id (MiMo ids are lowercase).</summary>
    public const string MimoTtsModelId = "mimo-v2.5-tts";

    /// <summary>OpenAI-compatible speech endpoint path.</summary>
    public const string MimoTtsEndpointPath = "audio/speech";

    /// <summary>小米 MiMo TTS 预留模板（端点未开通，仅接口与配置面）。</summary>
    public static TtsProviderEndpoint MimoV25Tts() => new()
    {
        Id = "xiaomi-mimo-tts",
        Name = "小米 MiMo TTS（预留模板）",
        BaseUrl = "https://api.xiaomimimo.com/v1",
        Model = MimoTtsModelId,
        EndpointPath = MimoTtsEndpointPath,
        DefaultVoice = string.Empty,
    };
}
