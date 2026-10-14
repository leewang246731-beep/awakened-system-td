using System.Text.Json;
using EraCore.Secrets;

namespace EraCore.Llm;

/// <summary>Outcome of one /models fetch (T-077 settings 模型拉取).</summary>
/// <param name="Ok">True iff the endpoint answered with a parseable model list.</param>
/// <param name="ModelIds">Model ids in server order; empty on failure or empty list.</param>
/// <param name="ErrorKey">Key from <see cref="LlmErrorKeys"/> on failure; null on success.</param>
/// <param name="MessageZh">简体中文结果文案（成功含域名与数量）。</param>
/// <param name="MessageEn">English result copy.</param>
public sealed record LlmModelListResult(
    bool Ok,
    IReadOnlyList<string> ModelIds,
    string? ErrorKey,
    string MessageZh,
    string MessageEn)
{
    /// <summary>Success copy format (zh): {0}=host, {1}=model count.</summary>
    public const string SuccessZhFormat = "已从 {0} 拉取 {1} 个模型。";

    /// <summary>Success copy format (en): {0}=host, {1}=model count.</summary>
    public const string SuccessEnFormat = "Fetched {1} model(s) from {0}.";
}

/// <summary>
/// C-16 /models fetch for the C-26 settings page (T-077; 方案 §11.6 模型
/// 可 /models 拉取+手填). GETs {base}/models (openai protocol) or
/// {base}/v1/models (anthropic protocol) through the injected
/// <see cref="IHttpPort"/> — EraCore itself never touches networking
/// (CS-1.3). Enforcement mirrors <see cref="LlmGateway.RouteAsync"/>: the
/// call is refused with ZERO network unless the provider is enabled, its
/// endpoint authority passes the supplied <see cref="LlmDomainWhitelist"/>
/// (the settings page passes the gateway's own frozen whitelist), and a
/// key exists in <see cref="ISecretStore"/>; auth headers come from the
/// same protocol adapters, so key material never leaves the headers path
/// (RL5).
/// </summary>
public static class LlmModelList
{
    /// <summary>Fetches the model list for one provider row.</summary>
    /// <param name="provider">Provider row (its own base/protocol/timeouts apply).</param>
    /// <param name="port">Wire port (real networking lives outside EraCore).</param>
    /// <param name="secrets">Provider key storage.</param>
    /// <param name="whitelist">
    /// Domain lock; defaults to the provider's own base URL authority. The
    /// settings page passes the gateway whitelist so the fetch obeys exactly
    /// the domains the player configured.
    /// </param>
    public static async Task<LlmModelListResult> FetchAsync(
        LlmProviderConfig provider,
        IHttpPort port,
        ISecretStore secrets,
        LlmDomainWhitelist? whitelist = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(secrets);

        if (!provider.Enable)
        {
            return Refused(LlmErrorKeys.Disabled);
        }

        Uri endpoint;
        try
        {
            endpoint = BuildModelsEndpoint(provider);
        }
        catch (UriFormatException)
        {
            return Refused(LlmErrorKeys.InvalidConfig);
        }

        if (!(whitelist ?? LlmDomainWhitelist.FromProviders([provider])).IsAllowed(endpoint))
        {
            return Refused(LlmErrorKeys.DomainNotAllowed);
        }

        var keyValue = secrets.Retrieve(provider.Id);
        if (keyValue.Length == 0)
        {
            return Refused(LlmErrorKeys.MissingKey);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };
        ChatProtocols.Resolve(provider.Protocol).ApplyAuthHeaders(headers, keyValue);

        HttpPortResponse reply;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(provider.RequestTimeoutS));
            reply = await port.SendAsync(
                new HttpPortRequest("GET", endpoint, headers, string.Empty,
                    TimeSpan.FromSeconds(provider.ConnectTimeoutS)),
                budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Refused(LlmErrorKeys.TimeoutFirstByte);
        }
        catch (Exception)
        {
            // Port contract: transport failures surface as exceptions.
            return Refused(LlmErrorKeys.Network);
        }

        if (!reply.IsSuccess)
        {
            return Refused(
                LlmErrorClassifier.ClassifyHttp(reply.StatusCode, Truncate(reply.Body)));
        }

        List<string> ids;
        var bodyText = reply.Body ?? ReadAll(reply.BodyStream);
        if (bodyText is null)
        {
            return Refused(LlmErrorKeys.BadResponse);
        }

        try
        {
            ids = ParseModelIds(bodyText);
        }
        catch (JsonException)
        {
            return Refused(LlmErrorKeys.BadResponse);
        }

        var host = provider.BaseUri.Host;
        return new LlmModelListResult(
            true, ids, null,
            string.Format(LlmModelListResult.SuccessZhFormat, host, ids.Count),
            string.Format(LlmModelListResult.SuccessEnFormat, host, ids.Count));
    }

    /// <summary>Endpoint shape: openai {base}/models; anthropic {base}/v1/models (same version segment as /v1/messages).</summary>
    private static Uri BuildModelsEndpoint(LlmProviderConfig provider) => provider.Protocol switch
    {
        LlmProtocol.Anthropic => OpenAiProtocol.Combine(provider.BaseUri, "v1/models"),
        _ => OpenAiProtocol.Combine(provider.BaseUri, "models"),
    };

    /// <summary>Parses the OpenAI/Anthropic shared shape {"data":[{"id":…},…]}.</summary>
    private static List<string> ParseModelIds(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        var ids = new List<string>();
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("models 响应缺少 data 数组");
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private static LlmModelListResult Refused(string errorKey)
    {
        _ = LlmErrorCatalog.TryGetCopy(errorKey, out var zh, out var en);
        return new LlmModelListResult(false, [], errorKey, zh, en);
    }

    private static string? Truncate(string? body) =>
        body is null ? null : body.Length <= LlmGateway.MaxErrorBodySnippetChars
            ? body
            : body[..LlmGateway.MaxErrorBodySnippetChars];

    private static string? ReadAll(Stream? stream)
    {
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
