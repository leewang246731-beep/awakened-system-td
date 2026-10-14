using System.Text;
using System.Text.Json;
using EraCore.Contracts.Agent;

namespace EraCore.Llm;

/// <summary>
/// OpenAI-compatible chat protocol (F-083): POST {base}/chat/completions
/// with <c>Authorization: Bearer</c>, SSE frames <c>data: {choices:[{delta:…}]}</c>
/// terminated by <c>data: [DONE]</c>. Covers GLM, MiMo, LM Studio and
/// OpenAI-shaped relays (方案 §11.6/§11.7).
/// </summary>
public sealed class OpenAiProtocol : IChatProtocol
{
    /// <inheritdoc />
    public string ProtocolId => "openai";

    /// <inheritdoc />
    public Uri BuildEndpoint(Uri baseUrl) => Combine(baseUrl, "chat/completions");

    /// <inheritdoc />
    public void ApplyAuthHeaders(IDictionary<string, string> headers, string apiKey)
    {
        headers["Authorization"] = "Bearer " + apiKey;
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
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in spec.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (payload.RootElement.GetArrayLength() > 0)
            {
                writer.WritePropertyName("tools");
                payload.RootElement.WriteTo(writer);
            }

            if (spec.Stream)
            {
                writer.WriteBoolean("stream", true);
            }

            writer.WriteNumber("temperature", spec.Temperature);
            writer.WriteNumber("max_tokens", spec.MaxTokens);
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
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"回复不是 JSON 对象：'{bodyJson[..Math.Min(64, bodyJson.Length)]}'");
        }

        var choices = root.TryGetProperty("choices", out var array)
            && array.ValueKind == JsonValueKind.Array
            ? array
            : throw new JsonException("回复缺少 choices 数组");
        if (choices.GetArrayLength() == 0)
        {
            throw new JsonException("choices 为空");
        }

        var message = choices[0].TryGetProperty("message", out var first)
            && first.ValueKind == JsonValueKind.Object
            ? first
            : throw new JsonException("choices[0] 缺少 message");

        var reducer = new Reducer();
        reducer.AbsorbMessage(message);
        if (choices[0].TryGetProperty("finish_reason", out var finish)
            && finish.ValueKind == JsonValueKind.String)
        {
            reducer.FinishReason = finish.GetString();
        }

        reducer.AbsorbUsage(root);
        return reducer.BuildOutcome();
    }

    internal static Uri Combine(Uri baseUrl, string pathSegment)
    {
        if (!baseUrl.IsAbsoluteUri
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException($"base_url 必须是 http(s) 绝对地址：'{baseUrl}'");
        }

        // Trim trailing slashes on BOTH sides exactly once so
        // "…/v1/" + "chat/completions" == "…/v1" + "/chat/completions".
        var builder = new UriBuilder(baseUrl)
        {
            Path = baseUrl.AbsolutePath.TrimEnd('/') + "/" + pathSegment.TrimStart('/'),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private sealed class Reducer : ChatStreamReducerBase
    {
        private readonly SortedDictionary<int, (StringBuilder Name, StringBuilder Args)> _toolCalls = [];

        public override void OnDataLine(string payload)
        {
            if (payload == "[DONE]")
            {
                IsDone = true;
                return;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"SSE 帧不是对象：'{payload}'");
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    AbsorbChoice(choice);
                }
            }

            // Providers append the usage block on the final frame (some only
            // with stream_options; relays at any point) — last report wins.
            AbsorbUsage(root);
        }

        /// <summary>Captures <c>usage.{prompt,completion}_tokens</c> when a frame carries it.</summary>
        internal void AbsorbUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            Usage = new LlmTokenUsage(
                ReadCount(usage, "prompt_tokens"),
                ReadCount(usage, "completion_tokens"));
        }

        private static int? ReadCount(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var count)
                    ? count
                    : null;

        internal void AbsorbMessage(JsonElement message)
        {
            // Non-stream shape: whole message at once; stream shape: delta.
            var node = message;
            if (message.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object)
            {
                node = delta;
            }

            if (OptionalString(node, "reasoning_content") is { Length: > 0 } reasoning)
            {
                AppendReasoning(reasoning);
            }

            if (OptionalString(node, "content") is { Length: > 0 } content)
            {
                AppendContent(content);
            }

            if (node.TryGetProperty("tool_calls", out var calls)
                && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    AbsorbToolCall(call);
                }
            }
        }

        private void AbsorbChoice(JsonElement choice)
        {
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object)
            {
                AbsorbMessage(delta);
            }
            else if (choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                AbsorbMessage(message);
            }

            if (OptionalString(choice, "finish_reason") is { Length: > 0 } finish)
            {
                FinishReason = finish;
            }
        }

        private void AbsorbToolCall(JsonElement call)
        {
            if (!call.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int index = 0;
            if (call.TryGetProperty("index", out var indexNode)
                && indexNode.TryGetInt32(out var parsed))
            {
                index = parsed;
            }

            if (!_toolCalls.TryGetValue(index, out var slot))
            {
                slot = (new StringBuilder(), new StringBuilder());
                _toolCalls[index] = slot;
            }

            if (function.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } nameText)
            {
                slot.Name.Clear();
                slot.Name.Append(nameText);
            }

            if (function.TryGetProperty("arguments", out var args)
                && args.ValueKind == JsonValueKind.String
                && args.GetString() is { Length: > 0 } argsText)
            {
                slot.Args.Append(argsText);
            }
        }

        protected override (string? Name, string Arguments) BuildToolCall()
        {
            if (_toolCalls.Count == 0)
            {
                return (null, string.Empty);
            }

            var first = _toolCalls.First().Value;
            return (first.Name.ToString(), first.Args.ToString());
        }
    }
}
