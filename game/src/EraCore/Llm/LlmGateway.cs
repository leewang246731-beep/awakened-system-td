using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EraCore.Contracts.Agent;
using EraCore.Secrets;

namespace EraCore.Llm;

/// <summary>
/// Rich result of one gateway call (used by the connection probe and
/// future T-091 instrumentation; <see cref="ILlmTransport"/> is the frozen
/// two-field projection of it).
/// </summary>
/// <param name="Ok">True iff a tool call was captured and merged into ActionJson.</param>
/// <param name="ActionJson">Candidate action JSON ({"action":tool,…args}) or null.</param>
/// <param name="ErrorKey">Key from <see cref="LlmErrorKeys"/>; null on success.</param>
/// <param name="Outcome">Reduced stream outcome when a reply body was parsed; else null.</param>
/// <param name="StatusCode">HTTP status when a reply arrived; else null.</param>
/// <param name="Elapsed">Wall time of the whole call (diagnostics only).</param>
public sealed record LlmGatewayCallResult(
    bool Ok,
    string? ActionJson,
    string? ErrorKey,
    LlmStreamOutcome? Outcome,
    int? StatusCode,
    TimeSpan Elapsed);

/// <summary>
/// C-16 LlmGateway 协议机 (T-072, F-162/F-079/F-083): the ONLY path LLM
/// traffic takes in this game (BYOK 架构裁决 D). Implements the K-9
/// transport-neutral <see cref="ILlmTransport"/> port over the injected
/// <see cref="IHttpPort"/> wire — EraCore itself never touches
/// System.Net.Http (CS-1.3). Guarantees per call:
/// <list type="bullet">
/// <item>整体禁用零网络 — disabled provider ⇒ classified refusal BEFORE
/// the wire port is ever touched;</item>
/// <item>域名白名单 — endpoint authority must be one of the configured
/// base_url authorities (frozen at construction) or the call is refused
/// with zero network;</item>
/// <item>Key 不过境配置 — keys are fetched from <see cref="ISecretStore"/>
/// per call and never embedded in results/logs (RL5/K-10);</item>
/// <item>双超时 — first-byte and chunk-idle budgets enforced on the
/// stream reader (方案 §11.7);</item>
/// <item>双协议 — openai/anthropic request building + SSE reduction via
/// <see cref="ChatProtocols"/>.</item>
/// </list>
/// </summary>
public sealed class LlmGateway : ILlmTransport
{
    /// <summary>Default first-byte budget (§11.7 首字节 10~15s, 取 15).</summary>
    public static readonly TimeSpan DefaultFirstByteTimeout = TimeSpan.FromSeconds(15); // ci-allow: R7 T-072 §11.7 双超时首字节上限 15s（调用工程域常量，非平衡数值）

    /// <summary>Default chunk-idle budget (§11.7 chunk 空闲 30s).</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the error-body snippet handed to classification.</summary>
    public const int MaxErrorBodySnippetChars = 4096;

    private readonly LlmConfig _config;
    private readonly IHttpPort _port;
    private readonly ISecretStore _secrets;
    private readonly LlmDomainWhitelist _whitelist;
    private readonly TimeSpan _firstByteTimeout;
    private readonly TimeSpan _idleTimeout;

    /// <param name="config">Config snapshot; the whitelist freezes from it here.</param>
    /// <param name="port">Wire port (real networking lives outside EraCore).</param>
    /// <param name="secrets">Provider key storage (key per provider id).</param>
    /// <param name="firstByteTimeout">Override for tests/production tuning.</param>
    /// <param name="idleTimeout">Override for tests/production tuning.</param>
    /// <param name="whitelist">
    /// Explicit domain lock — production derives it from
    /// <paramref name="config"/>; the parameter exists so the enforcement
    /// itself is provable (a whitelist missing the provider's host must
    /// abort with zero network, which no config-only test can express).
    /// </param>
    public LlmGateway(
        LlmConfig config,
        IHttpPort port,
        ISecretStore secrets,
        TimeSpan? firstByteTimeout = null,
        TimeSpan? idleTimeout = null,
        LlmDomainWhitelist? whitelist = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _firstByteTimeout = firstByteTimeout ?? DefaultFirstByteTimeout;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _whitelist = whitelist ?? LlmDomainWhitelist.FromProviders(config.Providers);
    }

    /// <summary>The frozen outbound-domain lock this gateway enforces.</summary>
    public LlmDomainWhitelist Whitelist => _whitelist;

    /// <summary>Active provider id of the frozen snapshot (diagnostics/probe).</summary>
    public string ActiveProviderId => _config.ActiveProvider;

    /// <summary>Looks up one provider row by id in the frozen snapshot.</summary>
    public LlmProviderConfig? FindProvider(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _config.FindProvider(providerId);

    /// <summary>K-9 transport port: one request → candidate action or classified failure.</summary>
    public async Task<LlmTransportResult> SendAsync(
        LlmTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = _config.FindProvider(_config.ActiveProvider);
        var call = await RouteAsync(provider, request.Messages, request.ToolsJson, cancellationToken)
            .ConfigureAwait(false);
        return new LlmTransportResult(call.ActionJson, call.ErrorKey);
    }

    /// <summary>
    /// Routes one chat to a specific provider row (used by the K-9 port
    /// with the active provider and by the connection probe with any row).
    /// All zero-network refusals happen here, in this order: provider
    /// existence → enable flag → endpoint/whitelist → key presence → body
    /// build. Only then does the wire port see a request.
    /// </summary>
    public async Task<LlmGatewayCallResult> RouteAsync(
        LlmProviderConfig? provider,
        IReadOnlyList<LlmChatMessage> messages,
        string toolsJson,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (provider is null)
        {
            return Result(false, null, LlmErrorKeys.UnknownProvider, null, null, stopwatch.Elapsed);
        }

        if (!provider.Enable)
        {
            return Result(false, null, LlmErrorKeys.Disabled, null, null, stopwatch.Elapsed);
        }

        var chatProtocol = ChatProtocols.Resolve(provider.Protocol);
        Uri endpoint;
        try
        {
            endpoint = chatProtocol.BuildEndpoint(provider.BaseUri);
        }
        catch (UriFormatException)
        {
            return Result(false, null, LlmErrorKeys.InvalidConfig, null, null, stopwatch.Elapsed);
        }

        if (!_whitelist.IsAllowed(endpoint))
        {
            return Result(false, null, LlmErrorKeys.DomainNotAllowed, null, null, stopwatch.Elapsed);
        }

        var apiKey = _secrets.Retrieve(provider.Id);
        if (apiKey.Length == 0)
        {
            return Result(false, null, LlmErrorKeys.MissingKey, null, null, stopwatch.Elapsed);
        }

        var spec = new LlmChatRequestSpec(
            provider.Model, messages, toolsJson,
            provider.Temperature, provider.MaxTokens, provider.UseStream);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = provider.UseStream ? "text/event-stream" : "application/json",
        };
        string bodyJson;
        try
        {
            chatProtocol.ApplyAuthHeaders(headers, apiKey);
            bodyJson = chatProtocol.BuildRequestBody(spec);
        }
        catch (JsonException)
        {
            return Result(false, null, LlmErrorKeys.BadTools, null, null, stopwatch.Elapsed);
        }

        HttpPortRequest wireRequest = new(
            "POST", endpoint, headers, bodyJson,
            TimeSpan.FromSeconds(provider.ConnectTimeoutS));

        HttpPortResponse wireReply;
        try
        {
            // The first-byte budget covers the whole connect+headers phase
            // (DNS/TLS/request flight), then the stream reader takes over
            // with the idle budget (§11.7 双超时).
            using var firstByteBudget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstByteBudget.CancelAfter(_firstByteTimeout);
            wireReply = await _port.SendAsync(wireRequest, firstByteBudget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(
                false, null, LlmErrorKeys.TimeoutFirstByte, null, null, stopwatch.Elapsed);
        }
        catch (Exception)
        {
            // Port contract: transport failures (DNS/TLS/refused/unreachable)
            // surface as exceptions; classified as network here.
            return Result(false, null, LlmErrorKeys.Network, null, null, stopwatch.Elapsed);
        }

        if (!wireReply.IsSuccess)
        {
            var snippet = Truncate(wireReply.Body);
            return Result(
                false, null, LlmErrorClassifier.ClassifyHttp(wireReply.StatusCode, snippet),
                null, wireReply.StatusCode, stopwatch.Elapsed);
        }

        LlmStreamOutcome outcome;
        try
        {
            outcome = provider.UseStream && wireReply.BodyStream is not null
                ? await ReadStreamOutcome(chatProtocol, wireReply.BodyStream, cancellationToken)
                    .ConfigureAwait(false)
                : chatProtocol.ParseNonStreamResponse(
                    wireReply.BodyStream is not null
                        ? await ReadAllAsync(wireReply.BodyStream, cancellationToken).ConfigureAwait(false)
                        : Truncate(wireReply.Body) ?? string.Empty);
        }
        catch (LlmStreamTimeoutException timeout)
        {
            return Result(false, null, timeout.ErrorKey, null, wireReply.StatusCode, stopwatch.Elapsed);
        }
        catch (JsonException)
        {
            return Result(
                false, null, LlmErrorKeys.BadResponse, null, wireReply.StatusCode, stopwatch.Elapsed);
        }

        var actionJson = outcome.SawToolCall ? MergeToolCallIntoAction(outcome) : null;
        if (actionJson is null)
        {
            return Result(
                false, null,
                outcome.ContentText.Length > 0 ? LlmErrorKeys.NoToolCall : LlmErrorKeys.BadResponse,
                outcome, wireReply.StatusCode, stopwatch.Elapsed);
        }

        return Result(true, actionJson, null, outcome, wireReply.StatusCode, stopwatch.Elapsed);
    }

    private async Task<LlmStreamOutcome> ReadStreamOutcome(
        IChatProtocol protocol, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var reducer = protocol.CreateReducer();
            var sse = new SseLineBuffer();
            var buffer = new byte[8192];
            var awaitingFirstByte = true;
            var sawDataLine = false;
            var rawLines = new List<string>();

            while (!reducer.IsDone)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(awaitingFirstByte ? _firstByteTimeout : _idleTimeout);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), budget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LlmStreamTimeoutException(
                        awaitingFirstByte ? LlmErrorKeys.TimeoutFirstByte : LlmErrorKeys.TimeoutIdle);
                }

                if (read == 0)
                {
                    break;
                }

                awaitingFirstByte = false;
                foreach (var line in sse.Feed(buffer.AsSpan(0, read)))
                {
                    rawLines.Add(line);
                    if (FeedLine(reducer, line))
                    {
                        sawDataLine = true;
                    }
                }
            }

            if (!reducer.IsDone && sse.Flush() is { } tail)
            {
                rawLines.Add(tail);
                if (FeedLine(reducer, tail))
                {
                    sawDataLine = true;
                }
            }

            if (sawDataLine)
            {
                return reducer.BuildOutcome();
            }

            // Server ignored stream:true and answered plain JSON — reduce it.
            return protocol.ParseNonStreamResponse(string.Join("\n", rawLines));
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Internal carrier for the §11.7 dual-timeout verdict (first byte vs
    /// chunk idle); never leaves <see cref="RouteAsync"/> as an exception.
    /// </summary>
    private sealed class LlmStreamTimeoutException(string errorKey) : Exception(errorKey)
    {
        public string ErrorKey { get; } = errorKey;
    }

    /// <summary>Dispatches one SSE line; returns true when it was a data line.</summary>
    private static bool FeedLine(IChatStreamReducer reducer, string line)
    {
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var payload = line["data:".Length..];
            if (payload.StartsWith(' '))
            {
                payload = payload[1..];
            }

            reducer.OnDataLine(payload);
            return true;
        }

        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            var name = line["event:".Length..].Trim();
            reducer.OnEventLine(name);
        }

        // Blank lines, comments (":…") and unknown fields are SSE noise.
        return false;
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return text;
    }

    /// <summary>
    /// Merges the reduced tool call into a candidate action JSON:
    /// <c>{"action": name, …args}</c>. The discriminator is written LAST so
    /// a model that smuggles its own <c>"action"</c> property inside the
    /// arguments cannot override the tool verb it actually called
    /// (injection defence; full validation is ISchemaValidator's job).
    /// Returns null when the arguments are neither empty nor a JSON object.
    /// </summary>
    internal static string? MergeToolCallIntoAction(LlmStreamOutcome outcome)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            var argsText = outcome.ToolArgumentsJson;
            if (argsText.Length > 0)
            {
                try
                {
                    using var args = JsonDocument.Parse(argsText);
                    if (args.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    foreach (var property in args.RootElement.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        property.Value.WriteTo(writer);
                    }
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            writer.WriteString("action", outcome.ToolName);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxErrorBodySnippetChars
            ? text
            : text[..MaxErrorBodySnippetChars];
    }

    private static LlmGatewayCallResult Result(
        bool ok, string? actionJson, string? errorKey,
        LlmStreamOutcome? outcome, int? statusCode, TimeSpan elapsed) =>
        new(ok, actionJson, errorKey, outcome, statusCode, elapsed);
}
