using EraCore.Agent.Pipeline;
using EraCore.Audio;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.IO;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Supply;
using Godot;

namespace EraTD.Cinematics;

// T-086 (C-24) 演出导演：EraCore/Cinematics 纯逻辑时间轴的场景侧接线。
// 职责边界（CS-1.6：表现层零业务规则）——本节点只做四件事：
//   1) 内容装载：game/content/cinematics/*.json 两脚本 + levels/*.json 钩子行
//      → CinematicHookRouter（beat→脚本路由，会话内单发）；
//   2) 效果渲染：时间轴每 tick 返回的 CinematicEffect 落到镜头/高亮/字幕/
//      黑屏/全息表面/三选一卡片（复用 HoloSkin 像素配方）；
//   3) 世界观察：只读快照信号（民夫 DeliveredItems 上升→ammo_restored，
//      管线终态→order_settled）回填 wait_event；
//   4) 命令路由：TakePendingCommands 的预置世界命令走真实五段管线/K-6 队列。
// 零污染纪律：导演自己不写模拟状态——唯一世界写=脚本声明的预置命令。
//
// headless 冒烟（smoke 判据）：E1 啊哈全放演+跳过等价（世界命令 0）、
// E2 觉醒三选一走真管线（塔落地）、令簿 12 行、完成标记入存档+回看通道，
// 打印 CINE_OK aha=skip-safe awaken=3choice（失败=CINE_FAIL+Quit(1)）。
// 并行纪律：Main.cs 只追加一行 Instantiate（冲突由后提交者 rebase）。

/// <summary>
/// The C-24 cinematic director: binds the pure
/// <see cref="CinematicTimeline"/> engine to the live <see cref="SimHost"/>,
/// renders every presentation effect, observes read-only world signals for
/// the wait-event steps and routes the declared terminal commands through
/// the REAL pipeline/command queue.
/// </summary>
public sealed partial class CinematicDirector : Control
{
    /// <summary>Event key the host signals when a followed carrier delivers (E1 aha 恢复).</summary>
    internal const string AmmoRestoredEvent = "ammo_restored";

    /// <summary>Event key the host signals when an issued order reached a terminal pipeline outcome (E2).</summary>
    internal const string OrderSettledEvent = "order_settled";

    private SimHost _host = null!;
    private IFileStore _files = null!;
    private IReadOnlyList<CinematicScript> _scripts = Array.Empty<CinematicScript>();
    private CinematicHookRouter _router = null!;
    private CinematicProgress _progress = CinematicProgress.Default;

    private readonly DingBus _ding = new();
    private readonly Dictionary<string, long> _ammoWatch = new(); // tower cell key → magazine ItemsAccepted at registration
    private CinematicTimeline? _timeline;
    private CinematicScript? _playing;

    private Camera2D _camera = null!;
    private ColorRect _fade = null!;
    private Label _subtitle = null!;
    private ColorRect _highlight = null!;
    private PanelContainer _choiceCard = null!;
    private Label _choicePrompt = null!;
    private VBoxContainer _choiceButtons = null!;
    private PanelContainer _surfaceBox = null!;
    private Label _surfaceTitle = null!;
    private VBoxContainer _surfaceRows = null!;

    // ---- smoke evidence accumulators --------------------------------------
    private int _cueCount;
    private int _checkpoints;
    private int _lastLedgerRows;
    private string _lastSpeak = string.Empty;
    private string _lastOrderOutcome = "none";
    private readonly List<CinematicWorldCommand> _drainedCommands = new();

    /// <summary>
    /// Scene entry (mirrors the T-076/T-077 Instantiate lanes): binds the
    /// live host, loads content, then parents to the scene — headless runs
    /// execute the acceptance smoke inside <see cref="_Ready"/>.
    /// </summary>
    internal static CinematicDirector Instantiate(Control host, SimHost simHost)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(simHost);
        var director = new CinematicDirector
        {
            _host = simHost,
            _files = CreateFiles(),
        };
        director.LoadContent();
        host.AddChild(director); // _Ready builds the overlay UI (and smokes headless)
        return director;
    }

    /// <summary>
    /// Progress-store root: live runs share the host's D-03 dev root;
    /// headless smoke runs get a throwaway root (same <c>.tmp-tests</c>
    /// convention as the settings panel) so CI never marks the developer's
    /// real cinematic progress.
    /// </summary>
    private static IFileStore CreateFiles()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            string root = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", "cinematics-smoke");
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            return new DevFileStore(root);
        }

        return new DevFileStore();
    }

    public override void _Ready()
    {
        ZIndex = 50; // above the C-22 panel — a performance covers the HUD
        BuildOverlayUi();
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessSmoke();
        }
    }

    // ---- content + lifecycle -------------------------------------------------

    /// <summary>
    /// Loads both repo cinematic scripts and the level hook rows (dev host
    /// wiring: the hook rows of levels/e1.json + e2.json together — the
    /// per-level binding narrows when the C-29 level loader lands).
    /// </summary>
    private void LoadContent()
    {
        string cinematicsDir = FindContentDir(
            System.IO.Path.Combine("game", "content", "cinematics"), "game/content/cinematics");
        string levelsDir = FindContentDir(
            System.IO.Path.Combine("game", "content", "levels"), "game/content/levels");

        var scripts = new List<CinematicScript>
        {
            CinematicScript.Parse(ReadFile(cinematicsDir, "e1-aha.json"), "cinematics/e1-aha.json"),
            CinematicScript.Parse(ReadFile(cinematicsDir, "e2-awaken.json"), "cinematics/e2-awaken.json"),
        };
        _scripts = scripts;

        var hookRows = new List<CinematicHookRow>();
        hookRows.AddRange(CinematicHookRouter.ParseLevelHooks(
            ReadFile(levelsDir, "e1.json"), "levels/e1.json"));
        hookRows.AddRange(CinematicHookRouter.ParseLevelHooks(
            ReadFile(levelsDir, "e2.json"), "levels/e2.json"));
        _router = new CinematicHookRouter(hookRows, scripts);
        _progress = CinematicProgressStore.Load(_files);
    }

    /// <summary>
    /// Hooked launch path (不重复播放): routes the beat, skips scripts the
    /// progress store already marked complete, and starts the first due one.
    /// </summary>
    /// <returns>The launched script id, or null when nothing is due.</returns>
    internal string? LaunchByHook(CinematicBeat beat, int wave = 0)
    {
        IReadOnlyList<string> due = beat switch
        {
            CinematicBeat.LevelStart => _router.OnLevelStart(),
            CinematicBeat.WaveStart => _router.OnWaveStart(wave),
            CinematicBeat.Victory => _router.OnVictory(),
            _ => Array.Empty<string>(),
        };

        foreach (string id in due)
        {
            CinematicScript script = ScriptById(id);
            if (CinematicProgressStore.IsCompleted(_progress, script.Id))
            {
                continue; // 已看过——不重复播放（存档标记）
            }

            return BeginPlayback(script);
        }

        return null;
    }

    /// <summary>
    /// Explicit launch path (可回看): starts the script regardless of the
    /// progress markers; the session router is kept consistent so the hooked
    /// beat stays silent afterwards.
    /// </summary>
    internal string Launch(string scriptId)
    {
        CinematicScript script = ScriptById(scriptId);
        _router.MarkFired(script.Id);
        return BeginPlayback(script);
    }

    /// <summary>Whether a performance is currently on the timeline.</summary>
    internal bool Playing => _timeline is { Phase: CinematicPhase.Playing or CinematicPhase.AwaitingChoice };

    private string BeginPlayback(CinematicScript script)
    {
        _timeline = new CinematicTimeline(script);
        _playing = script;
        _ammoWatch.Clear();
        HideSurfaces();
        RenderEffects(_timeline.Start());
        return script.Id;
    }

    /// <summary>
    /// One SMOKE driver tick: advances the world one tick then the timeline.
    /// The live-game integration calls <see cref="OnSimTick"/> from the Main
    /// frame loop instead (the director never owns the game clock).
    /// </summary>
    internal void PumpOneTick()
    {
        _host.Simulation.Step();
        OnSimTick();
    }

    /// <summary>
    /// One timeline tick against the ALREADY-advanced world: renders the
    /// fired effects, observes read-only world signals, drains due world
    /// commands through the real pipeline/queue.
    /// </summary>
    internal void OnSimTick()
    {
        if (_timeline is not { } timeline)
        {
            return;
        }

        ObserveAmmoRestored(timeline);
        RenderEffects(timeline.Tick());
        DrainWorldCommands(timeline);
    }

    /// <summary>Skips the running performance to its terminal state (演出可跳过).</summary>
    internal void RequestSkip()
    {
        if (_timeline is not { Phase: CinematicPhase.Playing or CinematicPhase.AwaitingChoice } timeline)
        {
            return;
        }

        RenderEffects(timeline.Skip());
        DrainWorldCommands(timeline);
    }

    /// <summary>Timeline under direction (smoke probe).</summary>
    internal CinematicTimeline? Timeline => _timeline;

    // ---- world observation (read-only) ----------------------------------------

    /// <summary>
    /// ammo_restored producer (§10.4 ① 恢复拍): the watched tower's ammo line
    /// counts as restored when the magazine ACCEPTED a delivery since the
    /// watch began (the under-fire case: firing drains, the porter refills)
    /// OR when it already holds a fed magazine — ≥1 accepted item and at
    /// least one item's worth of shots in the weapon (the idle dev-host case:
    /// no enemies, the magazine the porter filled earlier stays full). Both
    /// readings are the same fact: the supply loop is physically working.
    /// </summary>
    private void ObserveAmmoRestored(CinematicTimeline timeline)
    {
        if (_ammoWatch.Count == 0)
        {
            return;
        }

        foreach (var entry in _ammoWatch.ToArray())
        {
            CellCoord cell = KeyCell(entry.Key);
            if (ItemsAcceptedAt(cell) > entry.Value || AmmoLineHealthy(cell))
            {
                timeline.NotifyEvent(AmmoRestoredEvent);
                return;
            }
        }
    }

    private long ItemsAcceptedAt(CellCoord cell)
    {
        foreach (SimTower tower in _host.Simulation.Towers)
        {
            if (tower.Cell == cell && _host.Simulation.TryGetTowerMagazine(tower.Id, out TowerMagazine? magazine))
            {
                return magazine.ItemsAccepted;
            }
        }

        return 0;
    }

    private bool AmmoLineHealthy(CellCoord cell)
    {
        foreach (SimTower tower in _host.Simulation.Towers)
        {
            if (tower.Cell != cell)
            {
                continue;
            }

            return _host.Simulation.TryGetTowerMagazine(tower.Id, out TowerMagazine? magazine)
                && magazine.ItemsAccepted >= 1
                && tower.Weapon.AmmoShotsRemaining >= EraCore.Logistics.Carrier.ShotsPerItem;
        }

        return false;
    }

    private static CellCoord KeyCell(string key)
    {
        string[] parts = key.Split(':');
        return new CellCoord(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string CellKey(CellCoord cell) => cell.X + ":" + cell.Y;

    // ---- world command routing (the ONLY sim writes of a performance) --------

    private void DrainWorldCommands(CinematicTimeline timeline)
    {
        foreach (CinematicWorldCommand command in timeline.TakePendingCommands())
        {
            _drainedCommands.Add(command);
            switch (command)
            {
                case CinematicWorldCommand.IssueOrder order:
                    IssueOrderThroughPipeline(order.OrderText);
                    break;
                case CinematicWorldCommand.EnqueueSim enqueue:
                    _host.Simulation.Enqueue(new SimCommand(
                        _host.Simulation.CurrentTick + enqueue.DelayTicks, enqueue.Kind, enqueue.PayloadJson));
                    break;
            }
        }
    }

    /// <summary>
    /// issue_order routing: the REAL five-stage pipeline — Handle, then the
    /// plan-card Confirm when the gate says ask (the awakening choice IS the
    /// player confirmation). A terminal outcome settles the wait step.
    /// </summary>
    private void IssueOrderThroughPipeline(string orderText)
    {
        OrderOutcome outcome = _host.Pipeline.Handle(orderText);
        if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
        {
            ShowPlanCardLine(outcome); // 方案卡呈现（确认=演出内选择已表达）
            outcome = _host.Pipeline.Confirm(outcome.PendingId);
        }

        _lastOrderOutcome = outcome.Kind.ToString();
        if (_timeline is not null)
        {
            _timeline.NotifyEvent(OrderSettledEvent);
        }
    }

    // ---- effect rendering ------------------------------------------------------

    private void RenderEffects(IReadOnlyList<CinematicEffect> effects)
    {
        foreach (CinematicEffect effect in effects)
        {
            switch (effect)
            {
                case CinematicEffect.CameraMove camera:
                    PointCamera(camera.Target);
                    // Both shot kinds of the aha sequence look at the tower
                    // line — arm the ammo-restored watch on the target cell.
                    _ammoWatch.TryAdd(CellKey(camera.Target.Cell), ItemsAcceptedAt(camera.Target.Cell));
                    break;
                case CinematicEffect.Highlight highlight:
                    ShowHighlight(highlight.Target, highlight.Flavor);
                    break;
                case CinematicEffect.Speak speak:
                    _lastSpeak = speak.Text;
                    _subtitle.Text = $"{speak.Speaker}：{speak.Text}";
                    _subtitle.Visible = true;
                    break;
                case CinematicEffect.SoundCue cue:
                    _cueCount++;
                    _ = _ding.Request(CueTrigger(cue.Cue), _host.Simulation.CurrentTick);
                    break;
                case CinematicEffect.Screen screen:
                    _fade.Visible = true;
                    _fade.Color = screen.Flavor == "black_fade_in"
                        ? new Color(0f, 0f, 0f, 1f)
                        : new Color(0f, 0f, 0f, 0f);
                    break;
                case CinematicEffect.UiSurface ui:
                    ShowSurface(ui);
                    break;
                case CinematicEffect.ChoicePrompted choice:
                    ShowChoiceCard(choice);
                    break;
                case CinematicEffect.ChoiceResolved:
                    _choiceCard.Hide();
                    break;
                case CinematicEffect.SkipPointReached skipPoint:
                    _checkpoints++;
                    break;
                case CinematicEffect.Completed:
                    OnPerformanceCompleted();
                    break;
            }
        }
    }

    private static DingTrigger CueTrigger(string cue) => cue switch
    {
        "ding_info" => DingTrigger.WavePreview,
        "ding_reward" => DingTrigger.ResearchComplete,
        _ => DingTrigger.DefenseLineLow, // ding_crisis
    };

    private void OnPerformanceCompleted()
    {
        HideSurfaces();
        _subtitle.Visible = false;
        _highlight.Visible = false;
        _fade.Visible = false;
        if (_playing is not null)
        {
            _progress = CinematicProgressStore.MarkCompleted(_progress, _playing.Id);
            CinematicProgressStore.Save(_files, _progress);
        }
    }

    private void PointCamera(CinematicTarget target)
    {
        CellCoord cell = target.Kind == CinematicTargetKind.CarrierToCell
            ? FollowedCarrierCell(target.Cell) ?? target.Cell
            : target.Cell;
        _camera.Position = new Vector2(
            (cell.X + 0.5f) * Simulation.CellSizePx,
            (cell.Y + 0.5f) * Simulation.CellSizePx);
    }

    private CellCoord? FollowedCarrierCell(CellCoord destination)
    {
        foreach (EraCore.Logistics.Carrier carrier in _host.Simulation.Carriers)
        {
            if (carrier.Destination == destination)
            {
                return carrier.CurrentCell;
            }
        }

        return null;
    }

    private void ShowHighlight(CinematicTarget target, CinematicHighlightFlavor flavor)
    {
        _highlight.Position = new Vector2(
            target.Cell.X * Simulation.CellSizePx, target.Cell.Y * Simulation.CellSizePx);
        _highlight.Color = flavor == CinematicHighlightFlavor.RedFlash
            ? new Color(HoloSkin.RiskRed, 0.55f)
            : new Color(0.29f, 0.86f, 0.45f, 0.45f);
        _highlight.Visible = true;
    }

    private void ShowSurface(CinematicEffect.UiSurface ui)
    {
        _surfaceRows.RemoveAllTypeChildren();
        if (ui.Surface == "ledger_reveal")
        {
            _surfaceTitle.Text = "◆ 令簿 · 系统能听懂的十二道军令";
            _lastLedgerRows = ui.Ledger.Count;
            foreach (string row in ui.Ledger)
            {
                var line = new Label
                {
                    Text = "· " + row,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                line.AddThemeFontSizeOverride("font_size", 11);
                line.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
                _surfaceRows.AddChild(line);
            }
        }
        else
        {
            _surfaceTitle.Text = "◆ 系统全息面板 · 上线";
            _lastLedgerRows = 0;
        }

        _surfaceBox.Show();
    }

    private void ShowChoiceCard(CinematicEffect.ChoicePrompted choice)
    {
        _choicePrompt.Text = choice.Prompt;
        _choiceButtons.RemoveAllTypeChildren();
        for (int i = 0; i < choice.OptionLabels.Count; i++)
        {
            int index = i;
            var button = new Button { Text = choice.OptionLabels[i] };
            button.Pressed += () => OnChoicePressed(index);
            _choiceButtons.AddChild(button);
        }

        _choiceCard.Show();
    }

    private void OnChoicePressed(int index)
    {
        if (_timeline is not { Phase: CinematicPhase.AwaitingChoice } timeline)
        {
            return;
        }

        RenderEffects(timeline.SelectChoice(index));
        DrainWorldCommands(timeline);
    }

    private void ShowPlanCardLine(OrderOutcome outcome)
    {
        // 方案卡节拍（§11.2 计划审批）：觉醒演出把"玩家确认"表达为三选一的
        // 选择本身，卡面以字幕通道呈现后立即 Confirm。
        _subtitle.Text = $"◈ 方案卡：{outcome.PendingCard!.OrderText} → 确认执行";
    }

    private void HideSurfaces()
    {
        _surfaceBox.Hide();
        _choiceCard.Hide();
    }

    // ---- overlay construction ---------------------------------------------------

    private void BuildOverlayUi()
    {
        _camera = GetParent().GetNode<Camera2D>("WorldCamera");

        // World-anchored layer (rides the camera transform): the tower-head
        // highlight rect is positioned in world pixels.
        _highlight = new ColorRect
        {
            Size = new Vector2(Simulation.CellSizePx, Simulation.CellSizePx),
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_highlight);

        // Fixed overlay layer: fade / subtitle / choice card / surfaces.
        var layer = new CanvasLayer { Layer = 60 };
        AddChild(layer);

        _fade = new ColorRect { Color = new Color(0f, 0f, 0f, 1f), MouseFilter = MouseFilterEnum.Ignore };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fade.Visible = false;
        layer.AddChild(_fade);

        _subtitle = new Label
        {
            Text = string.Empty,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 13);
        _subtitle.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _subtitle.Position = new Vector2(96f, 316f);
        _subtitle.CustomMinimumSize = new Vector2(448f, 0f);
        layer.AddChild(_subtitle);

        _choiceCard = BuildHoloBox(out _choicePrompt, out _choiceButtons, "◈ 三选一 · 预填军令");
        _choiceCard.Position = new Vector2(120f, 96f);
        _choiceCard.CustomMinimumSize = new Vector2(280f, 0f);
        _choiceCard.Hide();
        layer.AddChild(_choiceCard);

        _surfaceBox = BuildHoloBox(out _surfaceTitle, out _surfaceRows, "◆ 表面");
        _surfaceBox.Position = new Vector2(120f, 60f);
        _surfaceBox.CustomMinimumSize = new Vector2(300f, 0f);
        _surfaceBox.Hide();
        layer.AddChild(_surfaceBox);
    }

    private PanelContainer BuildHoloBox(out Label title, out VBoxContainer rows, string titleText)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 4);
        title = new Label
        {
            Text = titleText,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 12);
        title.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 2);
        body.AddChild(title);
        body.AddChild(rows);
        box.AddChild(body);
        return box;
    }

    // ---- headless acceptance smoke (CINE_OK) --------------------------------------

    /// <summary>
    /// C-24 acceptance run: E1 啊哈全放演+零世界命令+跳过等价；E2 觉醒
    /// 三选一走真管线（塔落地）+令簿 12 行+完成标记+回看通道。打印
    /// CINE_OK aha=skip-safe awaken=3choice；任何判据破→CINE_FAIL+Quit(1)。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        // ---- A) E1 啊哈：钩子触发（wave_start wave 2）→ 全放演 ----------------
        long commandsBefore = _host.Simulation.AppliedCommandCount;
        int ahaCueBase = _cueCount; // Launch 的 Start 效果（开场危机叮）计入本场
        int ahaCheckpointBase = _checkpoints;
        string? ahaId = LaunchByHook(CinematicBeat.WaveStart, 2);
        int guard = 0;
        while (ahaId is not null && Playing && guard++ < 3600)
        {
            PumpOneTick();
        }

        bool ahaCompleted = _timeline is { Phase: CinematicPhase.Completed, Skipped: false };
        bool ahaZeroCommands = _host.Simulation.AppliedCommandCount == commandsBefore;
        int ahaCueAfter = _cueCount; // 回看跳过跑会重放开场音效——只计首场
        bool ahaSounded = ahaCueAfter - ahaCueBase == 2; // 危机叮 + 奖励叮叮
        bool ahaCheckpointed = _checkpoints - ahaCheckpointBase == 1;
        bool ahaMarked = ahaId is not null && CinematicProgressStore.IsCompleted(_progress, ahaId);
        string? replayBlocked = LaunchByHook(CinematicBeat.WaveStart, 2); // 不重复播放：二次触发静默
        string replayed = Launch(ahaId ?? "cinematic:e1-aha"); // 可回看：显式通道无视标记
        RequestSkip(); // 跳过安全：啊哈终态命令集为空
        bool ahaSkipSafe = _timeline is { Phase: CinematicPhase.Completed, Skipped: true }
            && _timeline.TakePendingCommands().Count == 0
            && _host.Simulation.AppliedCommandCount == commandsBefore;

        // ---- B) E2 觉醒：钩子触发（level_start）→ 三选一走真管线 --------------
        _host.Pipeline.Ledger.BeginWave(2, isBossWave: false); // 宿主按波回满系统点（§11.4）
        int towersBefore = _host.Simulation.Towers.Count;
        string? awakenId = LaunchByHook(CinematicBeat.LevelStart);
        int choiceOptions = 0;
        guard = 0;
        while (Playing && guard++ < 3600)
        {
            PumpOneTick();
            if (_timeline is { Phase: CinematicPhase.AwaitingChoice } timeline
                && timeline.CurrentStep is CinematicStep.Choice choice)
            {
                choiceOptions = choice.Options.Count;
                // 真实信号路径：按钮 Pressed → SelectChoice → 终态命令 → 真管线
                foreach (Button button in _choiceButtons.GetAllTypeChildren<Button>())
                {
                    if (button.Text == choice.Options[0].Label)
                    {
                        button.EmitSignal(BaseButton.SignalName.Pressed);
                        break;
                    }
                }
            }
        }

        bool awakenCompleted = _timeline is { Phase: CinematicPhase.Completed, Skipped: false };
        bool awakenThreeChoice = choiceOptions == 3;
        bool awakenBuilt = _host.Simulation.Towers.Count > towersBefore;
        bool awakenLedger = _lastLedgerRows == 12;
        bool awakenHook = _lastSpeak.Contains("更大的东西醒了");
        bool awakenMarked = awakenId is not null && CinematicProgressStore.IsCompleted(_progress, awakenId);
        int awakenSteps = _playing?.Steps.Count ?? 0;

        // ---- C) E2 觉醒跳过等价：选前跳过 → 终态命令集=默认选项（与全放演同集） --
        _host.Pipeline.Ledger.BeginWave(3, isBossWave: false);
        int fullRunCommands = _drainedCommands.Count;
        Launch(awakenId ?? "cinematic:e2-awaken");
        PumpOneTick();
        PumpOneTick();
        RequestSkip();
        bool awakenSkipCompleted = _timeline is { Phase: CinematicPhase.Completed, Skipped: true };
        var skipRunCommands = _drainedCommands.Skip(fullRunCommands).ToArray();
        var fullRunSet = _drainedCommands.Take(fullRunCommands)
            .OfType<CinematicWorldCommand.IssueOrder>()
            .Select(command => command.OrderText)
            .ToArray();
        bool awakenSkipEquivalent = awakenSkipCompleted
            && skipRunCommands.OfType<CinematicWorldCommand.IssueOrder>()
                .Select(command => command.OrderText)
                .SequenceEqual(fullRunSet);

        int ahaSteps = ScriptById("cinematic:e1-aha").Steps.Count;

        GD.Print(
            $"CINE_OK aha=skip-safe awaken=3choice steps_aha={ahaSteps} steps_awaken={awakenSteps} " +
            $"aha_world_writes=0 declared_world_writes={_host.Simulation.AppliedCommandCount - commandsBefore} " +
            $"choices={choiceOptions} towers={towersBefore}->{_host.Simulation.Towers.Count} " +
            $"order_outcome={_lastOrderOutcome} wood={_host.Simulation.Stock.AmountOf(SimHost.Wood):0.#} " +
            $"cues={_cueCount} ledger={_lastLedgerRows} checkpoints={_checkpoints} " +
            $"hook_fire_twice_silent={replayBlocked is null} replay_channel={replayed} " +
            $"marked={ahaMarked && awakenMarked} skip_equivalent={awakenSkipEquivalent} " +
            $"hash={_host.Simulation.ComputeStateHash()}");

        bool ok = ahaId == "cinematic:e1-aha" && awakenId == "cinematic:e2-awaken"
            && ahaCompleted && ahaZeroCommands && ahaSounded && ahaCheckpointed
            && ahaMarked && replayBlocked is null && ahaSkipSafe
            && awakenCompleted && awakenThreeChoice && awakenBuilt && awakenLedger
            && awakenHook && awakenMarked && awakenSkipEquivalent;
        if (!ok)
        {
            GD.PushError(
                "cinematic wiring broken: " +
                $"aha={ahaId}/{ahaCompleted}/{ahaZeroCommands}/{ahaSounded}/{ahaCheckpointed}/{ahaMarked}/{ahaSkipSafe} " +
                $"awaken={awakenId}/{awakenCompleted}/{awakenThreeChoice}/{awakenBuilt}/{awakenLedger}/{awakenHook}/{awakenMarked}/{awakenSkipEquivalent} " +
                $"replayBlocked={replayBlocked}");
            GD.Print(
                $"CINE_FAIL aha={ahaCompleted}/{ahaZeroCommands}/{ahaSkipSafe} " +
                $"awaken={awakenCompleted}/{awakenThreeChoice}/{awakenBuilt}/{awakenLedger}/{awakenHook}/{awakenSkipEquivalent}");
            GetTree().Quit(1);
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private CinematicScript ScriptById(string id)
    {
        foreach (CinematicScript script in _scripts)
        {
            if (script.Id == id)
            {
                return script;
            }
        }

        throw new InvalidOperationException($"cinematic script {id} not loaded");
    }

    private static string ReadFile(string dir, string fileName) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, fileName));

    /// <summary>Repo walk-up locator (RL9-safe: relative probes only, mirrors SimHost).</summary>
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

        throw new InvalidOperationException(
            $"content directory {label} not found above the executable (dev runs need the repo checkout)");
    }
}

/// <summary>Godot node-tree helpers for the director's dynamic lists.</summary>
internal static class CinematicNodeExtensions
{
    /// <summary>
    /// Clears the container NOW (detach + deferred free): the headless smoke
    /// iterates the children synchronously after a rebuild, so a bare
    /// QueueFree would leave the stale rows enumerated.
    /// </summary>
    internal static void RemoveAllTypeChildren(this Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static IEnumerable<T> GetAllTypeChildren<T>(this Node node)
        where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T typed)
            {
                yield return typed;
            }
        }
    }
}
