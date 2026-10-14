using System.Text.Json;
using EraCore.Contracts.Agent;

namespace EraCore.Llm;

/// <summary>
/// One chat request as seen by a protocol adapter: everything both wire
/// protocols need, nothing provider-specific (built by the gateway from a
/// provider row + <see cref="LlmTransportRequest"/>).
/// </summary>
/// <param name="Model">Model id from the provider config.</param>
/// <param name="Messages">Prompt turns (system/user/assistant/tool roles as-is).</param>
/// <param name="ToolsJson">L3 tools JSON array projected from the K-3 schema (OpenAI shape).</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="MaxTokens">Response token cap.</param>
/// <param name="Stream">True = SSE streaming; false = single JSON reply.</param>
public sealed record LlmChatRequestSpec(
    string Model,
    IReadOnlyList<LlmChatMessage> Messages,
    string ToolsJson,
    double Temperature,
    int MaxTokens,
    bool Stream);

/// <summary>
/// Provider-reported token usage of one reply (F-091 调用日志 token 数):
/// prompt/completion counts as the wire reported them — null when the
/// provider did not report (streaming relays often omit usage).
/// </summary>
/// <param name="PromptTokens">Input/prompt tokens when reported.</param>
/// <param name="CompletionTokens">Output/completion tokens when reported.</param>
public sealed record LlmTokenUsage(int? PromptTokens, int? CompletionTokens)
{
    /// <summary>Sum when both halves are known; else whichever half is known; else null.</summary>
    public int? TotalTokens =>
        PromptTokens is { } prompt && CompletionTokens is { } completion
            ? prompt + completion
            : PromptTokens ?? CompletionTokens;
}

/// <summary>
/// Fully-reduced outcome of one model reply (streaming or not): the two
/// text channels of reasoning models plus the first tool call. Content is
/// the player-facing 正文; Reasoning is the 折叠思考 channel
/// (<c>delta.reasoning_content</c> / <c>thinking_delta</c>, 方案 §11.7 推理模型兼容 F-087).
/// </summary>
/// <param name="ContentText">Accumulated 正文 (delta.content / text_delta).</param>
/// <param name="ReasoningText">Accumulated 思考 (delta.reasoning_content / thinking_delta).</param>
/// <param name="ToolName">First tool call's function name; null when none.</param>
/// <param name="ToolArgumentsJson">First tool call's arguments as raw JSON text ("" when absent).</param>
/// <param name="FinishReason">Provider stop reason (stop/tool_use/max_tokens…), when reported.</param>
/// <param name="Usage">Provider-reported token usage; null when unreported.</param>
public sealed record LlmStreamOutcome(
    string ContentText,
    string ReasoningText,
    string? ToolName,
    string ToolArgumentsJson,
    string? FinishReason,
    LlmTokenUsage? Usage = null)
{
    /// <summary>True when the model issued at least one tool call.</summary>
    public bool SawToolCall => ToolName is not null;

    /// <summary>True when the provider exposed a reasoning channel in this reply.</summary>
    public bool SawReasoningChannel => ReasoningText.Length > 0;

    /// <summary>Empty outcome (stream aborted before any frame reduced).</summary>
    public static LlmStreamOutcome Empty { get; } =
        new(string.Empty, string.Empty, null, string.Empty, null, null);
}

/// <summary>
/// Incremental reducer for one SSE session. The gateway feeds it decoded
/// SSE payloads in arrival order; implementations accumulate the two text
/// channels and tool-call fragments (fragmented across chunks per §11.7).
/// Purely accumulative — no I/O, no clock.
/// </summary>
public interface IChatStreamReducer
{
    /// <summary>True after the protocol's terminal marker ([DONE] / message_stop).</summary>
    bool IsDone { get; }

    /// <summary>Consumes one SSE data payload (text after "data:", leading space stripped).</summary>
    /// <exception cref="JsonException">Payload is not a JSON object (non-[DONE] lines).</exception>
    void OnDataLine(string payload);

    /// <summary>Consumes one SSE event-name line ("event: x") — meaningful to Anthropic only.</summary>
    void OnEventLine(string eventName);

    /// <summary>Builds the reduced outcome; callable at any point (partial on aborted streams).</summary>
    LlmStreamOutcome BuildOutcome();
}

/// <summary>
/// Wire-protocol adapter (F-083 协议路由): endpoint shape, auth headers,
/// request body encoding and both reply reductions (SSE + single JSON) for
/// one <see cref="LlmProtocol"/>. Pure text work — zero network, fully
/// unit-testable (架构守卫: 解析层零网络依赖).
/// </summary>
public interface IChatProtocol
{
    /// <summary>Wire id ("openai" / "anthropic").</summary>
    string ProtocolId { get; }

    /// <summary>Chat endpoint for one base URL (e.g. {base}/chat/completions).</summary>
    /// <exception cref="UriFormatException">Base URL not absolute http(s).</exception>
    Uri BuildEndpoint(Uri baseUrl);

    /// <summary>
    /// Adds auth headers for one key (Authorization: Bearer … / x-api-key …)
    /// into <paramref name="headers"/>. Values carry key material — never log.
    /// </summary>
    void ApplyAuthHeaders(IDictionary<string, string> headers, string apiKey);

    /// <summary>Serializes the request body for this protocol.</summary>
    /// <exception cref="JsonException">ToolsJson not a JSON array.</exception>
    string BuildRequestBody(LlmChatRequestSpec spec);

    /// <summary>New reducer for one streaming session.</summary>
    IChatStreamReducer CreateReducer();

    /// <summary>Parses one non-streaming 2xx reply body.</summary>
    /// <exception cref="JsonException">Body not a parseable reply.</exception>
    LlmStreamOutcome ParseNonStreamResponse(string bodyJson);
}

/// <summary>Protocol registry: the two frozen adapters, nothing else.</summary>
public static class ChatProtocols
{
    private static readonly OpenAiProtocol OpenAi = new();
    private static readonly AnthropicProtocol Anthropic = new();

    /// <summary>Returns the adapter for one protocol value.</summary>
    public static IChatProtocol Resolve(LlmProtocol protocol) => protocol switch
    {
        LlmProtocol.OpenAi => OpenAi,
        LlmProtocol.Anthropic => Anthropic,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };
}

/// <summary>Shared plumbing for both reducers (string accumulation).</summary>
internal abstract class ChatStreamReducerBase : IChatStreamReducer
{
    private readonly System.Text.StringBuilder _content = new();
    private readonly System.Text.StringBuilder _reasoning = new();

    /// <summary>Derived classes set this on their protocol's terminal marker.</summary>
    public bool IsDone { get; protected set; }

    public void OnEventLine(string eventName)
    {
        // Default: SSE event names carry no payload info; Anthropic overrides.
        _ = eventName;
    }

    public abstract void OnDataLine(string payload);

    public LlmStreamOutcome BuildOutcome()
    {
        var (name, args) = BuildToolCall();
        return new LlmStreamOutcome(
            _content.ToString(),
            _reasoning.ToString(),
            name,
            args,
            FinishReason,
            Usage);
    }

    /// <summary>Stop reason as reported by the protocol, when seen.</summary>
    public string? FinishReason { get; internal set; }

    /// <summary>Token usage as reported by the protocol, when seen (F-091).</summary>
    public LlmTokenUsage? Usage { get; internal set; }

    /// <summary>Accumulates 正文 text.</summary>
    protected void AppendContent(string text) => _content.Append(text);

    /// <summary>Accumulates 思考 text.</summary>
    protected void AppendReasoning(string text) => _reasoning.Append(text);

    /// <summary>Derived reducers produce their first tool call (name + raw args JSON).</summary>
    protected abstract (string? Name, string Arguments) BuildToolCall();

    /// <summary>Reads a string property that may be absent or null.</summary>
    protected static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
