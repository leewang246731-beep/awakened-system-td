using EraCore.Agent.Governance;
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

    // —— 活动战役（窗口车道真机局面；headless 冒烟走沙盒根实例）——
    private CampaignLevelHost _host = null!;

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
            DrainPendingLaunches(); // 胜利结算/跃迁/终章演出（CinematicDirector 显式通道）
            RefreshMenu();          // 胜利/跃迁落地后菜单立即反映新进度态
        }

        if (_host.InLevel && Engine.GetProcessFrames() % 30 == 0)
        {
            RefreshStatus();
        }
    }

    /// <summary>胜利结算演出走 LaunchVictorySettlement（先定后演），其余走 Launch（可回看通道）.</summary>
    private void DrainPendingLaunches()
    {
        Cinematics.CinematicDirector? director = GetNodeOrNull<Cinematics.CinematicDirector>("../CinematicDirector");
        foreach (CinematicLaunch launch in _host.DrainPendingLaunches())
        {
            if (director is null)
            {
                continue;
            }

            if (launch.Settlement is { } decided)
            {
                _ = director.LaunchVictorySettlement(decided);
            }
            else
            {
                _ = director.Launch(launch.ScriptId);
            }
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
    /// 入账+★行落 VictorySettlementStore+E1-02 解锁；</item>
    /// <item>退出重进续玩：E1-02 中途退出（tick T 存档）→重进 TryResume 无损
    /// 续到 ≥T（level 域）+campaign 域合并写/读回（时代+进度态恢复）；</item>
    /// <item>遥测：source=script 沙盒行（kind=Campaign/era 对位/outcome=
    /// Victory+Abandoned+Resumed 三面）；</item>
    /// <item>跃迁负向探针：E1 final 未占领→EraTransitionRejectedException
    /// （UI 路径接的是真事务前置，非装饰）；终章播片二选一=系统开关位。</item>
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

        host.ClearPendingLaunches();

        // —— ③ 退出重进续玩：E1-02 中途退出→重进无损续——
        long resumeTick = 0;
        if (e102After is { State: CampaignLevelState.Playable })
        {
            host.BeginLevel(e102After.Id);
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

            host.BeginLevel(e102After.Id); // TryResume 路径：#exit 快照在位即续
            long resumedTick = host.Current?.Session.Sim.CurrentTick ?? -1;
            bool resumedFlag = host.Current?.ResumedFromExit ?? false;
            if (!resumedFlag || resumedTick < resumeTick)
            {
                problems.Add($"退出重进续玩失败：resumed={resumedFlag} tick={resumedTick}<{resumeTick}");
            }

            host.ExitToMenu();
        }

        // campaign 域读回：时代+进度态恢复（合并写不清账）
        CampaignLevelHost reloaded = CampaignLevelHost.Create(files, port, telemetry, TelemetrySources.Script);
        if (reloaded.EraIndex != CampaignState.E1EraIndex)
        {
            problems.Add($"campaign 域读回时代不符：{reloaded.EraIndex}");
        }

        CampaignLevelRow? reloadedE102 = reloaded.Chapters().First().Rows
            .FirstOrDefault(row => row.Id.EndsWith("e1-02"));
        if (reloadedE102 is not { State: CampaignLevelState.Playable })
        {
            problems.Add($"重进菜单进度态不符（E1-02 应可玩续）：{reloadedE102?.State}");
        }

        // —— ④ 遥测行（source=script 沙盒）——
        IReadOnlyList<LevelDurationRecord> ledger = LevelDurationTelemetry.ReadAll(files);
        bool hasVictoryRow = ledger.Any(row =>
            row.Kind == TelemetryLevelKind.Campaign && row.LevelId == "campaign:level-e1-01"
            && row.EraIndex == 1 && row.Outcome == TelemetryOutcome.Victory && row.PlayTicks > 0
            && row.Source == TelemetrySources.Script);
        bool hasAbandonedRow = ledger.Any(row =>
            row.Kind == TelemetryLevelKind.Campaign && row.Outcome == TelemetryOutcome.Abandoned
            && !row.Resumed && row.Source == TelemetrySources.Script);
        bool hasResumedRow = ledger.Any(row => row.Resumed);
        if (!hasVictoryRow || !hasAbandonedRow || !hasResumedRow)
        {
            problems.Add(
                $"遥测行不全：victory={hasVictoryRow} abandoned={hasAbandonedRow} " +
                $"resumed={hasResumedRow} rows={ledger.Count}");
        }

        // —— ⑤ 跃迁负向探针 + 终章二选一（开关位驱动）——
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
                $"resume=lossless@tick{resumeTick} save=campaign-merged-reload " +
                "telemetry=victory+abandoned+resumed source=script " +
                "transition=refused-without-final-capture finale=switch-picked " +
                $"rows=[{string.Join(" ", matrixRows)}]");
        }
        else
        {
            GD.PushError("CAMPAIGN_FAIL " + string.Join("; ", problems));
            GD.Print("CAMPAIGN_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
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

/// <summary>The live level binding of the host (session + its level row + resume flag).</summary>
internal sealed record CampaignRun(LevelSession Session, HandcraftedLevel Level, bool ResumedFromExit);

/// <summary>Construction probe result (loading-matrix face).</summary>
internal sealed record LevelSessionProbe(HandcraftedLevel Level, LevelSession Session, IReadOnlyList<SimCommand> Commands);

/// <summary>One pending cinematic the windowed UI must launch (settlement carries the decided performance).</summary>
internal sealed record CinematicLaunch(string ScriptId, DecidedVictoryPerformance? Settlement);
