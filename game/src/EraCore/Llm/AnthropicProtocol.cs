using System.Text;
using System.Text.Json;
using EraCore.Contracts.Agent;

namespace EraCore.Llm;

/// <summary>
/// Anthropic messages protocol (F-083): POST {base}/v1/messages with
/// <c>x-api-key</c> + <c>anthropic-version: 2023-06-01</c>. SSE grammar is
/// event-typed (<c>content_block_start/delta</c>, <c>message_stop</c>);
/// tool declarations arrive OpenAI-shaped from the K-3 projection and are
/// converted to <c>{name,description,input_schema}</c> here (结构对齐 —
/// semantic consumption stays in T-068).
/// </summary>
public sealed class AnthropicProtocol : IChatProtocol
{
    /// <summary>Pinned wire version header.</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <inheritdoc />
    public string ProtocolId => "anthropic";

    /// <inheritdoc />
    public Uri BuildEndpoint(Uri baseUrl) => OpenAiProtocol.Combine(baseUrl, "v1/messages");

    /// <inheritdoc />
    public void ApplyAuthHeaders(IDictionary<string, string> headers, string apiKey)
    {
        headers["x-api-key"] = apiKey;
        headers["anthropic-version"] = AnthropicVersion;
    }

    /// <inheritdoc />
    public string BuildRequestBody(LlmChatRequestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        using var payload = JsonDocument.Parse(spec.ToolsJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("tools 必须是 JSON 数组");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", spec.Model);

            var systemText = string.Join(
                "\n\n",
                spec.Messages.Where(m => m.Role == "system").Select(m => m.Content));
            if (systemText.Length > 0)
            {
                writer.WriteString("system", systemText);
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in spec.Messages)
            {
                if (message.Role == "system")
                {
                    continue;
                }

                writer.WriteStartObject();
                // Anthropic accepts user/assistant turns only; tool-result
                // turns ride inside user content blocks upstream — structural
                // passthrough keeps every turn on the wire.
                var role = message.Role is "user" or "assistant" ? message.Role : "user";
                writer.WriteString("role", role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (payload.RootElement.GetArrayLength() > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in payload.RootElement.EnumerateArray())
                {
                    WriteAnthropicTool(writer, tool);
                }

                writer.WriteEndArray();
            }

            if (spec.Stream)
            {
                writer.WriteBoolean("stream", true);
            }

            writer.WriteNumber("max_tokens", spec.MaxTokens);
            writer.WriteNumber("temperature", spec.Temperature);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <inheritdoc />
    public IChatStreamReducer CreateReducer() => new Reducer();

    /// <inheritdoc />
    public LlmStreamOutcome ParseNonStreamResponse(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("回复缺少 content 数组");
        }

        var reducer = new Reducer();
        reducer.AbsorbContentBlocks(content);
        if (root.TryGetProperty("stop_reason", out var stop)
            && stop.ValueKind == JsonValueKind.String)
        {
            reducer.FinishReason = stop.GetString();
        }

        return reducer.BuildOutcome();
    }

    /// <summary>
    /// Converts one OpenAI-shaped tool (K-3 projection) into the Anthropic
    /// tool shape. Unknown shapes throw — a drifting projection must fail
    /// loudly at the boundary, not produce a half-converted schema.
    /// </summary>
    internal static void WriteAnthropicTool(Utf8JsonWriter writer, JsonElement openAiTool)
    {
        if (openAiTool.ValueKind != JsonValueKind.Object
            || !openAiTool.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("tool 声明缺少 function.name（K-3 投影漂移）");
        }

        writer.WriteStartObject();
        writer.WriteString("name", name.GetString());
        if (function.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            writer.WriteString("description", description.GetString());
        }

        if (function.TryGetProperty("parameters", out var parameters))
        {
            writer.WritePropertyName("input_schema");
            parameters.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private sealed class Reducer : ChatStreamReducerBase
    {
        // tool_use block index → (name from content_block_start, json fragments)
        private readonly Dictionary<int, (string Name, StringBuilder Args)> _toolBlocks = [];

        public override void OnDataLine(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeNode)
                || typeNode.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"SSE 帧缺少 type：'{payload}'");
            }

            switch (typeNode.GetString())
            {
                case "content_block_start":
                    AbsorbBlockStart(root);
                    break;
                case "content_block_delta":
                    AbsorbBlockDelta(root);
                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.ValueKind == JsonValueKind.Object
                        && OptionalString(delta, "stop_reason") is { Length: > 0 } stop)
                    {
                        FinishReason = stop;
                    }

                    break;
                case "message_stop":
                    IsDone = true;
                    break;
                case "ping":
                case "message_start":
                case "content_block_stop":
                case "error":
                    break;
                default:
                    // Unknown event types are skipped, not fatal (providers
                    // append new advisory events over time).
                    break;
            }
        }

        private void AbsorbBlockStart(JsonElement root)
        {
            if (!root.TryGetProperty("index", out var indexNode)
                || !indexNode.TryGetInt32(out var index)
                || !root.TryGetProperty("content_block", out var block)
                || block.ValueKind != JsonValueKind.Object
                || OptionalString(block, "type") != "tool_use")
            {
                return;
            }

            _toolBlocks[index] = (OptionalString(block, "name") ?? string.Empty, new StringBuilder());
        }

        private void AbsorbBlockDelta(JsonElement root)
        {
            if (!root.TryGetProperty("index", out var indexNode)
                || !indexNode.TryGetInt32(out var index)
                || !root.TryGetProperty("delta", out var delta)
                || delta.ValueKind != JsonValueKind.Object
                || !delta.TryGetProperty("type", out var typeNode)
                || typeNode.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeNode.GetString())
            {
                case "text_delta":
                    if (OptionalString(delta, "text") is { Length: > 0 } text)
                    {
                        AppendContent(text);
                    }

                    break;
                case "thinking_delta":
                    if (OptionalString(delta, "thinking") is { Length: > 0 } thinking)
                    {
                        AppendReasoning(thinking);
                    }

                    break;
                case "input_json_delta":
                    if (OptionalString(delta, "partial_json") is { Length: > 0 } json
                        && _toolBlocks.TryGetValue(index, out var slot))
                    {
                        slot.Args.Append(json);
                    }

                    break;
            }
        }

        internal void AbsorbContentBlocks(JsonElement content)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var typeNode)
                    || typeNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                switch (typeNode.GetString())
                {
                    case "text":
                        if (OptionalString(block, "text") is { Length: > 0 } text)
                        {
                            AppendContent(text);
                        }

                        break;
                    case "thinking":
                        if (OptionalString(block, "thinking") is { Length: > 0 } thinking)
                        {
                            AppendReasoning(thinking);
                        }

                        break;
                    case "tool_use":
                        var name = OptionalString(block, "name");
                        string args = string.Empty;
                        if (block.TryGetProperty("input", out var input))
                        {
                            using var buffer = new MemoryStream();
                            using (var writer = new Utf8JsonWriter(buffer))
                            {
                                input.WriteTo(writer);
                            }

                            args = Encoding.UTF8.GetString(buffer.ToArray());
                        }

                        _toolBlocks[_toolBlocks.Count] = (name ?? string.Empty, new StringBuilder(args));
                        break;
                }
            }
        }

        protected override (string? Name, string Arguments) BuildToolCall()
        {
            if (_toolBlocks.Count == 0)
            {
                return (null, string.Empty);
            }

            var first = _toolBlocks.OrderBy(kv => kv.Key).First();
            var name = first.Value.Name;
            return (name.Length == 0 ? null : name, first.Value.Args.ToString());
        }
    }
}
