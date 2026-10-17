using System.Text.Json;

namespace EraCore.Llm;

/// <summary>Outcome of parsing one llm.config.json document.</summary>
/// <param name="Ok">True only when the document parsed AND passed semantic validation.</param>
/// <param name="Config">The parsed config; null when <paramref name="Ok"/> is false.</param>
/// <param name="Errors">Every problem found, in document order (empty on success).</param>
public sealed record LlmConfigLoadResult(
    bool Ok,
    LlmConfig? Config,
    IReadOnlyList<string> Errors)
{
    /// <summary>Formats the error list for diagnostics (never contains key material).</summary>
    public override string ToString() =>
        Ok ? "llm.config.json OK" : string.Join("; ", Errors);
}

/// <summary>
/// Parser + validator for the BYOK config file (F-080, T-072; 方案 §11.6).
/// Pure text-in/result-out — file I/O and the <c>user://llm.config.json</c>
/// location belong to the T-077 settings UI. Three jobs:
/// <list type="bullet">
/// <item>reject any document that carries a non-empty <c>api_key</c> value
/// — the config file must NEVER hold key material (RL5/F-084; keys live in
/// ISecretStore);</item>
/// <item>deserialize via the source-gen context (D-05) with exact enum
/// names for <c>protocol</c>;</item>
/// <item>run the semantic validation in <see cref="ValidateConfig"/>
/// (version, active provider, per-provider invariants).</item>
/// </list>
/// </summary>
public static class LlmConfigLoader
{
    /// <summary>The one config schema version this build understands.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Parses and validates one llm.config.json document.</summary>
    public static LlmConfigLoadResult Load(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LlmConfigLoadResult(false, null, ["llm.config.json 为空"]);
        }

        List<string> errors = [];
        RejectApiKeyInFile(json, errors);
        if (errors.Count > 0)
        {
            return new LlmConfigLoadResult(false, null, errors);
        }

        LlmConfig config;
        try
        {
            config = JsonSerializer.Deserialize(
                json, LlmJsonContext.Default.LlmConfig)
                ?? throw new JsonException("根元素不是对象");
        }
        catch (JsonException ex)
        {
            return new LlmConfigLoadResult(false, null, [$"JSON 解析失败：{ex.Message}"]);
        }

        config = Normalize(config);
        errors.AddRange(ValidateConfig(config));
        return errors.Count == 0
            ? new LlmConfigLoadResult(true, config, [])
            : new LlmConfigLoadResult(false, null, errors);
    }

    /// <summary>
    /// Semantic validation beyond JSON shape. Enabled providers must be
    /// fully callable (absolute http(s) base URL without userinfo/fragment,
    /// non-empty model, sane numeric ranges); disabled template rows may be
    /// blank — that is how the §11.6 presets ship.
    /// </summary>
    public static IReadOnlyList<string> ValidateConfig(LlmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();
        if (config.Version != SupportedVersion)
        {
            errors.Add($"version 必须是 {SupportedVersion}，收到 {config.Version}");
        }

        if (config.Providers.Count == 0)
        {
            errors.Add("providers 不能为空");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id))
            {
                errors.Add("provider.id 不能为空");
            }
            else if (!ids.Add(provider.Id))
            {
                errors.Add($"provider.id 重复：'{provider.Id}'");
            }

            ValidateNumericFields(provider, errors);

            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                if (provider.Enable)
                {
                    errors.Add($"provider '{provider.Id}' 已启用但 base_url 为空");
                }

                continue;
            }

            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"provider '{provider.Id}' base_url 不是 http(s) 绝对地址：'{provider.BaseUrl}'");
                continue;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length > 0)
            {
                errors.Add($"provider '{provider.Id}' base_url 不允许携带 userinfo 或 fragment");
            }

            if (provider.Enable && string.IsNullOrWhiteSpace(provider.Model))
            {
                errors.Add($"provider '{provider.Id}' 已启用但 model 为空");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ActiveProvider))
        {
            var active = config.Providers.FirstOrDefault(
                p => string.Equals(p.Id, config.ActiveProvider, StringComparison.Ordinal));
            if (active is null)
            {
                errors.Add($"active_provider '{config.ActiveProvider}' 不在 providers 列表中");
            }
        }
        else
        {
            errors.Add("active_provider 不能为空");
        }

        if (config.Logging.LogRetention < 1)
        {
            errors.Add("logging.log_retention 必须 ≥ 1");
        }

        // T-138 (F-140) 预留 tts 段：仅当玩家显式启用时才要求其引用完整——
        // 默认 off 的出厂段（含手写缺省）零约束通过。
        if (config.Tts is { Enable: true } tts)
        {
            if (string.IsNullOrWhiteSpace(tts.Provider)
                || string.IsNullOrWhiteSpace(tts.Model)
                || string.IsNullOrWhiteSpace(tts.Voice))
            {
                errors.Add("tts 已启用但 provider/model/voice 不完整");
            }
            else if (config.FindProvider(tts.Provider) is null)
            {
                errors.Add($"tts.provider '{tts.Provider}' 不在 providers 列表中");
            }
        }

        return errors;
    }

    /// <summary>
    /// Re-applies defaults that source-gen deserialization wipes: absent
    /// <c>init</c> properties deserialize as null (property initializers
    /// are not honored by the generated reader), so every nullable surface
    /// is normalized back to its schema default before validation. Blank
    /// template rows thereby behave identically whether they came from
    /// presets (constructed) or from disk (parsed).
    /// </summary>
    internal static LlmConfig Normalize(LlmConfig config) => config with
    {
        ActiveProvider = config.ActiveProvider ?? string.Empty,
        Providers = (config.Providers ?? [])
            .Select(NormalizeProvider)
            .ToArray(),
        Logging = config.Logging ?? new LlmLoggingConfig(),
        Tts = NormalizeTts(config.Tts),
    };

    /// <summary>
    /// Provider-row normalization counterpart of <see cref="Normalize"/>:
    /// null string fields (absent in JSON) become empty strings; numeric
    /// surfaces stay as parsed (0 = "missing" → caught by range checks).
    /// </summary>
    internal static LlmProviderConfig NormalizeProvider(LlmProviderConfig provider) =>
        provider with
        {
            Id = provider.Id ?? string.Empty,
            Name = provider.Name ?? string.Empty,
            BaseUrl = provider.BaseUrl ?? string.Empty,
            Model = provider.Model ?? string.Empty,
            Note = provider.Note ?? string.Empty,
        };

    /// <summary>
    /// tts-section normalization counterpart (T-138, F-140): an absent
    /// section becomes the factory default (off); absent string fields
    /// fall back to their schema defaults (model → the reserved
    /// mimo-v2.5-tts value) so a hand-minimized <c>"tts": {"enable": true}</c>
    /// behaves identically to a fully written one.
    /// </summary>
    internal static LlmTtsConfig NormalizeTts(LlmTtsConfig? tts) =>
        tts is null
            ? new LlmTtsConfig()
            : tts with
            {
                Provider = tts.Provider ?? string.Empty,
                Model = tts.Model ?? TtsProviderPresets.MimoTtsModelId,
                Voice = tts.Voice ?? string.Empty,
            };

    private static void ValidateNumericFields(LlmProviderConfig provider, List<string> errors)
    {
        void AddIf(bool bad, string message)
        {
            if (bad)
            {
                errors.Add($"provider '{provider.Id}' {message}");
            }
        }

        AddIf(provider.Temperature < 0.0 || provider.Temperature > 2.0, "temperature 必须在 [0,2]");
        AddIf(provider.MaxTokens < 1 || provider.MaxTokens > 32768, "max_tokens 必须在 [1,32768]");
        AddIf(provider.RequestTimeoutS < 1, "request_timeout_s 必须 ≥ 1");
        AddIf(provider.ConnectTimeoutS < 1, "connect_timeout_s 必须 ≥ 1");
        AddIf(provider.Retries < 0 || provider.Retries > 5, "retries 必须在 [0,5]");
        AddIf(provider.HistoryRounds < 0, "history_rounds 必须 ≥ 0");
        AddIf(provider.ContextBudgetTokens < 1, "context_budget_tokens 必须 ≥ 1");
    }

    /// <summary>
    /// Scans the raw document for a non-empty api_key property ANYWHERE
    /// (top level or inside a provider row) and records a rejection. A key
    /// in the config file would leak through backups and syncs; the loader
    /// fails loudly instead of ignoring it silently.
    /// </summary>
    private static void RejectApiKeyInFile(string json, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            ScanNode(document.RootElement, errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"JSON 解析失败：{ex.Message}");
        }
    }

    private static void ScanNode(JsonElement node, List<string> errors)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("api_key")
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(property.Value.GetString()))
                    {
                        errors.Add(
                            "配置文件不允许携带 api_key（RL5/F-084）：Key 只存本机凭据存储（ISecretStore），请经设置界面录入");
                    }

                    ScanNode(property.Value, errors);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    ScanNode(item, errors);
                }

                break;
        }
    }
}
