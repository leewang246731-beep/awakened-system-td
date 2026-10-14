using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Llm;

/// <summary>
/// Chat wire protocol selector (方案 §11.6 <c>protocol: openai|anthropic</c>).
/// Decides endpoint path, auth header shape and SSE frame grammar
/// (F-083 协议路由). Wire names are lowercase and exact.
/// </summary>
[JsonConverter(typeof(LlmProtocolJsonConverter))]
public enum LlmProtocol
{
    /// <summary>POST {base}/chat/completions, Bearer auth, OpenAI SSE frames.</summary>
    OpenAi,

    /// <summary>POST {base}/v1/messages, x-api-key + anthropic-version, Anthropic SSE events.</summary>
    Anthropic,
}

/// <summary>
/// Wire-exact enum codec for <see cref="LlmProtocol"/>: only the two
/// schema strings "openai"/"anthropic" parse; anything else raises
/// <see cref="JsonException"/> so the loader can surface a precise
/// configuration error instead of silently defaulting (F-080).
/// </summary>
public sealed class LlmProtocolJsonConverter : JsonConverter<LlmProtocol>
{
    /// <inheritdoc />
    public override LlmProtocol Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "openai" => LlmProtocol.OpenAi,
            "anthropic" => LlmProtocol.Anthropic,
            _ => throw new JsonException(
                $"protocol 必须是 openai 或 anthropic，收到 '{value}'"),
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, LlmProtocol value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            LlmProtocol.OpenAi => "openai",
            LlmProtocol.Anthropic => "anthropic",
            _ => throw new JsonException($"unserializable protocol {value}"),
        });
    }
}

/// <summary>
/// One BYOK provider row of <c>llm.config.json</c> (方案 §11.6 schema).
/// NOTE: there is deliberately NO api-key property here — keys live only
/// in <see cref="EraCore.Secrets.ISecretStore"/> under
/// <see cref="Id"/>; the config file must never carry key material
/// (F-084 / RL5; the loader actively rejects a non-empty api_key value).
/// </summary>
public sealed record LlmProviderConfig
{
    /// <summary>Stable provider id; also the ISecretStore key namespace.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (UI only).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Wire protocol (endpoint/auth/SSE grammar selector).</summary>
    [JsonPropertyName("protocol")]
    public LlmProtocol Protocol { get; init; }

    /// <summary>Provider base URL, e.g. https://api.xiaomimimo.com/v1 — the whitelist source.</summary>
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Model id, e.g. glm-5.3 / mimo-v2.6-pro (MiMo ids are lowercase).</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>False = provider locked; the gateway refuses its calls with zero network.</summary>
    [JsonPropertyName("enable")]
    public bool Enable { get; init; }

    /// <summary>Sampling temperature.</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    /// <summary>max_tokens per request.</summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    /// <summary>Whole-request budget hint (seconds).</summary>
    [JsonPropertyName("request_timeout_s")]
    public int RequestTimeoutS { get; init; }

    /// <summary>Connect-phase budget (seconds) forwarded to the wire port.</summary>
    [JsonPropertyName("connect_timeout_s")]
    public int ConnectTimeoutS { get; init; }

    /// <summary>Retry budget (consumed by T-091; parsed for schema completeness).</summary>
    [JsonPropertyName("retries")]
    public int Retries { get; init; }

    /// <summary>History rounds kept in context (consumed by T-091).</summary>
    [JsonPropertyName("history_rounds")]
    public int HistoryRounds { get; init; }

    /// <summary>Context budget in tokens (consumed by T-091).</summary>
    [JsonPropertyName("context_budget_tokens")]
    public int ContextBudgetTokens { get; init; }

    /// <summary>True = SSE streaming; false = single JSON response.</summary>
    [JsonPropertyName("use_stream")]
    public bool UseStream { get; init; }

    /// <summary>Preset/usage note shown in the BYOK UI (§11.6 note field).</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Base URL as an absolute URI. Only valid for enabled providers —
    /// config validation guarantees non-empty http(s) base URLs there;
    /// disabled template rows may legitimately be blank.
    /// </summary>
    /// <exception cref="UriFormatException">Blank or relative base URL.</exception>
    [JsonIgnore]
    public Uri BaseUri => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        ? uri
        : throw new UriFormatException($"provider '{Id}' base_url 不是合法绝对 URI：'{BaseUrl}'");
}

/// <summary>Logging switches of §11.6 (consumed by the T-091 call log).</summary>
public sealed record LlmLoggingConfig
{
    /// <summary>Player-visible call log on/off.</summary>
    [JsonPropertyName("show_call_log")]
    public bool ShowCallLog { get; init; } = true;

    /// <summary>Full conversation capture on/off (off by default).</summary>
    [JsonPropertyName("log_conversation")]
    public bool LogConversation { get; init; }

    /// <summary>Call log retention count.</summary>
    [JsonPropertyName("log_retention")]
    public int LogRetention { get; init; } = 50; // ci-allow: R7 T-072 §11.6 llm.config.json 模板缺省值 log_retention=50（BYOK schema 域常量，非平衡数值）
}

/// <summary>
/// Root of <c>llm.config.json</c> (F-080; 方案 §11.6). Immutable snapshot;
/// the gateway freezes its domain whitelist from it at construction.
/// </summary>
public sealed record LlmConfig
{
    /// <summary>Schema version; only 1 is accepted.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>Id of the provider the LLM tier routes to.</summary>
    [JsonPropertyName("active_provider")]
    public string ActiveProvider { get; init; } = string.Empty;

    /// <summary>All configured providers (presets first, customs after).</summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<LlmProviderConfig> Providers { get; init; } = [];

    /// <summary>Logging switches.</summary>
    [JsonPropertyName("logging")]
    public LlmLoggingConfig Logging { get; init; } = new();

    /// <summary>Looks up one provider by exact id.</summary>
    public LlmProviderConfig? FindProvider(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// System.Text.Json source-generation context for llm.config.json (D-05:
/// reflection-free serialization in EraCore). The ONLY entry point for
/// config (de)serialization; casing follows the §11.6 schema exactly.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LlmConfig))]
[JsonSerializable(typeof(LlmProviderConfig))]
[JsonSerializable(typeof(LlmLoggingConfig))]
public sealed partial class LlmJsonContext : JsonSerializerContext
{
}
