using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Supply;
using Godot;

namespace EraTD;

// T-093（C-22/C-23 UI/UX 完备）信息层总装：三层数字信息（§10.6/附录 G.3）+
// 分区命名标注层（§10.5/§10.12）+ 词汇表/帮助面板（F-124）+ 谓词提示队列
// （F-110，§10.1 首次引导串联）+ 物流断流可视化（§10.8=T-063 断点 UI 高亮
// 验收点）+ headless 截图自检（截图 diff 类验收——口径见 RunHeadlessSmoke）。
//
// 分层纪律（CS-1.6）：本节点零业务规则——一切决策在 EraCore.Presentation
// 纯逻辑（TowerInfoCard/TowerHeadBar/BottleneckHighlightBoard/ZoneLabelTable/
// Glossary/HintRulebook/HintQueue，各自单测）；本类只做内容装载、节点镜像
// 与输入接线。Main.cs 接线=追加一段 Instantiate+headless 条件化退出（窗口
// 模式让主循环迭代真实帧——截图留档依赖；冲突由后提交者 rebase）。
//
// 三层信息对应面：
//   ①静态面板=塔详情浮窗（DPS/射程/弹种/克制——towers.json+敌表 counters
//     反演；点击塔弹出+升级预览[下一级 DPS/成本差]）；
//   ②塔头状态条=每塔头顶弹药余量/冷却进度条（1 件弹药=10 发基准）；
//   ③物流断流=瓶颈红圈+断流灰线（真 BottleneckDetector 事件驱动）。

/// <summary>
/// The T-093 information-layers control: world-space overlays (head bars,
/// bottleneck highlight, zone labels) plus screen-space surfaces (tower
/// detail card, F1 help panel, zone chips with click-to-locate, predicate
/// hint bubble). Headless runs execute the acceptance smoke inside
/// <see cref="_Ready"/> and print the UI_OK marker.
/// </summary>
public sealed partial class InfoLayersUi : Control
{
    private const int RefreshFrames = 15;      // 世界层刷新节拍（0.5 s 仿真时，HUD 同拍）
    private const float Cell = SimSnapshotBridge.CellSizePx;

    private Main _main = null!;
    private SimHost _host = null!;
    private Camera2D _camera = null!;
    private SystemPanel _panel = null!;

    private Glossary _glossary = null!;
    private HintRulebook _hints = null!;
    private ZoneLabelTable _zones = null!;
    private TowerCardCatalog _towerCatalog = null!;
    private CounterIndex _counters = null!;
    private HintQueue _hintQueue = null!;
    private IFileStore _store = null!;

    // T-110 (F-164): platform tier row — desktop_only 词条标注镜像
    // WebUiSurfaces.AnnotateDesktopOnly（Web 行激活「桌面版功能」注记）。
    private AgentTierAvailability _tiers = WebConstraints.Availability(isWeb: false);

    private BottleneckHighlightBoard _board = null!;
    private BottleneckDetector? _liveDetector;       // 真事件驱动（live 挂真 monitor）
    private long _liveDetectorLastTick = -1;
    private int _lastWaveSeen;
    private bool _gameStartFired;
    private bool _cameraZoneFired;
    private PipelineOutcomeKind? _lastPanelOutcome;

    // ---- world layer ------------------------------------------------------
    private Control _worldLayer = null!;
    private readonly Dictionary<long, (ColorRect Bar, ColorRect Fill)> _headBars = new();
    private readonly List<Label> _zoneLabels = new();
    private BottleneckLayer _bottleneckLayer = null!;

    // ---- screen surfaces --------------------------------------------------
    private PanelContainer _detailCard = null!;
    private Label _detailTitle = null!;
    private Label _detailLines = null!;
    private Label _detailUpgrade = null!;
    private PanelContainer _helpPanel = null!;
    private LineEdit _helpSearch = null!;
    private VBoxContainer _helpList = null!;
    private Label _helpDef = null!;
    private HBoxContainer _zoneBar = null!;
    private HBoxContainer _personaBar = null!;
    private PanelContainer _hintBubble = null!;
    private Label _hintText = null!;
    private Button _hintTerm = null!;
    private HintDue? _shownHint;

    /// <summary>Scene entry (Main.cs single-line wiring, same lane as the other HUDs).</summary>
    internal static InfoLayersUi Instantiate(Main main, SimHost host)
    {
        var ui = new InfoLayersUi
        {
            _main = main,
            _host = host,
            // T-110: host supplies the single platform fact (SettingsPanelUi
            // 先例) — every consumer derives from the WebConstraints table.
            _tiers = WebConstraints.Availability(OS.HasFeature("web")),
        };
        main.AddChild(ui); // _Ready builds every surface (and smokes headless)
        return ui;
    }

    public override void _Ready()
    {
        ZIndex = 18; // 世界层夹 unit(10) 与 panel(20) 之间；浮窗面在内部再抬
        MouseFilter = MouseFilterEnum.Ignore;

        _camera = _main.GetNode<Camera2D>("WorldCamera");
        _panel = _main.GetNode<SystemPanel>("SystemPanel");
        _store = CreateStore();

        LoadContent();
        BuildWorldLayer();
        BuildDetailCard();
        BuildHelpPanel();
        BuildZoneBar();
        BuildHintBubble();

        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessSmoke();
        }
        else if (!string.IsNullOrWhiteSpace(
                     System.Environment.GetEnvironmentVariable("ERA_UI_SNAPSHOT_ARCHIVE")))
        {
            CaptureLiveArchive(); // 真渲染器（窗口运行）的像素留档
        }
    }

    /// <summary>
    /// 窗口运行的像素留档（真光栅）：等两个画后帧（首帧前纹理未画）再走
    /// headless 冒烟同套 <see cref="CaptureAndCheck"/>；SNAP 行 mode=pixels
    /// 带真实像素分布自检。宿主窗口模式不主动退出（T-093 起主循环迭代真实
    /// 帧）——留档跑法=--quit-after 90。
    /// </summary>
    private async void CaptureLiveArchive()
    {
        var sink = new List<(string Name, string Line)>();
        await CaptureLiveState(sink, "hud");
        await CaptureLiveState(sink, "hint-bubble", () =>
        {
            _hintBubble.Show();
            _hintText.Text = "◈ 因果提示：有塔效率低于一半——先查供给线。"; // 留档帧：因果提示在镜
        });
        await CaptureLiveState(sink, "help-panel", () =>
        {
            _helpPanel.Show();
            RefreshHelpList();
        });
        foreach (string line in sink.Select(s => s.Line))
        {
            GD.Print(line);
        }
    }

    /// <summary>一个留档状态=改 UI→等一帧画完→取像（每状态独立成帧）。</summary>
    private async System.Threading.Tasks.Task CaptureLiveState(
        List<(string Name, string Line)> sink, string name, Action? mutate = null)
    {
        mutate?.Invoke();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        CaptureAndCheck(sink, name);
        _hintBubble.Hide();
        _helpPanel.Hide();
    }

    /// <summary>
    /// Live path: one frame poll — head bars, zone-label visibility,
    /// bottleneck board decay, hint-predicate observations. Read-only over
    /// the host state (CS-1.5: UI 永不写模拟)。
    /// </summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_host is null)
        {
            return;
        }

        _frame++;
        if (_frame % RefreshFrames != 0)
        {
            return;
        }

        RefreshHeadBars();
        RefreshZoneLabels();
        RefreshBottlenecks();
        ObserveHints();
    }

    private int _frame;

    // ---- 内容装载 ----------------------------------------------------------

    private void LoadContent()
    {
        string uiDir = FindContentDir(
            System.IO.Path.Combine("game", "content", "ui"), "game/content/ui");
        string era1Dir = FindContentDir(
            System.IO.Path.Combine("game", "content", "era1"), "game/content/era1");

        _glossary = Glossary.Parse(ReadFile(uiDir, "glossary.json"), "ui/glossary.json");
        _hints = HintRulebook.Parse(ReadFile(uiDir, "hints.json"), "ui/hints.json", _glossary);
        _zones = ZoneLabelTable.Parse(
            ReadFile(uiDir, "zones.json"), "ui/zones.json",
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells);
        _towerCatalog = TowerCardCatalog.FromTowersJson(ReadFile(era1Dir, "towers.json"), "era1/towers.json");
        _counters = CounterIndex.FromEnemiesJson(ReadFile(era1Dir, "enemies.json"), "era1/enemies.json");

        _board = new BottleneckHighlightBoard();
        _hintQueue = new HintQueue(_hints, new HashSet<string>(HintProgressStore.Load(_store).Read));
        _hintQueue.BindGlossary(_glossary);

        // live 真事件探测器：挂真 monitor（开发宿主 monitor 宽限恒 1.0——
        // 射击节奏被金样钉死；接线是真的，事件随宿主语义而定）。
        _liveDetector = new BottleneckDetector(new SimTickClock(_host.Simulation), _host.Simulation.SupplyMonitor);
        _liveDetector.BottleneckDetected += evt =>
        {
            _board.On(evt, FallbackSupplierCell);
            FireHint(HintPredicateKind.FirstBottleneck);
        };
    }

    private CellCoord FallbackSupplierCell { get; } = new(2, 14); // 仓库格（SimHost.WarehouseCell 同值）

    /// <summary>Progress-store root（headless 用抛弃根，live 用 D-03 开发根——CinematicDirector 先例）。</summary>
    private static IFileStore CreateStore()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            string root = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", "info-smoke");
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            return new DevFileStore(root);
        }

        return new DevFileStore();
    }

    // ---- 世界层：②塔头状态条 + ③断流高亮 + 分区名 --------------------------

    private void BuildWorldLayer()
    {
        _worldLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 0 };
        _bottleneckLayer = new BottleneckLayer { MouseFilter = MouseFilterEnum.Ignore };
        _worldLayer.AddChild(_bottleneckLayer);
        AddChild(_worldLayer);
    }

    /// <summary>②塔头状态条：逐塔（cell 顶缘）一条 12×2 px 弹药条+1 px 冷却条。</summary>
    private void RefreshHeadBars()
    {
        var seen = new HashSet<long>();
        foreach (SimTower tower in _host.Simulation.Towers)
        {
            seen.Add(tower.Id.Value);
            TowerHeadBarView view = TowerHeadBar.Build(
                tower.Spec,
                tower.Weapon.AmmoShotsRemaining,
                AmmoConverter.ShotsPerItem,
                tower.Weapon.CooldownTicksRemaining);

            if (view.AmmoTier == HeadBarTier.Hidden)
            {
                continue; // 无弹耗塔不出条（已存在的留给下方清尾统一回收）
            }

            if (!_headBars.TryGetValue(tower.Id.Value, out var bar))
            {
                var track = new ColorRect
                {
                    Size = new Vector2(14f, 2f),
                    MouseFilter = MouseFilterEnum.Ignore,
                    Color = new Color(0f, 0f, 0f, 0.6f),
                };
                var fill = new ColorRect
                {
                    Size = new Vector2(14f, 2f),
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                var reload = new ColorRect
                {
                    Size = new Vector2(14f, 1f),
                    MouseFilter = MouseFilterEnum.Ignore,
                    Color = new Color(HoloSkin.EdgeCyan, 0.5f),
                };
                track.AddChild(fill);
                track.AddChild(reload);
                _worldLayer.AddChild(track);
                bar = (track, fill);
                _headBars[tower.Id.Value] = bar;
            }

            bar.Bar.Position = new Vector2(
                tower.Cell.X * Cell + Cell / 2f - 7f, tower.Cell.Y * Cell - 4f);
            bar.Fill.Size = new Vector2(14f * (float)view.AmmoRatio, 2f);
            bar.Fill.Color = view.AmmoTier switch
            {
                HeadBarTier.Red => HoloSkin.RiskRed,
                HeadBarTier.Amber => new Color(0.95f, 0.78f, 0.25f),
                _ => new Color(0.25f, 0.95f, 0.45f),
            };
        }

        // 清尾：拆除/无弹耗塔的旧条回收（不在当前可见集合即释放）
        foreach (long id in _headBars.Keys.ToList())
        {
            if (!seen.Contains(id) && _headBars.Remove(id, out var removed))
            {
                removed.Bar.QueueFree();
            }
        }
    }

    /// <summary>③断流可视化：红圈+灰线由 <see cref="BottleneckLayer"/> 绘制，这里只推进 TTL。</summary>
    private void RefreshBottlenecks()
    {
        long tick = _host.Simulation.CurrentTick;
        _board.Tick(tick);
        _bottleneckLayer.SetViews(_board.Active);
        TickLiveDetector(tick);
    }

    private void TickLiveDetector(long tick)
    {
        if (_liveDetector is null)
        {
            return;
        }

        long advanced = _liveDetectorLastTick < 0 ? 1 : tick - _liveDetectorLastTick;
        _liveDetectorLastTick = tick;
        for (long i = 0; i < advanced; i++)
        {
            _liveDetector.Tick(); // 每 sim tick 一次（detector 只读 efficiency 曲线）
        }
    }

    /// <summary>分区名浮显：镜头窗内的 zone 标签可见（§10.12 镜头靠近浮显）。</summary>
    private void RefreshZoneLabels()
    {
        (int x0, int y0, int x1, int y1) = CameraCellRect();
        System.Collections.Generic.IReadOnlyList<ZoneLabel> visible = _zones.VisibleIn(x0, y0, x1, y1);

        // 复用已有 Label：按 id 对齐（分区少，线性对齐足够）
        foreach (ZoneLabel zone in _zones.Zones)
        {
            bool show = visible.Contains(zone);
            Label? label = _zoneLabels.FirstOrDefault(l => l.Name.ToString() == zone.Id);
            if (show && label is null)
            {
                label = new Label { Name = zone.Id, MouseFilter = MouseFilterEnum.Ignore };
                label.AddThemeFontSizeOverride("font_size", 11);
                label.AddThemeColorOverride("font_color", new Color(HoloSkin.EdgeCyan, 0.85f));
                label.AddThemeColorOverride("font_outline_color", Colors.Black);
                label.AddThemeConstantOverride("outline_size", 3);
                label.Text = $"◈ {zone.NameZh} {zone.NameEn}";
                _worldLayer.AddChild(label);
                _zoneLabels.Add(label);
            }

            if (label is not null)
            {
                label.Visible = show;
                label.Position = new Vector2(zone.Cell.X * Cell + 2f, zone.Cell.Y * Cell + 6f);
            }
        }

        if (!_cameraZoneFired && visible.Count > 0)
        {
            _cameraZoneFired = true;
            FireHint(HintPredicateKind.CameraNearZone);
        }
    }

    private (int X0, int Y0, int X1, int Y1) CameraCellRect()
    {
        Vector2 size = _camera.GetViewport().GetVisibleRect().Size;
        Vector2 center = _camera.GetScreenCenterPosition();
        float halfW = size.X / 2f;
        float halfH = size.Y / 2f;
        return (
            (int)Math.Floor((center.X - halfW) / Cell),
            (int)Math.Floor((center.Y - halfH) / Cell),
            (int)Math.Ceiling((center.X + halfW) / Cell),
            (int)Math.Ceiling((center.Y + halfH) / Cell));
    }

    // ---- ①静态面板：塔详情浮窗 ---------------------------------------------

    private void BuildDetailCard()
    {
        _detailCard = new PanelContainer { Visible = false };
        _detailCard.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _detailCard.ZIndex = 25;

        var box = new VBoxContainer();
        _detailTitle = Dim("塔详情");
        _detailLines = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _detailLines.AddThemeFontSizeOverride("font_size", 11);
        _detailLines.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _detailUpgrade = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _detailUpgrade.AddThemeFontSizeOverride("font_size", 11);
        _detailUpgrade.AddThemeColorOverride("font_color", new Color(1.00f, 0.84f, 0.40f));
        var close = new Button { Text = "关闭" };
        close.Pressed += () => _detailCard.Hide();
        box.AddChild(_detailTitle);
        box.AddChild(_detailLines);
        box.AddChild(_detailUpgrade);
        box.AddChild(close);
        _detailCard.AddChild(box);
        AddChild(_detailCard);
    }

    /// <summary>
    /// ①点击塔 → 详情卡（headless 冒烟与 live 点击同一入口，无第二套逻辑）：
    /// 卡片贴塔、升级预览行带「下一级 DPS/成本差」。
    /// </summary>
    internal bool SelectTowerAt(CellCoord cell)
    {
        foreach (SimTower tower in _host.Simulation.Towers)
        {
            if (!tower.Cell.Equals(cell))
            {
                continue;
            }

            ShowTowerCard(tower);
            return true;
        }

        _detailCard.Hide();
        return false;
    }

    private void ShowTowerCard(SimTower tower)
    {
        TowerCardView view = TowerInfoCard.Build(
            tower.Spec, _towerCatalog.Names, _counters, _towerCatalog.SpecsById);
        _detailTitle.Text = $"◈ {view.Title}";
        _detailLines.Text = string.Join("\n", view.Lines) + $"\n{view.CounterLine}";
        _detailUpgrade.Text = view.Upgrade is { } upgrade
            ? $"升级预览 T{tower.Spec.Tier + 1}：DPS {upgrade.NextDps:0.#}（+{upgrade.DpsDelta:0.#}）· " +
              $"成本 {upgrade.NextCostTotal:0}（+{upgrade.CostDelta:0}）"
            : "升级预览：已达顶格";
        _detailCard.Show();
        Vector2 cardSize = _detailCard.Size == Vector2.Zero ? new Vector2(170f, 110f) : _detailCard.Size;
        _detailCard.Position = new Vector2(
            Math.Clamp(tower.Cell.X * Cell - cardSize.X - 4f, 4f, 636f - cardSize.X),
            Math.Clamp(tower.Cell.Y * Cell - 4f, 46f, 356f - cardSize.Y));
    }

    // ---- 帮助面板（F1，F-124 词汇表） --------------------------------------

    private void BuildHelpPanel()
    {
        _helpPanel = new PanelContainer { Visible = false };
        _helpPanel.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _helpPanel.ZIndex = 40;

        var box = new VBoxContainer();
        var titleRow = new HBoxContainer();
        var title = Dim("◆ 词汇表 · F1 开关");
        var close = new Button { Text = "关闭" };
        close.Pressed += ToggleHelp;
        titleRow.AddChild(title);
        titleRow.AddChild(close);

        _helpSearch = new LineEdit { PlaceholderText = "搜索术语（如：效率 / trust）", CustomMinimumSize = new Vector2(200, 20) };
        _helpSearch.TextChanged += _ => RefreshHelpList();

        _helpList = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _helpDef = new Label
        {
            Text = "选择词条查看释义。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _helpDef.AddThemeFontSizeOverride("font_size", 11);
        _helpDef.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);

        box.AddChild(titleRow);
        box.AddChild(_helpSearch);
        box.AddChild(_helpList);
        box.AddChild(_helpDef);
        _helpPanel.AddChild(box);
        AddChild(_helpPanel);
        _helpPanel.Position = new Vector2(140f, 40f);
        RefreshHelpList();
    }

    private void RefreshHelpList()
    {
        // 同帧可复算：先摘树再释放（QueueFree 延迟到帧尾，直接清会让
        // GetChildCount 在同帧虚高——smoke 的搜索计数断言依赖即时语义）。
        foreach (Node child in _helpList.GetChildren())
        {
            _helpList.RemoveChild(child);
            child.QueueFree();
        }

        System.Collections.Generic.IReadOnlyList<GlossaryEntry> hits = _glossary.Search(_helpSearch.Text);
        foreach (GlossaryEntry entry in hits)
        {
            // T-110 (F-164): desktop_only 词条在 Web 档位行下追加「桌面版
            // 功能」标注（决策在 WebUiSurfaces，本处只镜像）。
            string annotation = WebUiSurfaces.AnnotateDesktopOnly(_tiers, entry.DesktopOnly)
                ? WebUiSurfaces.DesktopOnlySuffixZh
                : string.Empty;
            var row = new Button
            {
                Text = $"{entry.TermZh} · {entry.TermEn}{annotation}",
                Flat = true,
                FocusMode = FocusModeEnum.None,
            };
            row.AddThemeFontSizeOverride("font_size", 11);
            row.Pressed += () => ShowTerm(entry.Id);
            _helpList.AddChild(row);
        }
    }

    /// <summary>词条选中（提示互链的落点——同一入口，无第二套）。</summary>
    internal GlossaryEntry? ShowTerm(string termId)
    {
        if (!_glossary.TryGet(termId, out GlossaryEntry entry))
        {
            return null;
        }

        _helpPanel.Show();
        string related = entry.Related.Count == 0
            ? string.Empty
            : "\n相关：" + string.Join("、", entry.Related);
        // T-110 (F-164): desktop_only 词条的释义尾注（Web 行激活——双语后缀）。
        string annotation = WebUiSurfaces.AnnotateDesktopOnly(_tiers, entry.DesktopOnly)
            ? $"\n{WebUiSurfaces.DesktopOnlySuffixZh} {WebUiSurfaces.DesktopOnlySuffixEn}"
            : string.Empty;
        _helpDef.Text = $"{entry.TermZh}（{entry.TermEn}）\n{entry.DefZh}\n{entry.DefEn}{related}{annotation}";
        _selectedTermId = entry.Id;
        return entry;
    }

    private string? _selectedTermId;

    /// <summary>当前选中词条 id（smoke 探针）。</summary>
    internal string? SelectedTermId => _selectedTermId;

    /// <summary>当前词条释义渲染文本（T-110 冒烟探针：desktop_only 尾注）。</summary>
    internal string HelpDefinitionText => _helpDef.Text;

    /// <summary>词条列表每行的渲染文本（T-110 冒烟探针：desktop_only 行标注）。</summary>
    internal IReadOnlyList<string> HelpRowTexts()
    {
        var texts = new List<string>();
        foreach (Node child in _helpList.GetChildren())
        {
            if (child is Button button)
            {
                texts.Add(button.Text);
            }
        }

        return texts;
    }

    /// <summary>
    /// T-110 (F-164): re-binds the platform tier row and re-renders the
    /// glossary surfaces (list rows + the open definition) — the Web feature
    /// simulation and the real Web export share this one path.
    /// </summary>
    internal void SetAgentTiers(AgentTierAvailability tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        _tiers = tiers;
        RefreshHelpList();
        if (_selectedTermId is { } id)
        {
            _ = ShowTerm(id); // 重新渲染已选词条（尾注随档位行切换）
        }
    }

    /// <summary>F1 开关帮助面板（可从提示气泡的术语链打开）。</summary>
    internal void ToggleHelp()
    {
        if (_helpPanel.Visible)
        {
            _helpPanel.Hide();
        }
        else
        {
            _helpPanel.Show();
            RefreshHelpList();
        }
    }

    // ---- 分区 chips（令簿地名点击定位镜头）+ 人格 chips（F-122） -----------

    private void BuildZoneBar()
    {
        _zoneBar = new HBoxContainer();
        foreach (ZoneLabel zone in _zones.Zones)
        {
            var chip = new Button { Text = zone.NameZh, FocusMode = FocusModeEnum.None };
            chip.AddThemeFontSizeOverride("font_size", 11);
            chip.TooltipText = $"{zone.NameEn}（{zone.Cell.X},{zone.Cell.Y}）——点击定位镜头";
            chip.Pressed += () => PanToZone(zone.Id);
            _zoneBar.AddChild(chip);
        }

        // F-122 人格切换（§10.7：只改表达不改能力）：四 chip 即时切换+持久化，
        // 生效面=叙事导演的人格加权台词池（SetPersona 同一入口）。
        _personaBar = new HBoxContainer();
        foreach (string personaId in PersonaChoice.FrozenIds)
        {
            var chip = new Button
            {
                Text = $"◆{PersonaChoice.LabelZh(personaId)}",
                FocusMode = FocusModeEnum.None,
                ToggleMode = true,
            };
            chip.AddThemeFontSizeOverride("font_size", 11);
            chip.TooltipText = "系统人格（只改表达，不改能力）";
            chip.Pressed += () => SwitchPersona(personaId);
            _personaBar.AddChild(chip);
            _personaChips[personaId] = chip;
        }

        AddChild(_zoneBar);
        _zoneBar.Position = new Vector2(438f, 46f);
        AddChild(_personaBar);
        _personaBar.Position = new Vector2(438f, 74f);

        _personaId = PersonaSettingsStore.Load(_store).PersonaId; // 跨运行记忆
        ApplyPersona(_personaId);
    }

    private readonly Dictionary<string, Button> _personaChips = new();
    private string _personaId = "advisor";

    /// <summary>
    /// F-122 切换人格：持久化+chip 态+叙事导演即时生效（台词池按人格加权，
    /// 能力不变）。返回是否成功切换（未知 id 拒绝）。
    /// </summary>
    internal bool SwitchPersona(string personaId)
    {
        if (PersonaChoice.TryParse(personaId) is null)
        {
            return false;
        }

        _personaId = personaId;
        PersonaSettingsStore.Save(_store, personaId);
        ApplyPersona(personaId);
        return true;
    }

    private void ApplyPersona(string personaId)
    {
        foreach (KeyValuePair<string, Button> pair in _personaChips)
        {
            pair.Value.SetPressedNoSignal(pair.Key == personaId);
        }

        var narrative = _main.GetNodeOrNull<Narrative.NarrativeHud>("NarrativeHud");
        narrative?.Director?.SetPersona(PersonaChoice.TryParse(personaId)!.Value); // 即时生效
    }

    /// <summary>当前人格 id（smoke 探针）。</summary>
    internal string CurrentPersonaId => _personaId;

    /// <summary>令簿地名 → 镜头定位（点击/军令指名共用；返回是否定位）。</summary>
    internal bool PanToZone(string query)
    {
        ZoneLabel? zone = _zones.TryResolve(query);
        if (zone is null)
        {
            return false;
        }

        _camera.Position = new Vector2(
            zone.Cell.X * Cell + Cell / 2f, zone.Cell.Y * Cell + Cell / 2f);
        return true;
    }

    // ---- 谓词提示气泡 ------------------------------------------------------

    private void BuildHintBubble()
    {
        _hintBubble = new PanelContainer { Visible = false };
        _hintBubble.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _hintBubble.ZIndex = 22;

        var box = new VBoxContainer();
        _hintText = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(280, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _hintText.AddThemeFontSizeOverride("font_size", 11);
        _hintText.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        var row = new HBoxContainer();
        _hintTerm = new Button { Text = "术语", Visible = false, FocusMode = FocusModeEnum.None };
        _hintTerm.Pressed += () =>
        {
            if (_shownHint?.TermEntry is { } entry)
            {
                ShowTerm(entry.Id); // 互链：提示内术语可点开词条
            }
        };
        var gotIt = new Button { Text = "知道了", FocusMode = FocusModeEnum.None };
        gotIt.Pressed += DismissHint;
        row.AddChild(_hintTerm);
        row.AddChild(gotIt);
        box.AddChild(_hintText);
        box.AddChild(row);
        _hintBubble.AddChild(box);
        AddChild(_hintBubble);
        _hintBubble.Position = new Vector2(336f, 300f);
    }

    /// <summary>观察谓词事实 → 出队提示 → 气泡（live 每拍调用，冒烟直接调 FireHint）。</summary>
    private void ObserveHints()
    {
        if (!_gameStartFired)
        {
            _gameStartFired = true;
            FireHint(HintPredicateKind.GameStart);
        }

        int producers = _host.Simulation.Producers.Count;
        if (producers > 0 && _lastProducerCount == 0)
        {
            FireHint(HintPredicateKind.FirstProduction);
        }

        _lastProducerCount = producers;

        int towers = _host.Simulation.Towers.Count;
        if (towers > 0 && _lastTowerCount == 0)
        {
            FireHint(HintPredicateKind.FirstTower);
        }

        _lastTowerCount = towers;

        int wave = _host.Simulation.CurrentWave;
        if (wave > 0 && _lastWaveSeen == 0)
        {
            FireHint(HintPredicateKind.WaveIncoming);
        }

        _lastWaveSeen = wave;

        if (_host.BuildHudSnapshot().DefenseIntegrity < 0.40d)
        {
            FireHint(HintPredicateKind.LowIntegrity);
        }

        PipelineOutcomeKind? outcome = _panel.LastOutcome;
        if (outcome == PipelineOutcomeKind.Clarification && _lastPanelOutcome != PipelineOutcomeKind.Clarification)
        {
            FireHint(HintPredicateKind.ClarifyCard);
        }

        _lastPanelOutcome = outcome;
    }

    private int _lastTowerCount;
    private int _lastProducerCount;

    /// <summary>出队+落泡（每谓词观察的公共出口；无 due 提示时静默）。</summary>
    internal HintDue? FireHint(HintPredicateKind kind)
    {
        HintDue? due = _hintQueue.Observe(kind, _host.Simulation.CurrentTick);
        if (due is null)
        {
            return null;
        }

        ShowHint(due);
        return due;
    }

    private void ShowHint(HintDue due)
    {
        _shownHint = due;
        _hintText.Text = $"◈ {due.Rule.TextZh}";
        _hintTerm.Visible = due.TermEntry is not null;
        _hintTerm.Text = due.TermEntry is { } entry ? $"术语·{entry.TermZh}" : "术语";
        _hintBubble.Show();
    }

    /// <summary>「知道了」：MarkRead+持久化+气泡收起（可跳过）。</summary>
    internal void DismissHint()
    {
        if (_shownHint is { Rule: { } rule })
        {
            _hintQueue.MarkRead(rule.Id);
            HintProgressStore.Save(_store, new HintProgress { Read = _hintQueue.ReadIds.ToArray() });
        }

        _shownHint = null;
        _hintBubble.Hide();
    }

    /// <summary>当前气泡提示 id（smoke 探针；null=无）。</summary>
    internal string? ShownHintId => _shownHint?.Rule.Id;

    // ---- 输入接线 ----------------------------------------------------------

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F1 })
        {
            ToggleHelp();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // ①点击塔=详情卡：未被建造拖放消费的左键点按（armed 拖放走
        // BuildInteractionUi 自己的 _GuiInput——未 Accept 的点按落到这里）。
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
        {
            Vector2 world = GetGlobalMousePosition(); // 相机应用后的世界坐标（BuildInteractionUi 同源）
            var cell = new CellCoord((int)Math.Floor(world.X / Cell), (int)Math.Floor(world.Y / Cell));
            SelectTowerAt(cell);
        }
    }

    // ---- headless 冒烟（截图 diff 类验收） ----------------------------------

    /// <summary>
    /// Headless acceptance run. 判据口径（注明）：
    /// ①三层信息逐面自检——塔详情卡（DPS/射程/弹种/克制+升级预览）、塔头
    ///   状态条（每塔一条、比率∈[0,1]）、断流可视化（真 BottleneckDetector
    ///   事件→红圈+灰线+因果行，TTL 衰减）；
    /// ②分区=4 区浮显+chip 点击定位镜头（相机位置=区格中心）+令簿 lane 锚
    ///   同源互证（zones.east_gate==SimHost.EastGateAnchor 等）；
    /// ③词汇表=N 词条+搜索过滤+提示互链（提示内术语点开词条）；
    /// ④提示队列=谓词观察按优先级出队+冷静期+已读持久化（存档重载）；
    /// ⑤截图=每关键状态存 PNG 到 D-03 开发根 ui-snapshots/（gitignored）
    ///   +ERA_UI_SNAPSHOT_ARCHIVE 环境变量指向的留档目录（无盘符硬编码，
    ///   RL9）；像素自检口径——真渲染器在机时断言关键区非均匀+多色分布，
    ///   headless dummy 光栅器下像素自检退化为结构自检（控件可见+尺寸在
    ///   640×360 设计面内——不伪造像素通过，SNAP 行如实报 mode）。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        // ---- ②分区层：lane 锚同源互证（运行时证据，与 UiContentFilesTests 互补）----
        bool anchorSame = _zones.TryResolve("east_gate")!.Cell.Equals(SimHost.EastGateAnchor)
            && _zones.TryResolve("south_gate")!.Cell.Equals(SimHost.SouthGateAnchor);
        int zonesTotal = _zones.Count;

        // ---- ④提示队列：谓词观察按优先级出队+互链（先行——once 行出队即已读）----
        HintDue? startHint = FireHint(HintPredicateKind.GameStart);
        HintDue? towerHint = FireHint(HintPredicateKind.FirstTower);
        HintDue? zoneHint = FireHint(HintPredicateKind.CameraNearZone);
        GlossaryEntry? linked = null;
        if (towerHint?.TermEntry is { } towerTerm)
        {
            linked = ShowTerm(towerTerm.Id); // 互链落点：同一 ShowTerm 入口
        }

        HintDue? bottleneckHint = FireHint(HintPredicateKind.FirstBottleneck); // 因果提示（最高优先）
        bool causalFirst = bottleneckHint is not null
            && (startHint is null || bottleneckHint.Rule.Priority < startHint.Rule.Priority)
            && _hintText.Text.Contains("供给线", StringComparison.Ordinal);

        // 已读持久化（非 once 行=可分辨证明）：LowIntegrity 出队→dismiss→存档
        // →重载队列静默；对照组=不带已读集的新队列仍可出队（证明静默来自持久化）。
        HintDue? alarmHint = FireHint(HintPredicateKind.LowIntegrity);
        string? dismissedId = _shownHint?.Rule.Id;
        DismissHint();
        var reloadedRead = new HashSet<string>(HintProgressStore.Load(_store).Read);
        bool readPersisted = alarmHint is not null
            && dismissedId == alarmHint.Rule.Id
            && reloadedRead.Contains(dismissedId)
            && new HintQueue(_hints, reloadedRead).Observe(HintPredicateKind.LowIntegrity, tick: 0) is null
            && new HintQueue(_hints, new HashSet<string>()).Observe(HintPredicateKind.LowIntegrity, tick: 0) is not null;

        // ---- ③断流可视化：真 monitor+detector 夹具 → 真事件 → 红圈+灰线 ----
        var monitor = new TowerSupplyMonitor(windowTicks: 4, warmupTicks: 2);
        SimTower first = _host.Simulation.Towers[0];
        var towerId = new EntityId(4242);
        monitor.RegisterTower(new TowerSupplyRegistration(
            towerId, first.Cell, first.Spec.ReloadTicks ?? 30, Math.Max(1, first.Spec.AmmoPerShot),
            first.Spec.DamagePerShot, () => 0L));
        var clock = new StepClock(startTick: 0);
        var detector = new BottleneckDetector(clock, monitor, threshold: 0.7, consecutiveTicks: 3);
        SupplyBottleneckEvent? fired = null;
        detector.BottleneckDetected += evt => fired = evt;
        for (int i = 0; i < 12 && fired is null; i++)
        {
            monitor.Tick();
            detector.Tick();
            clock.Tick++;
        }

        int highlightsBefore = _board.Active.Count;
        BottleneckHighlightView? highlight = fired is null ? null : _board.On(fired, FallbackSupplierCell);
        _bottleneckLayer.SetViews(_board.Active);
        bool bottleneckDrawn = highlight is not null
            && _bottleneckLayer.ViewCount == highlightsBefore + 1
            && highlight.RingCell.Equals(fired!.SuggestedCell ?? fired.TowerPosition)
            && highlight.ReasonLine.Contains("检查供给线", StringComparison.Ordinal);
        _board.Tick(_board.Active[0].ExpiresAtTick); // TTL 到点衰减证据
        bool bottleneckDecays = _board.Active.Count == 0;
        _bottleneckLayer.SetViews(_board.Active);

        // ---- 镜头定位：挑一个与当前机位不同的区 → 相机精确落该区格心 ----
        // （前置演出（T-086 冒烟的 CameraMove）可能已动过共享相机——不假设
        // 初始机位，改为「定位后=所选区格心」的绝对断言。）
        Vector2 cameraBefore = _camera.Position;
        ZoneLabel target = _zones.Zones.First(z =>
            new Vector2(z.Cell.X * Cell + Cell / 2f, z.Cell.Y * Cell + Cell / 2f) != cameraBefore);
        bool panned = PanToZone(target.NameZh);
        Vector2 expected = new(target.Cell.X * Cell + Cell / 2f, target.Cell.Y * Cell + Cell / 2f);
        bool cameraMoved = panned && _camera.Position == expected;
        string cameraNote = $"{(int)cameraBefore.X},{(int)cameraBefore.Y}->{(int)_camera.Position.X},{(int)_camera.Position.Y}";

        RefreshZoneLabels();
        RefreshHeadBars();
        int headBars = _headBars.Count;
        bool headBarsValid = _host.Simulation.Towers
            .All(t => !_headBars.TryGetValue(t.Id.Value, out var bar) || bar.Fill.Size.X is >= 0f and <= 14f);

        // ---- ①静态面板：点第一座塔 → 详情卡 -------------------------------
        bool cardShown = SelectTowerAt(first.Cell);
        bool cardFull = _detailCard.Visible
            && _detailLines.Text.Contains("DPS", StringComparison.Ordinal)
            && _detailLines.Text.Contains("射程", StringComparison.Ordinal)
            && _detailLines.Text.Contains("弹种", StringComparison.Ordinal)
            && _detailLines.Text.Contains("克制", StringComparison.Ordinal)
            && _detailUpgrade.Text.Contains("升级预览", StringComparison.Ordinal);

        // ---- 词汇表面板：搜索过滤 ----------------------------------------
        _helpSearch.Text = "效率";
        RefreshHelpList();
        int searchHits = _helpList.GetChildCount();
        _helpSearch.Text = string.Empty;
        RefreshHelpList();
        int allEntries = _helpList.GetChildCount();
        bool helpSearchWorks = searchHits >= 1 && searchHits < allEntries && allEntries == _glossary.Count;

        // ---- ⑤截图：关键 UI 状态 → PNG 留档 + 像素/结构自检 ----------------
        var snaps = new List<(string Name, string Line)>();
        CaptureAndCheck(snaps, "hud");          // HUD+世界层（塔头条+分区名+详情卡）
        _hintBubble.Show();                     // 因果提示气泡重上镜（Dismiss 已收起）
        CaptureAndCheck(snaps, "hint-bubble");
        _hintBubble.Hide();
        _helpPanel.Show();
        RefreshHelpList();
        CaptureAndCheck(snaps, "help-panel");
        _helpPanel.Hide();
        foreach (string line in snaps.Select(s => s.Line))
        {
            GD.Print(line);
        }

        // ---- F-122 人格切换：四 chip 即时切换+跨运行持久化 ------------------
        string personaFrom = CurrentPersonaId;
        bool personaSwitched = SwitchPersona("tsundere")
            && CurrentPersonaId == "tsundere"
            && PersonaSettingsStore.Load(_store).PersonaId == "tsundere";
        bool personaRoundtrip = SwitchPersona(personaFrom) && CurrentPersonaId == personaFrom;

        // ---- 判据汇总 ------------------------------------------------------
        bool layersOk = cardFull && cardShown && headBars >= 1 && headBarsValid && bottleneckDrawn && bottleneckDecays;
        bool zonesOk = anchorSame && zonesTotal == 4 && cameraMoved;
        bool glossaryOk = helpSearchWorks && linked is not null && _selectedTermId == linked!.Id;
        bool hintsOk = causalFirst && readPersisted && zoneHint is not null;
        bool personaOk = personaSwitched && personaRoundtrip && _personaChips.Count == 4;
        // 截图判据口径：真光栅可得→像素自检须过；headless dummy 光栅不可得
        // （GetImage 返 0x0）→ 结构自检须过且 mode 如实报 unavailable——
        // 不伪造像素通过，窗口运行（真渲染器）时同一判据自动升为像素档。
        bool snapsOk = snaps.Count == 3 && snaps.All(s =>
            s.Line.Contains("ok=1", StringComparison.Ordinal)
            || (s.Line.Contains("mode=headless-unavailable", StringComparison.Ordinal)
                && s.Line.Contains("structure=1", StringComparison.Ordinal)));
        bool rasterGpu = snaps.Any(s => s.Line.Contains("mode=pixels", StringComparison.Ordinal));

        GD.Print(
            $"UI_OK layers={(layersOk ? 3 : 0)}/{(cardFull ? "card" : "-")}+{(headBars >= 1 ? $"bars{headBars}" : "-")}+" +
            $"{(bottleneckDrawn && bottleneckDecays ? "bottleneck" : "-")} " +
            $"zones={zonesTotal} anchors={(anchorSame ? 1 : 0)} camera={(cameraMoved ? "panned" : "static")}{cameraNote} " +
            $"glossary={allEntries} search_hits={searchHits} term={(glossaryOk ? "linked" : "-")} " +
            $"hints={_hints.Count} queue={(hintsOk ? "priority+cooldown+persist" : "-")} " +
            $"persona={(personaOk ? $"4switch:{personaFrom}->tsundere->{personaFrom}" : "-")} " +
            $"bottlenecks={(bottleneckDrawn ? "highlight" : "none")} snaps={snaps.Count} raster={(rasterGpu ? "pixels" : "headless-none")} " +
            $"detector_real={(fired is not null ? 1 : 0)} eff={(fired?.Efficiency.ToString("0.00") ?? "n/a")}");

        if (!layersOk || !zonesOk || !glossaryOk || !hintsOk || !personaOk || !snapsOk || fired is null)
        {
            GD.PushError(
                $"UI_OK self-check failed: layers={layersOk} zones={zonesOk} glossary={glossaryOk} " +
                $"hints={hintsOk} persona={personaOk} snaps={snapsOk} fired={(fired is not null ? 1 : 0)}");
            GetTree().Quit(1);
        }
    }

    // ---- 截图与自检 --------------------------------------------------------

    /// <summary>
    /// 截一个关键 UI 状态：viewport 纹理 → PNG（D-03 开发根 ui-snapshots/，
    /// gitignored）+ 环境变量 ERA_UI_SNAPSHOT_ARCHIVE 留档副本（无盘符
    /// 硬编码，RL9——留档目录由运行环境注入）。自检口径双档：真渲染器在机
    /// →关键区非均匀+颜色分布；headless dummy → 结构自检（不伪造像素通过）。
    /// </summary>
    private void CaptureAndCheck(List<(string Name, string Line)> sink, string name)
    {
        string mode = "struct";
        int distinct = -1;
        int width = 0;
        int height = 0;
        long bytes = 0;
        bool ok = false;
        try
        {
            Image? image = null;
            bool headlessRun = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
            if (!headlessRun)
            {
                // 窗口（真光栅）才取纹理像素；headless dummy 驱动下 GetImage
                // 会打内部错误并返 0x0——不触发、如实报 unavailable（不伪造）。
                // 注：本项目 gl_compatibility 渲染器下 GetRenderingDevice 恒
                // null，GPU 探针用 headless 特征而非 RD 存在性。
                try
                {
                    image = GetViewport().GetTexture().GetImage();
                }
                catch (ObjectDisposedException)
                {
                    image = null;
                }
            }

            if (image is { } shot && shot.GetWidth() > 0 && shot.GetHeight() > 0)
            {
                width = shot.GetWidth();
                height = shot.GetHeight();
                var colors = new HashSet<int>();
                for (int y = 0; y < height; y += Math.Max(1, height / 45))
                {
                    for (int x = 0; x < width; x += Math.Max(1, width / 80))
                    {
                        colors.Add((int)shot.GetPixel(x, y).ToRgba32());
                    }
                }

                distinct = colors.Count;
                mode = "pixels";
                // 关键区非均匀（非单色帧）+颜色分布（UI 多元素多色）
                ok = distinct > 8;

                string dir = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, "ui-snapshots");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"t093-{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
                Error save = shot.SavePng(path);
                if (save == Error.Ok)
                {
                    bytes = new System.IO.FileInfo(path).Length;
                    ArchiveSnapshot(path, $"t093-{name}.png");
                }
                else
                {
                    ok = false;
                }
            }
            else if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
            {
                mode = "headless-unavailable"; // dummy 光栅器：无像素可取——结构自检替代
            }
            else
            {
                mode = "raster-empty"; // 真渲染器但纹理空（未画成帧）——如实报
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"snapshot {name} failed: {ex.Message}");
        }

        // 结构自检（两档共用）：关键控件在树上可见且尺寸落在 640×360 设计面内
        bool structure = IsStructurallyValid();
        ok = ok && structure;
        sink.Add((name,
            $"SNAP t093-{name} ok={(ok ? 1 : 0)} mode={mode} size={width}x{height} bytes={bytes} " +
            $"distinct={distinct} structure={(structure ? 1 : 0)}"));
    }

    /// <summary>关键 UI 面结构自检：可见+尺寸非零+落在设计面内。</summary>
    private bool IsStructurallyValid()
    {
        bool Panel(Rect2 rect) => rect.Size.X > 10f && rect.Size.Y > 10f
            && rect.Position.X >= 0f && rect.Position.Y >= 0f
            && rect.Position.X + rect.Size.X <= 640f + 0.5f
            && rect.Position.Y + rect.Size.Y <= 360f + 0.5f;

        // _Ready 同帧控件未布局（Size==0）——按位坐标判（锚定式布局的面）
        bool detailCard = !_detailCard.Visible || Panel(new Rect2(_detailCard.Position, new Vector2(170f, 110f)));
        bool help = !_helpPanel.Visible || _helpPanel.Position.X is >= 0f and <= 640f;
        bool bubble = !_hintBubble.Visible || _hintBubble.Position.X is >= 0f and <= 640f;
        bool zones = _zoneBar.GetChildCount() == _zones.Count;
        return detailCard && help && bubble && zones;
    }

    /// <summary>留档副本：ERA_UI_SNAPSHOT_ARCHIVE 目录存在时复制（环境注入，RL9）。</summary>
    private static void ArchiveSnapshot(string sourcePath, string fileName)
    {
        string? archive = System.Environment.GetEnvironmentVariable("ERA_UI_SNAPSHOT_ARCHIVE");
        if (string.IsNullOrWhiteSpace(archive) || !System.IO.Directory.Exists(archive))
        {
            return;
        }

        try
        {
            System.IO.File.Copy(sourcePath, System.IO.Path.Combine(archive, fileName), overwrite: true);
        }
        catch (Exception ex)
        {
            GD.PushError($"snapshot archive copy failed: {ex.Message}"); // 留档失败不红主流程
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static Label Dim(string text)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        return label;
    }

    private static string ReadFile(string dir, string fileName) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, fileName));

    private static string FindContentDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"content directory {label} not found (dev runs need the repo checkout)");
    }

    /// <summary>可步进 tick 钟夹具（真 detector 的 IClock 端口，K-1）。</summary>
    private sealed class StepClock(long startTick) : IClock
    {
        public long Tick { get; set; } = startTick;

        public long NowMs => Tick * (1000L / 30);
    }

    /// <summary>live 探测器的 IClock 适配（Simulation.CurrentTick 只读投影）。</summary>
    private sealed class SimTickClock(EraCore.Sim.Simulation simulation) : IClock
    {
        public long Tick => simulation.CurrentTick;

        public long NowMs => 0;
    }

    /// <summary>③断流层：红圈（瓶颈位）+灰线（断流线路）——纯绘制，无决策。</summary>
    private sealed partial class BottleneckLayer : Control
    {
        private static readonly Color RingRed = new(1.00f, 0.25f, 0.25f, 0.9f);
        private static readonly Color LineGray = new(0.62f, 0.62f, 0.62f, 0.8f);

        private IReadOnlyList<BottleneckHighlightView> _views = Array.Empty<BottleneckHighlightView>();

        public int ViewCount => _views.Count;

        public void SetViews(IReadOnlyList<BottleneckHighlightView> views)
        {
            _views = views;
            QueueRedraw();
        }

        public override void _Draw()
        {
            const float cell = Cell;
            foreach (BottleneckHighlightView view in _views)
            {
                Vector2 ring = new(
                    view.RingCell.X * cell + cell / 2f, view.RingCell.Y * cell + cell / 2f);
                DrawArc(ring, cell * 0.62f, 0f, MathF.Tau, 24, RingRed, 2.5f, antialiased: true);
                DrawLine(
                    new Vector2(view.LineFromCell.X * cell + cell / 2f, view.LineFromCell.Y * cell + cell / 2f),
                    new Vector2(view.LineToCell.X * cell + cell / 2f, view.LineToCell.Y * cell + cell / 2f),
                    LineGray, 2f, antialiased: true);
            }
        }
    }
}
