using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.Audio;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.IO;
using EraCore.Presentation;
using EraCore.Progression;
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

    /// <summary>T-087 结算演出脚本 id（victory.json；结算缝显式发射，无叙事钩子）。</summary>
    internal const string VictoryScriptId = "cinematic:victory-settlement";

    /// <summary>Smoke 固定 run seed（T-087 结算冒烟：判分+宝箱+转盘同流）。</summary>
    private const long VictorySmokeSeed = 20261014;

    /// <summary>
    /// 冒烟用确定性 IRng（内容驱动的结算冒烟需要一个可复现流——演出产品
    /// 代码自身永不抽随机，RL12；决策处各自 Reseed，同流可复现）。
    /// </summary>
    private sealed class SmokeRng : IRng
    {
        private ulong _state = (ulong)VictorySmokeSeed;

        public void Reseed(long seed) => _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            Step();
            int span = maxValue - minValue;
            return span <= 0
                ? minValue
                : minValue + (int)(UpperBound(_state) % (ulong)span);
        }

        public double NextDouble()
        {
            Step();
            return UpperBound(_state) / (double)ulong.MaxValue;
        }

        private void Step() => _state = _state * 6364136223846793005UL + 1442695040888963407UL;

        private static ulong UpperBound(ulong state) => state >> 11;
    }

    private SimHost _host = null!;
    private IFileStore _files = null!;
    private IReadOnlyList<CinematicScript> _scripts = Array.Empty<CinematicScript>();
    private CinematicHookRouter _router = null!;
    private CinematicProgress _progress = CinematicProgress.Default;
    private VictorySettlementArchive _victoryArchive = VictorySettlementArchive.Default;
    private EraCore.Cinematics.VisualAssetCatalog _catalog = null!;

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

    // T-152 三新步渲染载体：video_frame 全屏播片 / image_card 插画 / palette_shift 色板渐变
    private VideoStreamPlayer _videoFrame = null!;
    private TextureRect _imageCard = null!;
    private ColorRect _paletteOverlay = null!;
    private Color _paletteFrom;
    private Color _paletteTo;
    private ulong _paletteStartMsec;
    private bool _paletteActive;
    private ulong _paletteDurationMs;

    // ---- smoke evidence accumulators --------------------------------------
    private int _cueCount;
    private int _checkpoints;
    private int _lastLedgerRows;
    private string _lastSpeak = string.Empty;
    private string _lastOrderOutcome = "none";
    private readonly List<CinematicWorldCommand> _drainedCommands = new();

    // T-087 结算演出渲染/冒烟累计器
    private int _verdictRows;
    private int _chestRows = -1;
    private int _chestLightLayers = -1;
    private int _wheelLandedStop = -1;
    private string _settlementCommentary = string.Empty;
    private CinematicStep.WheelSpin? _spinning;
    private DecidedVictoryPerformance? _spinningDecided;

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
    /// per-level binding narrows when the C-29 level loader lands). The
    /// T-087 victory settlement script loads hookless (结算缝显式发射).
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
            CinematicScript.Parse(ReadFile(cinematicsDir, "victory.json"), "cinematics/victory.json"),
        };

        // T-152 ③d 时代跃迁过场 5 条 + 终章播片两版（transition 拍由战役宿主在
        // F-003 跃迁事务点发射；finale-zero 声明 e6-final 胜利拍钩子行
        // （levels/e6.json，T-136 F-108 触发位）——dev 宿主只装载 e1+e2 钩子
        // 行（逐关装载随 C-29 落地），路由绑定只收钩子行在装载集内的脚本；
        // finale-zero-off=关闭系统字幕版（FinalePlayback.ScriptIdFor 二选一，
        // 无钩子键——开关选择变体，同拍显式通道））。
        foreach (string transition in new[]
                 {
                     "transition-e1-e2", "transition-e2-e3", "transition-e3-e4",
                     "transition-e4-e5", "transition-e5-e6", "finale-zero",
                     "finale-zero-off",
                 })
        {
            scripts.Add(CinematicScript.Parse(
                ReadFile(cinematicsDir, $"{transition}.json"), $"cinematics/{transition}.json"));
        }

        _scripts = scripts;

        _catalog = EraCore.Cinematics.VisualAssetCatalog.Parse(
            ReadFile(cinematicsDir, "visual-assets.json"), "cinematics/visual-assets.json");

        var hookRows = new List<CinematicHookRow>();
        hookRows.AddRange(CinematicHookRouter.ParseLevelHooks(
            ReadFile(levelsDir, "e1.json"), "levels/e1.json"));
        hookRows.AddRange(CinematicHookRouter.ParseLevelHooks(
            ReadFile(levelsDir, "e2.json"), "levels/e2.json"));
        // T-136：钩子行不在 dev 装载集（e1+e2）的脚本不进路由——finale-zero 的
        // e6-final 胜利拍绑定与单发由 FinaleCinematicsTests 以 levels/e6.json 全
        // 行构造路由执法；dev 会话内 finale 走显式通道（回看/战役宿主发射）。
        // 过滤不静默（审查批 3 #3）：每个被移出路由的脚本留一行日志——未来
        // hook id 笔误会在这里显形，而不是无声丢演出。
        List<CinematicScript> routable = new(scripts);
        foreach (CinematicScript script in scripts)
        {
            if (script.HookId is not null
                && hookRows.All(row => row.HookId != script.HookId))
            {
                routable.Remove(script);
                GD.Print($"cinematics: 脚本 {script.Id} 的钩子 {script.HookId} 不在当前装载集——移出路由（显式通道/测试执法面覆盖）");
            }
        }
        _router = new CinematicHookRouter(hookRows, routable);
        _progress = CinematicProgressStore.Load(_files);
        _victoryArchive = VictorySettlementStore.Load(_files); // 结算状态入存档（重进不重播、可回看）
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
            CinematicBeat.Transition => _router.OnTransition(),
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

    /// <summary>
    /// 结算缝发射 (T-087)：胜利结算时刻由宿主（战役循环的 Victory 状态 +
    /// CampaignProgression.SettleVictory 之后）显式调用——既定演出先入存档
    /// （中途关游戏不丢），再开演。不占用关卡叙事钩子位（victory 拍叙事
    /// 属 T-089）；重进不重播：本通道只在活胜利时刻走，存档记录只供回看。
    /// </summary>
    /// <returns>The launched script id.</returns>
    internal string LaunchVictorySettlement(DecidedVictoryPerformance decided)
    {
        ArgumentNullException.ThrowIfNull(decided);
        CinematicScript script = ScriptById(VictoryScriptId);
        _router.MarkFired(script.Id); // 会话面记账一致（钩子位不重复认领）
        _victoryArchive = VictorySettlementStore.Upsert(
            _victoryArchive,
            new VictorySettlementRecord { LevelId = decided.LevelId, Decided = decided, Viewed = false });
        VictorySettlementStore.Save(_files, _victoryArchive); // 先存后演（RL12 持久面）
        return BeginPlayback(script, decided);
    }

    /// <summary>
    /// 结算回看：以存档里的既定演出重放（同字节）。宿主在结算回顾界面调用；
    /// 不改写既有记录（Viewed 保持），重放结束再标记。
    /// </summary>
    /// <returns>The launched script id, or null when nothing is settled yet.</returns>
    internal string? ReplayLatestSettlement(string levelId)
    {
        VictorySettlementRecord? record = VictorySettlementStore.LatestFor(_victoryArchive, levelId);
        return record is null ? null : BeginPlayback(ScriptById(VictoryScriptId), record.Decided);
    }

    /// <summary>长按加速（结算演出唯一快进通道，≤4×；松开回 1×）——不可全跳（F-002）。</summary>
    internal void HoldAccelerate(bool pressed)
    {
        if (_timeline is not null)
        {
            _timeline.SetBoost(pressed ? CinematicTimeline.MaxBoost : 1);
        }
    }

    private string BeginPlayback(CinematicScript script)
    {
        return BeginPlayback(script, settlement: null);
    }

    private string BeginPlayback(CinematicScript script, DecidedVictoryPerformance? settlement)
    {
        _timeline = settlement is null ? new CinematicTimeline(script) : new CinematicTimeline(script, settlement);
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

    /// <summary>
    /// Skips the running performance to its terminal state (演出可跳过)——
    /// 结算演出受保护（不可全跳）：RequestSkip 对其无效，长按加速是唯一
    /// 快进通道（§12 映射#10）。
    /// </summary>
    internal void RequestSkip()
    {
        if (_timeline is not { Phase: CinematicPhase.Playing or CinematicPhase.AwaitingChoice } timeline
            || !timeline.CanSkip)
        {
            return; // 保护演出：无全跳路径（演出继续，静默拒绝 UI 请求）
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
                case CinematicEffect.SettlementVerdict verdict:
                    ShowVerdictRows(verdict);
                    break;
                case CinematicEffect.SettlementTierRevealed tier:
                    ShowTierReveal(tier);
                    break;
                case CinematicEffect.WheelSpinStarted spin:
                    ShowWheelStart(spin);
                    break;
                case CinematicEffect.WheelLanded landed:
                    MarkWheelLanded(landed);
                    break;
                case CinematicEffect.SettlementChestOpened chest:
                    ShowChestOpen(chest);
                    break;
                case CinematicEffect.SettlementCommentarySpoken comment:
                    ShowSettlementCommentary(comment);
                    break;
                case CinematicEffect.ImageCardShown card:
                    ShowImageCard(card.ImageKey);
                    break;
                case CinematicEffect.VideoFrameStarted frame:
                    ShowVideoFrame(frame);
                    break;
                case CinematicEffect.PaletteShiftStarted shift:
                    StartPaletteShift(shift);
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
        StopVideoFrame(); // T-152：播片立即停并释放（18 号 §2.3 整文件驻内存）
        _imageCard.Visible = false;
        _paletteOverlay.Visible = false;
        _paletteActive = false;
        if (_playing is not null)
        {
            _progress = CinematicProgressStore.MarkCompleted(_progress, _playing.Id);
            CinematicProgressStore.Save(_files, _progress);
        }

        if (_playing is not null && _playing.Id == VictoryScriptId)
        {
            VictorySettlementRecord? record = _victoryArchive.Records.Count > 0
                ? _victoryArchive.Records[^1]
                : null;
            if (record is not null)
            {
                _victoryArchive = VictorySettlementStore.Upsert(
                    _victoryArchive, VictorySettlementStore.MarkViewed(record));
                VictorySettlementStore.Save(_files, _victoryArchive); // 回看完成态入存档
            }
        }
    }

    // ---- T-087 结算演出渲染（判分行/档位/转盘/宝箱/点评） ---------------------

    private static string TierZh(EraCore.Progression.ObjectiveTier tier) => tier switch
    {
        EraCore.Progression.ObjectiveTier.Bronze => "青铜",
        EraCore.Progression.ObjectiveTier.Silver => "白银",
        EraCore.Progression.ObjectiveTier.Gold => "黄金",
        EraCore.Progression.ObjectiveTier.GoldExtra => "黄金+额外",
        _ => tier.ToString(),
    };

    private static string RarityZh(EraCore.Progression.RewardRarity rarity) => rarity switch
    {
        EraCore.Progression.RewardRarity.Common => "普通",
        EraCore.Progression.RewardRarity.Rare => "稀有",
        EraCore.Progression.RewardRarity.Legendary => "传说",
        _ => rarity.ToString(),
    };

    private void ShowVerdictRows(CinematicEffect.SettlementVerdict verdict)
    {
        _verdictRows = verdict.Rows.Count;
        _surfaceRows.RemoveAllTypeChildren();
        _surfaceTitle.Text = "◆ 三重目标判定";
        foreach (VictoryVerdictRow row in verdict.Rows)
        {
            var line = new Label
            {
                Text = (row.Passed ? "✓ " : "✗ ") + row.LabelZh,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            line.AddThemeFontSizeOverride("font_size", 12);
            line.AddThemeColorOverride(
                "font_color", row.Passed ? new Color(0.29f, 0.86f, 0.45f) : HoloSkin.RiskRed);
            _surfaceRows.AddChild(line);
        }

        _surfaceBox.Show();
    }

    private void ShowTierReveal(CinematicEffect.SettlementTierRevealed tier)
    {
        _surfaceRows.RemoveAllTypeChildren();
        _surfaceTitle.Text = "◆ 档位揭示";
        var line = new Label
        {
            Text = $"{TierZh(tier.Tier)}（完成 {tier.CompletedCount}/3）· 宝箱 {tier.ChestTierId}",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        line.AddThemeFontSizeOverride("font_size", 14);
        line.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _surfaceRows.AddChild(line);
        _surfaceBox.Show();
    }

    private void ShowWheelStart(CinematicEffect.WheelSpinStarted spin)
    {
        _wheelLandedStop = -1;
        _surfaceRows.RemoveAllTypeChildren();
        _surfaceTitle.Text = "◆ 幸运转盘（结算 · 无空档）";
        DecidedVictoryPerformance? decided = _timeline?.Settlement;
        if (decided is not null)
        {
            for (int i = 0; i < decided.Wheel.Segments.Count; i++)
            {
                VictoryWheelSegment segment = decided.Wheel.Segments[i];
                var line = new Label
                {
                    Text = $"{(i + 1).ToString().PadLeft(2)} 段 {segment.LabelZh}（{RarityZh(segment.Rarity)}）",
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                line.AddThemeFontSizeOverride("font_size", 11);
                line.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
                _surfaceRows.AddChild(line);
            }
        }

        _spinning = new CinematicStep.WheelSpin(spin.DurationTicks, spin.Segments, spin.DecelTicks.ToArray());
        _spinningDecided = decided;
        _surfaceBox.Show();
    }

    private void MarkWheelLanded(CinematicEffect.WheelLanded landed)
    {
        _wheelLandedStop = landed.StopIndex;
        _spinning = null; // 到停：停帧（落点=既定结果，演出倒推）
        int row = 0;
        foreach (Node child in _surfaceRows.GetChildren())
        {
            if (child is Label label)
            {
                label.Text = row == landed.StopIndex
                    ? "▶ " + label.Text.Substring(2) + $"  ← 落点·{landed.ItemId}"
                    : label.Text;
                if (row == landed.StopIndex)
                {
                    label.AddThemeColorOverride("font_color", new Color(0.98f, 0.84f, 0.32f));
                }

                row++;
            }
        }
    }

    private void ShowChestOpen(CinematicEffect.SettlementChestOpened chest)
    {
        _chestRows = chest.Items.Count;
        _chestLightLayers = chest.LightLayers;
        _surfaceRows.RemoveAllTypeChildren();
        _surfaceTitle.Text = $"◆ 宝箱开箱 · {chest.ChestTierId}（光效 {chest.LightLayers} 层）";
        for (int layer = 0; layer < chest.LightLayers; layer++)
        {
            // 光效分层：每层一道渐弱的辉光条（黄金档恰比白银多一层——视觉可辨）
            var glow = new ColorRect
            {
                Color = new Color(0.98f, 0.84f, 0.32f, 0.16f * (chest.LightLayers - layer)),
                CustomMinimumSize = new Vector2(240f, 3f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _surfaceRows.AddChild(glow);
        }

        foreach (GrantedChestItem item in chest.Items)
        {
            var line = new Label
            {
                Text = $"· {item.ItemId}（{RarityZh(item.Rarity)}）",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            line.AddThemeFontSizeOverride("font_size", 11);
            line.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
            _surfaceRows.AddChild(line);
        }

        _surfaceBox.Show();
    }

    private void ShowSettlementCommentary(CinematicEffect.SettlementCommentarySpoken comment)
    {
        string speaker = $"系统（{SystemPersonas.LabelZh(comment.Commentary.Persona)}·{TrustBadgeLabels.Label(comment.Commentary.Badge)}）";
        string text = comment.Commentary.TrustShiftEcho is { } echo
            ? comment.Commentary.MainLine + " " + echo
            : comment.Commentary.MainLine;
        _settlementCommentary = text;
        _lastSpeak = text;
        _subtitle.Text = $"{speaker}：{text}";
        _subtitle.Visible = true;
    }

    /// <summary>
    /// 帧循环：转盘指针按 WheelDynamics 纯函数游走（演出倒推——渲染只算
    /// 角度，不算结果）；Ctrl 长按=加速 4×、松开回 1×（结算演出无全跳）。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_spinning is { } spin && _spinningDecided is { } decided && _timeline is { } timeline)
        {
            double angle = WheelDynamics.PointerAngle(
                spin.Segments, decided.Wheel.StopIndex, spin.DurationTicks, spin.DecelTicks,
                timeline.ElapsedTicks);
            _surfaceTitle.Text = $"◆ 幸运转盘（结算）· 指针 {angle:0.0}°（长按 Ctrl 加速）";
        }

        if (_timeline is { CanSkip: false })
        {
            HoldAccelerate(Input.IsKeyPressed(Key.Ctrl)); // 长按加速（不可全跳）
        }
        else if (_timeline is not null && _timeline.BoostFactor != 1 && !Input.IsKeyPressed(Key.Ctrl))
        {
            HoldAccelerate(false);
        }

        // T-152 palette_shift 渐变推进（表现层时基；到端点后停在 To 色）
        if (_paletteActive)
        {
            ulong elapsed = Time.GetTicksMsec() - _paletteStartMsec;
            float t = _paletteDurationMs == 0
                ? 1f
                : MathF.Min(1f, elapsed / (float)_paletteDurationMs);
            _paletteOverlay.Color = new Color(_paletteFrom.Lerp(_paletteTo, t), 0.32f);
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

    // ---- T-152 三新步渲染（image_card / video_frame / palette_shift） ----------

    /// <summary>
    /// image_card：素材键→插画贴图（外部轨 D21-5），缺图自动降级——卡片隐藏、
    /// 字幕通道提示（31 号 §三「缺图自动降级纯文本」）。
    /// </summary>
    private void ShowImageCard(string imageKey)
    {
        Texture2D? texture = EraTD.Expand.ExpandAssetPaths.LoadPng(
            EraTD.Expand.ExpandAssetPaths.ExternalImagePath(_catalog, imageKey));
        if (texture is null)
        {
            _subtitle.Text = $"◈ 〔{imageKey}〕（插画缺失·降级为文本）";
            _subtitle.Visible = true;
            _imageCard.Visible = false;
            return;
        }

        _imageCard.Texture = texture;
        _imageCard.Visible = true;
    }

    /// <summary>
    /// video_frame：ogv 全屏播片（内置轨绝对路径，18 号 C7 VideoStreamTheora）。
    /// Web 静帧降级（D21-2）：素材缺失时黑屏+字幕提示，演出按拍数推进。
    /// </summary>
    private void ShowVideoFrame(CinematicEffect.VideoFrameStarted frame)
    {
        string? path = EraTD.Expand.ExpandAssetPaths.OgvPath(_catalog, frame.AssetId);
        if (path is null)
        {
            _subtitle.Text = $"◈ 〔{frame.AssetId}〕（视频缺失·静帧降级）";
            _subtitle.Visible = true;
            return;
        }

        _videoFrame.Stream = new VideoStreamTheora { File = path };
        _videoFrame.VolumeDb = frame.Skippable ? -6f : -3f; // A/D/E 原声压低让位 BGM
        _videoFrame.Visible = true;
        _videoFrame.Play();
    }

    private void StopVideoFrame()
    {
        _videoFrame.Stop();
        _videoFrame.Stream = null;
        _videoFrame.Visible = false;
    }

    /// <summary>
    /// palette_shift：时代色板层积渐变（31 号 §1.3——终章回环 E6→E1 的载体）。
    /// 端点色来自素材目录色板锚；进度按真实毫秒推进（表现层时基，不入 tick 域）。
    /// </summary>
    private void StartPaletteShift(CinematicEffect.PaletteShiftStarted shift)
    {
        _paletteFrom = EraColorOf(shift.FromEra);
        _paletteTo = EraColorOf(shift.ToEra);
        _paletteDurationMs = (ulong)(shift.DurationTicks * 1000L / 30); // 30 tick/s 时域换算（K-1）
        _paletteStartMsec = Time.GetTicksMsec();
        _paletteActive = true;
        _paletteOverlay.Color = new Color(_paletteFrom, 0.32f);
        _paletteOverlay.Visible = true;
    }

    private Color EraColorOf(string eraKey)
    {
        (int r, int g, int b) = _catalog.Palettes[eraKey].Primary;
        return new Color(r / 255f, g / 255f, b / 255f);
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

        // T-152 三新步载体（顺序=叠层：播片最底、色板纱、插画居中）
        _videoFrame = new VideoStreamPlayer
        {
            Expand = true,
            Visible = false,
            VolumeDb = -6f,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _videoFrame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_videoFrame);

        _paletteOverlay = new ColorRect
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _paletteOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_paletteOverlay);

        _imageCard = new TextureRect
        {
            Visible = false,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _imageCard.SetAnchorsPreset(Control.LayoutPreset.Center);
        _imageCard.Size = new Vector2(480f, 480f);
        _imageCard.Position = new Vector2(160f, 60f);
        layer.AddChild(_imageCard);
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

        // ---- D) T-087 结算演出：真实判分+真宝箱→先定后演→4× 加速过转盘→ ----
        //         落点=既定→回看同字节→无全跳（RequestSkip 静默拒绝）。
        string campaignDir = FindContentDir(
            System.IO.Path.Combine("game", "content", "campaign"), "game/content/campaign");
        var judge = new TripleObjectiveJudge(
            ObjectivesData.Parse(ReadFile(campaignDir, "objectives.json")),
            new SmokeRng());
        var chests = new ChestRewards(
            ChestRewardsData.Parse(ReadFile(campaignDir, "chests.json")),
            new SmokeRng());
        ObjectiveOutcome outcome = judge.Judge(
            "campaign:level-e1-01", 0.5, new LevelRunFacts(Survived: true));
        string tierId = CampaignProgression.TierToChestId(outcome.Tier);
        var smokeSettlement = new CampaignSettlement(
            "campaign:level-e1-01", outcome, tierId, chests.Open(tierId, VictorySmokeSeed),
            new List<EraCore.CampaignLoop.ResourceGrant>());
        CinematicStep.WheelSpin? smokeWheelFound = null;
        foreach (CinematicStep step in ScriptById(VictoryScriptId).Steps)
        {
            if (step is CinematicStep.WheelSpin found)
            {
                smokeWheelFound = found;
                break;
            }
        }

        DecidedVictoryPerformance smokeDecided = VictorySettlement.Decide(
            smokeSettlement, chests.Table, new SmokeRng(), smokeWheelFound!.Segments,
            VictorySmokeSeed, SystemPersona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy);
        byte[] smokeDecidedBytes = VictorySettlementStore.Encode(smokeDecided);

        string victoryLaunched = LaunchVictorySettlement(smokeDecided);
        bool victorySkipRefused;
        {
            RequestSkip(); // 不可全跳：结算演出受保护，静默拒绝（无"全跳"按钮路径）
            victorySkipRefused = _timeline is { Phase: CinematicPhase.Playing };
        }

        HoldAccelerate(true); // 长按加速 4×：唯一快进通道
        guard = 0;
        while (Playing && guard++ < 3600)
        {
            PumpOneTick();
        }

        HoldAccelerate(false);
        bool victoryCompleted = _timeline is { Phase: CinematicPhase.Completed, Skipped: false };
        bool victoryFloored = _timeline is { } done && done.ElapsedTicks >= 45; // 至少播放 1.5s@30Hz
        bool victoryLandedOnDecision = _wheelLandedStop == smokeDecided.Wheel.StopIndex;
        bool victoryChestRendered = _chestRows == smokeDecided.ChestItems.Count
            && _chestLightLayers == smokeDecided.ChestLightLayers;
        bool victoryVerdictRendered = _verdictRows == 3;
        bool victoryCommentaryRendered = _settlementCommentary == smokeDecided.Commentary.MainLine;
        VictorySettlementRecord? viewedRecord = VictorySettlementStore.LatestFor(
            _victoryArchive, "campaign:level-e1-01");
        bool victoryMarkedViewed = viewedRecord is { Viewed: true }
            && VictorySettlementStore.Encode(viewedRecord.Decided).SequenceEqual(smokeDecidedBytes);

        // 回看：以存档里的既定演出重放（同字节同落点）
        _wheelLandedStop = -1;
        string? replayed2 = ReplayLatestSettlement("campaign:level-e1-01");
        HoldAccelerate(true);
        guard = 0;
        while (Playing && guard++ < 3600)
        {
            PumpOneTick();
        }

        HoldAccelerate(false);
        bool victoryReplaySame = _wheelLandedStop == smokeDecided.Wheel.StopIndex
            && VictorySettlementStore.Encode(
                VictorySettlementStore.LatestFor(_victoryArchive, "campaign:level-e1-01")!.Decided)
                .SequenceEqual(smokeDecidedBytes);
        bool victoryNoAutoReplay = LaunchByHook(CinematicBeat.Victory) is null; // 重进不重播：victory 拍无自动结算重放

        GD.Print(
            $"CINE_OK aha=skip-safe awaken=3choice steps_aha={ahaSteps} steps_awaken={awakenSteps} " +
            $"aha_world_writes=0 declared_world_writes={_host.Simulation.AppliedCommandCount - commandsBefore} " +
            $"choices={choiceOptions} towers={towersBefore}->{_host.Simulation.Towers.Count} " +
            $"order_outcome={_lastOrderOutcome} wood={_host.Simulation.Stock.AmountOf(SimHost.Wood):0.#} " +
            $"cues={_cueCount} ledger={_lastLedgerRows} checkpoints={_checkpoints} " +
            $"hook_fire_twice_silent={replayBlocked is null} replay_channel={replayed} " +
            $"marked={ahaMarked && awakenMarked} skip_equivalent={awakenSkipEquivalent} " +
            $"victory=decided-before-shown tier={tierId} stop={smokeDecided.Wheel.StopIndex} " +
            $"chest_items={smokeDecided.ChestItems.Count} light_layers={smokeDecided.ChestLightLayers} " +
            $"boost_floor_held={victoryFloored} no_full_skip={victorySkipRefused} " +
            $"landed_on_decision={victoryLandedOnDecision} replay_same_bytes={victoryReplaySame} " +
            $"no_auto_replay={victoryNoAutoReplay} viewed_marked={victoryMarkedViewed} " +
            $"replay2={replayed2} " +
            $"hash={_host.Simulation.ComputeStateHash()}");

        bool ok = ahaId == "cinematic:e1-aha" && awakenId == "cinematic:e2-awaken"
            && ahaCompleted && ahaZeroCommands && ahaSounded && ahaCheckpointed
            && ahaMarked && replayBlocked is null && ahaSkipSafe
            && awakenCompleted && awakenThreeChoice && awakenBuilt && awakenLedger
            && awakenHook && awakenMarked && awakenSkipEquivalent
            && victoryLaunched == VictoryScriptId && victoryCompleted && victoryFloored
            && victorySkipRefused && victoryLandedOnDecision && victoryChestRendered
            && victoryVerdictRendered && victoryCommentaryRendered && victoryMarkedViewed
            && victoryReplaySame && victoryNoAutoReplay && replayed2 == VictoryScriptId;
        if (!ok)
        {
            GD.PushError(
                "cinematic wiring broken: " +
                $"aha={ahaId}/{ahaCompleted}/{ahaZeroCommands}/{ahaSounded}/{ahaCheckpointed}/{ahaMarked}/{ahaSkipSafe} " +
                $"awaken={awakenId}/{awakenCompleted}/{awakenThreeChoice}/{awakenBuilt}/{awakenLedger}/{awakenHook}/{awakenMarked}/{awakenSkipEquivalent} " +
                $"victory={victoryLaunched}/{victoryCompleted}/{victoryFloored}/{victorySkipRefused}/" +
                $"{victoryLandedOnDecision}/{victoryChestRendered}/{victoryVerdictRendered}/" +
                $"{victoryCommentaryRendered}/{victoryMarkedViewed}/{victoryReplaySame}/{victoryNoAutoReplay} " +
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
