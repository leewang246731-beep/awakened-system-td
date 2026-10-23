using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Presentation;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Units;
using Godot;
using Persona = EraCore.Cinematics.SystemPersona;

namespace EraTD;

// T-160 (C-29/F-005/F-001) 战役宿主脸——表现层战役装载与推进的最小总面：
//
//   入口+菜单 = 「战役」按钮 → 章节分组关卡列表（levels/e1~e6.json 全 29 行
//     + campaign 进度态：未解锁/可玩/已通关★）；进度态三源=CampaignState
//     时代位（K-7 campaign 文档）+ 胜利结算档案占领集 + LegacyLedger 奇观位
//     （e6-final 通关之门 requires_wonder 数据行）。EndlessMenuUi/
//     ProceduralMenuUi 同构入口。
//   真装载 = HandcraftedLevelCatalog.Parse（T-160 提升的生产装载器）→
//     EraLevelAssembly.BuildConfig/BuildCommands（生产组装律：E1~E4 践踏
//     调参族+E3/E4/E5 能源表挂载）→ LevelSessionConfig → LevelSession
//     （TryResume 中途续玩/新开局两路）——补平「玩家从 UI 玩全战役」的装载
//     缝；SimHost 保留为 headless 冒烟夹具（G4 既有段不破——边界声明见
//     REV-20261018-T160）。
//   推进 = 胜利结算（CampaignProgression.SettleVictory 判分/开箱/占领 +
//     VictorySettlement.Decide 先定后演入 VictorySettlementStore——★=三重
//     目标通过数）→ 下一关解锁；E1~E5 final 胜利 → 时代奇观位 +
//     EraTransitionService 原子事务（30% levy/遗迹映射/campaign 域一次写
//     ——CityFacts=终局关真实存续建筑，非合成校准）→ CampaignState.
//     AdvanceEra → 跃迁过场播片钩子（transition-eX-eY）→ 下一时代章节
//     解锁；e6-final 胜利 → 终章播片二选一（FinalePlayback.ScriptIdFor
//     按系统开关）→ 制作组/通关总结。
//   存档续玩 = 退出到菜单：LevelSession.SaveAndExitToMenu（level 域）+
//     campaign 域合并写（保留 legacy ledger 载荷/遗迹登记/解锁族——不清
//     账）；重进：读档→时代/池/奇观/占领全恢复 + TryResume 无损续关。
//   遥测 = LevelSessionConfig.Telemetry + LevelDurationTelemetry 双注入
//     （T-155/157 留缝收口——战役真人局 source=player 自动入账）。
//
// 承载面纪律（NetMenuUi/EndlessMenuUi/ProceduralMenuUi 同律）：headless 自带
// 冒烟（全 29 关装载矩阵+UI 构造的 E1-01 真通关+解锁推进+退出重进续玩+遥测
// 入账+跃迁负向探针+终章二选一），打印 CAMPAIGN_OK（任一自检不过
// Quit(1)）；窗口运行=面板选关开局，帧泵推进，胜利/跃迁演出经 CinematicDirector
// 显式通道开演，退出=诚实存档。E1→E2 连续推进链（含真跃迁+E2-01 开局）的
// 完整实证在 EraCore.Tests/CampaignHostProgressionChainTests——同一生产组装
// 路径（EraLevelAssembly），G1 门禁承载。

/// <summary>
/// The T-160 campaign host face: entry button, chapter-grouped level menu,
/// real level loading through the production assembly, victory progression,
/// era transitions, the finale pick, save/resume and the telemetry lane.
/// </summary>
public sealed partial class CampaignMenuUi : Control
{
    private readonly bool _headless;

    private Button _entryButton = null!;
    private Control _panelRoot = null!;
    private ScrollContainer _panel = null!;
    private VBoxContainer _list = null!;
    private Label _statusLabel = null!;
    private readonly Dictionary<string, Button> _levelButtons = new();

    // —— T-162 选关卡片化（banner 缩略图装载：目录清单一次、贴图按文件名缓存）——
    private IReadOnlyList<string>? _bannerFiles;
    private readonly Dictionary<string, Godot.ImageTexture?> _bannerCache = new();
    private Button? _firstCard; // 首卡引用（--shot 02-level-cards 取景探针）
    private Button _menuReturnButton = null!; // 「返回主菜单」承载钮（--shot 真信号驱动位）
    private Button _exitButton = null!; // 「退出本关」承载钮（T-163 HUD ✕ 钮真信号驱动位）
    private CanvasLayer _panelLayer = null!; // 面板层（Layer 90——盖夹具/信息层，让位主菜单 100）

    // —— T-160c 失败面板（败绩分档呈现+三行动）——
    private PanelContainer _defeatPanel = null!;
    private Label _defeatBody = null!;
    private Button _defeatContinue = null!;
    private Button _defeatRestart = null!;
    private Button _defeatMenu = null!;

    // —— 活动战役（窗口车道真机局面；headless 冒烟走沙盒根实例）——
    private CampaignLevelHost _host = null!;

    // —— T-160b 战场渲染绑定（REV §七遗留③收口）——
    // 视口=会话泳道自持渲染件（NetClientViewport 先例）；军师面板随开局重绑
    // 到战役板（CampaignAdvisorBoard），退出还原夹具板；夹具双层在局内隐藏、
    // 退出恢复（同层不叠画——恢复菜单态口径）。
    private Main? _main;
    private SessionBattlefieldViewport? _viewport;
    private CampaignAdvisorBoard? _board;
    private bool _fixtureHidden;

    private CampaignMenuUi()
    {
        _headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
    }

    /// <summary>Code-built instantiation (no .tscn — NetMenuUi 同律).</summary>
    public static CampaignMenuUi Instantiate(Control host)
    {
        var ui = new CampaignMenuUi
        {
            Name = "CampaignMenuUi",
        };
        ui.BuildUi();
        var playerStore = new DevFileStore(); // D-03 真根（campaign/level/结算/遥测全落玩家根）
        ui._host = CampaignLevelHost.Create(
            playerStore,
            new JsonFileSavePort(playerStore, "campaign"),
            new LevelDurationTelemetry(playerStore, new EndlessHostContent.WallClock()),
            TelemetrySources.Player); // 真人局 source=player（T-155 口径）
        ui._main = host as Main;
        host.AddChild(ui);
        if (ui._headless)
        {
            ui.RunHeadlessSmoke();
        }

        return ui;
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        _entryButton = new Button
        {
            Text = "战役",
            Position = new Vector2(668, 8),
            Size = new Vector2(80, 30),
        };
        _entryButton.Pressed += () =>
        {
            SetPanelVisible(!_panelRoot.Visible);
            RefreshMenu();
        };
        AddChild(_entryButton);
        if (!_headless)
        {
            _entryButton.Hide(); // T-161 入口归拢：窗口态入口在主菜单（headless 冒烟照旧）
        }

        // T-162 选关卡片化：面板=全画布面板根（压暗底+滚动卡片场），整体挂
        // CanvasLayer 90——盖过夹具战场与信息层浮显（分区命名/提示气泡是
        // 菜单态噪音），仍让位主菜单（Layer 100）。面板开合语义挂在
        // _panelRoot（PanelOpen 探针随之），640×360 画布域整幅利用。
        _panelLayer = new CanvasLayer { Layer = 90, Name = "CampaignPanelLayer" };
        AddChild(_panelLayer);
        _panelRoot = new Control { Name = "CampaignPanelRoot", Visible = false };
        _panelRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panelRoot.MouseFilter = Control.MouseFilterEnum.Stop;
        var backdrop = new ColorRect { Color = new Color(0.02f, 0.03f, 0.05f, 0.96f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panelRoot.AddChild(backdrop);

        _panel = new ScrollContainer
        {
            // 画布 640×360：左右留 8px 边、上 22（回主菜单钮带）、下 6。
            Position = new Vector2(8, 22),
            Size = new Vector2(624, 332),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        var headerTitle = new Label
        {
            Text = "战役 · 六时代（章节题图+关卡卡片）",
            CustomMinimumSize = new Vector2(300, 0),
        };
        UiFeel.Outline(headerTitle, 3); // T-164：面板标题带描边（可读性同律）
        header.AddChild(headerTitle);
        var exitButton = new Button { Text = "退出本关（存档续玩）" };
        StyleHeaderButton(exitButton);
        exitButton.Pressed += OnExitPressed;
        _exitButton = exitButton;
        header.AddChild(exitButton);
        var menuButton = new Button { Text = "返回主菜单" };
        StyleHeaderButton(menuButton);
        menuButton.Pressed += CloseFromMenu; // T-161：会话退出→回主菜单（承载钮）
        _menuReturnButton = menuButton;
        header.AddChild(menuButton);
        rows.AddChild(header);

        _list = new VBoxContainer();
        rows.AddChild(_list);
        _statusLabel = new Label
        {
            Text = "未开局",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(596, 40),
        };
        rows.AddChild(_statusLabel);
        _panel.AddChild(rows);
        _panelRoot.AddChild(_panel);
        _panelLayer.AddChild(_panelRoot);

        // T-160c 失败面板（覆盖菜单列表之上——败北时刻的面板态）。
        _defeatPanel = new PanelContainer
        {
            Position = new Vector2(170, 140),
            Size = new Vector2(430, 230),
            Visible = false,
        };
        var defeatRows = new VBoxContainer();
        defeatRows.AddChild(new Label { Text = "◆ 败北 · 失败分档" });
        _defeatBody = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(410, 110),
        };
        defeatRows.AddChild(_defeatBody);
        var defeatActions = new HBoxContainer();
        _defeatContinue = new Button { Text = "从检查点继续" };
        StyleHeaderButton(_defeatContinue); // T-164：败北面板行动排三态+音效（同选关面）
        _defeatContinue.Pressed += () =>
        {
            _ = _host.ContinueFromCheckpoint();
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatContinue);
        _defeatRestart = new Button { Text = "重开本关" };
        StyleHeaderButton(_defeatRestart);
        _defeatRestart.Pressed += () =>
        {
            _host.RestartLevel();
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatRestart);
        _defeatMenu = new Button { Text = "退回菜单" };
        StyleHeaderButton(_defeatMenu);
        _defeatMenu.Pressed += () =>
        {
            _host.DismissDefeatToMenu();
            SetPanelVisible(true); // T-163：败北「退回菜单」=重开选关面板（进局态面板已收）
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatMenu);
        defeatRows.AddChild(defeatActions);
        _defeatPanel.AddChild(defeatRows);
        _panelLayer.AddChild(_defeatPanel); // 同层尾序=面板根之上（败北面板压选关场）
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_headless)
        {
            return;
        }

        if (_host.PumpWindowFrame())
        {
            RefreshMenu(); // 胜利/败北落地后菜单立即反映新进度态
        }

        DrainPendingLaunches(); // 每帧排空：beat 驱动的剧情钩子/结算/收束/跃迁即时开演

        PumpBattlefield(); // T-160b：战场渲染+军师面板绑定（开局重绑/退出还原）

        RefreshDefeatPanel(); // T-160c：失败面板随败北事实在位/撤下

        if (_host.InLevel && Engine.GetProcessFrames() % 30 == 0)
        {
            RefreshStatus();
        }
    }

    /// <summary>失败面板刷新（T-160c 败绩分档呈现）：败北事实在位即呈现分档+残骸账+三行动（检查点不足=仅重开/退回）.</summary>
    private void RefreshDefeatPanel()
    {
        DefeatPanelFacts? defeat = _host.PendingDefeat;
        if (defeat is null)
        {
            _defeatPanel.Hide();
            return;
        }

        _defeatBody.Text =
            $"{defeat.LevelNameZh}（{defeat.LevelId}）——{defeat.Reason}\n" +
            $"档位：普通关失败（检查点波={defeat.CheckpointWave}）· " +
            (defeat.RestoredFromWave > 0
                ? $"检查点恢复至第 {defeat.RestoredFromWave} 波（tick {defeat.RestoredAtTick}）"
                : "未跨检查点波（从头重开）") +
            $"\n残骸修复账：{defeat.WreckCount} 条 · {defeat.RepairCostPoints:0.##} 点" +
            (defeat.FirstDefeat ? "（首败 ×0.8 已折）" : string.Empty);
        _defeatContinue.Visible = defeat.ContinueAvailable; // 检查点≥10 波才有继续档（恢复波>0 ⟺ 已跨）
        _defeatPanel.Show();
    }

    // ------------------------------------------ T-160b 战场渲染绑定（窗口车道）

    /// <summary>
    /// 战场绑定泵（每帧幂等）：局内=视口 Bind 到活动会话（sim 引用变化即整
    /// 面重建——关卡切换/跃迁重进再重绑律）+ 脏块渲染；HUD 三指标从战役板
    /// 活体刷新。退出到菜单=军师板 Dispose+视口 Release+夹具双层恢复+面板
    /// 还原夹具板（恢复菜单态——夹具画面/菜单覆盖口径，REV §七遗留③）。
    /// </summary>
    private void PumpBattlefield()
    {
        if (_host.InLevel && _host.Current is { } run)
        {
            EnsureViewport().Bind(run.Session, EraLevelAssembly.EraIndexOf(run.Level));
            EnsureViewport().PumpFrame();
            if (_board is null)
            {
                _board = _host.BuildAdvisorBoard(run);
                _main?.AdvisorPanel.Bind(_board); // 军师面板绑战役会话（BuildPlan 等经会话通道）
                SetFixtureHidden(true);
            }

            // T-163（C 批战斗 HUD 图标化）：资源条/预告横幅/节奏按钮/军师皮肤
            // 随会话绑定（双条件幂等——Bind 内部 sim+era 双比）。
            _main?.BattleHud?.Bind(run, EraLevelAssembly.EraIndexOf(run.Level));
            PumpBattleAudio(run); // T-161 遗留收口：会话 BGM 战况接线（战斗态≠calm）

            if (_main is not null && Engine.GetProcessFrames() % 15 == 0)
            {
                _main.AdvisorPanel.RefreshHud(_board.BuildHudSnapshot()); // HUD 面板随战役会话
                _main.BattleHud?.RefreshPulse(_board); // 资源数字+预告横幅+节奏镜像（同拍）
            }
        }
        else if (_board is not null || _viewport is { IsBound: true })
        {
            _board?.Dispose();
            _board = null;
            _viewport?.Release();
            SetFixtureHidden(false);
            _main?.BattleHud?.Release(); // T-163：图标 HUD 整组收起+军师面板还原菜单态
            GetNodeOrNull<Audio.AudioDirector>("../AudioDirector")?.ResetSessionEnemies(); // BGM 回夹具事实
            _audioSessionEnemies = -1;
            _audioEra = 0; // 会话音频路由位随局清零（下次进局重路由）
            if (_main is not null)
            {
                _main.AdvisorPanel.Bind(_main.BattleHost); // 还原夹具板（菜单态）
            }
        }
    }

    // —— T-161 遗留收口（T-163 半径内顺手项）：会话 BGM 战况接线 ——

    /// <summary>BGM 会话敌数记忆（-1=无会话，菜单态回落夹具事实）.</summary>
    private int _audioSessionEnemies = -1;

    /// <summary>战役会话 BGM 时代路由位（0=未路由）.</summary>
    private int _audioEra;

    /// <summary>
    /// 会话 BGM 战况泵（每帧）：①敌数注入（AudioDirector 态机的会话事实——
    /// 夹具在菜单态不步进，恒 calm 的既有面在此接通；有敌=Combat/Boss）；
    /// ②boss 波经内容 id 约定（-boss 尾在场）路由 SetBossWave；③关时代
    /// 变化=BGM 时代+语音时代双路由（六行齐——T-157）。
    /// </summary>
    private void PumpBattleAudio(CampaignRun run)
    {
        Audio.AudioDirector? audio = GetNodeOrNull<Audio.AudioDirector>("../AudioDirector");
        if (audio is null)
        {
            return;
        }

        int era = EraLevelAssembly.EraIndexOf(run.Level);
        if (_audioEra != era)
        {
            _audioEra = era;
            audio.SetActiveEra($"era{era}"); // BGM 六行全合法（T-157 十八循环）
            // T-164 实测收口：音色表只覆盖 era1~era4（16 行封闭集，T-126 口径）
            // ——era5/era6 会话 SetVoiceEra 抛 ArgumentException，会把本节点
            // _Process 后半截（HUD 每 15 帧刷新等）逐帧打断。守卫=表覆盖才
            // 路由语音时代；era5/6 语音沿用上一合法时代（REV 登记）。
            if (EraCore.Audio.PersonaTimbreTable.Eras.Contains($"era{era}", StringComparer.Ordinal))
            {
                audio.SetVoiceEra($"era{era}");
            }
        }

        int enemies = run.Session.Sim.Units.Count;
        if (_audioSessionEnemies != enemies)
        {
            _audioSessionEnemies = enemies;
            audio.ObserveSessionEnemies(enemies);
        }

        bool bossLive = run.Session.Sim.Units.Any(unit =>
            unit.ContentId.EndsWith("-boss", StringComparison.Ordinal));
        audio.SetBossWave(bossLive);
    }

    private SessionBattlefieldViewport EnsureViewport() =>
        _viewport ??= SessionBattlefieldViewport.Instantiate(this);

    /// <summary>夹具双层隐藏/恢复（战役局内同层不叠画；退出恢复菜单态）.</summary>
    private void SetFixtureHidden(bool hidden)
    {
        if (_fixtureHidden == hidden)
        {
            return;
        }

        _fixtureHidden = hidden;
        MultiMeshRenderer? ground = GetParent()?.GetNodeOrNull<MultiMeshRenderer>("GroundLayer");
        MultiMeshRenderer? units = GetParent()?.GetNodeOrNull<MultiMeshRenderer>("UnitLayer");
        if (ground is null || units is null)
        {
            return; // 非主场景挂载（独立测试树）——无夹具层可管
        }

        if (hidden)
        {
            ground.Hide();
            units.Hide();
        }
        else
        {
            ground.Show();
            units.Show();
        }
    }

    // T-160c 串行放演待演队（窗口车道）：帧泵每帧排空宿主队入此，导演空闲时逐场开演。
    private readonly Queue<CinematicLaunch> _launchQueue = new();

    /// <summary>
    /// 待演队列排空（窗口车道）：宿主入队的演出**串行放演**——上一场放完
    /// （导演非 Playing）再开下一场。T-160 原实现同一帧逐个 Launch，
    /// BeginPlayback 互相覆盖时间轴（[结算,收束,跃迁] 只剩最后一场）——
    /// 「收束→跃迁」发射序（T-160c）在窗口车道因此失义，本任务收口。
    /// </summary>
    private void DrainPendingLaunches()
    {
        Cinematics.CinematicDirector? director = GetNodeOrNull<Cinematics.CinematicDirector>("../CinematicDirector");
        foreach (CinematicLaunch launch in _host.DrainPendingLaunches())
        {
            if (director is null)
            {
                continue; // 无导演（独立测试树）——宿主队列照常排空，不开演
            }

            _launchQueue.Enqueue(launch);
        }

        if (director is null || director.Playing || _launchQueue.Count == 0)
        {
            return;
        }

        CinematicLaunch next = _launchQueue.Dequeue();
        if (next.Settlement is { } decided)
        {
            _ = director.LaunchVictorySettlement(decided); // 先定后演（结算缝）
        }
        else
        {
            _ = director.Launch(next.ScriptId); // 显式通道（钩子路由/收束/跃迁/终章）
        }
    }

    private void OnLevelPressed(string levelId)
    {
        if (_host.InLevel)
        {
            return; // 一屏一关：进行中不再开新局（退出或通关后再选）
        }

        _host.BeginLevel(levelId);
        SetPanelVisible(false); // T-163：进局收选关面板——战场+战斗 HUD 呈现（退出链=HUD ✕ 钮）
        RefreshStatus();
    }

    private void OnExitPressed()
    {
        if (!_host.InLevel)
        {
            return;
        }

        _host.ExitToMenu(); // Pause→SaveAndExitToMenu+campaign 域合并写（诚实存档续玩面）
        SetPanelVisible(false); // T-161：会话退出→回主菜单（面板收起，谓词翻真）
        RefreshMenu();
    }

    /// <summary>「退出本关」真信号驱动（T-163 战斗 HUD ✕ 钮/--shot 编排——与玩家点击同一条链）.</summary>
    internal void PressExitLevel() => _exitButton.EmitSignal(Button.SignalName.Pressed);

    // ------------------------------------------- T-161 主菜单入口归拢（窗口车道）

    /// <summary>主菜单「战役」入口打开面板（headless 冒烟不走此路）.</summary>
    internal void OpenFromMenu()
    {
        SetPanelVisible(true);
        RefreshMenu();
    }

    /// <summary>
    /// 返回主菜单：诚实收束一切战役态（败北面板在→按其菜单语义收下；局内
    /// →存档退出）后收面板——「会话退出→回主菜单」链路的战役半边。
    /// </summary>
    internal void CloseFromMenu()
    {
        if (_host.PendingDefeat is not null)
        {
            _host.DismissDefeatToMenu();
        }
        else if (_host.InLevel)
        {
            _host.ExitToMenu();
        }

        SetPanelVisible(false);
        _defeatPanel.Hide();
        RefreshMenu();
    }

    /// <summary>「返回主菜单」承载钮真信号驱动（--shot 验收钩子——与玩家点击同一条链）.</summary>
    internal void PressMenuReturn() => _menuReturnButton.EmitSignal(Button.SignalName.Pressed);

    /// <summary>
    /// 关卡卡真信号驱动（T-163 --shot-plan battle-hud 验收钩子——与玩家点击
    /// 同一条链：EmitSignal(Pressed) → OnLevelPressed → BeginLevel）。卡片
    /// 不在树（面板未开/已进局）返回 false，编排层如实跳步。
    /// </summary>
    internal bool PressLevel(string levelId) =>
        _levelButtons.TryGetValue(levelId, out Button? card) && card is not null
        && (_levelButtons[levelId].EmitSignal(Button.SignalName.Pressed) == Error.Ok);

    // —— T-162 卡片版式常量（640×360 画布域；表现层排版常量，非平衡数值） ——
    private const int ChapterGridColumns = 5;   // 最宽章=5 关（e1/e3~e6）一行铺满
    private const float ChapterBannerWidth = 612f;
    private const float ChapterBannerHeight = 52f;
    private const float CardWidth = 118f;
    private const float CardHeight = 102f;
    private const float ThumbHeight = 56f;      // banner 768×512→118×56 横裁带
    private const float ThreatBadgeWidth = 54f;  // T-165：44→54（角标字 8→10 后的带宽）
    private const float ThreatBadgeHeight = 16f; // T-165：14→16（同上——角标两档字高+衬）
    private const float StarRowHeight = 18f;
    private const float BannerTitleBandHeight = 18f; // 题图底部压暗带+章名带

    /// <summary>面板开合统一口（T-162 卡片化：面板根=压暗底+滚动场整体）.</summary>
    private void SetPanelVisible(bool visible) => _panelRoot.Visible = visible;

    /// <summary>
    /// 选关面头部/行动钮三态皮肤+音效（T-164 皮肤批）：跨时代面用中性青
    /// （HoloSkin.EdgeCyan——palettes 不可归属单时代的诚实回退色）；卡片
    /// 三态仍按章时代色（BuildLevelCard 既有）。
    /// </summary>
    private void StyleHeaderButton(Button button)
    {
        UiFeel.ApplyButtonSkin(button, HoloSkin.EdgeCyan);
        UiFeel.AttachCues(button);
    }

    /// <summary>战役会话在局位（主菜单可见性谓词探针）.</summary>
    internal bool SessionActive => _host.InLevel;

    /// <summary>战役面板打开位（主菜单可见性谓词探针）.</summary>
    internal bool PanelOpen => _panelRoot.Visible;

    /// <summary>选关滚动位（--shot 取景探针：滚动后角标/锁态章节的落位证据）.</summary>
    internal int CardScrollVertical => _panel.ScrollVertical;

    /// <summary>首卡全局矩形（--shot 02-level-cards 特写取景；无卡=null 探针返回零矩形）.</summary>
    internal Rect2? FirstCardGlobalRect => _firstCard?.GetGlobalRect();

    /// <summary>败北事实在位探针（--shot 编排快速退避用——只读）.</summary>
    internal bool DefeatPending => _host.PendingDefeat is not null;

    /// <summary>演出待演位（胜利结算/跃迁/终章队列非空——菜单不闪面）.</summary>
    internal bool PerformancesPending =>
        _launchQueue.Count > 0 || _host.PendingLaunches.Count > 0;

    private void RefreshMenu()
    {
        foreach (KeyValuePair<string, Button> pair in _levelButtons)
        {
            pair.Value.QueueFree();
        }

        foreach (Node child in _list.GetChildren())
        {
            child.QueueFree();
        }

        _levelButtons.Clear();
        _firstCard = null;
        int bannerHits = 0;
        int cardCount = 0;
        foreach (CampaignChapter chapter in _host.Chapters())
        {
            Button? chapterFirst = null;
            _list.AddChild(BuildChapterSection(chapter, ref bannerHits, ref cardCount, ref chapterFirst));
            _firstCard ??= chapterFirst;
        }

        // 卡片面证据行（窗口车道 RefreshMenu 即打印——29/29 命中=接线正确；
        // 缺图=接线错不是缺资产，缺数如实上报）。
        GD.Print(
            $"CAMPAIGN_CARDS chapters={_host.Chapters().Count} cards={cardCount} " +
            $"banners={bannerHits}/{cardCount} root={Expand.ExpandAssetPaths.RepoRoot() ?? "-"}");
    }

    // -------------------------------------------- T-162 章节题图+关卡卡片场

    /// <summary>一章=通栏题图（该时代终关 banner 整幅横裁）+关卡卡片网格.</summary>
    private Control BuildChapterSection(
        CampaignChapter chapter, ref int bannerHits, ref int cardCount, ref Button? chapterFirst)
    {
        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 4);
        section.AddChild(BuildChapterBanner(chapter));
        var grid = new GridContainer { Columns = ChapterGridColumns };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (CampaignLevelRow row in chapter.Rows)
        {
            Button card = BuildLevelCard(row, chapter.EraIndex);
            if (TryBannerTexture(LevelCardDecisions.LevelBannerStem(
                    chapter.EraIndex, row.Slot, row.IsFinal)) is not null)
            {
                bannerHits++;
            }

            chapterFirst ??= card;
            _levelButtons[row.Id] = card;
            grid.AddChild(card);
            cardCount++;
        }

        section.AddChild(grid);
        return section;
    }

    /// <summary>章节通栏题图：终关 banner 整幅 KeepAspectCovered 横裁+底部
    /// 压暗带+章名+章锁态（压暗带保章名在亮图上可读）. T-164：条带三件
    /// （贴图/压暗带/描边标题）聚合入 <see cref="UiFeel.BuildBannerStrip"/>
    /// 共享 helper——与无尽/程序关模式卡三连抄收口（T-162 遗留顺手项）.</summary>
    private Control BuildChapterBanner(CampaignChapter chapter)
    {
        bool chapterLocked = chapter.Rows.All(row => row.State == CampaignLevelState.Locked);
        var strip = UiFeel.BuildBannerStrip(
            chapterLocked ? $"{chapter.Title} · 未解锁" : chapter.Title,
            TryBannerTexture(LevelCardDecisions.ChapterBannerStem(chapter.EraIndex))
                ?? PlaceholderTexture(chapter.EraIndex),
            ChapterBannerHeight,
            BannerTitleBandHeight);
        strip.CustomMinimumSize = new Vector2(ChapterBannerWidth, ChapterBannerHeight); // 通栏定宽（共享 helper 缺省自适应高的补齐位）
        return strip;
    }

    /// <summary>
    /// 一张关卡卡片：banner 缩略图+威胁角标+锁遮罩+关名+★★★——点击进既有
    /// LevelHost 流（OnLevelPressed→BeginLevel，T-160 战役链零改动）。
    /// </summary>
    private Button BuildLevelCard(CampaignLevelRow row, int eraIndex)
    {
        Color era = EraPaletteColor(eraIndex);
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.09f, 0.13f, 0.95f),
            BorderColor = era,
        };
        SetCardBorder(normal, 2);
        var hover = new StyleBoxFlat
        {
            BgColor = new Color(era.R * 0.20f + 0.06f, era.G * 0.20f + 0.07f, era.B * 0.20f + 0.10f, 0.97f),
            BorderColor = new(era.R + (1f - era.R) * 0.3f, era.G + (1f - era.G) * 0.3f, era.B + (1f - era.B) * 0.3f),
        };
        SetCardBorder(hover, 3);
        var disabled = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.09f, 0.92f),
            // T-165：锁卡描边灰→时代色暗变体（53 号文 P2-4「锁遮罩灰与时代色
            // 并存显脏」——锁态降为时代色阶的暗部，不再引入第二族灰）
            BorderColor = new Color(era.R * 0.45f + 0.06f, era.G * 0.45f + 0.07f, era.B * 0.45f + 0.09f, 0.9f),
        };
        SetCardBorder(disabled, 2);

        var card = new Button
        {
            // 重玩挂账：CampaignProgression.RegisterCapture 单占领契约——已通关
            // 关重 settle 会撞重复占领（REV §七遗留⑥）。锁=既有解锁逻辑态。
            CustomMinimumSize = new Vector2(CardWidth, CardHeight),
            FocusMode = Control.FocusModeEnum.None,
            Disabled = row.State != CampaignLevelState.Playable,
        };
        card.AddThemeStyleboxOverride("normal", normal);
        card.AddThemeStyleboxOverride("hover", hover);
        card.AddThemeStyleboxOverride("pressed", hover);
        card.AddThemeStyleboxOverride("disabled", disabled);
        if (!card.Disabled)
        {
            // T-165 状态补全（53 号文 P1-3）：卡片 hover 放大+描边亮起——主菜单
            // 六卡同律（1.06 密网格档+抬 Z：放大卡压邻卡绘制序修正；锁卡不接）
            UiFeel.HoverScaleTween(card, 1.06f, 0.08f, hoverZ: 8);
        }

        // —— 缩略图（banner 768×512→118×56 KeepAspectCovered；缺图=时代色调垫底）——
        var thumb = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        thumb.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        thumb.OffsetBottom = ThumbHeight;
        thumb.MouseFilter = Control.MouseFilterEnum.Ignore;
        thumb.Texture = TryBannerTexture(LevelCardDecisions.LevelBannerStem(eraIndex, row.Slot, row.IsFinal))
            ?? PlaceholderTexture(eraIndex);
        card.AddChild(thumb);

        // —— 威胁角标（数据源=threat 既有字段三档；叠缩略图右上）——
        LevelCardDecisions.ThreatTier tier = LevelCardDecisions.ThreatTierOf(row.Threat);
        var threat = new Label
        {
            Text = LevelCardDecisions.ThreatBadgeZh(tier),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        threat.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        threat.OffsetLeft = -ThreatBadgeWidth;
        threat.OffsetBottom = ThreatBadgeHeight;
        threat.AddThemeFontSizeOverride("font_size", 10); // T-165：威胁角标 8→10（53 号文 P2-1 小字族首件）
        threat.AddThemeColorOverride("font_color", Colors.White);
        threat.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        threat.AddThemeConstantOverride("outline_size", 3);
        threat.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = ThreatBadgeBackground(tier),
            CornerRadiusTopLeft = 3,
            CornerRadiusBottomLeft = 3,
            ContentMarginLeft = 3f,
            ContentMarginRight = 3f,
            ContentMarginTop = 1f,
            ContentMarginBottom = 1f,
        });
        threat.MouseFilter = Control.MouseFilterEnum.Ignore;
        card.AddChild(threat);

        // —— 锁遮罩（未解锁：缩略图压暗+lock 图标+居中锁文案）——T-165：黑灰
        // 遮罩→时代色暗变体（53 号文 P2-4：去「脏灰」——锁态=时代色阶暗部）——
        if (row.State == CampaignLevelState.Locked)
        {
            var lockScrim = new ColorRect
            {
                Color = new Color(era.R * 0.20f + 0.02f, era.G * 0.20f + 0.03f, era.B * 0.20f + 0.05f, 0.62f), // ci-allow: R7 T-165 锁遮罩时代色暗变体（表现层）
            };
            lockScrim.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            lockScrim.OffsetBottom = ThumbHeight;
            lockScrim.MouseFilter = Control.MouseFilterEnum.Ignore;
            card.AddChild(lockScrim);
            var lockRow = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            lockRow.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            lockRow.OffsetBottom = ThumbHeight;
            if (UiFeel.ControlIcon("ctrl-lock") is { } lockIcon)
            {
                // T-164：锁徽记图标化（star/star-empty 同组素材；缺图=纯文字回退）
                lockRow.AddChild(new TextureRect
                {
                    Texture = lockIcon,
                    CustomMinimumSize = new Vector2(12f, 12f), // ci-allow: R7 T-164 锁徽记档位 px（表现层）
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });
            }

            var lockLabel = new Label
            {
                Text = "未解锁",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            lockLabel.AddThemeFontSizeOverride("font_size", 11);
            lockLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.87f, 0.9f, 0.95f));
            UiFeel.Outline(lockLabel, 4);
            lockRow.AddChild(lockLabel);
            card.AddChild(lockRow);
        }

        // —— 关名+终关徽记（终关=◆ 前缀区分普通关）——T-165：终关徽记放大
        //（12px 档 vs 普通关 10px——53 号文 P2-1：终关徽记◆偏小，字号对比敢拉开）
        var name = new Label
        {
            Text = row.IsFinal ? $"◆ {row.NameZh}" : row.NameZh,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        name.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        name.OffsetTop = ThumbHeight + 1;
        name.OffsetBottom = ThumbHeight + (row.IsFinal ? 22 : 20);
        name.AddThemeFontSizeOverride("font_size", row.IsFinal ? 12 : 10);
        name.AddThemeColorOverride("font_color", Colors.White);
        name.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        name.AddThemeConstantOverride("outline_size", 3);
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        card.AddChild(name);

        // —— 星槽（★★★=三重目标既有口径；未通关=空槽）——T-164：星槽图标化
        //（star/star-empty 控件图标组替换 10px 字体星号；缺图=字体星号回退）——
        // +状态角标
        Texture2D? starFilled = UiFeel.ControlIcon("ctrl-star");
        Texture2D? starEmpty = UiFeel.ControlIcon("ctrl-star-empty");
        if (starFilled is not null && starEmpty is not null)
        {
            var starRow = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Begin,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            starRow.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
            starRow.OffsetTop = -StarRowHeight;
            starRow.OffsetLeft = 4;
            for (int slot = 0; slot < 3; slot++) // 三重目标三星位（数据=StarsText 同源行）
            {
                starRow.AddChild(new TextureRect
                {
                    Texture = slot < row.Stars ? starFilled : starEmpty,
                    CustomMinimumSize = new Vector2(12f, 12f), // ci-allow: R7 T-164 星槽档位 px（表现层）
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });
            }

            card.AddChild(starRow);
        }
        else
        {
            var stars = new Label
            {
                Text = LevelCardDecisions.StarsText(row.Stars),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stars.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
            stars.OffsetTop = -StarRowHeight;
            stars.OffsetLeft = 4;
            stars.AddThemeFontSizeOverride("font_size", 10);
            stars.AddThemeColorOverride("font_color", new Color(1f, 0.84f, 0.36f, 0.95f));
            stars.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            stars.AddThemeConstantOverride("outline_size", 3);
            stars.MouseFilter = Control.MouseFilterEnum.Ignore;
            card.AddChild(stars);
        }

        string stateZh = row.State switch
        {
            CampaignLevelState.Cleared => "已通关",
            CampaignLevelState.Locked => "锁",
            _ => "可进入",
        };
        var state = new Label
        {
            Text = stateZh,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        state.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        state.OffsetTop = -StarRowHeight;
        state.OffsetLeft = -46;
        state.AddThemeFontSizeOverride("font_size", 9); // T-165：状态角标 8→9（小字族）
        state.AddThemeColorOverride(
            "font_color",
            row.State == CampaignLevelState.Playable ? HoloSkin.TextDimCyan : new Color(0.6f, 0.63f, 0.7f, 0.9f));
        state.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        state.AddThemeConstantOverride("outline_size", 3);
        state.MouseFilter = Control.MouseFilterEnum.Ignore;
        card.AddChild(state);

        string id = row.Id;
        card.Pressed += () => OnLevelPressed(id);
        return card;
    }

    private static void SetCardBorder(StyleBoxFlat box, int width)
    {
        box.BorderWidthTop = width;
        box.BorderWidthBottom = width;
        box.BorderWidthLeft = width;
        box.BorderWidthRight = width;
        box.CornerRadiusBottomLeft = 3;
        box.CornerRadiusBottomRight = 3;
        box.CornerRadiusTopLeft = 3;
        box.CornerRadiusTopRight = 3;
    }

    private static Color ThreatBadgeBackground(LevelCardDecisions.ThreatTier tier) => tier switch
    {
        LevelCardDecisions.ThreatTier.High => new Color(0.72f, 0.13f, 0.15f, 0.92f),
        LevelCardDecisions.ThreatTier.Mid => new Color(0.66f, 0.45f, 0.10f, 0.92f),
        _ => new Color(0.16f, 0.42f, 0.22f, 0.92f),
    };

    /// <summary>banner 贴图（文件名缓存；目录清单一次装载——决策表按主干挑唯一文件）.</summary>
    private Godot.ImageTexture? TryBannerTexture(string stem)
    {
        _bannerFiles ??= Expand.ExpandAssetPaths.BannerFileNames();
        string? file = LevelCardDecisions.PickBannerFile(stem, _bannerFiles);
        if (file is null)
        {
            return null;
        }

        if (!_bannerCache.TryGetValue(file, out Godot.ImageTexture? texture))
        {
            texture = Expand.ExpandAssetPaths.LoadPng(Expand.ExpandAssetPaths.BannerPath(file));
            _bannerCache[file] = texture;
        }

        return texture;
    }

    /// <summary>缺图垫底（时代主色调 4×4 拉伸底——卡片结构不因缺图塌陷）.</summary>
    private Godot.ImageTexture PlaceholderTexture(int eraIndex)
    {
        Color era = EraPaletteColor(eraIndex);
        var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(new Color(era.R * 0.30f + 0.05f, era.G * 0.30f + 0.06f, era.B * 0.30f + 0.08f, 1f));
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>时代主色（RL7：色值查 visual-assets.json palettes，键=e{N}）.</summary>
    private Color EraPaletteColor(int eraIndex)
    {
        EraCore.Cinematics.EraPalette? palette = null;
        _main?.ExpandDirector?.Catalog.Palettes.TryGetValue($"e{eraIndex}", out palette);
        if (palette is { } row)
        {
            return new Color(row.Primary.R / 255f, row.Primary.G / 255f, row.Primary.B / 255f);
        }

        return HoloSkin.EdgeCyan; // 目录不可达（极端态）——中性青回退
    }

    private void RefreshStatus()
    {
        if (_host.CompletionSummary.Length > 0)
        {
            _statusLabel.Text = _host.CompletionSummary; // 终章通关总结（制作组/全清面）
            return;
        }

        if (_host.Current is not { } current)
        {
            _statusLabel.Text = "未开局";
            return;
        }

        _statusLabel.Text =
            $"{current.Level.NameZh}（{current.Level.Id}）\n" +
            $"状态={current.Session.Machine.State} 波={current.Session.Sim.CurrentWave}/{current.Session.WinWaves} " +
            $"tick={current.Session.Sim.CurrentTick} 时代={_host.EraIndex} 遥测=player 在账";
    }

    // ------------------------------------------------------- headless smoke

    /// <summary>
    /// G4 冒烟（全部沙盒根=.tmp-tests/campaign-smoke，绝不写玩家真根）：
    /// <list type="number">
    /// <item>全 29 关装载矩阵：生产装载器+组装律把 e1~e6.json 每一行组装为
    /// 真 LevelSession（Menu 态+胜波=数据行 win_waves+命令流非空）；</item>
    /// <item>UI 构造的 E1-01 真通关：经菜单同一条工厂路径跑到 Victory，结算
    /// 入账+★行落 VictorySettlementStore+E1-02 解锁（T-160c 增：途中第 2 波
    /// 断弹钩子路由 e1-aha——钩子行随战役会话走）；</item>
    /// <item>T-160c 败北全链：E1-02 打过检查点波→声明败北（普通关分档+检查点
    /// 恢复+残骸账+失败面板事实）→从检查点继续（TryResumeCheckpoint 重放至
    /// 检查点波边界）→再打到 Victory；未跨检查点波的败北=仅重开档→重开=零点
    /// 新局；</item>
    /// <item>退出重进续玩：中途退出（tick T 存档）→重进 TryResume 无损续到
    /// ≥T（level 域）+campaign 域合并写/读回（时代+进度态恢复）；</item>
    /// <item>遥测：source=script 沙盒行（kind=Campaign/era 对位/outcome=
    /// Victory+Abandoned+Resumed+Defeat 四面）；</item>
    /// <item>跃迁负向探针+终章播片二选一（系统开关位）+收束→跃迁发射序
    /// （e3/e4/e5 收束先行——ExecuteTransition 同一函数探针×5）+剧情钩子
    /// 开局拍（e2-01→e2-awaken）。</item>
    /// </list>
    /// 打印 CAMPAIGN_OK；任一自检不过 Quit(1)。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        var problems = new List<string>();

        string smokeRoot = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", "campaign-smoke");
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        var files = new DevFileStore(smokeRoot);
        var port = new JsonFileSavePort(files, "campaign");
        var clock = new SteppedClock();
        var telemetry = new LevelDurationTelemetry(files, clock);

        // —— ① 全 29 关装载矩阵（生产装载器+组装律→真 LevelSession）——
        CampaignLevelHost host = CampaignLevelHost.Create(files, port, telemetry, TelemetrySources.Script);
        var matrixRows = new List<string>();
        int constructed = 0;
        foreach (CampaignChapter chapter in host.Chapters())
        {
            foreach (CampaignLevelRow row in chapter.Rows)
            {
                LevelSessionProbe probe = host.ProbeSession(row.Id);
                if (probe.Session.WinWaves != probe.Level.WinWaves
                    || probe.Session.Machine.State != GameFlowState.Menu
                    || probe.Commands.Count == 0)
                {
                    problems.Add(
                        $"装载矩阵 {row.Id}: win={probe.Session.WinWaves}/{probe.Level.WinWaves} " +
                        $"state={probe.Session.Machine.State} commands={probe.Commands.Count}");
                }

                constructed++;
                matrixRows.Add($"{row.Id}-s{probe.Level.Slot}:{row.State switch
                {
                    CampaignLevelState.Playable => "play",
                    CampaignLevelState.Locked => "lock",
                    _ => "clear",
                }}");
            }
        }

        int totalLevels = host.Chapters().Sum(chapter => chapter.Rows.Count);
        if (constructed != totalLevels || totalLevels < 24)
        {
            problems.Add($"装载矩阵行数不符：constructed={constructed} total={totalLevels}（任务口径 ≥24）");
        }

        CampaignChapter firstChapter = host.Chapters().First();
        if (firstChapter.Rows.First().State != CampaignLevelState.Playable
            || firstChapter.Rows.Count(row => row.State == CampaignLevelState.Playable) != 1)
        {
            problems.Add("初始进度态应恰 E1-01 可玩");
        }

        // —— ② UI 构造的 E1-01 真通关（同一工厂路径）——
        host.BeginLevel("campaign:level-e1-01");
        if (host.Current is not { } run)
        {
            problems.Add("E1-01 起局失败");
        }
        else
        {
            long horizon = HorizonTicks(run.Level);
            long steps = 0;
            while (host.InLevel && steps < horizon)
            {
                host.PumpOneTick();
                clock.AdvanceMs(33);
                steps++;
            }

            // 通关判据：胜利结算已走完（CompleteLevel 落 Menu）且 Victory 结算
            // 事实在位（胜波+tick+run seed——LevelSession 公共 API）。
            if (host.Current?.Session.Victory is not { } victory
                || host.Current.Session.Machine.State != GameFlowState.Menu)
            {
                problems.Add(
                    $"E1-01 未通关：state={host.Current?.Session.Machine.State} " +
                    $"wave={host.Current?.Session.Sim.CurrentWave} tick={host.Current?.Session.Sim.CurrentTick}");
            }
            else if (victory.WinWaves != run.Level.WinWaves || victory.Tick <= 0)
            {
                problems.Add($"E1-01 Victory 结算不符：win={victory.WinWaves}/{run.Level.WinWaves} tick={victory.Tick}");
            }
        }

        CampaignLevelRow e101After = host.Chapters().First().Rows.First(row => row.Id.EndsWith("e1-01"));
        CampaignLevelRow? e102After = host.Chapters().First().Rows.FirstOrDefault(row => row.Id.EndsWith("e1-02"));
        if (e101After is not { State: CampaignLevelState.Cleared } || e101After.Stars < 1)
        {
            problems.Add($"E1-01 通关态未落：{e101After.State} stars={e101After.Stars}");
        }

        if (e102After is not { State: CampaignLevelState.Playable })
        {
            problems.Add($"E1-02 应解锁：{e102After?.State}");
        }

        if (host.PendingLaunches.Count(launch => launch.Settlement is not null) < 1)
        {
            problems.Add("胜利结算演出（先定后演）未入队");
        }

        // T-160c 剧情钩子·波始拍：E1-01 第 2 波断弹位在胜局途中被路由（e1-aha
        // 入待演队列——钩子行随战役会话走的直证）。
        bool ahaRouted = host.PendingLaunches.Any(
            launch => launch.ScriptId == "cinematic:e1-aha"); // cinematics/e1-aha.json 的 hook 行=e1-01 第 2 波位
        host.ClearPendingLaunches();

        // —— ②d T-160c 败北全链（断点一判据）：e1-02 打过检查点波→声明败北
        //     （检查点分档+失败面板事实+遥测 Defeat 行）→从检查点继续
        //     （TryResumeCheckpoint 重放到检查点波边界）→再打到 Victory——
        int checkpointWave = 0;
        bool checkpointContinueWon = false;
        DefeatPanelFacts? defeatFacts = null;
        if (e102After is { State: CampaignLevelState.Playable })
        {
            host.BeginLevel(e102After.Id);
            CampaignRun defeatRun = host.Current!;
            checkpointWave = defeatRun.Session.Checkpoints.CheckpointWave;
            long boundary = CheckpointBoundaryTicks(defeatRun.Level, checkpointWave);
            for (long step = 0; step < boundary + IntervalTicksOf(defeatRun.Level) && host.InLevel; step++)
            {
                host.PumpOneTick();
                clock.AdvanceMs(33);
            }

            if (defeatRun.Session.Sim.CurrentWave <= checkpointWave)
            {
                problems.Add(
                    $"败北前置未跨检查点波：wave={defeatRun.Session.Sim.CurrentWave} checkpoint={checkpointWave}");
            }
            else if (!host.DeclareDefeat("smoke-defeat-after-checkpoint"))
            {
                problems.Add("宿主声明式败北入口未生效（活局上应可声明）");
            }
            else if (host.PendingDefeat is not { } facts)
            {
                problems.Add("失败面板事实未在位（败北结算未落）");
            }
            else
            {
                defeatFacts = facts;
                if (facts.RestoredFromWave != checkpointWave || checkpointWave < 10
                    || !facts.ContinueAvailable || !facts.FirstDefeat
                    || facts.WreckCount < 1 || facts.RepairCostPoints <= 0)
                {
                    problems.Add(
                        $"败北分档事实不符：restored={facts.RestoredFromWave}/{checkpointWave} " +
                        $"continue={facts.ContinueAvailable} first={facts.FirstDefeat} " +
                        $"wrecks={facts.WreckCount} repair={facts.RepairCostPoints:0.##}");
                }

                if (host.Current?.Session.FailureTier != EraCore.Checkpoint.FailureScenarioKind.NormalLevel)
                {
                    problems.Add($"败北分档应为普通关档：{host.Current?.Session.FailureTier}");
                }

                // 从检查点继续：重放到检查点波边界（tick=恢复点+波=检查点波已过界）。
                if (!host.ContinueFromCheckpoint())
                {
                    problems.Add("从检查点继续未生效（检查点在位应可续）");
                }
                else
                {
                    CampaignRun continued = host.Current!;
                    if (!continued.ResumedFromCheckpoint
                        || continued.Session.Sim.CurrentTick != facts.RestoredAtTick
                        || continued.Session.Sim.CurrentWave != checkpointWave + 1
                        || continued.Session.Machine.State != GameFlowState.InLevel)
                    {
                        problems.Add(
                            $"检查点续局位置不符：resumed={continued.ResumedFromCheckpoint} " +
                            $"tick={continued.Session.Sim.CurrentTick}/{facts.RestoredAtTick} " +
                            $"wave={continued.Session.Sim.CurrentWave}/{checkpointWave + 1} " +
                            $"state={continued.Session.Machine.State}");
                    }

                    long horizon = HorizonTicks(continued.Level);
                    long steps = 0;
                    while (host.InLevel && steps < horizon)
                    {
                        host.PumpOneTick();
                        clock.AdvanceMs(33);
                        steps++;
                    }

                    checkpointContinueWon = host.Current?.Session.Victory is { WinWaves: > 0 }
                        && host.Current.Session.Machine.State == GameFlowState.Menu;
                    if (!checkpointContinueWon)
                    {
                        problems.Add(
                            $"检查点续局未通关：state={host.Current?.Session.Machine.State} " +
                            $"wave={host.Current?.Session.Sim.CurrentWave}/{host.Current?.Session.WinWaves}");
                    }
                }
            }
        }
        else
        {
            problems.Add("败北链前置失败（E1-02 应可玩）");
        }

        CampaignLevelRow? e102Defeated = host.Chapters().First().Rows
            .FirstOrDefault(row => row.Id.EndsWith("e1-02", StringComparison.Ordinal));
        CampaignLevelRow? e103After = host.Chapters().First().Rows
            .FirstOrDefault(row => row.Id.EndsWith("e1-03", StringComparison.Ordinal));
        if (e102Defeated is not { State: CampaignLevelState.Cleared })
        {
            problems.Add($"败北链后 E1-02 应已通关：{e102Defeated?.State}");
        }

        if (e103After is not { State: CampaignLevelState.Playable })
        {
            problems.Add($"E1-03 应解锁：{e103After?.State}");
        }

        // —— ②e 未跨检查点波的败北：分档=从头重开（面板无「继续」档）→重开=零点新局——
        bool earlyRestartFresh = false;
        if (e103After is { State: CampaignLevelState.Playable })
        {
            host.BeginLevel(e103After.Id);
            for (int i = 0; i < 300 && host.InLevel; i++)
            {
                host.PumpOneTick();
                clock.AdvanceMs(33);
            }

            if (!host.DeclareDefeat("smoke-early-defeat") || host.PendingDefeat is not { } early)
            {
                problems.Add("未跨检查点波的败北未声明/未入面板");
            }
            else if (early.RestoredFromWave != 0 || early.ContinueAvailable)
            {
                problems.Add(
                    $"未跨检查点波分档应仅重开：restored={early.RestoredFromWave} " +
                    $"continue={early.ContinueAvailable}");
            }
            else
            {
                host.RestartLevel();
                CampaignRun restarted = host.Current!;
                earlyRestartFresh = restarted.Session.Sim.CurrentTick == 0
                    && !restarted.ResumedFromExit
                    && !restarted.ResumedFromCheckpoint
                    && restarted.Session.Machine.State == GameFlowState.InLevel;
                if (!earlyRestartFresh)
                {
                    problems.Add(
                        $"重开本关应为零点新局：tick={restarted.Session.Sim.CurrentTick} " +
                        $"exit={restarted.ResumedFromExit} ckpt={restarted.ResumedFromCheckpoint} " +
                        $"state={restarted.Session.Machine.State}");
                }
            }
        }
        else
        {
            problems.Add("早败段前置失败（E1-03 应可玩）");
        }

        // —— ③ 退出重进续玩：E1-03 中途退出→重进无损续——
        long resumeTick = 0;
        if (e103After is { State: CampaignLevelState.Playable } || earlyRestartFresh)
        {
            if (host.Current is null || !host.InLevel)
            {
                host.BeginLevel(e103After!.Id);
            }

            for (int i = 0; i < 300 && host.InLevel; i++)
            {
                host.PumpOneTick();
                clock.AdvanceMs(33);
            }

            resumeTick = host.Current?.Session.Sim.CurrentTick ?? 0;
            host.ExitToMenu();
            if (resumeTick <= 0)
            {
                problems.Add("续玩段前置 tick 应 >0");
            }

            host.BeginLevel(e103After!.Id); // TryResume 路径：#exit 快照在位即续
            long resumedTick = host.Current?.Session.Sim.CurrentTick ?? -1;
            bool resumedFlag = host.Current?.ResumedFromExit ?? false;
            if (!resumedFlag || resumedTick < resumeTick)
            {
                problems.Add($"退出重进续玩失败：resumed={resumedFlag} tick={resumedTick}<{resumeTick}");
            }

            host.ExitToMenu();
        }
        else
        {
            problems.Add("续玩段前置失败");
        }

        // campaign 域读回：时代+进度态恢复（合并写不清账——败北链通关的 E1-02
        // 进度态应随合并写持久为已通关）。
        CampaignLevelHost reloaded = CampaignLevelHost.Create(files, port, telemetry, TelemetrySources.Script);
        if (reloaded.EraIndex != CampaignState.E1EraIndex)
        {
            problems.Add($"campaign 域读回时代不符：{reloaded.EraIndex}");
        }

        CampaignLevelRow? reloadedE102 = reloaded.Chapters().First().Rows
            .FirstOrDefault(row => row.Id.EndsWith("e1-02"));
        if (reloadedE102 is not { State: CampaignLevelState.Cleared })
        {
            problems.Add($"重进菜单进度态不符（E1-02 应已通关）：{reloadedE102?.State}");
        }

        // —— ④ 遥测行（source=script 沙盒；T-160c 增 Defeat 面）——
        IReadOnlyList<LevelDurationRecord> ledger = LevelDurationTelemetry.ReadAll(files);
        bool hasVictoryRow = ledger.Any(row =>
            row.Kind == TelemetryLevelKind.Campaign && row.LevelId == "campaign:level-e1-01"
            && row.EraIndex == 1 && row.Outcome == TelemetryOutcome.Victory && row.PlayTicks > 0
            && row.Source == TelemetrySources.Script);
        bool hasAbandonedRow = ledger.Any(row =>
            row.Kind == TelemetryLevelKind.Campaign && row.Outcome == TelemetryOutcome.Abandoned
            && !row.Resumed && row.Source == TelemetrySources.Script);
        bool hasResumedRow = ledger.Any(row => row.Resumed);
        bool hasDefeatRow = ledger.Any(row =>
            row.Kind == TelemetryLevelKind.Campaign && row.Outcome == TelemetryOutcome.Defeat
            && row.Source == TelemetrySources.Script);
        if (!hasVictoryRow || !hasAbandonedRow || !hasResumedRow || !hasDefeatRow)
        {
            problems.Add(
                $"遥测行不全：victory={hasVictoryRow} abandoned={hasAbandonedRow} " +
                $"resumed={hasResumedRow} defeat={hasDefeatRow} rows={ledger.Count}");
        }

        // —— ⑤ 跃迁负向探针 + 终章二选一（开关位驱动）+ 收束发射序（T-160c）——
        string? refusal = host.ProbeTransitionRefusal(1);
        if (refusal is null)
        {
            problems.Add("跃迁负向探针未硬拒（E1 final 未占领应 EraTransitionRejectedException）");
        }

        string finaleOn = FinalePlayback.ScriptIdFor(systemEnabled: true);
        string finaleOff = FinalePlayback.ScriptIdFor(systemEnabled: false);
        if (finaleOn != FinalePlayback.OnScriptId || finaleOff != FinalePlayback.OffScriptId)
        {
            problems.Add($"终章播片二选一不符：on={finaleOn} off={finaleOff}");
        }

        // 收束→跃迁发射序（数据驱动：e3/e4/e5 有收束段文件→收束先行；e1/e2 无
        // 段文件→仅跃迁）。探针=ExecuteTransition 入队的同一函数。
        string contentRoot = CampaignLevelHost.FindContentRoot();
        var finaleOrder = new List<string>();
        for (int era = 1; era <= 5; era++)
        {
            finaleOrder.Add($"e{era}:[{string.Join(",", CampaignLevelHost.EraFinaleLaunchIds(era, contentRoot))}]");
        }

        string expect1 = "e1:[cinematic:transition-e1-e2]";
        if (!finaleOrder.Contains(expect1)
            || !finaleOrder.Contains("e3:[cinematic:e3-finale,cinematic:transition-e3-e4]")
            || !finaleOrder.Contains("e4:[cinematic:e4-finale,cinematic:transition-e4-e5]")
            || !finaleOrder.Contains("e5:[cinematic:e5-finale,cinematic:transition-e5-e6]"))
        {
            problems.Add($"收束→跃迁发射序不符：{string.Join(" ", finaleOrder)}");
        }
        // —— ⑥ T-160b 战场渲染绑定（CAMPAIGN_VIEW——本文件problems合流执法）——
        CampaignViewFacts view = RunViewSmoke(host, problems);

        // T-160c 剧情钩子·开局拍：⑥ 的 e2-01 开局应路由 e2-awaken（level_start
        // 位——钩子行随战役会话走的第二直证；Headless 下演出只入队不开演）。
        bool awakenRouted = host.PendingLaunches.Any(
            launch => launch.ScriptId == "cinematic:e2-awaken"); // cinematics/e2-awaken.json 的 hook 行=e2-01 开局位
        host.ClearPendingLaunches();

        if (!ahaRouted || !awakenRouted || !checkpointContinueWon || defeatFacts is null)
        {
            problems.Add(
                $"T-160c 断面缺事实：aha={ahaRouted} awaken={awakenRouted} " +
                $"checkpoint_continue_won={checkpointContinueWon} defeat={defeatFacts is not null}");
        }

        // 沙盒清场（dev 根 .tmp-tests 下，gitignored）
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"CAMPAIGN_OK matrix={constructed}/{totalLevels} chapters=6 " +
                $"e1-01=victory stars={e101After.Stars} unlock=e1-02 " +
                $"defeat=declared-after-checkpoint tier=normal restored=wave{defeatFacts!.RestoredFromWave} " +
                $"checkpoint_continue=resumed@tick{defeatFacts.RestoredAtTick}->victory " +
                $"early_defeat=restart-only restart=fresh@tick0 " +
                $"telemetry=victory+abandoned+resumed+defeat " +
                $"cinematic=aha-routed+awaken-routed finale-order=e3/e4/e5-finale-first " +
                $"resume=lossless@tick{resumeTick} save=campaign-merged-reload " +
                "source=script " +
                "transition=refused-without-final-capture finale=switch-picked " +
                $"rows=[{string.Join(" ", matrixRows)}]");
            GD.Print(
                $"CAMPAIGN_VIEW e1={view.E1Width}x{view.E1Height} ground_rebuilds={view.E1GroundRebuilds} " +
                $"units_seen={view.E1MaxUnits} unit_rebuilds={view.E1UnitRebuilds} " +
                $"terrain=level-world-equal rock_cells={view.E1RockCells} " +
                $"e2={view.E2Width}x{view.E2Height} era2_atlas={view.E2EraRouted} " +
                $"rebind_terrain_changed={view.RebindTerrainChanged} " +
                $"advisor=build-plan-on-session-channel towers={view.TowersBefore}->{view.TowersAfter} " +
                $"issued_advisor_commands={view.AdvisorIssuedCommands} " +
                $"advisor_resume_towers_kept={view.AdvisorResumeTowersKept} " +
                $"panel_hud={view.PanelHudBound} exit_restore={view.ExitRestored}");
        }
        else
        {
            GD.PushError("CAMPAIGN_FAIL " + string.Join("; ", problems));
            GD.Print("CAMPAIGN_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
    }

    // --------------------------------------- T-160b CAMPAIGN_VIEW（G4 判据段）

    /// <summary>CAMPAIGN_VIEW 事实行（打印面——判据经 problems 执法）.</summary>
    internal sealed record CampaignViewFacts(
        int E1Width, int E1Height, int E1GroundRebuilds, int E1MaxUnits, int E1UnitRebuilds,
        int E1RockCells, int E2Width, int E2Height, bool E2EraRouted, bool RebindTerrainChanged,
        int TowersBefore, int TowersAfter, int AdvisorIssuedCommands, bool AdvisorResumeTowersKept,
        bool PanelHudBound, bool ExitRestored);

    /// <summary>
    /// T-160b 战场渲染绑定冒烟（判据=任务卡②）：①进 e1-01→渲染层脏块重建
    /// &gt;0+地形签名=该关真图（视口帧缓冲逐格等于会话世界地形——非夹具：
    /// e1 行 rock=0 而夹具图 rock≈19 格）+若干帧后单位层有实体；②退出到菜
    /// 单→释放（夹具层恢复）；③切换 e2-01 重绑生效（图尺寸 16×24→20×26/
    /// era2 图集路由/地形签名随图变）；④军师面板绑战役会话——BuildPlan 确认
    /// 命令经 LevelSession 通道（命令流记账+退出重进塔数无损）。
    /// </summary>
    private CampaignViewFacts RunViewSmoke(CampaignLevelHost host, List<string> problems)
    {
        var viewport = SessionBattlefieldViewport.Instantiate(this);
        SetFixtureHidden(true); // 局内夹具双层隐藏（窗口车道同律；冒烟结束恢复）

        // —— ① e1-01：绑定+泵到首波入场（判据：脏块/真图/单位）——
        host.BeginLevel("campaign:level-e1-01");
        CampaignRun run = host.Current!;
        long firstEntry = FirstEntryTicks(run.Level);
        for (long step = 0; step < firstEntry && host.InLevel; step++)
        {
            host.PumpOneTick();
            viewport.Bind(run.Session, EraLevelAssembly.EraIndexOf(run.Level));
            viewport.PumpFrame();
        }

        int e1Width = viewport.BoundWidthCells;
        int e1Height = viewport.BoundHeightCells;
        int e1Ground = viewport.GroundChunkRebuilds;
        int e1Units = viewport.MaxUnitsSeen;
        int e1UnitRebuilds = viewport.UnitChunkRebuilds;
        IReadOnlyDictionary<int, int> e1Signature = viewport.TerrainSignature();
        int e1Rock = e1Signature.TryGetValue(SessionBattlefieldViewport.ColorOfTerrain(
            EraCore.World.TerrainKind.Rock), out int rockCells) ? rockCells : 0;

        if (e1Ground <= 0)
        {
            problems.Add($"e1-01 渲染层脏块重建应>0（ground={e1Ground}）");
        }

        if (e1Units <= 0 || e1UnitRebuilds <= 0)
        {
            problems.Add($"e1-01 若干帧后单位层应有实体（units={e1Units} unit_rebuilds={e1UnitRebuilds}）");
        }

        if (e1Width != run.Level.Map.Width || e1Height != run.Level.Map.Height)
        {
            problems.Add($"e1-01 图尺寸不符：viewport={e1Width}x{e1Height} level={run.Level.Map.Width}x{run.Level.Map.Height}");
        }

        if (!TerrainSignatureEquals(e1Signature, run))
        {
            problems.Add("e1-01 地形签名≠该关真图（视口帧缓冲与会话世界逐格不等——仍夹具？）");
        }

        if (e1Rock != 0)
        {
            problems.Add($"e1 行 rock_ratio=0 应零岩石格（实得 {e1Rock}——夹具图特征）");
        }

        // —— ② 退出到菜单：视口释放+夹具层恢复（恢复菜单态口径）——
        host.ExitToMenu();
        viewport.Release();
        SetFixtureHidden(false);
        bool exitRestored = !viewport.IsBound && !_fixtureHidden;
        if (!exitRestored)
        {
            problems.Add($"退出到菜单未恢复菜单态（bound={viewport.IsBound} fixture_restored={!_fixtureHidden}）");
        }

        // —— ③ e2-01：重绑生效（尺寸/era 路由/地形随图变）——
        host.BeginLevel("campaign:level-e2-01");
        CampaignRun e2Run = host.Current!;
        for (int step = 0; step < 4 && host.InLevel; step++)
        {
            host.PumpOneTick();
            viewport.Bind(e2Run.Session, EraLevelAssembly.EraIndexOf(e2Run.Level));
            viewport.PumpFrame();
        }

        int e2Width = viewport.BoundWidthCells;
        int e2Height = viewport.BoundHeightCells;
        bool eraRouted = viewport.BoundEra == 2;
        IReadOnlyDictionary<int, int> e2Signature = viewport.TerrainSignature();
        bool terrainChanged = !TerrainSignatureEquals(e2Signature, run); // 新图≠旧图（尺寸+森林密度都变）
        if (e2Width != e2Run.Level.Map.Width || e2Height != e2Run.Level.Map.Height || e2Width == e1Width)
        {
            problems.Add($"e2-01 重绑图尺寸未变：e1={e1Width}x{e1Height} e2={e2Width}x{e2Height}");
        }

        if (!eraRouted)
        {
            problems.Add($"e2-01 时代路由应 era=2（实得 {viewport.BoundEra}）");
        }

        if (!TerrainSignatureEquals(e2Signature, e2Run))
        {
            problems.Add("e2-01 地形签名≠该关真图（重绑后帧缓冲未跟随新会话）");
        }

        if (!terrainChanged)
        {
            problems.Add("e1→e2 地形签名未随图变化（重绑未生效）");
        }

        // —— ④ 军师面板绑战役会话：BuildPlan 确认命令走 LevelSession 通道 ——
        int towersBefore = e2Run.Session.Sim.Towers.Count;
        using (CampaignAdvisorBoard board = host.BuildAdvisorBoard(e2Run))
        {
            bool panelHudBound = false;
            if (_main is not null)
            {
                _main.AdvisorPanel.Bind(board); // 面板绑战役板（窗口车道同一调用）
                _main.AdvisorPanel.RefreshHud(board.BuildHudSnapshot());
                string hud = _main.AdvisorPanel.HudText();
                panelHudBound = hud.Contains(
                    HudMetrics.FormatWave(e2Run.Session.Sim.CurrentWave), StringComparison.Ordinal);
                _ = _main.AdvisorPanel.SubmitOrder("建一座箭塔"); // 军令路径经战役板真管线（面板活面）
                _main.AdvisorPanel.Bind(_main.BattleHost); // 还原夹具板（后续段不被本段残留污染）
                if (!panelHudBound)
                {
                    problems.Add($"军师面板 HUD 未随战役会话（hud=\"{hud}\"）");
                }
            }

            // BuildPlan 确认命令：宿主驱动的单动作入口（面板 L2 回投同通道）。
            (int buildX, int buildY)? free = FindFreePlainCell(e2Run);
            if (free is null)
            {
                problems.Add("e2-01 扫不到空 Plain 格（军令建造落点）");
            }
            else
            {
                // 通道基线：军令入账前的命令流（含关卡脚本建造——IssuedAtTick
                // 晚于退出刻的脚本命令按引擎律不入退出快照，不得计入军令面）。
                int towerCommandsBefore = e2Run.Session.IssuedCommands.Count(command =>
                    command.PayloadJson.Contains("era2:tower-arrow", StringComparison.Ordinal));
                OrderOutcome planned = board.Pipeline.HandleSystemAction(
                    EraCore.Contracts.Actions.SystemActionKind.BuildPlan,
                    $"{{\"action\":\"BuildPlan\",\"tower_id\":\"era2:tower-arrow\",\"cells\":[{{\"x\":{free.Value.buildX},\"y\":{free.Value.buildY}}}]}}");
                OrderOutcome executed = planned.Kind == PipelineOutcomeKind.AwaitingApproval
                    ? board.Pipeline.Confirm(planned.PendingId)
                    : planned;
                int towersAfter = e2Run.Session.Sim.Towers.Count;
                int advisorCommands = e2Run.Session.IssuedCommands.Count(command =>
                    command.PayloadJson.Contains("era2:tower-arrow", StringComparison.Ordinal))
                    - towerCommandsBefore;
                SimCommand advisorCommand = e2Run.Session.IssuedCommands[^1]; // 军令=最后一条入账命令
                if (executed.Kind != PipelineOutcomeKind.Completed || towersAfter <= towersBefore)
                {
                    problems.Add(
                        $"军令 BuildPlan 未落地：kind={executed.Kind} towers={towersBefore}->{towersAfter}");
                }

                if (advisorCommands <= 0 || !advisorCommand.PayloadJson.Contains(
                        "era2:tower-arrow", StringComparison.Ordinal))
                {
                    problems.Add("军令命令未走 LevelSession 通道（IssuedCommands 无军令 era2:tower-arrow 建造）");
                }

                // —— 通道的硬证明：退出→重进，军令建造经重放键无损保留 ——
                int towersWithAdvisor = towersAfter;
                host.ExitToMenu();
                host.BeginLevel("campaign:level-e2-01");
                CampaignRun resumed = host.Current!;
                int resumedTowers = resumed.Session.Sim.Towers.Count;
                bool resumeKept = resumedTowers == towersWithAdvisor && resumed.ResumedFromExit
                    && resumed.Session.IssuedCommands.Any(command =>
                        command.IssuedAtTick == advisorCommand.IssuedAtTick
                        && command.Kind == advisorCommand.Kind
                        && command.PayloadJson == advisorCommand.PayloadJson);
                if (!resumeKept)
                {
                    problems.Add(
                        $"军令建造未随会话重放保留：resumed_towers={resumedTowers} expect={towersWithAdvisor}");
                }

                host.ExitToMenu();
                int finalTowersAfter = towersWithAdvisor;
                int finalAdvisorIssued = advisorCommands;
                SetFixtureHidden(false);
                viewport.QueueFree();
                return new CampaignViewFacts(
                    e1Width, e1Height, e1Ground, e1Units, e1UnitRebuilds, e1Rock,
                    e2Width, e2Height, eraRouted, terrainChanged,
                    towersBefore, finalTowersAfter, finalAdvisorIssued, resumeKept,
                    panelHudBound, exitRestored);
            }
        }

        // 落点扫描失败路径（problems 已记）：清场后返回保守事实行。
        SetFixtureHidden(false);
        viewport.QueueFree();
        host.ExitToMenu();
        return new CampaignViewFacts(
            e1Width, e1Height, e1Ground, e1Units, e1UnitRebuilds, e1Rock,
            e2Width, e2Height, eraRouted, terrainChanged,
            towersBefore, towersBefore, 0, false, false, exitRestored);
    }

    /// <summary>视口地形签名 vs 会话世界直扫（逐色计数相等=帧缓冲即该关真图）.</summary>
    private static bool TerrainSignatureEquals(IReadOnlyDictionary<int, int> signature, CampaignRun run)
    {
        var expected = new Dictionary<int, int>();
        for (int y = 0; y < run.Session.Sim.World.Height; y++)
        {
            for (int x = 0; x < run.Session.Sim.World.Width; x++)
            {
                int rgb = SessionBattlefieldViewport.ColorOfTerrain(
                    run.Session.Sim.World.TerrainAt(new EraCore.Contracts.Foundation.CellCoord(x, y)));
                expected[rgb] = expected.TryGetValue(rgb, out int seen) ? seen + 1 : 1;
            }
        }

        return signature.Count == expected.Count
            && signature.All(pair => expected.TryGetValue(pair.Key, out int count) && count == pair.Value);
    }

    /// <summary>波间隔 tick 数（威胁度公式 120s×乘子——冒烟各段的波界推算）.</summary>
    internal static long IntervalTicksOf(HandcraftedLevel level)
    {
        return (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>检查点波边界 tick（宽限+检查点波×间隔——波 N 的结束界=N 的开始界）.</summary>
    internal static long CheckpointBoundaryTicks(HandcraftedLevel level, int checkpointWave)
    {
        long intervalTicks = IntervalTicksOf(level);
        long graceTicks = (long)Math.Round(
            intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + (checkpointWave * intervalTicks);
    }

    /// <summary>首两波入场 horizon（grace+2 波窗——泵到此帧数单位层必有实体且过波峰）.</summary>
    internal static long FirstEntryTicks(HandcraftedLevel level)
    {
        long intervalTicks = IntervalTicksOf(level);
        long graceTicks = (long)Math.Round(
            intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + (2 * intervalTicks);
    }

    /// <summary>首个既无建筑又是 Plain 的格（军令建造落点——与地形抽签解耦）.</summary>
    private static (int X, int Y)? FindFreePlainCell(CampaignRun run)
    {
        for (int y = 0; y < run.Session.Sim.World.Height; y++)
        {
            for (int x = 0; x < run.Session.Sim.World.Width; x++)
            {
                var cell = new EraCore.Contracts.Foundation.CellCoord(x, y);
                if (run.Session.Sim.World.TerrainAt(cell) != EraCore.World.TerrainKind.Plain
                    || run.Session.Sim.Buildings.Any(building => building.Cell == cell))
                {
                    continue;
                }

                return (x, y);
            }
        }

        return null;
    }

    /// <summary>胜波+12 波窗 horizon（失败护栏，通关即停——V10 链同律）.</summary>
    internal static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 12) * intervalTicks);
    }

    /// <summary>Stepped fake wall clock（冒烟确定性：33ms/步进≈30Hz 墙钟参考列）.</summary>
    private sealed class SteppedClock : IClock
    {
        private long _nowMs;

        public long Tick => 0;

        public long NowMs => _nowMs;

        public void AdvanceMs(long delta) => _nowMs += delta;
    }
}

/// <summary>One level row's menu state (T-160 战役菜单进度态).</summary>
internal enum CampaignLevelState
{
    Locked,
    Playable,
    Cleared,
}

/// <summary>One chapter section of the menu (era + its level rows in slot order).</summary>
internal sealed record CampaignChapter(int EraIndex, string Title, IReadOnlyList<CampaignLevelRow> Rows);

/// <summary>
/// One menu row: the level plus its resolved state and star count, plus the
/// T-162 card inputs (slot/threat/final——banner 映射与威胁角标的数据面，
/// 源=HandcraftedLevel 既有字段，别不新造口径).
/// </summary>
internal sealed record CampaignLevelRow(
    string Id, string NameZh, CampaignLevelState State, int Stars, int Slot, double Threat, bool IsFinal);

/// <summary>The live level binding of the host (session + its level row + resume flag + session config).</summary>
internal sealed record CampaignRun(
    LevelSession Session,
    HandcraftedLevel Level,
    bool ResumedFromExit,
    LevelSessionConfig Config,
    bool ResumedFromCheckpoint = false);

/// <summary>
/// T-160c 失败面板事实（败绩分档呈现）：败北原因+检查点档位（恢复波 0=未跨
/// 检查点波）+残骸修复账（条数/总价/首败折扣）+「从检查点继续」可用位。
/// </summary>
internal sealed record DefeatPanelFacts(
    string LevelId,
    string LevelNameZh,
    string Reason,
    int RestoredFromWave,
    long RestoredAtTick,
    int CheckpointWave,
    int WreckCount,
    double RepairCostPoints,
    bool FirstDefeat,
    bool ContinueAvailable);

/// <summary>Construction probe result (loading-matrix face).</summary>
internal sealed record LevelSessionProbe(HandcraftedLevel Level, LevelSession Session, IReadOnlyList<SimCommand> Commands);

/// <summary>One pending cinematic the windowed UI must launch (settlement carries the decided performance).</summary>
internal sealed record CinematicLaunch(string ScriptId, DecidedVictoryPerformance? Settlement);
