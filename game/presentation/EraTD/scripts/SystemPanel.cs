using EraCore.Agent.Pipeline;
using EraCore.Contracts.Agent;
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
    private SimHost _host = null!;

    private PanelContainer _hudBar = null!;
    private Label _waveField = null!;
    private Label _rateField = null!;
    private Label _integrityField = null!;
    private Button _reenableButton = null!;

    private Button _bubble = null!;
    private PanelContainer _expandedPanel = null!;
    private LineEdit _orderInput = null!;
    private Label _ghostHint = null!;
    private CheckBox _offSwitch = null!;
    private Label _reportText = null!;
    private TextureRect _avatar = null!;

    private PanelContainer _planCard = null!;
    private Label _planOrder = null!;
    private Label _planSteps = null!;
    private Label _planCost = null!;
    private Label _planRisks = null!;

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

    /// <summary>Binds the real sim + pipeline host (must precede any order).</summary>
    internal void Bind(SimHost host)
    {
        _host = host;
        RefreshHud(host.BuildHudSnapshot());
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
        _reenableButton = new Button { Text = "开启系统", Visible = false };
        _reenableButton.Pressed += OnReenablePressed;

        row.AddChild(_waveField);
        row.AddChild(_rateField);
        row.AddChild(_integrityField);
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

        var bevel = new PanelContainer();
        bevel.AddThemeStyleboxOverride("panel", HoloSkin.BuildInnerBevel());
        bevel.AddThemeConstantOverride("margin_left", 6);
        bevel.AddThemeConstantOverride("margin_right", 6);
        bevel.AddThemeConstantOverride("margin_top", 4);
        bevel.AddThemeConstantOverride("margin_bottom", 4);

        var body = Separation(new VBoxContainer(), 4);

        var titleRow = new HBoxContainer();
        var title = DimLabel("◆ 系统全息面板");
        var collapse = new Button { Text = "收起" };
        collapse.Pressed += OnCollapsePressed;
        titleRow.AddChild(title);
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

        var reportRow = Separation(new HBoxContainer(), 6);
        _avatar = new TextureRect
        {
            Texture = BuildAvatarTexture(),
            CustomMinimumSize = new Vector2(14, 14),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
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
        reportRow.AddChild(_reportText);

        body.AddChild(titleRow);
        body.AddChild(orderRow);
        body.AddChild(_ghostHint);
        body.AddChild(wheelRow);
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
        _bubble.Visible = _machine.State == PanelState.Bubble;
        _expandedPanel.Visible = _machine.OrderBoxVisible;
        if (!_machine.PanelVisible)
        {
            _planCard.Hide();
            _pendingId = -1;
        }

        // HUD stays in both modes; the skin swaps holo ↔ neutral banner
        // (§10.7: metrics survive the off-switch, functionality lossless).
        _hudBar.AddThemeStyleboxOverride(
            "panel", _machine.HudNeutral ? HoloSkin.BuildNeutralBox() : HoloSkin.BuildHoloBox());
        _reenableButton.Visible = _machine.HudNeutral;
        Color metricColor = _machine.HudNeutral ? Colors.White : HoloSkin.TextDimCyan;
        _waveField.AddThemeColorOverride("font_color", metricColor);
        _rateField.AddThemeColorOverride("font_color", metricColor);
        _integrityField.AddThemeColorOverride("font_color", metricColor);
    }

    /// <summary>Drives the off-switch (setting item) through the tested machine.</summary>
    internal void SetSystemEnabled(bool enabled)
    {
        _machine.SetSystemEnabled(enabled);
        _offSwitch.SetPressedNoSignal(!enabled);
        ApplyState();
    }

    private void OnOffSwitchToggled(bool pressed) => SetSystemEnabled(!pressed);

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
            throw new InvalidOperationException("SystemPanel.Bind(SimHost) must run before orders.");
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
        return outcome.Kind;
    }

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

    /// <summary>Renders the three metrics (mono-padded digit fields from EraCore.Presentation.HudMetrics).</summary>
    internal void RefreshHud(HudSnapshot snapshot)
    {
        _waveField.Text = $"波次 {HudMetrics.FormatWave(snapshot.Wave)}";
        _rateField.Text = $"产出 {HudMetrics.FormatProduction(snapshot.ProductionPerSecond)}";
        _integrityField.Text =
            $"防线 {HudMetrics.FormatIntegrity(snapshot.DefenseIntegrity)}({snapshot.TowerCount}塔)";
    }

    /// <summary>One-line HUD text for the smoke log (smoke probe).</summary>
    internal string HudText() =>
        $"{_waveField.Text} {_rateField.Text} {_integrityField.Text}";

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
