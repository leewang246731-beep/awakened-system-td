using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Telemetry;
using EraCore.Units;
using Godot;

namespace EraTD;

// T-157 (F-006/C-21) 无尽开局 UI 两档接线 + 策略表宿主装载 + 真人局遥测
// （W9 批 1 Spec 审查点破的三缝——引擎面 T-154/T-155 已就绪，本文件是
// 表现层接线半边）：
//
//   ① 开局界面两档词缀节奏选择：开局面板显式两档（Rolling 滚词缀=生产
//      缺省 / ClassicFixed 经典 SP-7b 固定表），随 EndlessEntryRequest.
//      AffixSchedule 冻结入局；局内 HUD 明示行经只读查询位
//      EndlessSession.AffixSchedule（引擎侧留好的 C-21 接线位）。
//   ② endless-policies.json 宿主装载：宿主启动读表递交
//      EndlessEntryRequest.PolicyTable（EndlessPolicyTable 装载器已存在）
//      ——「改表即改产」由此成立；漏表=null 递交，引擎走 REV-20261018-T154
//      §5 既裁定的无表兜底链（EndlessMixPolicy.Default 1:1 固定档），
//      本层不改不吞该行为。
//   ③ 真人局遥测记录：无尽局接 EndlessTelemetry 组合件三调用
//      （BeginRun 开局 / WaveLedgerTicks 逐波台账 / End 终局）——窗口
//      （玩家）局自动入账 source=player；headless 冒烟局走 source=script
//      分账（T-155 口径，冒烟沙盒根绝不污染玩家账本）。
//
// 承载面纪律（NetMenuUi 同律）：
//  - headless（G4 冒烟）：自带零外设冒烟——真表装载+改表即改产+漏表兜底
//    镜像+两档分账+遥测三调用落账，打印 ENDLESS_OK（任一自检不过 Quit(1)）；
//  - 窗口运行：开局面板选档开局，真实时间波钟驱动（每波 IntervalSeconds
//      台账），死亡终局记分/弃局诚实退出，遥测随终局落账；
//  - 单机零耦合：本 UI 自持无尽会话（EndlessEntry.Begin 组装 seam——
//      EndlessEntry 文档裁定的正式入口），不触碰 Main 的 SimHost/桥/渲染
//      泳道；Main.cs 仅追加一行实例化（并行纪律：冲突由后提交者 rebase）。

/// <summary>
/// The T-157 endless opening face: affix-schedule two-tier chooser (T-154
/// owner ruling wired), the run HUD line mirroring the frozen schedule, the
/// host-side policy-table load, and the endless duration-telemetry lane.
/// </summary>
public sealed partial class EndlessMenuUi : Control
{
    private const long SmokeSeed = 20261190; // 与 Endless 测试族同带的独立种子
    private const int SmokeWaves = 5;        // 遥测道波数（台账样本）
    private const int SmokeTierWaves = 45;   // 两档分账道波数（词缀面 41 波起分野——EndlessEntryTests 同带）

    private readonly bool _headless;

    private Button _entryButton = null!;
    private PanelContainer _panel = null!;
    private OptionButton _scheduleField = null!;
    private OptionButton _eraField = null!;
    private Label _scheduleHint = null!;
    private Label _statusLabel = null!;
    private Button _startButton = null!;
    private Button _dieButton = null!;
    private Button _abandonButton = null!;

    // —— 活动无尽会话（窗口车道）——
    private EndlessSession? _session;
    private LevelDurationTelemetry? _telemetry;
    private LevelDurationTracker? _tracker;
    private long _waveLedgerTicks;      // EndlessTelemetry.WaveLedgerTicks 累计（主口径）
    private double _waveSecondsElapsed; // 波内真实时间累计（窗口波钟）
    private double _waveIntervalSeconds = 120.0; // 活动波间隔（开波计划 IntervalSeconds——SP-7a 冻结律，数据面）
    private bool _waveActive;

    private EndlessMenuUi()
    {
        _headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
    }

    /// <summary>Code-built instantiation (no .tscn — NetMenuUi 同律，包装任务另立).</summary>
    public static EndlessMenuUi Instantiate(Control host)
    {
        var ui = new EndlessMenuUi
        {
            Name = "EndlessMenuUi",
        };
        ui.BuildUi();
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
            Text = "无尽",
            Position = new Vector2(856, 8),
            Size = new Vector2(72, 30),
        };
        _entryButton.Pressed += () => _panel.Visible = !_panel.Visible;
        AddChild(_entryButton);

        _panel = new PanelContainer
        {
            Position = new Vector2(170, 48),
            Size = new Vector2(380, 300),
            Visible = false,
        };
        var rows = new VBoxContainer();
        _panel.AddChild(rows);

        rows.AddChild(new Label { Text = "无尽模式（任选时代开局）" });

        rows.AddChild(new Label { Text = "词缀节奏（开局两档，整局冻结）" });
        _scheduleField = new OptionButton();
        _scheduleField.AddItem("滚词缀（缺省：每 10 波滚换激活表）", (int)EndlessAffixSchedule.Rolling);
        _scheduleField.AddItem("经典固定表（SP-7b 冻结累加表）", (int)EndlessAffixSchedule.ClassicFixed);
        _scheduleField.Selected = (int)EndlessAffixSchedule.Rolling; // T-154 生产缺省=滚词缀
        _scheduleField.ItemSelected += _ => RefreshScheduleHint();
        rows.AddChild(_scheduleField);
        _scheduleHint = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(360, 34),
        };
        rows.AddChild(_scheduleHint);
        RefreshScheduleHint();

        rows.AddChild(new Label { Text = "开局时代（已装载内容集动态枚举）" });
        _eraField = new OptionButton();
        foreach ((int era, string name) in EndlessHostContent.Eras())
        {
            _eraField.AddItem($"E{era} {name}", era);
        }

        if (_eraField.ItemCount > 0)
        {
            _eraField.Selected = 0;
        }

        rows.AddChild(_eraField);

        _startButton = new Button { Text = "开局" };
        _startButton.Pressed += OnStartPressed;
        rows.AddChild(_startButton);

        var terminalRow = new HBoxContainer();
        _dieButton = new Button { Text = "死亡终局（记分）" };
        _dieButton.Pressed += OnDiePressed;
        _abandonButton = new Button { Text = "弃局（不记分）" };
        _abandonButton.Pressed += OnAbandonPressed;
        terminalRow.AddChild(_dieButton);
        terminalRow.AddChild(_abandonButton);
        rows.AddChild(terminalRow);

        _statusLabel = new Label
        {
            Text = "未开局",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(360, 84),
        };
        rows.AddChild(_statusLabel);

        AddChild(_panel);
    }

    private void RefreshScheduleHint() => _scheduleHint.Text =
        Schedule() == EndlessAffixSchedule.Rolling
            ? "滚词缀：每 10 波块界滚换激活词缀表（量级/叠加=SP-7b 冻结集）。"
            : "经典固定表：41 迅捷/51 硬化/61 相位/71 自爆强化逐档累加（回切档）。";

    private EndlessAffixSchedule Schedule() => (EndlessAffixSchedule)(_scheduleField?.Selected ?? 0);

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_headless)
        {
            return; // headless 冒烟在 Instantiate 内同步跑完；窗口车道才有帧循环
        }

        PumpWindowSession(delta);
    }

    // ------------------------------------------------------ windowed wiring

    /// <summary>Builds the session from the panel selection and starts the telemetry lane.</summary>
    private void OnStartPressed()
    {
        if (_session is not null)
        {
            return;
        }

        int era = _eraField.Selected >= 0 ? _eraField.GetItemId(_eraField.Selected) : 1;
        long seed = DateTimeOffset.UtcNow.UtcTicks; // 真人局种子=开局时刻（每局身份不同）
        _session = EndlessHostContent.BeginSession(
            seed, era, Schedule(), TelemetrySources.Player, out _telemetry,
            EndlessHostContent.LoadPolicyTable()); // ② 生产装载：改表即改产（漏表=null→兜底链）
        _tracker = _telemetry is null ? null : EndlessTelemetry.BeginRun(
            _telemetry, era, seed, TelemetrySources.Player); // ③ 调用一：BeginRun
        _waveLedgerTicks = 0;
        _waveSecondsElapsed = 0;
        BeginNextWave();
        _entryButton.Text = "无尽·进行中";
        RefreshStatus();
    }

    /// <summary>死亡终局：Die() 记分 + 遥测 End(Defeat)——无尽唯一记分终态。</summary>
    private void OnDiePressed()
    {
        if (_session is not { State: EndlessSessionState.Running } || _tracker is null)
        {
            return;
        }

        EndlessSettlement settlement = _session.Die();
        _tracker.End(TelemetryOutcome.Defeat, _waveLedgerTicks);
        _session.Return();
        _entryButton.Text = $"无尽·{settlement.Score:0} 分";
        CloseSession();
    }

    /// <summary>弃局：诚实不记分退出 + 遥测 End(Abandoned)。</summary>
    private void OnAbandonPressed()
    {
        if (_session is not { State: EndlessSessionState.Running })
        {
            return;
        }

        _session.Abandon();
        _tracker?.End(TelemetryOutcome.Abandoned, _waveLedgerTicks);
        _session.Return();
        _entryButton.Text = "无尽";
        CloseSession();
    }

    private void CloseSession()
    {
        _session = null;
        _tracker = null;
        _telemetry = null;
        _waveActive = false;
        RefreshStatus();
    }

    /// <summary>
    /// Window wave clock: the run is host-paced — one wave per the ACTIVE
    /// wave plan's <see cref="EndlessWavePlan.IntervalSeconds"/>（SP-7a 冻结律
    /// 的数据面——EndlessRules.PlanWave 产出，非本层常数），the ledger
    /// accumulates <see cref="EndlessTelemetry.WaveLedgerTicks"/> per BEGUN
    /// wave（主口径，与帧率无关——遥测账本不吃真实时间）。
    /// </summary>
    private void PumpWindowSession(double delta)
    {
        if (_session is not { State: EndlessSessionState.Running } || !_waveActive)
        {
            return;
        }

        _waveSecondsElapsed += delta;
        if (_waveSecondsElapsed >= _waveIntervalSeconds)
        {
            _waveSecondsElapsed = 0;
            _session.Run.CompleteWave(_pendingEliteKills, _pendingBossKills);
            BeginNextWave();
            RefreshStatus();
        }
    }

    private long _pendingEliteKills;
    private long _pendingBossKills;

    private void BeginNextWave()
    {
        if (_session is not { State: EndlessSessionState.Running })
        {
            return;
        }

        EndlessWavePlan plan = _session.Run.BeginWave();
        _waveLedgerTicks += EndlessTelemetry.WaveLedgerTicks(plan); // 台账在开波时累计（begun wave 口径）
        _waveIntervalSeconds = plan.IntervalSeconds; // 窗口波钟跟随活动波计划（数据面）
        _pendingEliteKills = plan.EliteCount;
        _pendingBossKills = plan.BossCount;
        _waveActive = true;
    }

    private void RefreshStatus()
    {
        if (_session is not { } session)
        {
            _statusLabel.Text = "未开局";
            return;
        }

        // ① 本局档位明示：只读查询位 EndlessSession.AffixSchedule（整局冻结）
        string scheduleZh = session.AffixSchedule == EndlessAffixSchedule.Rolling
            ? "滚词缀（每 10 波滚换）"
            : "经典固定表（SP-7b 冻结累加）";
        _statusLabel.Text =
            $"状态={session.State} 波={session.Run.CurrentWave} " +
            $"参与档={session.Participation.LockedMode}\n" +
            $"本局词缀节奏={scheduleZh}\n" +
            $"遥测台账={_waveLedgerTicks} tick（主口径，终局落账）";
    }

    // ------------------------------------------------- headless smoke lane

    /// <summary>
    /// G4 冒烟（零外设）：①真表装载（endless-policies.json 从仓库读到）
    /// +「改表即改产」（改 ramp 终值的表文本→会话配置随之变化）+漏表兜底
    /// 镜像（缺文件=null→无表兜底档）；②两档分账（Rolling 缺省/ClassicFixed
    /// 回切——档位查询位+同 seed 两档哈希分野）；③遥测三调用（BeginRun/
    /// 逐波台账/End）双终局（死亡+弃局）落账断言（source=script 沙盒根）。
    /// 打印 ENDLESS_OK；任一自检不过 Quit(1)。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        var problems = new List<string>();

        // ---- ② 策略表宿主装载 ------------------------------------------------
        EndlessPolicyTable? table = EndlessHostContent.LoadPolicyTable();
        if (table is null)
        {
            problems.Add("endless-policies.json 未从仓库装载到（宿主装载缝失效）");
        }

        // 改表即改产：同一 schema 下改 mix 渐升终值 → 入口解析出的生产档随之
        // 变化（证明表→产链路，不写仓库文件）。
        const string edited = """
            {
              "$schema": "urn:eratd:content:endless:policies:1",
              "content_type": "EndlessPolicyTable",
              "mix": {
                "mix_from_wave": 41,
                "start_era_weight": 9.0,
                "other_era_weight": 1.0,
                "ramp_end_wave": 61,
                "end_start_era_weight": 5.0,
                "end_other_era_weight": 1.0
              },
              "affix_rolls": {
                "roll_every_waves": 10,
                "roll_start_wave": 21,
                "kinds_per_roll": 2,
                "allow_consecutive_repeat": false,
                "repeat_intensity_gain": 1.25
              }
            }
            """;
        EndlessPolicyTable editedTable = EndlessPolicyTable.Parse(edited, "smoke-edited");
        EndlessSession repoSession = EndlessHostContent.BeginSession(
            SmokeSeed, 1, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _,
            policyTable: table);
        EndlessSession editedSession = EndlessHostContent.BeginSession(
            SmokeSeed, 1, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _,
            policyTable: editedTable);
        if (repoSession.Run.Config.MixPolicy is not { } repoMix
            || editedSession.Run.Config.MixPolicy is not { } editedMix
            || Math.Abs(repoMix.WeightsAt(81).StartEraWeight - editedMix.WeightsAt(81).StartEraWeight) < 1e-9)
        {
            problems.Add("改表未改产：渐升终值不随表文本变化");
        }

        // 漏表兜底：TryLoad 缺文件 → null → 无表兜底档（REV-20261018-T154 §5 原文行为）
        if (EndlessHostContent.TryLoadPolicyTable(
                System.IO.Path.Combine("game", "content", "endless", "does-not-exist.json")) is not null)
        {
            problems.Add("漏表应回 null（无表兜底链），实得表");
        }

        EndlessSession bareSession = EndlessHostContent.BeginSession(
            SmokeSeed, 1, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _,
            policyTable: null);
        if (bareSession.Run.Config.MixPolicy != EndlessMixPolicy.Default)
        {
            problems.Add("漏表会话未走无表兜底档（EndlessMixPolicy.Default）");
        }

        // ---- ① 两档分账 ------------------------------------------------------
        EndlessSession rolling = EndlessHostContent.BeginSession(
            SmokeSeed, 3, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _, table);
        EndlessSession classic = EndlessHostContent.BeginSession(
            SmokeSeed, 3, EndlessAffixSchedule.ClassicFixed, TelemetrySources.Script, out _, table);
        if (rolling.AffixSchedule != EndlessAffixSchedule.Rolling
            || classic.AffixSchedule != EndlessAffixSchedule.ClassicFixed)
        {
            problems.Add("档位查询位（EndlessSession.AffixSchedule）未随开局选择镜像");
        }

        if (classic.Run.Config.AffixRolls is not null)
        {
            problems.Add("ClassicFixed 档不应携带滚动策略（SP-7b 冻结表）");
        }

        DriveWaves(rolling, SmokeTierWaves);
        DriveWaves(classic, SmokeTierWaves);
        if (rolling.Run.ComputeStateHash() == classic.Run.ComputeStateHash())
        {
            problems.Add("两档同 seed 哈希应分野（换档=换运行身份——分账纪律）");
        }

        // ---- ③ 遥测三调用（沙盒根，source=script） ---------------------------
        string smokeRoot = System.IO.Path.Combine(
            EraCore.IO.DevFileStore.DefaultDevRoot, ".tmp-tests", "endless-smoke");
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        var files = new EraCore.IO.DevFileStore(smokeRoot);
        var clock = new SteppedClock();
        var telemetry = new LevelDurationTelemetry(files, clock);

        // 死亡终局道：BeginRun → 逐波台账 → Die → End(Defeat)
        EndlessSession dieSession = EndlessHostContent.BeginSession(
            SmokeSeed + 1, 1, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _, table);
        LevelDurationTracker tracker = EndlessTelemetry.BeginRun(
            telemetry, dieSession.Run.Config.StartEraIndex, dieSession.Run.Config.Seed,
            TelemetrySources.Script);
        long dieLedger = 0;
        for (int wave = 0; wave < SmokeWaves; wave++)
        {
            EndlessWavePlan plan = dieSession.Run.BeginWave();
            dieLedger += EndlessTelemetry.WaveLedgerTicks(plan);
            dieSession.Run.CompleteWave(plan.EliteCount, plan.BossCount);
            clock.AdvanceMs((long)(plan.IntervalSeconds * 1000));
        }

        dieSession.Die();
        LevelDurationRecord dieRecord = tracker.End(TelemetryOutcome.Defeat, dieLedger);
        dieSession.Return();

        // 弃局道：Abandon → End(Abandoned)
        EndlessSession abandonSession = EndlessHostContent.BeginSession(
            SmokeSeed + 2, 1, EndlessAffixSchedule.Rolling, TelemetrySources.Script, out _, table);
        LevelDurationTracker abandonTracker = EndlessTelemetry.BeginRun(
            telemetry, abandonSession.Run.Config.StartEraIndex, abandonSession.Run.Config.Seed,
            TelemetrySources.Script);
        EndlessWavePlan onlyWave = abandonSession.Run.BeginWave();
        long abandonLedger = EndlessTelemetry.WaveLedgerTicks(onlyWave);
        abandonSession.Run.CompleteWave(onlyWave.EliteCount, onlyWave.BossCount);
        clock.AdvanceMs(1000);
        abandonSession.Abandon();
        LevelDurationRecord abandonRecord = abandonTracker.End(TelemetryOutcome.Abandoned, abandonLedger);
        abandonSession.Return();

        IReadOnlyList<LevelDurationRecord> ledger = LevelDurationTelemetry.ReadAll(files);
        if (ledger.Count != 2)
        {
            problems.Add($"遥测账本应 2 行（死亡+弃局），实得 {ledger.Count}");
        }
        else
        {
            if (dieRecord.Kind != TelemetryLevelKind.Endless
                || dieRecord.LevelId != LevelTelemetryScope.EndlessId
                || dieRecord.Outcome != TelemetryOutcome.Defeat
                || dieRecord.Source != TelemetrySources.Script
                || dieRecord.PlayTicks != dieLedger
                || dieRecord.PlayTicks <= 0)
            {
                problems.Add($"死亡终局行不符：{dieRecord}");
            }

            if (abandonRecord.Outcome != TelemetryOutcome.Abandoned
                || abandonRecord.Source != TelemetrySources.Script
                || abandonRecord.PlayTicks != abandonLedger)
            {
                problems.Add($"弃局行不符：{abandonRecord}");
            }

            if (ledger[0].WallMs <= 0 || ledger[1].WallMs <= 0)
            {
                problems.Add("墙钟参考列未随注入时钟记录");
            }
        }

        // 沙盒清场（dev 根 .tmp-tests 下，gitignored）
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"ENDLESS_OK schedule=2tiers table=loaded edit-table=changes-production " +
                $"missing-table=fallback-default tiers=rolling+classic hash=divergent " +
                $"telemetry=3calls(defeat+abandoned) ledger_rows=2 source=script " +
                $"waves={SmokeWaves} play_ticks={dieLedger} eras={EndlessHostContent.Eras().Count}");
        }
        else
        {
            GD.PushError("ENDLESS_FAIL " + string.Join("; ", problems));
            GD.Print("ENDLESS_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
    }

    /// <summary>Drives N begin/complete cycles (elite/boss kills fed back — EndlessEntryTests 同律).</summary>
    private static void DriveWaves(EndlessSession session, int waves)
    {
        for (int wave = 0; wave < waves; wave++)
        {
            EndlessWavePlan plan = session.Run.BeginWave();
            session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
        }
    }

    /// <summary>Stepped fake wall clock（冒烟确定性：墙钟列随波间隔步进）.</summary>
    private sealed class SteppedClock : IClock
    {
        private long _nowMs;

        public long Tick => 0;

        public long NowMs => _nowMs;

        public void AdvanceMs(long delta) => _nowMs += delta;
    }
}

/// <summary>
/// Host-side content lane for the endless face (T-157 ②): repo walk-up loads
/// the era sets (dynamic enumeration — era dirs probed while present, 时代数
/// 不落码面), the policy table, and composes real <see cref="EndlessSession"/>
/// starts through the production entry seam. Missing table = null (the engine
/// fallback chain stays the REV-20261018-T154 ruling — this layer never
/// invents a replacement table).
/// </summary>
internal static class EndlessHostContent
{
    /// <summary>
    /// Loads the production policy table from
    /// <c>game/content/endless/endless-policies.json</c>; null when the file
    /// is absent (漏表=无表兜底链，REV 口径不变). Malformed tables throw —
    /// a present-but-broken production table is a bug, not a fallback case.
    /// </summary>
    public static EndlessPolicyTable? LoadPolicyTable() =>
        TryLoadPolicyTable(System.IO.Path.Combine("game", "content", "endless", "endless-policies.json"));

    /// <summary>Same load against an explicit repo-relative path (smoke lanes probe misses).</summary>
    public static EndlessPolicyTable? TryLoadPolicyTable(string relativePath)
    {
        string? text = ReadRepoTextOrNull(relativePath);
        return text is null ? null : EndlessPolicyTable.Parse(text, "endless/endless-policies.json");
    }

    /// <summary>
    /// The loaded era list (dynamic): (era index, Chinese name) for every
    /// <c>game/content/era{N}</c> directory present, in ascending order.
    /// </summary>
    public static IReadOnlyList<(int Era, string NameZh)> Eras()
    {
        var eras = new List<(int, string)>();
        string? root = FindRepoPath(System.IO.Path.Combine("game", "content"));
        if (root is null)
        {
            return eras;
        }

        for (int era = 1; ; era++)
        {
            string dir = System.IO.Path.Combine(root, $"era{era}");
            if (!System.IO.Directory.Exists(dir))
            {
                break;
            }

            string nameZh = $"E{era}";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "era.json")));
                if (doc.RootElement.TryGetProperty("items", out JsonElement items)
                    && items.ValueKind == JsonValueKind.Array
                    && items.GetArrayLength() > 0
                    && items[0].TryGetProperty("name_zh", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    nameZh = name.GetString() ?? nameZh;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // 命名面缺失不阻断开局面（era.json 解析失败=显示名回落 E{N}）
            }

            eras.Add((era, nameZh));
        }

        return eras;
    }

    /// <summary>
    /// Composes one real endless session through <see cref="EndlessEntry.Begin"/>
    /// with the loaded era sets and the caller-supplied policy table (null =
    /// 漏表——引擎走 REV-20261018-T154 无表兜底链，本层不自动补装); for
    /// player-source sittings it also hands out a telemetry service over the
    /// D-03 dev root (the caller owns the three-call tracker lane —
    /// BeginRun/WaveLedger/End per the EndlessTelemetry composition ruling).
    /// </summary>
    public static EndlessSession BeginSession(
        long seed,
        int startEra,
        EndlessAffixSchedule schedule,
        string source,
        out LevelDurationTelemetry? telemetry,
        EndlessPolicyTable? policyTable)
    {
        var content = new List<EndlessEraContent>();
        foreach ((int era, _) in Eras())
        {
            string dir = FindRepoPath(System.IO.Path.Combine("game", "content", $"era{era}"))!;
            string enemiesJson = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "enemies.json"));
            var catalog = EnemySpecCatalog.Parse(enemiesJson);
            using JsonDocument doc = JsonDocument.Parse(enemiesJson);
            IReadOnlyList<EnemySpec> enemies = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => catalog.Get(item.GetProperty("id").GetString()!))
                .OrderBy(spec => spec.Id, StringComparer.Ordinal)
                .ToArray();
            (int Base, int Begin, int Spacing, double Scaling) shape = LevelShape(era);
            content.Add(new EndlessEraContent(era, enemies, shape.Base, shape.Begin, shape.Spacing, shape.Scaling));
        }

        var request = new EndlessEntryRequest(
            seed,
            startEra,
            EndlessParticipationMode.Manual,
            ThreatLevel: 0.9,
            EraContent: content,
            WonderIds: Array.Empty<string>(),
            AffixSchedule: schedule,
            PolicyTable: policyTable);

        if (source == TelemetrySources.Player)
        {
            // 真人局：D-03 dev 根账本 + 墙钟注入（CS-1.4——墙钟经 IClock 入口）。
            telemetry = new LevelDurationTelemetry(new EraCore.IO.DevFileStore(), new WallClock());
        }
        else
        {
            telemetry = null; // script 局的沙盒账本由冒烟自建（分账纪律）
        }

        return EndlessEntry.Begin(request, new SplitMix64Rng(seed));
    }

    /// <summary>First level's wave shape of the era (levels/e{N}.json 首关 waves 行——数据面不落码).</summary>
    private static (int Base, int Begin, int Spacing, double Scaling) LevelShape(int era)
    {
        string path = System.IO.Path.Combine("game", "content", "levels", $"e{era}.json");
        string? text = ReadRepoTextOrNull(path)
            ?? throw new InvalidOperationException($"未找到 game/content/levels/e{era}.json（无尽宿主须在仓库内运行）");
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement waves = doc.RootElement.GetProperty("items")[0].GetProperty("waves");
        return (
            waves.GetProperty("base_count").GetInt32(),
            waves.GetProperty("begin_wave").GetInt32(),
            waves.GetProperty("spacing").GetInt32(),
            waves.GetProperty("scaling").GetDouble());
    }

    private static string? ReadRepoTextOrNull(string relativePath)
    {
        string? full = FindRepoPath(relativePath);
        return full is null ? null : System.IO.File.ReadAllText(full);
    }

    private static string? FindRepoPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relativePath);
            if (System.IO.File.Exists(probe) || System.IO.Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        return null;
    }

    /// <summary>Real wall clock (window lane only — player ledger reference column).</summary>
    internal sealed class WallClock : IClock
    {
        public long Tick => 0;

        public long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>SplitMix64 IRng seam (K-1 port; NetDemoLevel 同款——同 seed 同流).</summary>
    internal sealed class SplitMix64Rng(long seed) : IRng
    {
        private ulong _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxValue));
            }

            long span = (long)maxValue - minValue;
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
}
