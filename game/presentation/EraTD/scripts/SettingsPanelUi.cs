using System.Text;
using EraCore.Contracts.Foundation;
using EraCore.IO;
using EraCore.Llm;
using EraCore.Presentation;
using EraCore.Secrets;
using Godot;

namespace EraTD;

// T-077（C-26 BYOK 设置界面，F-081；方案 §11.6）：settings_panel.tscn 的唯一
// 挂载脚本。所有表单决策在 EraCore.Presentation.SettingsFormState（纯逻辑，
// 已测）——本节点只做四件事（CS-1.6）：把控件值搬进/搬出表单状态、把
// llm.config.json 读写接到 D-03 注入根（DevFileStore）、把「测试连接/拉取
// 列表」接到 C-16 的 LlmConnectionProbe/LlmModelList、把 T-102 画质档位行
// （渲染三档偏好，非 BYOK 字段）即时持久化到 settings/render-tier.json。
//
// Key 纪律（RL5/F-084）：KeyInput 的值只流向 ISecretStore——表单状态、
// llm.config.json、状态/结果文案一律不接触明文；回显永远走 KeyMasker
// （尾 4 位）。常驻声明页脚随 base_url 逐字刷新，Coding Plan 1315 提示常驻。
//
// 并行纪律：Main.cs 只追加一行 Instantiate（冲突由后提交者 rebase）；
// headless 下自带冒烟序列并打印 SETTINGS_OK（smoke 判据）。

/// <summary>
/// The C-26 BYOK settings panel (T-077): full-field editor over one
/// provider row of llm.config.json with the four-preset dropdown, masked
/// key entry backed by <see cref="ISecretStore"/>, connection probe,
/// /models fetch and the persistent data-flow declaration footer.
/// </summary>
public sealed partial class SettingsPanelUi : Control
{
    /// <summary>Synthetic smoke key (never real key material; RL5).</summary>
    internal const string SmokeKey = "synthetic-smoke-key-000111";

    /// <summary>
    /// T-106 (F-164 Web 版约束): notice replacing the whole BYOK form on the
    /// Web export — the tier switches themselves live in
    /// <see cref="WebConstraints"/> (pure, tested); this copy only states
    /// the outcome to the player.
    /// </summary>
    internal const string WebTierNoticeZh =
        "Web 版约束：网页版仅保留 L1 军令语法（离线解析）；L2 本地模型与 L3 BYOK 云模型在浏览器沙箱中禁用，不会发出任何网络请求。";

    private IFileStore _files = null!;
    private ISecretStore _secrets = null!;
    private IHttpPort _port = null!;
    private SettingsFormState _form = new();
    private LlmConfig _config = null!;

    // T-106 (F-164): the platform tier row (Web ⇒ L3 off ⇒ BYOK surfaces
    // hidden + gateway platform gate). Sourced ONCE from WebConstraints —
    // the panel never branches on OS.HasFeature itself (能力表驱动).
    private AgentTierAvailability _tiers = WebConstraints.Availability(isWeb: false);

    // True while ApplyFieldsToUi mirrors form state into the controls —
    // programmatic setters re-emit their signals, and those re-entrant
    // handler runs must not be treated as player edits.
    private bool _applying;

    private OptionButton _renderTierDropdown = null!;
    private OptionButton _templateDropdown = null!;
    private CheckBox _openAiRadio = null!;
    private CheckBox _anthropicRadio = null!;
    private LineEdit _baseUrlInput = null!;
    private LineEdit _keyInput = null!;
    private Button _clearKeyButton = null!;
    private Label _keyEcho = null!;
    private LineEdit _modelInput = null!;
    private Button _fetchModelsButton = null!;
    private Label _fetchResult = null!;
    private Label _noteText = null!;
    private SpinBox _temperature = null!;
    private SpinBox _maxTokens = null!;
    private CheckBox _useStream = null!;
    private SpinBox _requestTimeout = null!;
    private SpinBox _connectTimeout = null!;
    private SpinBox _retries = null!;
    private SpinBox _historyRounds = null!;
    private SpinBox _contextBudget = null!;
    private CheckBox _enable = null!;
    private CheckBox _showCallLog = null!;
    private CheckBox _logConversation = null!;
    private SpinBox _logRetention = null!;
    private Button _testConnectionButton = null!;
    private Button _saveButton = null!;
    private Label _statusText = null!;
    private Label _probeResult = null!;
    private Label _declarationFooter = null!;
    private Label _codingPlanNotice = null!;

    /// <summary>The pure form state (EraCore, tested) — smoke/assert access.</summary>
    internal SettingsFormState Form => _form;

    /// <summary>The persisted config baseline this panel saves against.</summary>
    internal LlmConfig Config => _config;

    /// <summary>Instance factory for the host (Main.cs appends one init line).</summary>
    public static SettingsPanelUi Instantiate(
        Control host, IFileStore? files = null, ISecretStore? secrets = null, IHttpPort? port = null)
    {
        var scene = GD.Load<PackedScene>("res://scenes/settings_panel.tscn");
        var ui = scene.Instantiate<SettingsPanelUi>();

        // Dependencies land BEFORE AddChild — _Ready fires during AddChild
        // (headless smoke starts from there), and WireNodes/LoadFromDisk
        // only walk the already-instantiated subtree, so pre-tree is safe.
        ui._files = files ?? CreateDefaultFiles();
        ui._secrets = secrets ?? SecretStoreFactory.Create(ui._files, new PanelLogSink());
        ui._port = port ?? new SettingsHttpPort(ui);
        ui._tiers = WebConstraints.Availability(OS.HasFeature("web")); // T-106 F-164
        ui.WireNodes();
        ui.LoadFromDisk();
        host.AddChild(ui);
        return ui;
    }

    /// <summary>
    /// File root: live runs use the injected dev root (ERA_DATA_ROOT →
    /// pinned D-drive root); headless smoke runs get a throwaway root under
    /// the same <c>.tmp-tests</c> convention the test suite uses, so CI
    /// never touches the developer's real llm.config.json or key store.
    /// </summary>
    private static IFileStore CreateDefaultFiles()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            string root = System.IO.Path.Combine(
                DevFileStore.DefaultDevRoot, ".tmp-tests", "settings-smoke");
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            return new DevFileStore(root);
        }

        return new DevFileStore();
    }

    /// <summary>Opens/closes the panel over the game (backdrop + form).</summary>
    internal void Open() => Visible = true;

    /// <summary>Hides the panel (state stays loaded).</summary>
    internal void Close() => Visible = false;

    public override void _Ready()
    {
        // WireNodes/LoadFromDisk already ran inside Instantiate (deps must
        // precede AddChild). Headless: the smoke sequence runs synchronously
        // inside _Ready — every await it hits completes synchronously
        // (zero-network refusals return before the wire port is called).
        // .Wait() turns a would-be unobserved task exception into a printed
        // SETTINGS_FAIL instead of a silent skip.
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            try
            {
                RunHeadlessSmokeAsync().Wait();
            }
            catch (Exception ex)
            {
                GD.PushError($"SETTINGS_FAIL smoke crashed: {ex}");
                GD.Print($"SETTINGS_FAIL smoke crashed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- setup

    private void WireNodes()
    {
        _renderTierDropdown = GetNode<OptionButton>("Scroll/Form/RenderTierRow/RenderTierDropdown");
        _templateDropdown = GetNode<OptionButton>("Scroll/Form/TemplateRow/TemplateDropdown");
        _openAiRadio = GetNode<CheckBox>("Scroll/Form/ProtocolRow/OpenAiRadio");
        _anthropicRadio = GetNode<CheckBox>("Scroll/Form/ProtocolRow/AnthropicRadio");
        _baseUrlInput = GetNode<LineEdit>("Scroll/Form/BaseUrlRow/BaseUrlInput");
        _keyInput = GetNode<LineEdit>("Scroll/Form/KeyRow/KeyInput");
        _clearKeyButton = GetNode<Button>("Scroll/Form/KeyRow/ClearKeyButton");
        _keyEcho = GetNode<Label>("Scroll/Form/KeyRow/KeyEcho");
        _modelInput = GetNode<LineEdit>("Scroll/Form/ModelRow/ModelInput");
        _fetchModelsButton = GetNode<Button>("Scroll/Form/ModelRow/FetchModelsButton");
        _fetchResult = GetNode<Label>("Scroll/Form/FetchResult");
        _noteText = GetNode<Label>("Scroll/Form/NoteText");
        _temperature = GetNode<SpinBox>("Scroll/Form/TempRow/Temperature");
        _maxTokens = GetNode<SpinBox>("Scroll/Form/MaxTokensRow/MaxTokens");
        _useStream = GetNode<CheckBox>("Scroll/Form/StreamRow/UseStream");
        _requestTimeout = GetNode<SpinBox>("Scroll/Form/TimeoutRow/RequestTimeout");
        _connectTimeout = GetNode<SpinBox>("Scroll/Form/TimeoutRow/ConnectTimeout");
        _retries = GetNode<SpinBox>("Scroll/Form/RetryRow/Retries");
        _historyRounds = GetNode<SpinBox>("Scroll/Form/HistoryRow/HistoryRounds");
        _contextBudget = GetNode<SpinBox>("Scroll/Form/ContextRow/ContextBudget");
        _enable = GetNode<CheckBox>("Scroll/Form/EnableRow/Enable");
        _showCallLog = GetNode<CheckBox>("Scroll/Form/LogRow/ShowCallLog");
        _logConversation = GetNode<CheckBox>("Scroll/Form/LogRow/LogConversation");
        _logRetention = GetNode<SpinBox>("Scroll/Form/LogRow/LogRetention");
        _testConnectionButton = GetNode<Button>("Scroll/Form/ActionRow/TestConnectionButton");
        _saveButton = GetNode<Button>("Scroll/Form/ActionRow/SaveButton");
        _statusText = GetNode<Label>("Scroll/Form/ActionRow/StatusText");
        _probeResult = GetNode<Label>("Scroll/Form/ProbeResult");
        _declarationFooter = GetNode<Label>("Scroll/Form/DeclarationFooter");
        _codingPlanNotice = GetNode<Label>("Scroll/Form/CodingPlanNotice");

        // 四模板下拉（§11.6：智谱 GLM / 小米 MiMo / 自定义 / 本地 LM Studio）
        foreach (var template in SettingsFormState.Templates)
        {
            _templateDropdown.AddItem($"{template.Name}  {template.BaseUrl}");
        }

        // T-102 画质档位下拉（渲染三档+自动；索引=RenderTierPreference 枚举序）
        _renderTierDropdown.AddItem("自动（探测：Web/帧率自适应）");   // Auto = 0
        _renderTierDropdown.AddItem("低（集显/Web 流畅优先）");         // Low = 1
        _renderTierDropdown.AddItem("中（默认画质）");                  // Medium = 2
        _renderTierDropdown.AddItem("高（全开+更细脏粒度）");           // High = 3

        // 协议单选（同一 ButtonGroup → 互斥 radio）
        var group = new ButtonGroup();
        _openAiRadio.ButtonGroup = group;
        _anthropicRadio.ButtonGroup = group;

        // 数值域对齐 LlmConfigLoader 的校验区间（越界在 Review 里拦截，
        // 这里只约束控件本身，避免 UI 允许一个保存必然拒绝的值）
        _temperature.MinValue = 0d;
        _temperature.MaxValue = 2d;
        _temperature.Step = 0.1d;
        _maxTokens.MinValue = 1;
        _maxTokens.MaxValue = 32768;
        _requestTimeout.MinValue = 1;
        _requestTimeout.MaxValue = 300;
        _connectTimeout.MinValue = 1;
        _connectTimeout.MaxValue = 120;
        _retries.MinValue = 0;
        _retries.MaxValue = 5;
        _historyRounds.MinValue = 0;
        _historyRounds.MaxValue = 100;
        _contextBudget.MinValue = 256;
        _contextBudget.MaxValue = 1_000_000;
        _logRetention.MinValue = 1;
        _logRetention.MaxValue = 1000;

        // 信号（玩家输入路径；程序化写值由 _applying 屏蔽）
        _templateDropdown.ItemSelected += OnTemplateSelected;
        _renderTierDropdown.ItemSelected += OnRenderTierSelected;
        _openAiRadio.Toggled += _ => OnProtocolChanged();
        _anthropicRadio.Toggled += _ => OnProtocolChanged();
        _baseUrlInput.TextChanged += OnBaseUrlChanged;
        _keyInput.TextChanged += OnKeyChanged;
        _clearKeyButton.Pressed += OnClearKey;
        _modelInput.TextChanged += OnModelChanged;
        _fetchModelsButton.Pressed += () => _ = FetchModelsAsync(_form.Fields.ProviderId);
        _temperature.ValueChanged += _ => OnNumericChanged();
        _maxTokens.ValueChanged += _ => OnNumericChanged();
        _requestTimeout.ValueChanged += _ => OnNumericChanged();
        _connectTimeout.ValueChanged += _ => OnNumericChanged();
        _retries.ValueChanged += _ => OnNumericChanged();
        _historyRounds.ValueChanged += _ => OnNumericChanged();
        _contextBudget.ValueChanged += _ => OnNumericChanged();
        _logRetention.ValueChanged += _ => OnNumericChanged();
        _useStream.Toggled += _ => OnNumericChanged();
        _enable.Toggled += _ => OnNumericChanged();
        _showCallLog.Toggled += _ => OnNumericChanged();
        _logConversation.Toggled += _ => OnNumericChanged();
        _testConnectionButton.Pressed += () => _ = ProbeAsync(_form.Fields.ProviderId);
        _saveButton.Pressed += OnSave;

        _codingPlanNotice.Text = SettingsFormState.CodingPlanNoticeZh;

        // T-106 (F-164): platform tier constraints apply last — the Web
        // export hides every BYOK (L3) surface and keeps only the render
        // tier row; the probe/fetch paths get the gateway platform gate.
        ApplyWebTierConstraints();
    }

    /// <summary>
    /// T-106 (F-164 Web 版约束): on the Web export every BYOK (L3) form row
    /// hides (key entry included — nothing to configure when the tier is
    /// platform-disabled) and a notice states the constraint; the render
    /// tier row stays (a Web-relevant setting, not a BYOK field). Desktop is
    /// untouched. The L3 enforcement itself lives in the gateway gate + the
    /// <see cref="ProbeAsync"/>/<see cref="FetchModelsAsync"/> guards below.
    /// </summary>
    private void ApplyWebTierConstraints()
    {
        if (_tiers.L3CloudByok)
        {
            return; // 桌面：完整 BYOK 表单（现行行为不变）
        }

        var form = GetNode<Control>("Scroll/Form");
        foreach (Node child in form.GetChildren())
        {
            if (child is Control control && child.Name != "RenderTierRow")
            {
                control.Visible = false;
            }
        }

        form.AddChild(new Label
        {
            Name = "WebTierNotice",
            Text = WebTierNoticeZh,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 64),
        });
    }

    private void LoadFromDisk()
    {
        _config = LoadConfig();
        _form.Load(_config);
        ApplyFieldsToUi();

        // T-102: 画质档位非 BYOK 表单字段——独立偏好文件，加载即回显
        _renderTierDropdown.Select((int)RenderTierStore.Load(_files).Preference);
    }

    /// <summary>
    /// T-102 (C-21 渲染三档): quality-tier row handler — the preference is
    /// NOT a BYOK form field, so it persists immediately to
    /// settings/render-tier.json (no 保存 button hop); Main.cs reads it on
    /// the next launch and probes from it.
    /// </summary>
    private void OnRenderTierSelected(long index)
    {
        if (_applying || index is < 0 or > (int)RenderTierPreference.High)
        {
            return;
        }

        RenderTierStore.Save(_files, new RenderTierSettings
        {
            Preference = (RenderTierPreference)index,
        });
    }

    private LlmConfig LoadConfig()
    {
        if (_files.Exists(SettingsFormState.ConfigFileName))
        {
            string json = Encoding.UTF8.GetString(
                _files.ReadAllBytes(SettingsFormState.ConfigFileName));
            var parsed = LlmConfigLoader.Load(json);
            if (parsed.Ok)
            {
                return parsed.Config!;
            }

            GD.PushWarning($"BYOK 配置不可用，回落出厂模板：{parsed}");
        }

        return LlmProviderPresets.DefaultConfig();
    }

    // ------------------------------------------------- UI ← form (mirror)

    private void ApplyFieldsToUi()
    {
        _applying = true;
        var f = _form.Fields;

        _templateDropdown.Select(TemplateIndex(f.ProviderId));
        _openAiRadio.ButtonPressed = f.Protocol == LlmProtocol.OpenAi;
        _anthropicRadio.ButtonPressed = f.Protocol == LlmProtocol.Anthropic;
        _baseUrlInput.Text = f.BaseUrl;
        _modelInput.Text = f.Model;
        _temperature.Value = f.Temperature;
        _maxTokens.Value = f.MaxTokens;
        _useStream.ButtonPressed = f.UseStream;
        _requestTimeout.Value = f.RequestTimeoutS;
        _connectTimeout.Value = f.ConnectTimeoutS;
        _retries.Value = f.Retries;
        _historyRounds.Value = f.HistoryRounds;
        _contextBudget.Value = f.ContextBudgetTokens;
        _enable.ButtonPressed = f.Enable;
        _showCallLog.ButtonPressed = f.ShowCallLog;
        _logConversation.ButtonPressed = f.LogConversation;
        _logRetention.Value = f.LogRetention;
        _noteText.Text = f.Note;
        _keyInput.Text = string.Empty;

        RefreshKeyEcho();
        RefreshDeclaration();
        _applying = false;
    }

    private int TemplateIndex(string providerId)
    {
        for (int i = 0; i < SettingsFormState.Templates.Count; i++)
        {
            if (string.Equals(
                    SettingsFormState.Templates[i].Id, providerId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1; // custom row id outside the four presets — no dropdown mark
    }

    // ------------------------------------------------- form ← UI (edits)

    private void OnTemplateSelected(long index)
    {
        if (_applying || index < 0 || index >= SettingsFormState.Templates.Count)
        {
            return;
        }

        _form.SelectTemplate(SettingsFormState.Templates[(int)index].Id);
        ApplyFieldsToUi();
        _statusText.Text = string.Empty;
        _probeResult.Text = string.Empty;
        _fetchResult.Text = string.Empty;
    }

    private void OnProtocolChanged()
    {
        if (_applying)
        {
            return;
        }

        _form.Edit(f => f with
        {
            Protocol = _anthropicRadio.ButtonPressed ? LlmProtocol.Anthropic : LlmProtocol.OpenAi,
        });
    }

    private void OnBaseUrlChanged(string text)
    {
        if (_applying)
        {
            return;
        }

        _form.Edit(f => f with { BaseUrl = text });
        RefreshDeclaration(); // 常驻声明随 base_url 逐字刷新（§11.6）
    }

    private void OnModelChanged(string text)
    {
        if (_applying)
        {
            return;
        }

        _form.Edit(f => f with { Model = text });
    }

    private void OnNumericChanged()
    {
        if (_applying)
        {
            return;
        }

        _form.Edit(f => f with
        {
            Temperature = _temperature.Value,
            MaxTokens = (int)_maxTokens.Value,
            RequestTimeoutS = (int)_requestTimeout.Value,
            ConnectTimeoutS = (int)_connectTimeout.Value,
            Retries = (int)_retries.Value,
            HistoryRounds = (int)_historyRounds.Value,
            ContextBudgetTokens = (int)_contextBudget.Value,
            UseStream = _useStream.ButtonPressed,
            Enable = _enable.ButtonPressed,
            ShowCallLog = _showCallLog.ButtonPressed,
            LogConversation = _logConversation.ButtonPressed,
            LogRetention = (int)_logRetention.Value,
        });
    }

    // ------------------------------------------------------------ API key

    private void OnKeyChanged(string text)
    {
        if (_applying)
        {
            return;
        }

        _form.NotifyKeyEdited(); // 值只进 SecretStore（保存时），表单只见脏标记
        _keyEcho.Text = KeyMasker.Mask(text); // 输入即掩码回显（尾 4 位）
    }

    private void OnClearKey()
    {
        _secrets.Clear(_form.Fields.ProviderId);
        _form.NotifyKeyCleared();
        _keyInput.Text = string.Empty;
        RefreshKeyEcho();
        _statusText.Text = "已从本机凭据存储清除该服务的 Key。";
    }

    private void RefreshKeyEcho()
    {
        string stored = _secrets.Retrieve(_form.Fields.ProviderId);
        _keyEcho.Text = stored.Length > 0 ? KeyMasker.Mask(stored) : string.Empty;
    }

    // -------------------------------------------------------- declaration

    private void RefreshDeclaration()
    {
        _declarationFooter.Text = _form.DeclarationText();
    }

    // ------------------------------------------------------ save/persist

    private void OnSave()
    {
        string providerId = _form.Fields.ProviderId;
        bool typedKey = _keyInput.Text.Length > 0;
        bool keyAvailable = typedKey || _secrets.Retrieve(providerId).Length > 0;

        var review = _form.Review(keyAvailable);
        if (!review.CanSave)
        {
            // 错误阻断保存（坏 URL/越界数值/启用行缺模型）；文案不含 Key
            _statusText.Text = "未保存：" + string.Join("；", review.Errors);
            return;
        }

        var next = _form.BuildConfig(_config);
        _files.WriteAllBytes(
            SettingsFormState.ConfigFileName,
            Encoding.UTF8.GetBytes(SettingsFormState.Serialize(next)));

        // Key 只进 SecretStore；typed key 立即入库并清空输入框（明文不驻 UI）
        if (typedKey)
        {
            _secrets.Store(providerId, _keyInput.Text);
            _keyInput.Text = string.Empty;
        }

        _config = next;
        _form.MarkPersisted();
        RefreshKeyEcho();

        _statusText.Text = review.Warnings.Count == 0
            ? $"已保存 llm.config.json（{next.Providers.Count} 个服务；Key 仅存本机 {_secrets.Backend}）"
            : $"已保存（Key 仅存本机 {_secrets.Backend}）；提示：{string.Join("；", review.Warnings)}";
    }

    // ------------------------------------------- probe + /models (C-16)

    /// <summary>
    /// Builds a candidate config (unsaved edits included — the player tests
    /// what the form shows, not the last save) and probes one provider row
    /// through the real gateway path; result copy is bilingual with the
    /// endpoint host + latency on success, or the classified error text.
    /// </summary>
    private async Task<LlmProbeResult> ProbeAsync(string providerId)
    {
        _testConnectionButton.Disabled = true;
        _probeResult.Text = "测试中…";
        try
        {
            var candidate = _form.BuildConfig(_config);
            // T-106 (F-164): the platform gate rides the gateway itself —
            // on Web every probe is refused web-tier-disabled with zero
            // network BEFORE the wire port exists in the call path.
            var probe = new LlmConnectionProbe(new LlmGateway(
                candidate, _port, _secrets, cloudTierEnabled: _tiers.L3CloudByok));
            var result = await probe.ProbeAsync(providerId).ConfigureAwait(true);
            _probeResult.Text = $"{result.MessageZh}\n{result.MessageEn}";
            return result;
        }
        catch (Exception ex)
        {
            // 探针永不把异常抛给 UI；文案只含异常类型，不含请求内容（RL5）
            _probeResult.Text = $"测试未完成：{ex.GetType().Name}";
            return new LlmProbeResult(
                false, LlmErrorKeys.Network, "测试未完成（端口异常）", "probe aborted", false, false, null, TimeSpan.Zero);
        }
        finally
        {
            _testConnectionButton.Disabled = false;
        }
    }

    /// <summary>
    /// /models fetch for one provider row over the same wire port; on
    /// success the first model id lands in the model box (still editable)
    /// and the result line names the host and the count.
    /// </summary>
    private async Task<LlmModelListResult> FetchModelsAsync(string providerId)
    {
        _fetchModelsButton.Disabled = true;
        _fetchResult.Text = "拉取中…";
        try
        {
            // T-106 (F-164): /models bypasses the gateway (raw port call) —
            // the platform gate is enforced HERE instead: on Web the fetch
            // refuses with the same classified key, zero wire calls.
            if (!_tiers.L3CloudByok)
            {
                LlmErrorCatalog.TryGetCopy(LlmErrorKeys.WebTierDisabled, out string zh, out string en);
                var refused = new LlmModelListResult(
                    false, [], LlmErrorKeys.WebTierDisabled, zh, en);
                _fetchResult.Text = $"{zh}\n{en}";
                return refused;
            }

            var candidate = _form.BuildConfig(_config);
            var row = candidate.FindProvider(providerId);
            if (row is null)
            {
                var missing = new LlmModelListResult(
                    false, [], LlmErrorKeys.UnknownProvider, "该服务不在配置中", "provider not found");
                _fetchResult.Text = missing.MessageZh;
                return missing;
            }

            var result = await LlmModelList.FetchAsync(
                row, _port, _secrets,
                LlmDomainWhitelist.FromProviders(candidate.Providers)).ConfigureAwait(true);
            _fetchResult.Text = $"{result.MessageZh}\n{result.MessageEn}";
            if (result.Ok && result.ModelIds.Count > 0)
            {
                _modelInput.Text = result.ModelIds[0]; // player keeps typing/editing after
            }

            return result;
        }
        catch (Exception ex)
        {
            var failed = new LlmModelListResult(
                false, [], LlmErrorKeys.Network, $"拉取未完成（{ex.GetType().Name}）", "fetch aborted");
            _fetchResult.Text = failed.MessageZh;
            return failed;
        }
        finally
        {
            _fetchModelsButton.Disabled = false;
        }
    }

    // ---------------------------------------------------------- smoke (I)

    /// <summary>
    /// Headless acceptance run: template dropdown → full-field edit → save
    /// → disk round-trip → masked key handling → declaration/Coding-Plan
    /// footers → classified zero-network probe/fetch refusals. Prints the
    /// SETTINGS_OK marker (or SETTINGS_FAIL with the broken steps). Every
    /// await inside completes synchronously — the probed rows are disabled,
    /// so the gateway refuses with zero network before the port is touched.
    /// </summary>
    private async Task RunHeadlessSmokeAsync()
    {
        var problems = new List<string>();

        // 常驻声明起点：默认模板的 base_url 已在页脚
        if (!_declarationFooter.Text.Contains("https://open.bigmodel.cn/api/paas/v4"))
        {
            problems.Add("初始声明未含默认 base_url");
        }

        if (_templateDropdown.ItemCount != 4)
        {
            problems.Add($"模板下拉项数 {_templateDropdown.ItemCount}≠4");
        }

        // 四模板锚点：下拉与 EraCore 预置逐项一致
        string[] expectedBases =
        [
            "https://open.bigmodel.cn/api/paas/v4",
            "https://api.xiaomimimo.com/v1",
            string.Empty,
            "http://localhost:1234/v1",
        ];
        for (int i = 0; i < expectedBases.Length; i++)
        {
            if (SettingsFormState.Templates[i].BaseUrl != expectedBases[i])
            {
                problems.Add($"模板[{i}] base_url 锚点不符");
            }
        }

        // 走真实信号路径：选第 4 模板（本地 LM Studio）→ 全字段编辑 → 保存
        _templateDropdown.Select(3);
        _templateDropdown.EmitSignal(OptionButton.SignalName.ItemSelected, 3L);
        SetTextInput(_modelInput, "local-smoke-model");
        _enable.ButtonPressed = true;
        SetTextInput(_keyInput, SmokeKey);

        // 声明随模板 base_url 动态刷新（保存前即生效）
        if (!_declarationFooter.Text.Contains("http://localhost:1234/v1"))
        {
            problems.Add("声明未随 base_url 动态刷新");
        }

        // 输入即掩码：回显含 **** 且不含明文
        string typingEcho = _keyEcho.Text;
        if (!typingEcho.Contains(KeyMasker.Filler) || typingEcho.Contains(SmokeKey))
        {
            problems.Add("Key 输入回显未走掩码");
        }

        _saveButton.EmitSignal(BaseButton.SignalName.Pressed);
        if (!_statusText.Text.StartsWith("已保存"))
        {
            problems.Add($"保存未成功：{_statusText.Text}");
        }

        // 持久化判据：盘上 llm.config.json providers=4、active=local-lmstudio、无 Key 明文
        int providerCount = -1;
        if (!_files.Exists(SettingsFormState.ConfigFileName))
        {
            problems.Add("llm.config.json 未写入");
        }
        else
        {
            string json = Encoding.UTF8.GetString(
                _files.ReadAllBytes(SettingsFormState.ConfigFileName));
            if (json.Contains(SmokeKey))
            {
                problems.Add("配置文件含 Key 明文（红线）");
            }

            var loaded = LlmConfigLoader.Load(json);
            if (!loaded.Ok)
            {
                problems.Add($"保存产物未过校验：{loaded}");
            }
            else
            {
                providerCount = loaded.Config!.Providers.Count;
                if (providerCount != 4)
                {
                    problems.Add($"providers={providerCount}≠4");
                }

                if (loaded.Config.ActiveProvider != "local-lmstudio")
                {
                    problems.Add($"active={loaded.Config.ActiveProvider}≠local-lmstudio");
                }

                // 全字段持久化：盘上重载 → 表单全量等值
                _form.Load(loaded.Config, "local-lmstudio");
                ApplyFieldsToUi();
                if (_modelInput.Text != "local-smoke-model" || !_enable.ButtonPressed
                    || _baseUrlInput.Text != "http://localhost:1234/v1")
                {
                    problems.Add("盘上重载后字段未恢复");
                }
            }
        }

        // Key 只进 SecretStore：入库+掩码回显+一键清除
        if (_secrets.Retrieve("local-lmstudio") != SmokeKey)
        {
            problems.Add("Key 未进 SecretStore");
        }

        if (!_keyEcho.Text.Contains(KeyMasker.Filler) || _keyEcho.Text.Contains(SmokeKey))
        {
            problems.Add("已存 Key 的回显未走掩码");
        }

        _clearKeyButton.EmitSignal(BaseButton.SignalName.Pressed);
        if (_secrets.Retrieve("local-lmstudio").Length != 0)
        {
            problems.Add("一键清除未删除 Key");
        }

        if (!_codingPlanNotice.Text.Contains("1315") || !_codingPlanNotice.Text.Contains("Coding Plan"))
        {
            problems.Add("Coding Plan 白名单提示缺失");
        }

        // T-102 画质档位行：四项下拉 + 真实信号路径选「低」→ 即时持久化 →
        // 盘上回读为 Low → 选回「自动」恢复出厂位
        if (_renderTierDropdown.ItemCount != 4)
        {
            problems.Add($"画质档位下拉项数 {_renderTierDropdown.ItemCount}≠4");
        }

        _renderTierDropdown.Select(1);
        _renderTierDropdown.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
        if (RenderTierStore.Load(_files).Preference != RenderTierPreference.Low)
        {
            problems.Add("画质档位行未即时持久化为 Low");
        }

        _renderTierDropdown.Select(0);
        _renderTierDropdown.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
        if (RenderTierStore.Load(_files).Preference != RenderTierPreference.Auto)
        {
            problems.Add("画质档位行未恢复出厂 Auto");
        }

        // 测试连接/拉取列表接线：探针走禁用预置行 → 零网络分类拒绝（中英文案）
        var probe = await ProbeAsync("xiaomi-mimo").ConfigureAwait(true);
        if (probe.Ok || probe.ErrorKey != LlmErrorKeys.Disabled
            || probe.MessageZh.Length == 0 || probe.MessageEn.Length == 0)
        {
            problems.Add($"探针零网络拒绝未按预期：{probe.ErrorKey}");
        }

        var fetch = await FetchModelsAsync("xiaomi-mimo").ConfigureAwait(true);
        if (fetch.Ok || fetch.ErrorKey != LlmErrorKeys.Disabled)
        {
            problems.Add($"模型列表零网络拒绝未按预期：{fetch.ErrorKey}");
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"SETTINGS_OK providers={providerCount} mask=on declare=on " +
                $"probe={probe.ErrorKey} fetch={fetch.ErrorKey} backend={_secrets.Backend} " +
                "render_tier=roundtrip-ok key_file=clean");
        }
        else
        {
            GD.PushError("SETTINGS_FAIL " + string.Join("; ", problems));
            GD.Print("SETTINGS_FAIL " + string.Join("; ", problems));
        }
    }

    /// <summary>Sets a LineEdit's text AND fires its TextChanged signal (player path).</summary>
    private static void SetTextInput(LineEdit input, string text)
    {
        input.Text = text;
        input.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    // --------------------------------------------------------- wire port

    /// <summary>
    /// Diagnostic sink (K-1) routing secret-store diagnostics to the Godot
    /// console (mirrors SimHost.GodotLogSink — kept private per file).
    /// </summary>
    private sealed class PanelLogSink : ILogSink
    {
        public void Write(LogLevel level, long tick, string message) =>
            GD.Print($"[settings][{level}] {message}");
    }

    /// <summary>
    /// Godot-side <see cref="IHttpPort"/> for the settings page (probe +
    /// /models; §11.7): full-body collection over one HttpRequest node —
    /// enough for these short calls, while the SSE streaming port with the
    /// thread+call_deferred discipline stays with the C-16/T-091 lane.
    /// Contract honored: zero redirect following (a 3xx surfaces as-is so
    /// the domain whitelist cannot be bypassed), failures surface as
    /// exceptions, and no header value or body is ever logged (RL5).
    /// </summary>
    private sealed class SettingsHttpPort : IHttpPort
    {
        private readonly HttpRequest _request;

        public SettingsHttpPort(Node owner)
        {
            _request = new HttpRequest
            {
                MaxRedirects = 0,
                Timeout = 60d,
                BodySizeLimit = 8 * 1024 * 1024,
                UseThreads = true,
            };
            owner.AddChild(_request);
        }

        public async Task<HttpPortResponse> SendAsync(
            HttpPortRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var completion = new TaskCompletionSource<HttpPortResponse>();

            void OnCompleted(long result, long code, string[] headers, byte[] body)
            {
                if (result != (long)HttpRequest.Result.Success)
                {
                    if (result == (long)HttpRequest.Result.Timeout)
                    {
                        completion.TrySetException(new OperationCanceledException(nameof(HttpRequest.Result.Timeout)));
                    }
                    else
                    {
                        completion.TrySetException(new IOException($"transport failure result={result}"));
                    }

                    return;
                }

                if (code is >= 200 and <= 299)
                {
                    completion.TrySetResult(new HttpPortResponse(
                        (int)code, null, new MemoryStream(body ?? [])));
                }
                else
                {
                    string text = body is { Length: > 0 }
                        ? Encoding.UTF8.GetString(body)
                        : string.Empty;
                    completion.TrySetResult(new HttpPortResponse((int)code, text, null));
                }
            }

            _request.RequestCompleted += OnCompleted;
            CancellationTokenRegistration cancel = cancellationToken.Register(
                () =>
                {
                    _request.CancelRequest();
                    completion.TrySetCanceled();
                });

            var headers = new string[request.Headers.Count];
            int i = 0;
            foreach (var (name, value) in request.Headers)
            {
                headers[i++] = $"{name}: {value}";
            }

            var method = request.Method == "GET"
                ? Godot.HttpClient.Method.Get
                : Godot.HttpClient.Method.Post;
            Error sent = _request.Request(
                request.Url.ToString(), headers, method, request.BodyJson);
            if (sent != Error.Ok)
            {
                _request.RequestCompleted -= OnCompleted;
                cancel.Dispose();
                throw new IOException($"request rejected result={sent}");
            }

            try
            {
                return await completion.Task.ConfigureAwait(true);
            }
            finally
            {
                _request.RequestCompleted -= OnCompleted;
                cancel.Dispose();
            }
        }
    }
}
