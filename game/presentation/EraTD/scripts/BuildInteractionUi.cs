using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Contracts.Sim;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Units;
using Godot;

namespace EraTD;

// T-056（C-23 建造交互薄版，F-036/F-119）：拖放建造 UI——场景=scenes/
// build_ui.tscn（本脚本唯一挂载点；Main.cs 仅追加实例化行——并行纪律：
// 与 T-055 的冲突面在 Main.cs，后提交者 rebase）。
//
// T-076（C-23 全量建造交互）在其上追加：
//   §10.5 暂停可建造+变速——空格=暂停/恢复（PauseClock；暂停中松手=命令
//   入队暂存，恢复瞬间 FlushInto 同一 K-6 SimCommandQueue，(tick,seq) 语义
//   不变保确定性），1/2/3=1x/2x/3x 变速（宿主每帧步进 N tick）；
//   §10.7 两级灰度——预览格三色（绿/黄灰=缺资源 tooltip/红=不可放置原因
//   ——BuildGreyMap 纯映射，判定输入=BuildPlan 草案同源输出）+建造栏逐项
//   染色（时代锁红/缺资源黄灰）；
//   §6.1 批量同限 UX——超限截断提示（「军师与你同限」）+R/C 整行/整列
//   快捷（仍受 cells≤4+2×E 截断）。
//
// 交互链：建造栏 4 塔 icon（E1 目录驱动+类色色块+两级灰度染色）→按下拖动
// =矩形选格（单格=1×1 矩形；cells≤4+2×E 同限截断）→三色格预览（合法性
// =BuildPlan 草案输出，红格带共享拒绝码；黄灰格带逐项造价缺口）→松开→
// BuildPlan 动作 JSON→PlayerBuildLane（与军师同一咽喉：PipelineExecutor.
// InvokeStep→ActionRegistry.Invoke→SimCommand 落子；不扣系统点——口径裁定
// 见 PlayerBuildLane.cs 注释）。暂停中松开=同一批 build-tower 命令入
// PauseClock 暂存队列（payload 与执行道 PlaceBuildableCells 逐字节同构），
// 恢复后按序入同一队列。ESC=取消清预览；右键点塔=拆除模式（confirm 弹层，
// confirm_sell=true 走 SellTower 同路径草案——暂停中拆除被拒（其结算立即
// 步进，破坏冻结窗口），恢复后再试）。
//
// headless（smoke）下自动跑一遍脚本化拖放/取消/红格/拆除序列并打印
// BUILD_OK（含注册表调用计数），随后跑全量序列打印 BUILD_V2（pause=speed3
// grey=two-tier cap=trunc——任一自检不过即 Quit(1)）；有头模式走真实鼠标
// 输入，同一控制器与泳道，无第二套逻辑。
//
// T-141（移动端触控原型，08 W8 批 6；49 号文 §3/§4）：InputEventScreen
// Touch/Drag → TouchGestureMapper 纯决策 → 既有鼠标面**同一**控制器/提交
// 路径（只加输入映射，不改命令语义）；单指=格选/矩形（=左键），双指=缩放
// 平移（手势期建造预览取消，防误触）。headless 第三幕打印 BUILD_TOUCH
// （合成触控事件→建造命令发出的冒烟断言）。分辨率自适应开关=
// MobileViewportPolicy 决策 → Window.ContentScaleStretch 切换（整数档=
// project.godot 原样，桌面零改动）。

/// <summary>
/// The C-23 thin build-interaction control (T-056): build bar, drag
/// rectangle preview, commit through the shared BuildPlan throat, ESC
/// cancel and right-click demolish with the mandatory confirm card.
/// </summary>
public sealed partial class BuildInteractionUi : Control
{
    private const int MapWidthCells = 16;
    private const int MapHeightCells = 24;
    private const long DemoSeed = 20261014;      // 与 Sim/Agent 测试夹具同种子族（E1 关卡形态）
    private const int AgentEra = 2;              // BuildPlan/SellTower 为 E2 动词（K-3 解锁列）——与军师测试夹具同口径
    private const float Cell = Simulation.CellSizePx;

    private static readonly ResourceKey Wood = new("era1:res-wood");

    private readonly List<ColorRect> _highlights = new();
    private readonly Dictionary<string, Button> _towerButtons = new();

    // T-163（C 批建造栏图标化）：槽位三件面（icon/价/快捷键角标）——禁用态
    // 染色与选中态描边的着色目标（RefreshBarTints/ArmTower 共用）。
    private readonly Dictionary<string, TextureRect> _slotIcons = new();
    private readonly Dictionary<string, Label> _slotPrices = new();
    private readonly Dictionary<char, string> _slotHotkeys = new(); // 键字符 → 塔 id（真绑定）
    private readonly Dictionary<string, Color> _classSwatch = new()
    {
        ["pierce"] = Colors.LimeGreen,
        ["physical"] = Colors.SaddleBrown,
        ["slow"] = Colors.CornflowerBlue,
        ["support"] = Colors.Orange,
    };

    private Simulation _sim = null!;
    private PlayerBuildLane _lane = null!;
    private BuildDragController _controller = null!;
    private CountingLogSink _registrySink = null!;
    private InMemoryJournalWriter _journal = null!;
    private HBoxContainer _buildBar = null!;
    private Control _previewLayer = null!;
    private PanelContainer _demolishCard = null!;
    private Label _status = null!;
    private Label _demolishLabel = null!;
    private string? _armedTower;
    private bool _leftDrag;
    private long _demolishTargetId;

    /// <summary>T-141 触控原型：触控事件→决策翻译（纯逻辑，EraCore.Presentation）。</summary>
    private readonly TouchGestureMapper _touch = new();

    // T-141 冒烟诊断计数（与 _registrySink.Lines 同范式——headless 决策流观测面）。
    private int _touchBuildPresses;
    private int _touchBuildCommits;
    private int _touchBuildCancels;
    private int _touchPanZooms;

    /// <summary>
    /// T-076 (F-118) 暂停/变速时钟：0 tick/帧 while paused（建造照常，
    /// 命令暂存），否则 1x/2x/3x。headless 帧循环与有头 _Process 同一
    /// RunFrame 入口，无第二套节奏。
    /// </summary>
    private readonly EraCore.Presentation.PauseClock _pauseClock = new();

    // §10.7 两级灰度色板：绿=可放置且付得起；黄灰=缺资源（tooltip 报
    // 缺什么/需多少/差多少）；红=不可放置（地形/占用/时代锁）。
    private static readonly Color TintGreen = new(0.20f, 1.00f, 0.30f, 0.45f);
    private static readonly Color TintYellowGrey = new(0.87f, 0.76f, 0.34f, 0.52f);
    private static readonly Color TintRed = new(1.00f, 0.20f, 0.20f, 0.45f);
    private static readonly Color BarTintYellowGrey = new(1.00f, 0.92f, 0.55f);
    private static readonly Color BarTintRed = new(1.00f, 0.55f, 0.55f);

    /// <summary>Instance factory for the host (Main.cs appends one init line).</summary>
    public static BuildInteractionUi Instantiate(Control host)
    {
        var scene = GD.Load<PackedScene>("res://scenes/build_ui.tscn");
        var ui = scene.Instantiate<BuildInteractionUi>();
        host.AddChild(ui);
        return ui;
    }

    public override void _Ready()
    {
        WireScene();
        SetupCore();
        BuildBar();
        ApplyMobileViewportTier(); // T-141 分辨率自适应开关（headless 零副作用）
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessDemo();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            switch (mouse.ButtonIndex)
            {
                case MouseButton.Left when mouse.Pressed && _armedTower is not null:
                    _leftDrag = true;
                    _controller.BeginDrag(_armedTower, CellAtMouse());
                    RepaintPreview();
                    AcceptEvent();
                    break;
                case MouseButton.Left when !_leftDrag:
                    break;
                case MouseButton.Left:
                    _leftDrag = false;
                    CommitDrag();
                    AcceptEvent();
                    break;
                case MouseButton.Right when mouse.Pressed:
                    EnterDemolishMode(CellAtMouse());
                    AcceptEvent();
                    break;
            }
        }
        else if (@event is InputEventMouseMotion && _leftDrag)
        {
            _controller.UpdateDrag(CellAtMouse());
            RepaintPreview();
        }
        else if (@event is InputEventScreenTouch touch)
        {
            // T-141 触控原型：只加输入映射——决策应用与鼠标分支同一控制器/提交路径。
            Vector2 world = GetGlobalTransformWithCanvas() * touch.Position;
            ApplyTouchDecision(_touch.Touch(touch.Index, world.X, world.Y, touch.Pressed), world);
        }
        else if (@event is InputEventScreenDrag drag)
        {
            Vector2 world = GetGlobalTransformWithCanvas() * drag.Position;
            ApplyTouchDecision(_touch.Drag(drag.Index, world.X, world.Y), world);
        }
    }

    /// <summary>
    /// T-141：把 TouchGestureMapper 决策应用到既有鼠标面同一命令流
    /// （BeginDrag/UpdateDrag/CommitDrag/Cancel——与左键按下/拖动/松开、
    /// ESC 逐字同参，无第二套语义）。双指手势期撤建造预览（防误触）；
    /// 缩放/平移的 viewport 应用属正式移植面，原型只保证不出建造命令。
    /// </summary>
    private void ApplyTouchDecision(TouchGestureDecision decision, Vector2 world)
    {
        switch (decision.Kind)
        {
            case TouchGestureKind.BuildPress when _armedTower is not null:
                _touchBuildPresses++;
                _controller.BeginDrag(_armedTower, CellAtWorld(world));
                RepaintPreview();
                AcceptEvent();
                break;
            case TouchGestureKind.BuildDrag:
                _controller.UpdateDrag(CellAtWorld(world));
                RepaintPreview();
                AcceptEvent();
                break;
            case TouchGestureKind.BuildCommit:
                _touchBuildCommits++;
                CommitDrag(); // 与鼠标左键松开同一提交（暂停分支/PlayerBuildLane 咽喉）
                AcceptEvent();
                break;
            case TouchGestureKind.BuildCancel:
                _touchBuildCancels++;
                _controller.Cancel(); // 双指接管：撤预览（幂等——Idle 态无副作用）
                RepaintPreview();
                AcceptEvent();
                break;
            case TouchGestureKind.TwoFingerPanZoom:
                _touchPanZooms++; // 手势事实（span/center 增量随决策携带）；非建造
                break;
        }
    }

    /// <summary>
    /// T-141 分辨率自适应开关：MobileViewportPolicy 纯决策 → 运行时切换
    /// Window.ContentScaleStretch（Integer↔Fractional——4.7.2 GodotSharp
    /// 反射实证，49 号文 §4）。整数档=project.godot 既有 canvas_items+keep+
    /// integer 原样（桌面零改动）；触屏事实=IsTouchscreenAvailable 或
    /// android/ios feature（headless 下无窗口意义，直接短路）。
    /// </summary>
    private void ApplyMobileViewportTier()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            return; // headless 冒烟窗口尺寸无意义（Main.cs 同款判定）
        }

        bool touchPlatform = DisplayServer.IsTouchscreenAvailable()
            || OS.HasFeature("android") || OS.HasFeature("ios");
        Vector2I window = DisplayServer.WindowGetSize();
        ViewportScaleDecision decision = MobileViewportPolicy.Decide(window.X, window.Y, touchPlatform);
        if (decision.Tier == ViewportScaleTier.MobileFractional)
        {
            GetWindow().ContentScaleStretch = Window.ContentScaleStretchEnum.Fractional;
            SetStatus($"移动缩放档：分数 ×{decision.Scale:0.00}（{decision.ScaledWidth}×{decision.ScaledHeight} 居中）");
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.Escape:
                _controller.Cancel(); // ESC 清预览/取消拖放（含拆除弹层）
                HideDemolishCard();
                RepaintPreview();
                SetStatus("已取消（ESC）");
                GetViewport().SetInputAsHandled();
                break;
            case Key.Space: // T-076 §10.5：空格=暂停/恢复（恢复时冲刷暂存命令）
                TogglePauseAndFlush();
                GetViewport().SetInputAsHandled();
                break;
            case Key.Key1:
            case Key.Key2:
            case Key.Key3: // 1x/2x/3x 变速（暂停中改档合法——恢复即生效）
                _pauseClock.SetSpeed(key.Keycode switch
                {
                    Key.Key2 => 2,
                    Key.Key3 => 3,
                    _ => 1,
                });
                SetStatus(PaceStatus());
                GetViewport().SetInputAsHandled();
                break;
            case Key.R: // 整行快捷（锚格→行尾，仍受 cells≤4+2×E 同限截断）
                ExpandShortcut(row: true);
                break;
            case Key.C: // 整列快捷（锚格→列尾，同限截断）
                ExpandShortcut(row: false);
                break;
        }

        // T-163 建造栏槽位快捷键（真绑定：与按钮 Pressed 同一 ArmTower 链；
        // 键序=HudIconDecisions.SlotHotkeys 按槽位序——跳 R/C 既有语义零回退）。
        if (key.Keycode is Key.Q or Key.W or Key.E or Key.T or Key.A or Key.S or Key.D
            or Key.G or Key.F or Key.Z or Key.X or Key.B
            && _slotHotkeys.TryGetValue((char)key.Keycode, out string? hotkeyTowerId)
            && _towerButtons.TryGetValue(hotkeyTowerId, out Button? hotkeyButton))
        {
            ArmTower(hotkeyTowerId, hotkeyButton);
            SetStatus($"[{(char)key.Keycode}] 已选 {hotkeyTowerId}——地图上左键拖矩形（或再点槽位切换）");
        }
    }

    private void WireScene()
    {
        _buildBar = GetNode<HBoxContainer>("BuildBar");
        _previewLayer = GetNode<Control>("PreviewLayer");
        _demolishCard = GetNode<PanelContainer>("DemolishCard");
        _status = GetNode<Label>("StatusLabel");
        _demolishLabel = GetNode<Label>("DemolishCard/VBox/DemolishLabel");
        GetNode<Button>("DemolishCard/VBox/Buttons/ConfirmButton").Pressed += OnDemolishConfirmed;
        GetNode<Button>("DemolishCard/VBox/Buttons/CancelButton").Pressed += HideDemolishCard;
        MouseFilter = MouseFilterEnum.Stop;
    }

    private void SetupCore()
    {
        IReadOnlyList<TowerSpec> catalog = LoadTowerCatalog();
        var enemies = EnemySpecCatalog.Parse(System.IO.File.ReadAllText(FindContentFile("enemies.json")));
        _sim = new Simulation(DemoConfig(catalog, enemies), new DemoRng(DemoSeed), DemoSeed);
        _registrySink = new CountingLogSink();
        _journal = new InMemoryJournalWriter();
        // F5：PlayerBuildLane 的 journal 契约=sequence-ordered per run（其
        // ctor 参数注释明示 SequencingJournalWriter 负责 Seq 重编号）——裸
        // 传入内存写入器会使玩家事件 Seq=0 恒不递增，违 K-8 单流有序。
        _lane = PlayerBuildLane.OverSimulation(
            _sim, DemoConfig(catalog, enemies), () => AgentEra, new SequencingJournalWriter(_journal), _registrySink);
        _controller = new BuildDragController(_lane.View, () => AgentEra);
        SetStatus("建栏选塔 → 左键拖矩形 → 松开建造（ESC 取消 / 右键拆除 / 空格暂停 / 1-2-3 变速 / R 整行 C 整列）");
        RefreshBarTints(); // §10.7 建造栏两级灰度（时代锁红 / 缺资源黄灰 / 可建绿）
    }

    /// <summary>
    /// 建造栏（T-163 图标化）：塔 icon（res:// 项目内轨 128×128，缺图=类色
    /// 色块回退）+造价+快捷键角标（Q/W/E…按槽位序——HudIconDecisions.
    /// SlotHotkeys 跳 R/C 冻结面）；towers.json 序即栏序（既有律零改动）。
    /// 按下仍走 ArmTower 同一链（点击建造链零回退）。
    /// </summary>
    private void BuildBar()
    {
        int slotIndex = 0;
        foreach (TowerSpec spec in LoadTowerCatalog())
        {
            char hotkey = HudIconDecisions.HotkeyOfSlot(slotIndex++);
            var button = new Button
            {
                CustomMinimumSize = new Vector2(78f, 70f),
                FocusMode = FocusModeEnum.None,
            };
            button.Pressed += () => ArmTower(spec.Id, button);

            var face = new VBoxContainer();
            face.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            face.AddThemeConstantOverride("separation", 2);
            face.Alignment = BoxContainer.AlignmentMode.Center;

            var icon = new TextureRect
            {
                CustomMinimumSize = new Vector2(HudIconDecisions.BuildIconPx, HudIconDecisions.BuildIconPx),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Texture = LoadTowerIcon(spec.Id);
            var price = new Label
            {
                Text = $"{spec.CostTotal:0}",
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            price.AddThemeFontSizeOverride("font_size", 11);
            price.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
            face.AddChild(icon);
            face.AddChild(price);
            button.AddChild(face);

            if (hotkey != '\0')
            {
                var badge = new Label
                {
                    Text = hotkey.ToString(),
                    Position = new Vector2(60f, 0f),
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                badge.AddThemeFontSizeOverride("font_size", 9);
                badge.AddThemeColorOverride("font_color", new Color(1.00f, 0.84f, 0.40f));
                badge.AddThemeColorOverride("font_outline_color", Colors.Black);
                badge.AddThemeConstantOverride("outline_size", 2);
                button.AddChild(badge);
                _slotHotkeys[hotkey] = spec.Id;
            }

            _buildBar.AddChild(button);
            _towerButtons[spec.Id] = button;
            _slotIcons[spec.Id] = icon;
            _slotPrices[spec.Id] = price;
        }

        RefreshSlotStyles(); // T-163：槽位三态底样式（选中/禁用/常态）初始一遍
    }

    /// <summary>塔 icon 装载（res:// 项目内轨；缺图=类色色块回退——类色语义既有）.</summary>
    private Texture2D LoadTowerIcon(string towerId)
    {
        string path = HudIconDecisions.TowerIconPath(towerId);
        if (ResourceLoader.Exists(path))
        {
            return GD.Load<Texture2D>(path);
        }

        Color swatch = _classSwatch.TryGetValue(TowerClassOf(towerId), out Color tint) ? tint : Colors.Gray;
        var image = Image.CreateEmpty(24, 24, false, Image.Format.Rgba8);
        image.Fill(new Color(swatch, 0.55f));
        image.SetPixel(4, 4, Colors.White);
        image.SetPixel(5, 4, Colors.White);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>建造泳道时代主色（era1=装载目录时代；palettes 同源经 ExpandDirector）.</summary>
    private Color BuildEraColor()
    {
        Expand.VisualExpansionDirector? expand =
            (GetParent() as Main)?.ExpandDirector;
        return expand?.EraColor("e1") ?? HoloSkin.EdgeCyan;
    }

    /// <summary>
    /// 槽位样式三态（T-163）：选中=时代色描边+类色 tint（既有 armed 语义）；
    /// 禁用（资源不足/科技未解锁，读 RefreshBarTints 同源判定面 BuildGreyMap）
    /// =图标灰暗+价红；常态=暗底细青边。SelfModulate 两级灰度另由
    /// RefreshBarTints 施加（既有零回退），本样式与之分层。
    /// </summary>
    private void RefreshSlotStyles()
    {
        Color eraColor = BuildEraColor();
        double available = _lane.View.ResourceAmount(_lane.View.CostCurrency);
        string currency = _lane.View.CostCurrency.Value;
        foreach (KeyValuePair<string, Button> pair in _towerButtons)
        {
            _lane.View.TryGetTowerCost(pair.Key, out double unitCost);
            BuildTintVerdict verdict = BuildGreyMap.BarEntry(AgentEra, unitCost, available, currency);
            bool selected = _armedTower == pair.Key;
            bool disabled = verdict.Tier != BuildTintTier.Green;

            var box = new StyleBoxFlat();
            box.BgColor = selected
                ? new Color(eraColor, 0.20f)
                : new Color(0.05f, 0.09f, 0.16f, 0.72f);
            box.SetCornerRadiusAll(4);
            box.SetBorderWidthAll(selected ? 2 : 1); // 选中态时代色描边（任务卡口径）
            box.BorderColor = selected ? eraColor : new Color(HoloSkin.EdgeCyan, 0.35f);
            pair.Value.AddThemeStyleboxOverride("normal", box);
            pair.Value.AddThemeStyleboxOverride("hover", box);
            pair.Value.AddThemeStyleboxOverride("pressed", box);
            pair.Value.AddThemeStyleboxOverride("disabled", box);

            if (_slotIcons.TryGetValue(pair.Key, out TextureRect? icon))
            {
                icon.Modulate = disabled ? Colors.White with { A = 0.42f } : Colors.White; // 禁用灰暗
            }

            if (_slotPrices.TryGetValue(pair.Key, out Label? price))
            {
                price.AddThemeColorOverride("font_color", disabled ? HoloSkin.RiskRed : HoloSkin.TextDimCyan); // 禁用价红
            }
        }
    }

    /// <summary>Pacing fact probe（T-163 BattleHudUi 节奏按钮态镜像——只读，钟仍归本类）.</summary>
    internal (bool Paused, int Speed) PaceFact() => (_pauseClock.IsPaused, _pauseClock.SpeedMultiplier);

    private void ArmTower(string towerId, Button button)
    {
        _armedTower = towerId;
        foreach (KeyValuePair<string, Button> pair in _towerButtons)
        {
            pair.Value.Modulate = pair.Key == towerId
                ? _classSwatch.TryGetValue(TowerClassOf(towerId), out Color swatch) ? swatch : Colors.White
                : Colors.White with { A = 0.6f };
        }

        RefreshSlotStyles(); // T-163：选中槽位时代色描边同拍生效
        SetStatus($"已选 {towerId}——地图上左键拖矩形（≤{PlayerBuildLane.MaxBuildCells(AgentEra)} 格）");
    }

    private void CommitDrag()
    {
        BuildDragCommit? commit = _controller.Commit();
        RepaintPreview();
        if (commit is null)
        {
            SetStatus("无可建格（红格）——未发送任何动作");
            return;
        }

        // §10.5 暂停可建造：Sim tick 冻结，松手产生的 build-tower 命令入
        // PauseClock 暂存队列——payload 与执行道 PipelineExecutor.PlaceBuildable
        // Cells 逐字节同构（BuildTowerCommandJson 源生成），恢复时 FlushInto
        // 同一 K-6 SimCommandQueue 按 (tick,seq) 原序应用（迟到命令本就合法）。
        if (_pauseClock.IsPaused)
        {
            long applyTick = _sim.CurrentTick + 1; // 冻结 tick + 1（与执行道 applyTick 同式）
            foreach (CellCoord cell in commit.Cells)
            {
                _pauseClock.EnqueueWhilePaused(new SimCommand(
                    applyTick,
                    SimCommandKind.PlayerBuild,
                    JsonSerializer.Serialize(
                        new BuildTowerCommandJson("build-tower", commit.TowerId, new CellJson(cell.X, cell.Y)),
                        PipelinePlannerJsonContext.Default.BuildTowerCommandJson)));
            }

            SetStatus($"暂停中：{commit.TowerId} ×{commit.Cells.Count} 格已入队，恢复后按序应用（空格恢复）");
            return;
        }

        PlayerBuildOutcome outcome = _lane.Build(commit.TowerId, commit.Cells);
        SetStatus(outcome.Result.Status == ActionResultStatus.Ok
            ? $"已建 {outcome.CellsPlaced} 座（走 ActionRegistry→SimCommand 同一咽喉）"
            : $"建造被拒：{outcome.Result.ErrorMessage}");
        RefreshBarTints(); // 木料变动——建造栏两级灰度即时刷新
    }

    private void EnterDemolishMode(CellCoord cell)
    {
        foreach (SimTower tower in _sim.Towers)
        {
            if (!tower.Cell.Equals(cell))
            {
                continue;
            }

            _demolishTargetId = tower.Id.Value;
            _demolishLabel.Text = $"拆除目标塔 #{tower.Id.Value}？\nSellTower 需显式 confirm_sell=true（危险位，永不自动允许）";
            _demolishCard.Visible = true;
            return;
        }

        SetStatus($"({cell.X},{cell.Y}) 无可拆塔");
    }

    private void OnDemolishConfirmed()
    {
        // 暂停中拆除被拒：SellTower 走 EnqueueAndApply 立即步进结算，会击穿
        // 冻结窗口（§10.5 暂停=Sim tick 不动）——恢复后再试（诚实拒绝，不静默）。
        if (_pauseClock.IsPaused)
        {
            SetStatus("暂停中不可拆除（SellTower 立即步进结算）——空格恢复后再试");
            HideDemolishCard();
            return;
        }

        PlayerBuildOutcome outcome = _lane.Sell(_demolishTargetId, confirmSell: true);
        SetStatus(outcome.Result.Status == ActionResultStatus.Ok
            ? $"已拆除 #{_demolishTargetId}"
            : $"SellTower 走同一注册表（诚实拒绝）：{outcome.Result.ErrorMessage}");
        HideDemolishCard();
    }

    private void HideDemolishCard() => _demolishCard.Visible = false;

    /// <summary>空格：暂停⇄恢复。恢复瞬间把暂存命令按入队序冲刷进同一 K-6 队列。</summary>
    private void TogglePauseAndFlush()
    {
        if (_pauseClock.IsPaused)
        {
            _pauseClock.Resume();
            int flushed = _pauseClock.FlushInto(_sim);
            SetStatus(flushed > 0
                ? $"已恢复 {PaceStatus()}——{flushed} 道暂停期命令按 (tick,seq) 原序入队，下一 tick 生效"
                : $"已恢复 {PaceStatus()}");
        }
        else
        {
            _pauseClock.Pause();
            SetStatus("已暂停（建造照常下达，恢复后按序应用）——空格恢复 / 1-2-3 变速");
        }
    }

    /// <summary>R/C 整行/整列快捷：控制器扩到行/列尾，同限截断时状态栏给「军师与你同限」提示。</summary>
    private void ExpandShortcut(bool row)
    {
        if (row)
        {
            _controller.ExpandToRow();
        }
        else
        {
            _controller.ExpandToColumn();
        }

        RepaintPreview(); // CappedByLimit 时状态栏即显截断提示
    }

    /// <summary>当前节奏描述（状态栏用）——暂停 0 tick/帧，否则 N tick/帧。</summary>
    private string PaceStatus() => _pauseClock.IsPaused
        ? "暂停（0 tick/帧，建造入队暂存）"
        : $"{_pauseClock.SpeedMultiplier}x（{_pauseClock.SpeedMultiplier} tick/帧）";

    /// <summary>
    /// 建造栏两级灰度（§10.7 第一表面）：时代锁=红、缺资源=黄灰（tooltip 报
    /// 逐项造价缺口）、可建=原色。判定全部走 BuildGreyMap.BarEntry 纯映射
    /// （解锁时代读 K-3 冻结表），本类只做颜色翻译。SelfModulate 与选中态的
    /// Modulate 分离——两级灰度不干扰 armed 高亮。
    /// </summary>
    private void RefreshBarTints()
    {
        double available = _lane.View.ResourceAmount(_lane.View.CostCurrency);
        string currency = _lane.View.CostCurrency.Value;
        foreach (KeyValuePair<string, Button> pair in _towerButtons)
        {
            _lane.View.TryGetTowerCost(pair.Key, out double unitCost);
            BuildTintVerdict verdict = BuildGreyMap.BarEntry(AgentEra, unitCost, available, currency);
            pair.Value.SelfModulate = verdict.Tier switch
            {
                BuildTintTier.YellowGrey => BarTintYellowGrey,
                BuildTintTier.Red => BarTintRed,
                _ => Colors.White,
            };
            pair.Value.TooltipText = verdict.Reason ?? $"{pair.Key}：可建造（{unitCost:0} {currency}）";
        }

        RefreshSlotStyles(); // T-163：图标灰暗+价红随两级灰度同拍刷新
    }

    /// <summary>
    /// 预览帧 → 逐格两级灰度判定（Repaint 与 headless 演示同一入口，无第二
    /// 套映射）：可放置格按计划序累计造价（含本格），首格绿、库存耗尽后的
    /// 可放置格转黄灰（一级），拒绝码格红（二级——红优先于黄灰）。
    /// </summary>
    private List<(BuildDragPreviewCell Cell, BuildTintVerdict Verdict)> TintedPreview(BuildDragPreview preview)
    {
        double available = _lane.View.ResourceAmount(_lane.View.CostCurrency);
        string currency = _lane.View.CostCurrency.Value;
        double cumulative = 0;
        var tinted = new List<(BuildDragPreviewCell, BuildTintVerdict)>(preview.Cells.Count);
        foreach (BuildDragPreviewCell cell in preview.Cells)
        {
            if (cell.Buildable)
            {
                cumulative += preview.UnitCost;
            }

            tinted.Add((cell, BuildGreyMap.Cell(cell, cumulative, available, currency)));
        }

        return tinted;
    }

    private void RepaintPreview()
    {
        foreach (ColorRect rect in _highlights)
        {
            rect.QueueFree();
        }

        _highlights.Clear();
        BuildDragPreview preview = _controller.Preview();
        foreach ((BuildDragPreviewCell cell, BuildTintVerdict verdict) in TintedPreview(preview))
        {
            var rect = new ColorRect
            {
                Position = new Vector2(cell.Cell.X * Cell, cell.Cell.Y * Cell),
                Size = new Vector2(Cell, Cell),
                Color = verdict.Tier switch
                {
                    BuildTintTier.YellowGrey => TintYellowGrey,
                    BuildTintTier.Red => TintRed,
                    _ => TintGreen,
                },
                MouseFilter = MouseFilterEnum.Ignore,
            };
            if (verdict.Reason is not null)
            {
                rect.TooltipText = verdict.Reason; // 黄灰=缺什么/差多少；红=不可放置原因
            }

            _previewLayer.AddChild(rect);
            _highlights.Add(rect);
        }

        if (preview.CappedByLimit)
        {
            // §6.1 批量同限截断 UX：玩家与系统公平同规（上限读 K-3 冻结表）
            SetStatus(BuildGreyMap.CapTruncationNote(preview.Cells.Count, AgentEra));
        }
    }

    private CellCoord CellAtMouse()
    {
        Vector2 world = GetGlobalMousePosition(); // camera-applied world coordinates
        return CellAtWorld(world);
    }

    /// <summary>T-141：世界坐标→格坐标（触控/鼠标同一格选换算——floor(world/cell)）。</summary>
    private static CellCoord CellAtWorld(Vector2 world) => new(
        (int)Math.Floor(world.X / Cell),
        (int)Math.Floor(world.Y / Cell));

    private void SetStatus(string text) => _status.Text = text;

    private static string TowerClassOf(string towerId) => towerId switch
    {
        "era1:tower-wood-arrow" => "pierce",
        "era1:tower-rockfall" => "physical",
        "era1:tower-spike-trap" => "slow",
        "era1:tower-campfire" => "support",
        _ => "pierce",
    };

    // ---- headless smoke（脚本化同一控制器/泳道——无第二套逻辑） ----

    private void RunHeadlessDemo()
    {
        int okBuilds = 0;
        int rejected = 0;

        // 1) 单格拖放：锚定首个可建格 → 松开 → 同一咽喉落子
        CellCoord green = FirstBuildableCell();
        _controller.BeginDrag("era1:tower-wood-arrow", green);
        _controller.UpdateDrag(green); // 单格=1×1 矩形
        okBuilds += CommitDemo("era1:tower-wood-arrow", green);

        // 2) 批量矩形：3×3 超 era2 上限（4+2×2=8）→ 截断后批量落子
        _controller.BeginDrag("era1:tower-rockfall", green with { X = green.X + 1, Y = green.Y + 1 });
        _controller.UpdateDrag(green with { X = green.X + 3, Y = green.Y + 3 });
        okBuilds += CommitDemo("era1:tower-rockfall", green with { X = green.X + 1, Y = green.Y + 1 });

        // 3) ESC 取消：拖放中取消，零动作零调用
        _controller.BeginDrag("era1:tower-wood-arrow", green with { X = green.X + 2 });
        _controller.UpdateDrag(green with { X = green.X + 2, Y = green.Y + 2 });
        _controller.Cancel();
        int escIdle = _controller.State == BuildDragState.Idle ? 1 : 0;

        // 4) pit#6 红格：步进到首敌在图，对敌格建造→同一咽喉诚实拒绝
        StepUntilEnemies(maxTicks: 6_000);
        Unit? enemy = FirstLivingEnemy();
        if (enemy is not null)
        {
            var enemyCell = new CellCoord(
                (int)Math.Floor(enemy.X / Simulation.CellSizePx),
                (int)Math.Floor(enemy.Y / Simulation.CellSizePx));
            PlayerBuildOutcome refused = _lane.Build("era1:tower-wood-arrow", new[] { enemyCell });
            rejected += refused.Result.Status == ActionResultStatus.Ok ? 0 : 1;
        }

        // 5) 右键拆除草案：confirm_sell=true → SellTower 同一注册表（薄版诚实拒绝）
        PlayerBuildOutcome sell = _sim.Towers.Count > 0
            ? _lane.Sell(_sim.Towers[0].Id.Value, confirmSell: true)
            : _lane.Sell(1, confirmSell: true);
        int sellRefused = sell.Result.Status == ActionResultStatus.Ok ? 0 : 1;

        GD.Print(
            $"BUILD_OK registry_invocations={_registrySink.Lines} ok_builds={okBuilds} " +
            $"towers_placed={_sim.Towers.Count} throat_rejections={rejected} esc_idle={escIdle} " +
            $"sell_refusals={sellRefused} player_journal_events={_journal.PlayerEvents} " +
            $"player_journal_last_seq={_journal.LastPlayerSeq} " +
            $"tick={_sim.CurrentTick} state_hash={_sim.ComputeStateHash()}");

        // ---- T-076 (C-23 全量) 第二幕：暂停+变速 / 两级灰度 / 同限截断 ----
        RunFullInteractionDemoV2();

        // ---- T-141 第三幕：触控原型（合成事件→建造命令发出） ----
        RunTouchPrototypeDemo();
    }

    /// <summary>
    /// BUILD_TOUCH 冒烟段（T-141 触控原型判据：tap=build drag=rect twofinger=
    /// cancel+panzoom）：合成 InputEventScreenTouch/Drag 事件**经 _GuiInput
    /// 事件分发入口**驱动（非直调控制器——证明事件类型分支本身可用），
    /// 断言：①单指点击=单格建造命令发出（塔+1/journal player 事件+1）；
    /// ②单指拖动=矩形提交；③armed 拖放中第二指按下→预览取消（控制器回
    /// Idle、零命令），双指移动=手势非建造，双指全抬后单指建造恢复可用。
    /// 任一自检不过即 Quit(1)。
    /// </summary>
    private void RunTouchPrototypeDemo()
    {
        _pauseClock.SetSpeed(1); // 上一幕可能停在任意档——触控幕以 1x 稳态跑
        int towersBefore = _sim.Towers.Count;
        int journalBefore = _journal.PlayerEvents;
        _touch.Reset();
        _touchBuildPresses = 0;
        _touchBuildCommits = 0;
        _touchBuildCancels = 0;
        _touchPanZooms = 0;
        ArmTower("era1:tower-wood-arrow", _towerButtons["era1:tower-wood-arrow"]);

        // ① 点击=格选：单指按下→抬起（单格=1×1 矩形，与鼠标单击同一提交）
        CellCoord tapCell = FirstBuildableCell();
        TouchEventAt(tapCell, pressed: true, index: 0);
        TouchEventAt(tapCell, pressed: false, index: 0);
        int towersAfterTap = _sim.Towers.Count;
        bool tapBuilt = towersAfterTap == towersBefore + 1;
        bool tapJournaled = _journal.PlayerEvents == journalBefore + 1;

        // ② 拖拽=矩形：单指按下→ScreenDrag 扩选 1×2→抬起提交（≥1 可建格）
        CellCoord anchor = FirstBuildableCell();
        TouchEventAt(anchor, pressed: true, index: 0);
        TouchDragEventAt(anchor with { X = anchor.X + 1 }, index: 0);
        TouchEventAt(anchor with { X = anchor.X + 1 }, pressed: false, index: 0);
        int dragBuilt = _sim.Towers.Count - towersAfterTap;

        // ③ 双指=缩放平移+防误触：armed 拖放中第二指按下→取消（回 Idle、
        // 零命令）；双指移动=手势非建造；先抬主指再抬副指→零提交
        CellCoord gestureCell = FirstBuildableCell();
        TouchEventAt(gestureCell, pressed: true, index: 0); // BeginDrag
        bool dragging = _controller.State == BuildDragState.Dragging;
        TouchEventAt(gestureCell with { X = gestureCell.X + 3 }, pressed: true, index: 1); // 第二指
        bool cancelledIdle = _controller.State == BuildDragState.Idle;
        int towersAtGesture = _sim.Towers.Count;
        TouchDragEventAt(gestureCell with { X = gestureCell.X + 2 }, index: 0); // 双指移动
        TouchEventAt(gestureCell, pressed: false, index: 0); // 主指先抬（无提交）
        TouchEventAt(gestureCell with { X = gestureCell.X + 3 }, pressed: false, index: 1); // 副指后抬（手势结束）
        bool gestureZeroBuild = _sim.Towers.Count == towersAtGesture;

        // ④ 手势后单指映射恢复：press 事件再次驱动 BeginDrag（零提交零木料
        // 消耗——恢复性由 Dragging 态证明，不靠第三次建造）
        TouchEventAt(gestureCell, pressed: true, index: 0);
        bool recovered = _controller.State == BuildDragState.Dragging;
        _controller.Cancel();

        GD.Print(
            $"BUILD_TOUCH tap=build tap_built={tapBuilt} tap_journaled={tapJournaled} " +
            $"drag=rect drag_built={dragBuilt} dragging={dragging} twofinger=cancel+panzoom " +
            $"cancelled_idle={cancelledIdle} gesture_zero_build={gestureZeroBuild} recovered={recovered} " +
            $"decisions presses={_touchBuildPresses} commits={_touchBuildCommits} " +
            $"cancels={_touchBuildCancels} panzooms={_touchPanZooms} " +
            $"towers={_sim.Towers.Count} player_journal_events={_journal.PlayerEvents} " +
            $"tick={_sim.CurrentTick} state_hash={_sim.ComputeStateHash()}");

        if (!tapBuilt || !tapJournaled || dragBuilt < 1 || !dragging || !cancelledIdle
            || !gestureZeroBuild || _touchPanZooms < 1 || !recovered)
        {
            GD.PushError(
                $"BUILD_TOUCH self-check failed: tap_built={tapBuilt} tap_journaled={tapJournaled} " +
                $"drag_built={dragBuilt} dragging={dragging} cancelled_idle={cancelledIdle} " +
                $"gesture_zero_build={gestureZeroBuild} panzooms={_touchPanZooms} recovered={recovered}");
            GetTree().Quit(1);
        }
    }

    /// <summary>合成单指按下/抬起事件（世界格→格心世界坐标→本控件局部坐标→_GuiInput）。</summary>
    private void TouchEventAt(CellCoord cell, bool pressed, int index)
    {
        var world = new Vector2(cell.X * Cell + Cell / 2f, cell.Y * Cell + Cell / 2f);
        Vector2 local = GetGlobalTransformWithCanvas().AffineInverse() * world;
        _GuiInput(new InputEventScreenTouch { Index = index, Position = local, Pressed = pressed });
    }

    /// <summary>合成单指移动事件（世界格→格心→局部坐标→_GuiInput 的 ScreenDrag 分支）。</summary>
    private void TouchDragEventAt(CellCoord cell, int index)
    {
        var world = new Vector2(cell.X * Cell + Cell / 2f, cell.Y * Cell + Cell / 2f);
        Vector2 local = GetGlobalTransformWithCanvas().AffineInverse() * world;
        _GuiInput(new InputEventScreenDrag { Index = index, Position = local });
    }

    /// <summary>
    /// BUILD_V2 冒烟段（任务卡判据 pause=speed3 grey=two-tier cap=trunc）：
    /// ①暂停中拖放松手→命令暂存（冻结证据：暂停帧 0 tick、塔数不动）→恢复
    /// 冲刷→下一 tick 按序落子；②3x 变速=每帧 3 tick（4 帧=12 tick），暂停
    /// 闸住变速（暂停帧 0 tick）；③两级灰度可区分——同一预览帧绿/黄灰/红
    /// 三色齐全（黄灰=缺资源 tooltip、红=占用原因）；④整行快捷超限截断
    /// （kept=上限，读 K-3 冻结表）。任一自检不过即 Quit(1)。
    /// </summary>
    private void RunFullInteractionDemoV2()
    {
        _pauseClock.SetSpeed(1);

        // ① 暂停可建造：冻结 tick 上拖放两格 → 暂存 → 冻结帧证据 → 恢复冲刷
        _pauseClock.Pause();
        CellCoord pauseAnchor = FirstBuildableCell();
        _controller.BeginDrag("era1:tower-wood-arrow", pauseAnchor);
        _controller.UpdateDrag(pauseAnchor with { X = pauseAnchor.X + 1 }); // 1×2 两格
        int pendingBefore = _pauseClock.PendingCount;
        CommitDrag(); // 走 UI 同一路径（暂停分支=入队暂存）
        int parked = _pauseClock.PendingCount - pendingBefore; // 期望 2（两可建格）

        long frozenTick = _sim.CurrentTick;
        int frozenTowers = _sim.Towers.Count;
        const int frozenFrames = 3;
        for (int frame = 0; frame < frozenFrames; frame++)
        {
            RunFrame();
        }

        bool frozenHeld = _sim.CurrentTick == frozenTick && _sim.Towers.Count == frozenTowers;
        _controller.Cancel();

        _pauseClock.Resume();
        int flushed = _pauseClock.FlushInto(_sim);
        RunFrame(); // 1x 一帧=1 tick——冲刷命令在本 tick 边界按 (tick,seq) 应用
        bool resumeApplied = _sim.Towers.Count == frozenTowers + flushed;

        // ② 变速：3x=每帧 3 tick；暂停闸住变速（0 tick/帧）
        _pauseClock.SetSpeed(3);
        long speedTickBefore = _sim.CurrentTick;
        const int speedFrames = 4;
        for (int frame = 0; frame < speedFrames; frame++)
        {
            RunFrame();
        }

        long speedTicks = _sim.CurrentTick - speedTickBefore; // 期望 12
        _pauseClock.Pause();
        long gatedTick = _sim.CurrentTick;
        for (int frame = 0; frame < 2; frame++)
        {
            RunFrame();
        }

        bool pauseGatesSpeed = _sim.CurrentTick == gatedTick;
        _pauseClock.Resume();
        _pauseClock.SetSpeed(1); // 回 1x——后续木料核算按 1 tick/帧推进

        // ③ 两级灰度：把库存抽到 [60,120)——rockfall 60/座 ⇒ 行内首格绿、
        // 第二格起黄灰（累计 120 > 库存）；行内既有占用/地形格=红。
        DrainWoodBelow(keepBelow: 120);
        CellCoord greyAnchor = FindRowAnchorForGreyDemo();
        _controller.BeginDrag("era1:tower-rockfall", greyAnchor);
        _controller.ExpandToRow(); // ④ 同一预览兼证 cap=trunc（行长>上限 8）
        BuildDragPreview greyPreview = _controller.Preview();
        bool capped = greyPreview.CappedByLimit;
        int kept = greyPreview.Cells.Count;
        int tierGreen = 0, tierYellow = 0, tierRed = 0;
        string? yellowTooltip = null, redTooltip = null;
        foreach ((BuildDragPreviewCell cell, BuildTintVerdict verdict) in TintedPreview(greyPreview))
        {
            switch (verdict.Tier)
            {
                case BuildTintTier.Green:
                    tierGreen++;
                    break;
                case BuildTintTier.YellowGrey:
                    tierYellow++;
                    yellowTooltip ??= verdict.Reason;
                    break;
                default:
                    tierRed++;
                    redTooltip ??= verdict.Reason;
                    break;
            }
        }

        // 红格补证：仓库格（恒占用）单独预览，拒绝码归「占用」类
        _controller.BeginDrag("era1:tower-rockfall", new CellCoord(2, 14));
        BuildTintVerdict warehouse = TintedPreview(_controller.Preview())[0].Verdict;
        _controller.Cancel();
        bool warehouseRed = warehouse.Tier == BuildTintTier.Red;
        if (warehouseRed)
        {
            redTooltip ??= warehouse.Reason;
        }

        string capNote = BuildGreyMap.CapTruncationNote(kept, AgentEra);
        int tierRedTotal = tierRed + (warehouseRed ? 1 : 0);
        GD.Print(
            $"BUILD_V2 pause=speed3 parked={parked} flushed={flushed} frozen_frames={frozenFrames} " +
            $"frozen_held={frozenHeld} resume_applied={resumeApplied} " +
            $"speed3_frames={speedFrames} speed3_ticks={speedTicks} pause_gates_speed={pauseGatesSpeed} " +
            $"grey=two-tier green={tierGreen} yellow={tierYellow} red={tierRedTotal} " +
            $"yellow_tooltip=\"{yellowTooltip ?? "none"}\" red_tooltip=\"{redTooltip ?? "none"}\" " +
            $"cap=trunc kept={kept} limit={PlayerBuildLane.MaxBuildCells(AgentEra)} capped={capped} " +
            $"note=\"{capNote}\" towers={_sim.Towers.Count} tick={_sim.CurrentTick} " +
            $"wood={_lane.View.ResourceAmount(_lane.View.CostCurrency):0} " +
            $"state_hash={_sim.ComputeStateHash()}");

        // 自检门（与 PANEL 冒烟同判据形态）：任一不过即非 0 退出
        if (parked < 1 || flushed != parked || !frozenHeld || !resumeApplied)
        {
            GD.PushError(
                $"BUILD_V2 pause self-check failed: parked={parked} flushed={flushed} " +
                $"frozen_held={frozenHeld} resume_applied={resumeApplied}");
            GetTree().Quit(1);
            return;
        }

        if (speedTicks != 3 * speedFrames || !pauseGatesSpeed)
        {
            GD.PushError(
                $"BUILD_V2 speed self-check failed: speed3_ticks={speedTicks} " +
                $"(want {3 * speedFrames}) pause_gates_speed={pauseGatesSpeed}");
            GetTree().Quit(1);
            return;
        }

        if (tierGreen < 1 || tierYellow < 1 || tierRedTotal < 1
            || yellowTooltip is null || redTooltip is null || !warehouseRed)
        {
            GD.PushError(
                $"BUILD_V2 grey self-check failed: green={tierGreen} yellow={tierYellow} " +
                $"red={tierRedTotal} warehouse_red={warehouseRed} " +
                $"yellow_tooltip=\"{yellowTooltip}\" red_tooltip=\"{redTooltip}\"");
            GetTree().Quit(1);
            return;
        }

        if (!capped || kept != PlayerBuildLane.MaxBuildCells(AgentEra))
        {
            GD.PushError(
                $"BUILD_V2 cap self-check failed: capped={capped} kept={kept} " +
                $"limit={PlayerBuildLane.MaxBuildCells(AgentEra)}");
            GetTree().Quit(1);
        }
    }

    /// <summary>一个 render 帧经 PauseClock：0 tick while paused，否则 SpeedMultiplier 个（SimHost.StepFrame 同式）。
    /// 预算每帧只取一次——TicksThisFrame() 带计数副作用，不得置于循环条件。</summary>
    private void RunFrame()
    {
        int ticks = _pauseClock.TicksThisFrame();
        for (int t = 0; t < ticks; t++)
        {
            _sim.Step();
        }
    }

    /// <summary>
    /// 木料抽干到 keepBelow 以下（BUILD_V2 黄灰段夹具）：逐座 30 木箭塔走
    /// 同一咽喉扣款，直至库存 < keepBelow。起始 1000 且全部支出为 30/60 的
    /// 整数倍 ⇒ 终值恒为 100（∈[90,120)）——rockfall 60/座下首格绿、次格起
    /// 黄灰，两格即分档。
    /// </summary>
    private void DrainWoodBelow(double keepBelow)
    {
        var tried = new HashSet<CellCoord>();
        for (int guard = 0; guard < 60; guard++)
        {
            if (_lane.View.ResourceAmount(_lane.View.CostCurrency) < keepBelow)
            {
                return;
            }

            if (FirstBuildableCell(exclude: tried) is not { } cell)
            {
                return; // 排除集打满（不可能在冒烟里发生）——按现状继续，黄灰自检兜底
            }

            tried.Add(cell);
            _lane.Build("era1:tower-wood-arrow", new[] { cell });
            RefreshBarTints();
        }
    }

    /// <summary>
    /// 黄灰演示的整行锚：自底向上找一行，其最左可建格 x≤7（保证整行快捷
    /// 长 ≥9 格 > 上限 8 ⇒ 截断）且行内可建格 ≥2（保证绿+黄灰并存）。
    /// 种子钉死 ⇒ 每次运行同一锚。
    /// </summary>
    private CellCoord FindRowAnchorForGreyDemo()
    {
        for (int y = MapHeightCells - 1; y >= 0; y--)
        {
            int buildable = 0;
            CellCoord? leftmost = null;
            for (int x = 0; x < MapWidthCells; x++)
            {
                var cell = new CellCoord(x, y);
                if (!_lane.View.IsBuildable(cell, out _))
                {
                    continue;
                }

                leftmost ??= cell;
                buildable++;
            }

            if (buildable >= 2 && leftmost!.X <= MapWidthCells / 2)
            {
                return leftmost;
            }
        }

        throw new InvalidOperationException("grey demo 找不到可建 ≥2 且最左格 x≤7 的行（内容/种子漂移）");    }

    private int CommitDemo(string towerId, CellCoord anchor)
    {
        BuildDragCommit? commit = _controller.Commit();
        if (commit is null)
        {
            return 0;
        }

        PlayerBuildOutcome outcome = _lane.Build(towerId, commit.Cells);
        return outcome.Result.Status == ActionResultStatus.Ok ? 1 : 0;
    }

    private CellCoord FirstBuildableCell(HashSet<CellCoord>? exclude = null)
    {
        for (int y = 0; y < MapHeightCells; y++)
        {
            for (int x = 0; x < MapWidthCells; x++)
            {
                var cell = new CellCoord(x, y);
                if (exclude?.Contains(cell) == true)
                {
                    continue;
                }

                if (_lane.View.IsBuildable(cell, out _))
                {
                    return cell;
                }
            }
        }

        return exclude is null
            ? throw new InvalidOperationException("demo 地图无可建格（内容/种子漂移）")
            : null!; // 排除集打满：调用方按「无处可建」处理
    }

    private void StepUntilEnemies(int maxTicks)
    {
        for (int i = 0; i < maxTicks && _sim.Units.Count == 0; i++)
        {
            _sim.Step();
        }
    }

    private Unit? FirstLivingEnemy()
    {
        foreach (Unit unit in _sim.Units)
        {
            if (unit.Alive)
            {
                return unit;
            }
        }

        return null;
    }

    // ---- 内容装载（仓库相对向上探测，与 EraCore.Tests 同模式；RL9 无盘符路径） ----

    private static string FindContentFile(string fileName)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = System.IO.Path.Combine(dir.FullName, "game", "content", "era1", fileName);
            if (System.IO.File.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new System.IO.FileNotFoundException(
            $"未找到 game/content/era1/{fileName}（headless 冒烟须在仓库内运行）");
    }

    private static IReadOnlyList<TowerSpec> LoadTowerCatalog() =>
        TowerSpec.LoadCollection(System.IO.File.ReadAllText(FindContentFile("towers.json")));

    private static SimulationConfig DemoConfig(IReadOnlyList<TowerSpec> catalog, EnemySpecCatalog enemies)
    {
        var entries = new[]
        {
            new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1),
        };
        return new SimulationConfig(
            MapWidthCells,
            MapHeightCells,
            ForestRatio: 0.10,
            RockRatio: 0.05,
            WaterRatio: 0.0,
            BaseCell: new CellCoord(8, 23),
            EntryCells: entries,
            TowerCatalog: catalog,
            Enemies: enemies,
            Waves: new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
                BatchesPerWave: 4,
                EntryCells: entries),
            InitialStock: new List<KeyValuePair<ResourceKey, double>> { new(Wood, 1000.0) },
            WarehouseCell: new CellCoord(2, 14),
            Costs: new BuildCostPolicy(Wood));
    }

    /// <summary>SplitMix64 演示种子流（K-1 端口适配；headless 冒烟确定性）。</summary>
    private sealed class DemoRng(long seed) : IRng
    {
        private ulong _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            long span = maxValue - (long)minValue;
            return minValue + (int)(NextDouble() * span);
        }

        public double NextDouble()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / 9007199254740992.0);
        }

        public void Reseed(long newSeed) => _state = (ulong)newSeed;
    }

    /// <summary>注册表诊断计数 sink——每条 verdict 行=一次 ActionRegistry.Invoke。</summary>
    private sealed class CountingLogSink : ILogSink
    {
        public int Lines { get; private set; }

        public void Write(LogLevel level, long tick, string message) => Lines++;
    }

    /// <summary>内存 K-8 sink（headless 冒烟计数；正式宿主换 JSONL 落盘写入器）。
    /// 只见经 SequencingJournalWriter 重编号后的事件（F5）——lastSeq 供冒烟
    /// 观测玩家流 Seq 递增，不再出现裸 Seq=0。</summary>
    private sealed class InMemoryJournalWriter : IJournalWriter
    {
        public int PlayerEvents { get; private set; }

        public long LastPlayerSeq { get; private set; }

        public void Append(JournalEvent evt)
        {
            if (evt.PayloadJson.Contains("\"Actor\":\"player\"", StringComparison.Ordinal))
            {
                PlayerEvents++;
                LastPlayerSeq = evt.Seq;
            }
        }
    }
}
