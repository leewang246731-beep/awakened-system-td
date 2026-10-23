using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.IO;
using EraCore.Llm.L2;
using EraCore.Presentation;
using Godot;

namespace EraTD;

// T-055 (C-22) thin system hologram panel (F-110/F-111/F-115/F-116/F-131):
// holo skin (HoloSkin, §13.4),常驻 HUD three metrics, the order input box
// (real AgentPipeline round trip), the 4-intent quick wheel, the plan-card
// popup (confirm/refuse) and the off-switch (pillar 4: 关闭系统可全通关).
// All decisions about state live in EraCore.Presentation.PanelStateMachine —
// this node only mirrors state into visibility/styles (CS-1.6: presentation
// holds no business rules).
//
// T-075 (C-22 full): the third HUD metric regains its full口径 (wave
// composition + next-wave preview, WaveComposition), the advisor avatar
// carries the three-tier trust badge (TrustBadgeTracker, §11.4), the
// defense-line alarm renders through OffModeDowngrade in BOTH switch
// positions (§10.7 警报不缺失), and the off-switch position persists across
// runs (PanelSettingsStore, F-097).
//
// T-110 (F-164 Web UI 禁用适配): the order box carries the「AI 高级语法」
// hint (L3-suggestive affordance, copy from WebUiSurfaces) whose visibility
// mirrors <see cref="WebUiSurfaces.Surfaces"/> — desktop shows it, the Web
// tier row hides it. Main supplies the platform row once
// (SetAgentTiers 能力表驱动——本节点不读 OS.HasFeature).

/// <summary>
/// The C-22 panel control. Input path: LineEdit.TextSubmitted (Enter) and
/// the quick-wheel buttons both funnel into <see cref="SubmitOrder"/>,
/// which calls the REAL five-stage pipeline (T-053) on the bound
/// <see cref="SimHost"/>; an Ask-tier verdict opens the plan card whose
/// confirm/refuse buttons drive Pipeline.Confirm/Refuse; every terminal
/// outcome lands in the report bubble (AgentReport text).
/// </summary>
public sealed partial class SystemPanel : Control
{
    /// <summary>
    /// T-160b（战役军师面板可用面）：面板消费的宿主面——SimHost（E1 夹具
    /// 泳道）与战役关的 <c>CampaignAdvisorBoard</c>（LevelSession 泳道）同
    /// 一接口；面板在战役开局/退出时随宿主 UI 重绑/还原。BuildPlan 等确认
    /// 命令经宿主实现方的 Pipeline 落子（战役侧=经
    /// <see cref="EraCore.CampaignLoop.LevelSession"/> 的命令观察缝记账，
    /// 重放键完整——引擎最小扩见 LevelSession）。
    /// </summary>
    internal interface IAdvisorBoardHost
    {
        /// <summary>Order-path pipeline (five stages, registry-gated).</summary>
        AgentPipeline Pipeline { get; }

        /// <summary>Store the panel settings persist through (F-097).</summary>
        IFileStore Store { get; }

        /// <summary>HUD three metrics of the CURRENT board (F-111).</summary>
        HudSnapshot BuildHudSnapshot();
    }

    /// <summary>The always-legal example order shown in the input box (F-115 常驻合法示例句).</summary>
    internal const string ExampleOrder = "守住东门";

    /// <summary>Ghost hint line under the input box (F-115 ghost text via hint label).</summary>
    internal const string GhostHint =
        "军令示例：守住东门 / 在东门建两座木箭塔 / 敌情怎么样 / 我们还剩多少木材 / 三分钟后叫我";

    /// <summary>
    /// The 4 high-frequency wheel orders (F-116 快捷轮盘): the T-052 union
    /// subset minus SetAlarm (the deferred low-frequency intent).
    /// </summary>
    internal static readonly string[] WheelOrders =
    {
        "守住东门",
        "在东门建两座木箭塔",
        "敌情怎么样",
        "我们还剩多少木材",
    };

    private PanelStateMachine _machine = new();
    private IAdvisorBoardHost _host = null!; // T-160b：夹具/战役关同一宿主面（Bind 重绑）
    private TrustBadgeTracker _trustTracker = new(TrustBadge.Trustworthy); // era reset = score 50
    private bool _alarmAnnounced;

    // T-137 (F-078): L2 lane binding (coordinator + wave-gap service). Null
    // until BindL2 — the order path then consults the frozen route matrix on
    // every L1 clarification and mirrors the neutral degradation banner.
    private L2.L2Coordinator? _l2;
    private L2.WaveGapL2Service? _waveGap;
    private Label _l2TierHint = null!;
    private Label _l2Banner = null!;

    // T-110 (F-164): platform tier row (Web ⇒ L3 off ⇒ AI 高级语法 hint
    // hidden). Desktop default until the host binds the real platform fact
    // (Main.SetAgentTiers 一次供给——能力表驱动).
    private AgentTierAvailability _tiers = WebConstraints.Availability(isWeb: false);

    private PanelContainer _hudBar = null!;
    private Label _waveField = null!;
    private Label _rateField = null!;
    private Label _integrityField = null!;
    private Label _compField = null!;
    private Label _nextWaveField = null!;
    private Label _alarmField = null!;
    private Button _reenableButton = null!;

    private Button _bubble = null!;
    private PanelContainer _expandedPanel = null!;
    private TextureRect _scanlineOverlay = null!; // T-102: tier-gated scanline pass (§13.4)
    private LineEdit _orderInput = null!;
    private Label _ghostHint = null!;
    private Label _aiSyntaxHint = null!; // T-110: L3-suggestive entry, Web tier hides it
    private CheckBox _offSwitch = null!;
    private Label _reportText = null!;
    private TextureRect _avatar = null!;
    private Label _trustMark = null!;
    private Label _trustLabel = null!;

    private PanelContainer _planCard = null!;
    private Label _planOrder = null!;
    private Label _planSteps = null!;
    private Label _planCost = null!;
    private Label _planRisks = null!;

    // T-163（C 批军师面板时代皮肤+立绘）：标题带 Label（时代色着色面）+
    // 立绘绑定时代位（0=未绑/菜单态——程序头像回退）。
    private Label _panelTitle = null!;
    private int _portraitEra;

    private long _pendingId = -1;
    private PipelineOutcomeKind? _lastOutcome;

    /// <summary>The pure state machine this node mirrors (EraCore, tested).</summary>
    internal PanelStateMachine Machine => _machine;

    /// <summary>Kind of the last handled order (smoke probe; null before the first order).</summary>
    internal PipelineOutcomeKind? LastOutcome => _lastOutcome;

    /// <summary>Plan-card popup visible right now (smoke probe).</summary>
    internal bool PlanCardVisible => _planCard is { Visible: true };

    /// <summary>Current report-bubble text (smoke probe).</summary>
    internal string ReportText => _reportText.Text;

    /// <summary>Order input box node (smoke drives its signal directly).</summary>
    internal LineEdit OrderInput => _orderInput;

    /// <summary>Current wave-composition line of the third HUD metric (smoke probe).</summary>
    internal string CompText => _compField.Text;

    /// <summary>Current next-wave preview line (smoke probe).</summary>
    internal string NextWaveText => _nextWaveField.Text;

    /// <summary>Full tier label of the trust badge, e.g. 可托付 (smoke probe; tier only, never the raw score).</summary>
    internal string BadgeLabelText => TrustBadgeLabels.Label(_trustTracker.Current);

    /// <summary>Trust corner mark visible right now (false while the system is off).</summary>
    internal bool BadgeMarkVisible => _trustMark is { Visible: true };

    /// <summary>Defense alarm line when firing, empty otherwise (smoke probe).</summary>
    internal string AlarmText => _alarmField.Visible ? _alarmField.Text : string.Empty;

    /// <summary>Binds the real sim + pipeline host (must precede any order). T-160b:
    /// takes the board-host seam — the E1 fixture lane (SimHost) and campaign
    /// levels (CampaignAdvisorBoard) re-bind through the same call.</summary>
    internal void Bind(IAdvisorBoardHost host)
    {
        _host = host;

        // F-097: the remembered switch position restores on startup (a UI
        // preference must never block startup — corrupt files already fell
        // back to the factory default inside the store).
        if (!PanelSettingsStore.Load(host.Store).SystemEnabled)
        {
            SetSystemEnabled(false);
        }

        RefreshHud(host.BuildHudSnapshot());
    }

    /// <summary>
    /// T-137: binds the L2 lane (coordinator + wave-gap service). After this,
    /// every L1-clarified order consults the route matrix: L2-ready orders
    /// queue for the wave-gap fill; downgraded lanes show the neutral banner
    /// once per transition (降级可见, OffModeDowngrade 中性先例).
    /// </summary>
    internal void BindL2(L2.L2Coordinator coordinator, L2.WaveGapL2Service waveGap)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(waveGap);
        _l2 = coordinator;
        _waveGap = waveGap;
        _waveGap.FillLanded += OnL2FillLanded;
        _waveGap.FillAbandoned += OnL2FillAbandoned;
        RefreshL2Surfaces();
    }

    /// <summary>Re-renders the tier hint + banner from the coordinator's current lane state.</summary>
    internal void RefreshL2Surfaces()
    {
        if (_l2 is null || _l2TierHint is null)
        {
            return;
        }

        L2RouteDecision decision = _l2.DecideRoute();
        _l2TierHint.Text = decision.EffectiveTier switch
        {
            LlmTierKind.L2Local => "军师档位：L2 本地模型（127.0.0.1 本地推理，不上传）",
            LlmTierKind.L3Cloud => "军师档位：L3 云模型（BYOK）",
            _ => decision.Downgraded
                ? $"军师档位：L1 军令语法（L2 已选，暂不可用——{L2DegradationNotices.BannerZh(decision.Reason)}）"
                : "军师档位：L1 军令语法（离线）",
        };
        _l2Banner.Text = decision.Downgraded
            ? L2DegradationNotices.BannerZh(decision.Reason)
            : string.Empty;
        _l2Banner.Visible = decision.Downgraded;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 20; // above the z=10 unit layer — UI never hides under the world
        BuildHudBar();
        BuildBubble();
        BuildExpandedPanel();
        BuildPlanCard();
        Layout();
        ApplyState();
        UpdateBadgeUi();
        SetAgentTiers(_tiers); // T-110: mirror the default (desktop) row once
        RefreshHud(new HudSnapshot(0, 0d, 1d, 0));
    }

    /// <summary>
    /// Manual layout inside the 640×360 design resolution (the root is a
    /// plain Control, children are anchored by hand): HUD strip under the
    /// debug label, bubble + expanded panel docked bottom-left, plan card
    /// centered on screen.
    /// </summary>
    private void Layout()
    {
        _hudBar.Position = new Vector2(8f, 46f);
        _expandedPanel.Position = new Vector2(8f, 170f);
        _expandedPanel.CustomMinimumSize = new Vector2(360f, 0f);
        _bubble.Position = new Vector2(8f, 324f);
        _planCard.SetAnchorsPreset(Control.LayoutPreset.Center);
        _planCard.ZIndex = 30; // popup above everything
    }

    // ---- construction ------------------------------------------------------

    private void BuildHudBar()
    {
        _hudBar = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _hudBar.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _hudBar.AddThemeConstantOverride("margin_left", 6);
        _hudBar.AddThemeConstantOverride("margin_right", 6);
        _hudBar.AddThemeConstantOverride("margin_top", 2);
        _hudBar.AddThemeConstantOverride("margin_bottom", 2);

        var row = Separation(new HBoxContainer(), 12);
        _waveField = MetricLabel("波次 W00");
        _rateField = MetricLabel("产出 0.0/s");
        _integrityField = MetricLabel("防线 100%");
        _compField = MetricLabel("构成 —");
        _nextWaveField = MetricLabel("下波 —");
        _alarmField = new Label
        {
            Text = string.Empty,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _alarmField.AddThemeFontSizeOverride("font_size", 12);
        _alarmField.AddThemeColorOverride("font_color", HoloSkin.RiskRed); // §10.3 crisis = red display
        _reenableButton = new Button { Text = "开启系统", Visible = false };
        _reenableButton.Pressed += OnReenablePressed;
        UiFeel.AttachCues(_reenableButton); // T-164：hover/press 叮音效（骨架超然于时代——只接音效不换肤，§10.2）

        row.AddChild(_waveField);
        row.AddChild(_rateField);
        row.AddChild(_integrityField);
        row.AddChild(_compField);
        row.AddChild(_nextWaveField);
        row.AddChild(_alarmField);
        row.AddChild(_reenableButton);
        _hudBar.AddChild(row);
        AddChild(_hudBar);
    }

    private void BuildBubble()
    {
        var normal = HoloSkin.BuildHoloBox();
        var pressed = HoloSkin.BuildHoloBox();
        pressed.BgColor = new Color(HoloSkin.BaseBlue, 0.9f);
        _bubble = new Button
        {
            Text = "◈ 系统·待命",
            Flat = true,
            MouseFilter = MouseFilterEnum.Stop,
        };
        _bubble.AddThemeStyleboxOverride("normal", normal);
        _bubble.AddThemeStyleboxOverride("hover", normal);
        _bubble.AddThemeStyleboxOverride("pressed", pressed);
        _bubble.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _bubble.AddThemeFontSizeOverride("font_size", 12);
        _bubble.Pressed += OnBubblePressed;
        UiFeel.AttachCues(_bubble); // T-164：气泡 hover/press 叮音效
        AddChild(_bubble);
    }

    private void BuildExpandedPanel()
    {
        // Outer holo box → 2 px margin → inner 1 px dark-cyan bevel box:
        // the two nested boxes render the §13.4 edge+inner-shadow recipe.
        _expandedPanel = new PanelContainer();
        _expandedPanel.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());

        var scan = new TextureRect
        {
            Texture = HoloSkin.BuildScanlineTexture(),
            StretchMode = TextureRect.StretchModeEnum.Tile,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };

        _scanlineOverlay = scan;

        var bevel = new PanelContainer();
        bevel.AddThemeStyleboxOverride("panel", HoloSkin.BuildInnerBevel());
        bevel.AddThemeConstantOverride("margin_left", 6);
        bevel.AddThemeConstantOverride("margin_right", 6);
        bevel.AddThemeConstantOverride("margin_top", 4);
        bevel.AddThemeConstantOverride("margin_bottom", 4);

        var body = Separation(new VBoxContainer(), 4);

        var titleRow = new HBoxContainer();
        _panelTitle = DimLabel("◆ 系统全息面板");
        var collapse = new Button { Text = "收起" };
        collapse.Pressed += OnCollapsePressed;
        UiFeel.AttachCues(collapse); // T-164
        titleRow.AddChild(_panelTitle);
        titleRow.AddChild(collapse);

        var orderRow = Separation(new HBoxContainer(), 4);
        _orderInput = new LineEdit
        {
            PlaceholderText = ExampleOrder, // F-115: the standing legal example
            CustomMinimumSize = new Vector2(190, 22),
            Editable = true,
        };
        _orderInput.TextSubmitted += OnOrderSubmitted;
        var submit = new Button { Text = "下令" };
        submit.Pressed += OnSubmitPressed;
        UiFeel.AttachCues(submit); // T-164
        orderRow.AddChild(DimLabel("军令"));
        orderRow.AddChild(_orderInput);
        orderRow.AddChild(submit);

        _ghostHint = new Label
        {
            Text = GhostHint,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _ghostHint.AddThemeFontSizeOverride("font_size", 10);
        _ghostHint.AddThemeColorOverride("font_color", new Color(HoloSkin.TextDimCyan, 0.6f));

        // T-110 (F-110 ①): 「AI 高级语法」入口——L3 暗示性提示行（指向设置页
        // BYOK 可选档；措辞锚 §11.5 L3 行，WebUiSurfaces 单源）。可见性镜像
        // WebUiSurfaces.Surfaces：桌面可见、Web 档位行隐藏。
        _aiSyntaxHint = new Label
        {
            Text = WebUiSurfaces.AiAdvancedSyntaxHintZh,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _aiSyntaxHint.AddThemeFontSizeOverride("font_size", 10);
        _aiSyntaxHint.AddThemeColorOverride("font_color", new Color(1.00f, 0.84f, 0.40f, 0.9f));
        _aiSyntaxHint.TooltipText = WebUiSurfaces.AiAdvancedSyntaxHintEn;

        var wheelRow = Separation(new HBoxContainer(), 4);
        foreach (string order in WheelOrders)
        {
            var quick = new Button { Text = ShortenWheelLabel(order) };
            quick.Pressed += () => SubmitOrder(order);
            wheelRow.AddChild(quick);
        }

        _offSwitch = new CheckBox { Text = "关闭系统" };
        _offSwitch.Toggled += OnOffSwitchToggled;
        wheelRow.AddChild(_offSwitch);

        // T-137: L2 档位提示行（当前生效档）+ 降级中性横幅（OffModeDowngrade
        // 先例——白字非红，降级是能力事实非危机）
        _l2TierHint = new Label
        {
            Text = "军师档位：L1 军令语法（离线）",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _l2TierHint.AddThemeFontSizeOverride("font_size", 10);
        _l2TierHint.AddThemeColorOverride("font_color", new Color(HoloSkin.TextDimCyan, 0.75f));

        _l2Banner = new Label
        {
            Text = string.Empty,
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(300, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _l2Banner.AddThemeFontSizeOverride("font_size", 10);
        _l2Banner.AddThemeColorOverride("font_color", Colors.White); // 中性横幅：白字（§10.7 先例）

        var reportRow = Separation(new HBoxContainer(), 6);
        // T-163（C 批军师立绘位）：36×36 展示位（256×256 era{N}-advisor
        // 立绘的军师面板位；缺图=既有 14×14 程序头像拉伸回退——不阻塞）。
        _avatar = new TextureRect
        {
            Texture = BuildAvatarTexture(),
            CustomMinimumSize = new Vector2(HudIconDecisions.AdvisorPortraitPx, HudIconDecisions.AdvisorPortraitPx),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };

        // §11.4 三档徽记: the one-glyph corner mark rides the avatar's top
        // right corner (疑/托/从); the full tier label sits next to it. The
        // raw score is never shown.
        _trustMark = new Label
        {
            Text = TrustBadgeLabels.Mark(TrustBadge.Trustworthy),
            Position = new Vector2(HudIconDecisions.AdvisorPortraitPx - 12f, -2f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _trustMark.AddThemeFontSizeOverride("font_size", 9);
        _avatar.AddChild(_trustMark);

        _trustLabel = new Label
        {
            Text = "信任·可托付",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _trustLabel.AddThemeFontSizeOverride("font_size", 10);
        _trustLabel.AddThemeColorOverride("font_color", new Color(HoloSkin.TextDimCyan, 0.75f));

        _reportText = new Label
        {
            Text = "军师待命。下单后我会先出方案卡，你确认再动手。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(300, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _reportText.AddThemeFontSizeOverride("font_size", 11);
        _reportText.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        reportRow.AddChild(_avatar);
        reportRow.AddChild(_trustLabel);
        reportRow.AddChild(_reportText);

        body.AddChild(titleRow);
        body.AddChild(orderRow);
        body.AddChild(_ghostHint);
        body.AddChild(_aiSyntaxHint);
        body.AddChild(wheelRow);
        body.AddChild(_l2TierHint);
        body.AddChild(_l2Banner);
        body.AddChild(reportRow);
        bevel.AddChild(body);
        _expandedPanel.AddChild(scan);
        _expandedPanel.AddChild(bevel);
        AddChild(_expandedPanel);
    }

    private void BuildPlanCard()
    {
        _planCard = new PanelContainer { Visible = false };
        _planCard.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());

        var box = Separation(new VBoxContainer(), 4);
        _planOrder = DimLabel("军令：-");
        _planSteps = new Label { Text = "步骤：-", MouseFilter = MouseFilterEnum.Ignore };
        _planCost = DimLabel("消耗：-");
        _planRisks = new Label { Text = "风险：无标记", MouseFilter = MouseFilterEnum.Ignore };
        _planRisks.AddThemeColorOverride("font_color", HoloSkin.RiskRed); // F-066 风险红标

        var buttons = Separation(new HBoxContainer(), 8);
        var confirm = new Button { Text = "确认执行" };
        confirm.Pressed += OnConfirmPressed;
        var refuse = new Button { Text = "拒绝" };
        refuse.Pressed += OnRefusePressed;
        UiFeel.AttachCues(confirm); // T-164
        UiFeel.AttachCues(refuse);
        buttons.AddChild(confirm);
        buttons.AddChild(refuse);

        var title = DimLabel("◈ 方案卡 · 待确认");
        title.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(title);
        box.AddChild(_planOrder);
        box.AddChild(_planSteps);
        box.AddChild(_planCost);
        box.AddChild(_planRisks);
        box.AddChild(buttons);
        _planCard.AddChild(box);
        AddChild(_planCard);
    }

    // ---- state mirroring ---------------------------------------------------

    private void ApplyState()
    {
        // T-075: OffModeDowngrade.Surfaces is the single frozen matrix the
        // panel mirrors (开关×表面); PanelStateMachine keeps the visibility
        // STATE (bubble/expanded memory), this matrix decides what the
        // off-position degrades (§10.7 人格元素全隐+三指标中性横幅).
        OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(_machine.SystemEnabled);

        _bubble.Visible = _machine.State == PanelState.Bubble;        // persona surface
        _expandedPanel.Visible = _machine.OrderBoxVisible;            // order box + wheel + report
        _trustMark.Visible = surfaces.TrustBadgeVisible;
        _trustLabel.Visible = surfaces.TrustBadgeVisible;
        if (!surfaces.PlanCardVisible && _planCard.Visible)
        {
            _planCard.Hide();
            _pendingId = -1;
        }

        // HUD stays in both modes; the skin swaps holo ↔ neutral banner
        // (§10.7: metrics survive the off-switch, functionality lossless).
        _hudBar.AddThemeStyleboxOverride(
            "panel", surfaces.HudNeutral ? HoloSkin.BuildNeutralBox() : HoloSkin.BuildHoloBox());
        _reenableButton.Visible = surfaces.HudNeutral;
        Color metricColor = surfaces.HudNeutral ? Colors.White : HoloSkin.TextDimCyan;
        _waveField.AddThemeColorOverride("font_color", metricColor);
        _rateField.AddThemeColorOverride("font_color", metricColor);
        _integrityField.AddThemeColorOverride("font_color", metricColor);
        _compField.AddThemeColorOverride("font_color", metricColor);
        _nextWaveField.AddThemeColorOverride("font_color", metricColor);
    }

    /// <summary>
    /// Drives the off-switch (setting item) through the tested machine and
    /// persists the position (F-097 开关持久化 — survives restarts; only
    /// written once a host is bound, pure UI state before that).
    /// </summary>
    internal void SetSystemEnabled(bool enabled)
    {
        _machine.SetSystemEnabled(enabled);
        _offSwitch.SetPressedNoSignal(!enabled);
        if (_host is not null)
        {
            PanelSettingsStore.Save(_host.Store, new PanelSettings { SystemEnabled = enabled });
        }

        ApplyState();
    }

    private void OnOffSwitchToggled(bool pressed) => SetSystemEnabled(!pressed);

    /// <summary>
    /// T-102 (C-21 渲染三档): toggles the §13.4 scanline overlay pass from the
    /// render-tier decision (<see cref="EraCore.Presentation.RendererDecisionMap"/>
    /// — L 档无扫描线). Mirrors the decision only; no tier logic lives here.
    /// </summary>
    internal void SetScanlineEnabled(bool enabled)
    {
        if (_scanlineOverlay is not null)
        {
            _scanlineOverlay.Visible = enabled;
        }
    }

    /// <summary>
    /// T-110 (F-164): re-binds the platform tier row and re-mirrors the
    /// AI-advanced-syntax hint visibility from
    /// <see cref="WebUiSurfaces.Surfaces"/> (能力表驱动——Web 档位行隐藏该
    /// 入口，桌面行显示；Main 供真实平台事实，headless 冒烟以 Web 行做
    /// feature 模拟，D-34 桌面口径).
    /// </summary>
    internal void SetAgentTiers(AgentTierAvailability tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        _tiers = tiers;
        if (_aiSyntaxHint is not null)
        {
            _aiSyntaxHint.Visible = WebUiSurfaces.Surfaces(_tiers).AiAdvancedSyntaxHintVisible;
        }
    }

    // ------------------------------------------- T-163 军师面板时代皮肤+立绘 --

    /// <summary>皮肤应用面（时代皮肤=边框/底纹着色目标；VisualExpansionDirector.ApplyEraSkin 消费——palettes 同源，别新造根解析）.</summary>
    internal PanelContainer ExpandSkinTarget => _expandedPanel;

    /// <summary>最近一次立绘绑定是否真装载到 era{N}-advisor 贴图（缺图=false——观测探针）.</summary>
    internal bool PortraitLoaded { get; private set; }

    /// <summary>
    /// 时代主题应用（T-163）：标题带按时代色着色+军师立绘位重绑
    /// （<c>era{N}-advisor.png</c>——eraIndex 变化才重装载，SessionBattlefield
    /// Viewport.Bind 双条件重绑同律；缺图=既有程序头像优雅回退不阻塞，
    /// 素材落地后随下次重绑自动出现）。
    /// </summary>
    /// <param name="eraColor">时代主色（visual-assets.json palettes 同源，调用方经 ExpandDirector.EraColor 取）.</param>
    /// <param name="eraIndex">时代序 1..6（立绘键）.</param>
    internal void ApplyEraTheme(Color eraColor, int eraIndex)
    {
        _panelTitle.AddThemeColorOverride("font_color", eraColor);
        if (_portraitEra == eraIndex)
        {
            return;
        }

        _portraitEra = eraIndex;
        string portraitPath = HudIconDecisions.AdvisorPortraitPath(eraIndex);
        Texture2D? portrait = ResourceLoader.Exists(portraitPath)
            ? GD.Load<Texture2D>(portraitPath)
            : null;
        _avatar.Texture = portrait ?? BuildAvatarTexture();
        PortraitLoaded = portrait is not null;
    }

    /// <summary>时代主题还原（退出战役会话=恢复菜单态口径：标题带回全息青+程序头像+默认全息框）.</summary>
    internal void ResetEraTheme()
    {
        _portraitEra = 0;
        PortraitLoaded = false;
        _avatar.Texture = BuildAvatarTexture();
        _panelTitle.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _expandedPanel.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
    }

    /// <summary>AI-advanced-syntax hint visible right now (T-110 smoke probe).</summary>
    internal bool AiSyntaxHintVisible => _aiSyntaxHint is { Visible: true };

    private void OnReenablePressed() => SetSystemEnabled(true);

    /// <summary>Bubble press: expand the full panel (with entrance animation).</summary>
    internal void Expand()
    {
        if (_machine.SetExpanded(true))
        {
            ApplyState();
            PlayEntrance(_expandedPanel);
        }
    }

    /// <summary>Panel collapse button: back to the resting bubble.</summary>
    internal void Collapse()
    {
        if (_machine.SetExpanded(false))
        {
            ApplyState();
        }
    }

    private void OnBubblePressed() => Expand();

    private void OnCollapsePressed() => Collapse();

    // ---- order path (real pipeline) ----------------------------------------

    private void OnSubmitPressed() => SubmitOrder(_orderInput.Text);

    private void OnOrderSubmitted(string text) => SubmitOrder(text);

    /// <summary>
    /// One real round trip through the five-stage pipeline. Rejected (null)
    /// when the input surface is closed (system off — no order path exists).
    /// </summary>
    internal PipelineOutcomeKind? SubmitOrder(string text)
    {
        if (_host is null)
        {
            throw new InvalidOperationException("SystemPanel.Bind(IAdvisorBoardHost) must run before orders.");
        }

        if (!_machine.OrderBoxVisible)
        {
            return null;
        }

        string order = text.Trim();
        if (order.Length == 0)
        {
            _reportText.Text = $"空军令不算数——示例「{ExampleOrder}」。";
            return null;
        }

        _orderInput.Text = order;
        OrderOutcome outcome = _host.Pipeline.Handle(order);
        _lastOutcome = outcome.Kind;
        RenderOutcome(outcome);

        // T-137 三档路由（L1 澄清面）：L1 没听懂的军令交给路由矩阵——L2 就绪
        // → 入波间队列（下一波间异步填充）；降级 → 中性横幅一次（L1 澄清卡
        // 已在屏上，功能零损失）。L3 选用时同样经 LlmGateway 走异步填充。
        if (outcome.Kind is PipelineOutcomeKind.Clarification or PipelineOutcomeKind.Refused
            && _l2 is not null && _waveGap is not null)
        {
            L2RouteDecision decision = _l2.DecideRoute();
            if (decision.EffectiveTier == LlmTierKind.L2Local && _waveGap.Enqueue(order) is not null)
            {
                _reportText.Text =
                    $"这道令 L1 语法没听懂——L2 本地军师将在波间帮你再想（超时自动放弃，不卡波）。{outcome.ClarifyingKey}";
                _bubble.Text = $"◈ 系统·{_reportText.Text}";
            }

            RefreshL2Surfaces(); // mirrors the degradation banner when the lane is down
        }

        return outcome.Kind;
    }

    /// <summary>
    /// T-137: one wave-gap fill landed — its candidate action re-enters the
    /// SAME five-stage pipeline through <see cref="AgentPipeline.HandleSystemAction"/>
    /// (registry gates unchanged — no RL6 bypass) and renders through the
    /// normal outcome lane.
    /// </summary>
    private void OnL2FillLanded(L2.WaveGapLandedFill landed)
    {
        if (_host is null || !landed.Result.Ok || landed.Result.ActionJson is null)
        {
            return;
        }

        if (System.Text.Json.JsonDocument.Parse(landed.Result.ActionJson).RootElement
                .TryGetProperty("action", out var actionElement)
            && Enum.TryParse<SystemActionKind>(actionElement.GetString(), out SystemActionKind verb))
        {
            OrderOutcome outcome = _host.Pipeline.HandleSystemAction(verb, landed.Result.ActionJson);
            _lastOutcome = outcome.Kind;
            RenderOutcome(outcome);
            string line = outcome.Report is not null
                ? ReportLine(outcome.Report)
                : "方案卡已出：确认后我才动手。";
            _reportText.Text = $"[L2 波间] {landed.Order.OrderText} → {line}";
            _bubble.Text = $"◈ 系统·{_reportText.Text}";
        }
    }

    /// <summary>T-137: fill abandoned (budget exhausted / lane died) — neutral L1-fallback banner only.</summary>
    private void OnL2FillAbandoned(L2QueuedFill order, L2DegradationReason reason)
    {
        _l2Banner.Text = L2DegradationNotices.BannerZh(reason);
        _l2Banner.Visible = true;
    }

    /// <summary>Current degradation banner text (empty when none) — smoke probe.</summary>
    internal string L2BannerText => _l2Banner?.Text ?? string.Empty;

    /// <summary>Current tier hint text — smoke probe.</summary>
    internal string L2TierHintText => _l2TierHint?.Text ?? string.Empty;

    /// <summary>Confirm button → Pipeline.Confirm of the pending plan card.</summary>
    internal PipelineOutcomeKind? ConfirmPending()
    {
        if (_pendingId < 0)
        {
            return null;
        }

        OrderOutcome outcome = _host.Pipeline.Confirm(_pendingId);
        _pendingId = -1;
        _planCard.Hide();
        _lastOutcome = outcome.Kind;
        RenderOutcome(outcome);
        return outcome.Kind;
    }

    /// <summary>Refuse button → Pipeline.Refuse of the pending plan card.</summary>
    internal PipelineOutcomeKind? RefusePending()
    {
        if (_pendingId < 0)
        {
            return null;
        }

        OrderOutcome outcome = _host.Pipeline.Refuse(_pendingId);
        _pendingId = -1;
        _planCard.Hide();
        _lastOutcome = outcome.Kind;
        RenderOutcome(outcome);
        return outcome.Kind;
    }

    private void OnConfirmPressed() => ConfirmPending();

    private void OnRefusePressed() => RefusePending();

    private void RenderOutcome(OrderOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case PipelineOutcomeKind.AwaitingApproval:
                _pendingId = outcome.PendingId;
                ShowPlanCard(outcome.PendingCard!);
                _reportText.Text = "方案卡已出：确认后我才动手。";
                break;
            case PipelineOutcomeKind.Completed:
            case PipelineOutcomeKind.Denied:
            case PipelineOutcomeKind.ApprovalRefused:
                _reportText.Text = ReportLine(outcome.Report!);
                break;
            case PipelineOutcomeKind.Clarification:
            case PipelineOutcomeKind.Refused:
                // F-110 clarification card copy: the key plus the wheel hint.
                _reportText.Text =
                    $"没听懂这道令（{outcome.ClarifyingKey}）。可用轮盘直接点：「{WheelOrders[0]}」。";
                break;
        }

        _bubble.Text = $"◈ 系统·{_reportText.Text}";
    }

    private void ShowPlanCard(PlanCard card)
    {
        _planOrder.Text = $"军令：{card.OrderText}";
        _planSteps.Text = "步骤：\n" + string.Join("\n", card.Steps.Select((step, i) =>
            $"  {i + 1}. {step.Verb} · {step.Points}pt"));
        _planCost.Text = $"消耗：系统点 {card.TotalPoints} · 资源 {card.TotalResources:0.#}";
        _planRisks.Text = card.Risks.Count == 0
            ? "风险：无红标"
            : "风险：\n" + string.Join("\n", card.Risks.Select(risk => $"  ! {risk}"));
        _planCard.Show();
    }

    private static string ReportLine(AgentReport report) =>
        report.Findings.Count == 0
            ? report.Summary
            : $"{report.Summary}（{string.Join("；", report.Findings.Select(f => f.Text))}）";

    // ---- HUD ---------------------------------------------------------------

    /// <summary>
    /// Renders the three metrics (mono-padded digit fields from EraCore.Presentation.HudMetrics)
    /// and the defense-line alarm decision: below the §10.3 warning line the
    /// alarm fires in BOTH switch positions (T-075 警报不缺失) — red HUD field
    /// always, plus the advisor bubble line on the rising edge while the
    /// system is on (OffModeDowngrade owns the decision, tested there).
    /// </summary>
    internal void RefreshHud(HudSnapshot snapshot)
    {
        _waveField.Text = $"波次 {HudMetrics.FormatWave(snapshot.Wave)}";
        _rateField.Text = $"产出 {HudMetrics.FormatProduction(snapshot.ProductionPerSecond)}";
        _integrityField.Text =
            $"防线 {HudMetrics.FormatIntegrity(snapshot.DefenseIntegrity)}({snapshot.TowerCount}塔)";

        DefenseAlarmView alarm = OffModeDowngrade.DefenseAlarmAt(
            _machine.SystemEnabled, snapshot.DefenseIntegrity);
        _alarmField.Visible = alarm.Fires;
        _alarmField.Text = alarm.Text;
        if (alarm.Fires)
        {
            if (!_alarmAnnounced && alarm.Channel == AlarmChannel.PersonaBubble)
            {
                _reportText.Text = alarm.Text; // F-105 persona line, rising edge only
                _bubble.Text = $"◈ 系统·{alarm.Text}";
            }

            _alarmAnnounced = true;
        }
        else
        {
            _alarmAnnounced = false;
        }
    }

    /// <summary>
    /// Renders the FULL third metric (T-075, F-111 全口径): current-wave
    /// composition (类型×数量×甲) plus the read-only next-wave preview digest.
    /// </summary>
    /// <param name="view">Aggregated view from <see cref="SimHost.BuildWaveCompositionView"/>.</param>
    /// <param name="nameOf">Content-id → display-name lookup (content catalog).</param>
    internal void RefreshComposition(WaveCompositionView view, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(nameOf);
        _compField.Text = $"构成 {WaveComposition.FormatSummary(view.CurrentWave, nameOf)}";
        _nextWaveField.Text =
            $"下波{HudMetrics.FormatWave(view.NextWave)} {WaveComposition.FormatDigest(view.NextWavePreview)}";
    }

    /// <summary>
    /// Feeds the live trust score into the badge tracker (T-075, §11.4):
    /// silent while the tier holds; a crossing swaps the corner mark/label
    /// and, while the system is on, speaks the tier-change self-deprecating
    /// line through the advisor channel (L1 template — never a model call).
    /// </summary>
    /// <returns>The observed transition, or null when the tier held.</returns>
    internal TrustBadgeChange? ObserveTrustScore(int score)
    {
        TrustBadgeChange? change = _trustTracker.Observe(score);
        if (change is not null && _machine.SystemEnabled)
        {
            _reportText.Text = change.Line;
            _bubble.Text = $"◈ 系统·{change.Line}";
        }

        UpdateBadgeUi();
        return change;
    }

    /// <summary>Mirrors the tracker tier into the corner mark + label (three states, no raw score).</summary>
    private void UpdateBadgeUi()
    {
        TrustBadge tier = _trustTracker.Current;
        _trustMark.Text = TrustBadgeLabels.Mark(tier);
        _trustMark.AddThemeColorOverride("font_color", tier switch
        {
            TrustBadge.Skeptical => HoloSkin.NeutralEdge,   // 疑 = dim slate
            TrustBadge.Trustworthy => HoloSkin.EdgeCyan,    // 托 = panel cyan
            _ => new Color(1.00f, 0.84f, 0.40f),            // 从 = warm gold
        });
        _trustLabel.Text = $"信任·{TrustBadgeLabels.Label(tier)}";
    }

    /// <summary>One-line HUD text for the smoke log (smoke probe).</summary>
    internal string HudText()
    {
        string alarm = _alarmField.Visible ? $" {_alarmField.Text}" : string.Empty;
        return $"{_waveField.Text} {_rateField.Text} {_integrityField.Text} {_compField.Text} {_nextWaveField.Text}{alarm}";
    }

    // ---- entrance animation (§13.4 中线展开 3 帧 + 扫描光带) -----------------

    /// <summary>
    /// Mid-line expand over 3 render frames (0.1 s at 30 fps) plus a
    /// horizontal additive scan band sweeping the panel. Tween-based — the
    /// frame-count equivalence is documented in the task card.
    /// </summary>
    internal void PlayEntrance(Control target)
    {
        target.PivotOffset = target.Size / 2f;
        target.Scale = new Vector2(1f, 0.05f);

        var band = new ColorRect
        {
            Color = new Color(HoloSkin.EdgeCyan, 0.55f),
            Size = new Vector2(6f, target.Size.Y),
            MouseFilter = MouseFilterEnum.Ignore,
            Material = HoloSkin.BuildAdditiveMaterial(),
        };
        target.AddChild(band);

        Tween tween = CreateTween();
        tween.SetParallel();
        tween.TweenProperty(target, "scale", Vector2.One, 0.1f);
        tween.TweenProperty(band, "position:x", target.Size.X, 0.1f).From(0f);
        tween.Chain().TweenCallback(Callable.From(band.QueueFree));
    }

    // ---- helpers -----------------------------------------------------------

    private static T Separation<T>(T box, int pixels) where T : BoxContainer
    {
        box.AddThemeConstantOverride("separation", pixels);
        return box;
    }

    private static Label MetricLabel(string text)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        return label;
    }

    private static Label DimLabel(string text)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        return label;
    }

    private static string ShortenWheelLabel(string order) => order switch
    {
        "守住东门" => "守东门",
        "在东门建两座木箭塔" => "建塔",
        "敌情怎么样" => "敌情",
        "我们还剩多少木材" => "存量",
        _ => order,
    };

    /// <summary>14×14 default avatar: dark fill + cyan frame (聊天窗默认头像, no binary asset).</summary>
    private static ImageTexture BuildAvatarTexture()
    {
        var image = Image.CreateEmpty(14, 14, false, Image.Format.Rgba8);
        image.Fill(new Color(HoloSkin.BaseBlue, 0.95f));
        for (int i = 0; i < 14; i++)
        {
            image.SetPixel(i, 0, HoloSkin.EdgeCyan);
            image.SetPixel(i, 13, HoloSkin.EdgeCyan);
            image.SetPixel(0, i, HoloSkin.EdgeCyan);
            image.SetPixel(13, i, HoloSkin.EdgeCyan);
        }

        // Two “eye” pixels so the avatar reads as a face at 1× scale.
        image.SetPixel(4, 6, HoloSkin.EdgeCyan);
        image.SetPixel(9, 6, HoloSkin.EdgeCyan);
        return ImageTexture.CreateFromImage(image);
    }
}
