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
    private ScrollContainer _panel = null!;
    private VBoxContainer _list = null!;
    private Label _statusLabel = null!;
    private readonly Dictionary<string, Button> _levelButtons = new();

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
            _panel.Visible = !_panel.Visible;
            RefreshMenu();
        };
        AddChild(_entryButton);

        _panel = new ScrollContainer
        {
            Position = new Vector2(170, 120),
            Size = new Vector2(430, 470),
            Visible = false,
        };
        var rows = new VBoxContainer();
        rows.AddChild(new Label { Text = "战役（六时代——从 UI 玩全战役）" });
        _list = new VBoxContainer();
        rows.AddChild(_list);
        var actionRow = new HBoxContainer();
        var exitButton = new Button { Text = "退出本关（存档续玩）" };
        exitButton.Pressed += OnExitPressed;
        actionRow.AddChild(exitButton);
        rows.AddChild(actionRow);
        _statusLabel = new Label
        {
            Text = "未开局",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(410, 60),
        };
        rows.AddChild(_statusLabel);
        _panel.AddChild(rows);
        AddChild(_panel);

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
        _defeatContinue.Pressed += () =>
        {
            _ = _host.ContinueFromCheckpoint();
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatContinue);
        _defeatRestart = new Button { Text = "重开本关" };
        _defeatRestart.Pressed += () =>
        {
            _host.RestartLevel();
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatRestart);
        _defeatMenu = new Button { Text = "退回菜单" };
        _defeatMenu.Pressed += () =>
        {
            _host.DismissDefeatToMenu();
            RefreshMenu();
        };
        defeatActions.AddChild(_defeatMenu);
        defeatRows.AddChild(defeatActions);
        _defeatPanel.AddChild(defeatRows);
        AddChild(_defeatPanel);
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

            if (_main is not null && Engine.GetProcessFrames() % 15 == 0)
            {
                _main.AdvisorPanel.RefreshHud(_board.BuildHudSnapshot()); // HUD 面板随战役会话
            }
        }
        else if (_board is not null || _viewport is { IsBound: true })
        {
            _board?.Dispose();
            _board = null;
            _viewport?.Release();
            SetFixtureHidden(false);
            if (_main is not null)
            {
                _main.AdvisorPanel.Bind(_main.BattleHost); // 还原夹具板（菜单态）
            }
        }
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
        RefreshStatus();
    }

    private void OnExitPressed()
    {
        if (!_host.InLevel)
        {
            return;
        }

        _host.ExitToMenu(); // Pause→SaveAndExitToMenu+campaign 域合并写（诚实存档续玩面）
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        foreach (KeyValuePair<string, Button> pair in _levelButtons)
        {
            pair.Value.QueueFree();
        }

        _levelButtons.Clear();
        foreach (CampaignChapter chapter in _host.Chapters())
        {
            _list.AddChild(new Label { Text = chapter.Title });
            foreach (CampaignLevelRow row in chapter.Rows)
            {
                var line = new HBoxContainer();
                string state = row.State switch
                {
                    CampaignLevelState.Locked => "未解锁",
                    CampaignLevelState.Playable => "可玩",
                    CampaignLevelState.Cleared => $"已通关 {new string('★', row.Stars)}",
                    _ => row.State.ToString(),
                };
                line.AddChild(new Label
                {
                    Text = $"{row.NameZh}（{row.Id}）{state}",
                    CustomMinimumSize = new Vector2(330, 0),
                });
                var button = new Button
                {
                    // 重玩挂账：CampaignProgression.RegisterCapture 单占领契约——
                    // 已通关关重 settle 会撞重复占领（REV §七遗留⑥）。
                    Text = row.State switch
                    {
                        CampaignLevelState.Cleared => "已通关",
                        CampaignLevelState.Locked => "未解锁",
                        _ => "进入",
                    },
                    Disabled = row.State != CampaignLevelState.Playable,
                };
                string id = row.Id;
                button.Pressed += () => OnLevelPressed(id);
                line.AddChild(button);
                _levelButtons[id] = button;
                _list.AddChild(line);
            }
        }
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

/// <summary>One menu row: the level plus its resolved state and star count.</summary>
internal sealed record CampaignLevelRow(string Id, string NameZh, CampaignLevelState State, int Stars);

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
