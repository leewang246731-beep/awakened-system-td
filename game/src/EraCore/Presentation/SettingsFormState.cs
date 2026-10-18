using System.Text.Json;
using EraCore.Llm;

namespace EraCore.Presentation;

/// <summary>
/// Editable field bundle of the C-26 BYOK settings form (T-077 / F-081;
/// 方案 §11.6 设置界面字段清单): one provider row of llm.config.json plus
/// the global logging switches, as the form sees it. Deliberately carries
/// NO api-key value — key material transits only
/// <see cref="EraCore.Secrets.ISecretStore"/> (F-084); the form works with
/// a presence flag the presenter passes into <see cref="SettingsFormState.Review"/>.
/// </summary>
public sealed record ByokFormFields
{
    /// <summary>Provider row id (set by the template dropdown or the loaded row; not player-editable text).</summary>
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>Display name (UI only; preserved through save).</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>§11.6 note passthrough (preset/usage note kept verbatim on save).</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Wire protocol radio: openai | anthropic (path/auth/SSE grammar selector).</summary>
    public LlmProtocol Protocol { get; init; }

    /// <summary>base_url text — whitelist source and declaration insert.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Model id (manual entry or /models fetch).</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Provider enable switch (false = gateway refuses with zero network).</summary>
    public bool Enable { get; init; }

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; init; }

    /// <summary>Response token cap.</summary>
    public int MaxTokens { get; init; }

    /// <summary>Whole-request budget hint (seconds).</summary>
    public int RequestTimeoutS { get; init; }

    /// <summary>Connect-phase budget (seconds).</summary>
    public int ConnectTimeoutS { get; init; }

    /// <summary>Retry budget.</summary>
    public int Retries { get; init; }

    /// <summary>History rounds kept in context.</summary>
    public int HistoryRounds { get; init; }

    /// <summary>Context budget in tokens.</summary>
    public int ContextBudgetTokens { get; init; }

    /// <summary>True = SSE streaming; false = single JSON response.</summary>
    public bool UseStream { get; init; }

    /// <summary>Player-visible call log switch (§11.6 logging.show_call_log).</summary>
    public bool ShowCallLog { get; init; }

    /// <summary>Full conversation capture switch (§11.6 logging.log_conversation).</summary>
    public bool LogConversation { get; init; }

    /// <summary>Call log retention count (§11.6 logging.log_retention).</summary>
    public int LogRetention { get; init; }

    /// <summary>Maps one provider row + logging block into form fields (field→row inverse of the private ToProvider).</summary>
    public static ByokFormFields FromProvider(
        LlmProviderConfig provider, LlmLoggingConfig logging) => new()
    {
        ProviderId = provider.Id,
        ProviderName = provider.Name,
        Note = provider.Note,
        Protocol = provider.Protocol,
        BaseUrl = provider.BaseUrl,
        Model = provider.Model,
        Enable = provider.Enable,
        Temperature = provider.Temperature,
        MaxTokens = provider.MaxTokens,
        RequestTimeoutS = provider.RequestTimeoutS,
        ConnectTimeoutS = provider.ConnectTimeoutS,
        Retries = provider.Retries,
        HistoryRounds = provider.HistoryRounds,
        ContextBudgetTokens = provider.ContextBudgetTokens,
        UseStream = provider.UseStream,
        ShowCallLog = logging.ShowCallLog,
        LogConversation = logging.LogConversation,
        LogRetention = logging.LogRetention,
    };
}

/// <summary>Form validation outcome: blocking errors vs advisory warnings.</summary>
/// <param name="Errors">Blocking problems (bad URL, out-of-range numbers); save is refused.</param>
/// <param name="Warnings">Advisories (missing key, protocol/endpoint suspicion); save proceeds.</param>
public sealed record ByokFormReview(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    /// <summary>True when saving is allowed (§11.6 空 key 警示不阻保存).</summary>
    public bool CanSave => Errors.Count == 0;
}

/// <summary>
/// Pure form-state machine behind the C-26 BYOK settings page (T-077,
/// F-081; 方案 §11.6). Owns three jobs the Godot presenter must not carry
/// (CS-1.6): field→<see cref="LlmConfig"/> mapping (template dropdown +
/// upsert-on-save), validation (reusing <see cref="LlmConfigLoader"/> on a
/// single-row candidate config, plus the two §11.6 advisories: empty key
/// warns but never blocks; anthropic protocol against an OpenAI-style
/// trailing-/v1 base_url warns about endpoint shape), and dirty checking
/// against the loaded baseline. File I/O stays with the presenter — this
/// class only serializes the finished document (source-gen, D-05).
/// </summary>
public sealed class SettingsFormState
{
    /// <summary>llm.config.json file name (relative to the injected IFileStore root).</summary>
    public const string ConfigFileName = "llm.config.json";

    /// <summary>常驻声明 format (§11.6 verbatim): {0} = current base_url.</summary>
    public const string DeclarationZhFormat =
        "请求将发送至你配置的第三方服务（{0}）；密钥仅存本机；本游戏不收取任何费用。";

    /// <summary>Declaration insert when base_url is still blank.</summary>
    public const string DeclarationBlankZh = "尚未配置 base_url";

    /// <summary>Coding Plan 白名单提示 (§11.6 zhipu note, verbatim wording).</summary>
    public const string CodingPlanNoticeZh =
        "GLM Coding Plan 订阅密钥仅限官方编程工具白名单使用，不能用于本游戏（服务端拒绝，错误码 1315）。";

    /// <summary>Advisory when an enabled row has no key in the secret store (警示不阻保存).</summary>
    public const string MissingKeyWarningZh =
        "未录入 API Key：配置仍可保存，但调用会被拒绝（missing-key）；密钥仅存本机凭据存储，可稍后在设置界面录入。";

    /// <summary>T-138 语音（预留）行标题——设置页灰置条目，纯说明非开关（F-140）。</summary>
    public const string TtsReservedCaptionZh = "语音（预留）";

    /// <summary>T-138 语音（预留）行说明文案（任务卡 verbatim 口径；渲染层原样上屏）。</summary>
    public const string TtsReservedNoticeZh = "语音通道已预留，功能随后续版本开放";

    /// <summary>
    /// T-155 时长遥测行标题——设置页披露条目（F-006；数据流向声明口径同
    /// T-109/BYOK 先例：本地文件、不上传、可删）。
    /// </summary>
    public const string TelemetryNoticeCaptionZh = "时长遥测（本地）";

    /// <summary>
    /// T-155 时长遥测行说明文案：逐字上屏——本地 JSONL、零上传零联网、
    /// 删除即清除。路径只述相对段（真实根=宿主注入盘，发布件任意盘纪律）。
    /// </summary>
    public const string TelemetryNoticeZh =
        "每关/每局用时自动记入本机数据目录 telemetry/durations.jsonl（仅用于校准关卡时长）；该文件不上传、不联网，随时可手动删除。";

    private static readonly LlmProviderConfig[] TemplateRows =
    [
        LlmProviderPresets.ZhipuGlm(),
        LlmProviderPresets.XiaomiMiMo(),
        LlmProviderPresets.CustomRelay(),
        LlmProviderPresets.LocalLmStudio(),
    ];

    // Non-null seeds (CS8618): the constructor immediately re-loads the
    // default config; these only satisfy definite-assignment before that.
    private ByokFormFields _fields = InitialFields();
    private ByokFormFields _baseline = InitialFields();
    private bool _keyDirty;

    /// <summary>Fresh state over the factory default config (active row = zhipu-glm).</summary>
    public SettingsFormState()
    {
        Load(LlmProviderPresets.DefaultConfig());
        _keyDirty = false;
    }

    private static ByokFormFields InitialFields() => ByokFormFields.FromProvider(
        LlmProviderPresets.ZhipuGlm(), new LlmLoggingConfig());

    /// <summary>The four dropdown templates (F-081 四模板下拉), single-sourced from <see cref="LlmProviderPresets"/>.</summary>
    public static IReadOnlyList<LlmProviderConfig> Templates => TemplateRows;

    /// <summary>Current editable fields.</summary>
    public ByokFormFields Fields => _fields;

    /// <summary>True when fields (or the key entry) differ from the loaded/persisted baseline.</summary>
    public bool IsDirty => !_fields.Equals(_baseline) || _keyDirty;

    /// <summary>
    /// Loads the config's active row (falling back to the first enabled row,
    /// then the first row) plus logging switches; resets the dirty baseline.
    /// </summary>
    public void Load(LlmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var row = config.FindProvider(config.ActiveProvider)
            ?? config.Providers.FirstOrDefault(p => p.Enable)
            ?? config.Providers.FirstOrDefault();
        if (row is null)
        {
            Load(LlmProviderPresets.DefaultConfig());
            return;
        }

        Adopt(ByokFormFields.FromProvider(row, config.Logging));
    }

    /// <summary>Loads one specific provider row by id; resets the dirty baseline.</summary>
    /// <exception cref="ArgumentException">No row with that id.</exception>
    public void Load(LlmConfig config, string providerId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("provider id 不能为空", nameof(providerId));
        }

        var row = config.FindProvider(providerId)
            ?? throw new ArgumentException(
                $"provider '{providerId}' 不在配置中", nameof(providerId));
        Adopt(ByokFormFields.FromProvider(row, config.Logging));
    }

    /// <summary>
    /// Fills the form from one of the four templates (dropdown select):
    /// provider identity and note come from the preset; the global logging
    /// switches stay at their current positions.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown template id.</exception>
    public void SelectTemplate(string templateId)
    {
        var template = TemplateRows.FirstOrDefault(
            t => string.Equals(t.Id, templateId, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"未知模板 id '{templateId}'（可选：{string.Join("/", TemplateRows.Select(t => t.Id))}）",
                nameof(templateId));

        _fields = ByokFormFields.FromProvider(
            template,
            new LlmLoggingConfig
            {
                ShowCallLog = _fields.ShowCallLog,
                LogConversation = _fields.LogConversation,
                LogRetention = _fields.LogRetention,
            });
    }

    /// <summary>Applies one immutable field edit (the presenter's only write path).</summary>
    public void Edit(Func<ByokFormFields, ByokFormFields> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        _fields = edit(_fields) ?? throw new InvalidOperationException("编辑器返回了 null 字段集");
    }

    /// <summary>Records that a key was typed (dirty until persisted; the value never enters this class).</summary>
    public void NotifyKeyEdited() => _keyDirty = true;

    /// <summary>Records that the stored key was cleared (dirty until persisted).</summary>
    public void NotifyKeyCleared() => _keyDirty = true;

    /// <summary>Clears the dirty flags after a successful save round-trip.</summary>
    public void MarkPersisted()
    {
        _baseline = _fields;
        _keyDirty = false;
    }

    /// <summary>
    /// Validates the current fields. Errors come from
    /// <see cref="LlmConfigLoader.ValidateConfig"/> over a single-row
    /// candidate config (blank base_url/model stay legal on disabled rows —
    /// that is how the §11.6 presets ship); warnings are the two §11.6
    /// advisories (empty key, protocol/endpoint shape).
    /// </summary>
    /// <param name="keyAvailable">True when the store holds a key for the row or one was just typed.</param>
    public ByokFormReview Review(bool keyAvailable)
    {
        var row = ToProvider(_fields);
        var candidate = new LlmConfig
        {
            Version = LlmConfigLoader.SupportedVersion,
            ActiveProvider = row.Id,
            Providers = [row],
            Logging = ToLogging(_fields),
        };

        var errors = LlmConfigLoader.ValidateConfig(candidate);
        var warnings = new List<string>();
        if (_fields.Enable && !keyAvailable)
        {
            warnings.Add(MissingKeyWarningZh);
        }

        if (ProtocolEndpointSuspect(_fields))
        {
            warnings.Add(
                "协议与端点可能不匹配：anthropic 协议会请求 {base_url}/v1/messages，"
                + "而以 /v1 结尾的 base_url 通常是 OpenAI 形端点（如需 Anthropic 兼容端点，"
                + "请填写不带 /v1 后缀的服务根地址）。");
        }

        return new ByokFormReview(errors, warnings);
    }

    /// <summary>
    /// Builds the next config document: the edited row upserts into
    /// <paramref name="baseline"/> (replaces its id-matching row, else is
    /// appended); the edited row becomes active_provider exactly when it is
    /// enabled, otherwise the baseline's active id survives; logging
    /// switches carry over. Validation is <see cref="Review"/> — call it
    /// first, the presenter refuses to save on errors.
    /// </summary>
    public LlmConfig BuildConfig(LlmConfig baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var row = ToProvider(_fields);
        var providers = new List<LlmProviderConfig>(baseline.Providers.Count + 1);
        var replaced = false;
        foreach (var existing in baseline.Providers)
        {
            if (string.Equals(existing.Id, row.Id, StringComparison.Ordinal))
            {
                providers.Add(row);
                replaced = true;
            }
            else
            {
                providers.Add(existing);
            }
        }

        if (!replaced)
        {
            providers.Add(row);
        }

        string active = row.Enable || baseline.ActiveProvider.Length == 0
            ? row.Id
            : baseline.ActiveProvider;

        return new LlmConfig
        {
            Version = LlmConfigLoader.SupportedVersion,
            ActiveProvider = active,
            Providers = providers,
            Logging = ToLogging(_fields),
            // T-138 (F-140): the reserved tts section is not a form field —
            // save carries the loaded baseline's section through verbatim.
            Tts = baseline.Tts ?? new LlmTtsConfig(),
        };
    }

    /// <summary>常驻声明 with the current base_url inserted (blank → <see cref="DeclarationBlankZh"/>).</summary>
    public string DeclarationText() => DeclarationFor(_fields.BaseUrl);

    /// <summary>Declaration for an arbitrary base URL (the footer refreshes per keystroke).</summary>
    public static string DeclarationFor(string baseUrl) => string.Format(
        DeclarationZhFormat,
        string.IsNullOrWhiteSpace(baseUrl) ? DeclarationBlankZh : baseUrl.Trim());

    /// <summary>Serializes one config document via the source-gen context (D-05); the ONLY writer of llm.config.json.</summary>
    public static string Serialize(LlmConfig config) =>
        JsonSerializer.Serialize(config, LlmJsonContext.Default.LlmConfig);

    private void Adopt(ByokFormFields fields)
    {
        _fields = fields;
        _baseline = fields;
        _keyDirty = false;
    }

    private static LlmProviderConfig ToProvider(ByokFormFields f) => new()
    {
        Id = f.ProviderId.Trim(),
        Name = f.ProviderName.Trim(),
        Note = f.Note,
        Protocol = f.Protocol,
        BaseUrl = f.BaseUrl.Trim(),
        Model = f.Model.Trim(),
        Enable = f.Enable,
        Temperature = f.Temperature,
        MaxTokens = f.MaxTokens,
        RequestTimeoutS = f.RequestTimeoutS,
        ConnectTimeoutS = f.ConnectTimeoutS,
        Retries = f.Retries,
        HistoryRounds = f.HistoryRounds,
        ContextBudgetTokens = f.ContextBudgetTokens,
        UseStream = f.UseStream,
    };

    private static LlmLoggingConfig ToLogging(ByokFormFields f) => new()
    {
        ShowCallLog = f.ShowCallLog,
        LogConversation = f.LogConversation,
        LogRetention = f.LogRetention,
    };

    /// <summary>
    /// Protocol/endpoint shape suspicion (§11.6 连接测试前置提醒): an
    /// anthropic-protocol base_url whose path ends in "/v1" reads like an
    /// OpenAI-style endpoint — the wire call would then double the version
    /// segment ({base}/v1/messages on top of a /v1 base). Advisory only:
    /// exotic relays exist, so this warns instead of blocking.
    /// </summary>
    private static bool ProtocolEndpointSuspect(ByokFormFields f)
    {
        if (f.Protocol != LlmProtocol.Anthropic
            || !Uri.TryCreate(f.BaseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
    }
}
