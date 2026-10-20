using System.Text.Json;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Persistence;
using EraCore.Procedural;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Units;
using Godot;

namespace EraTD;

// T-157 (F-007/C-21) 程序关表现层入口按钮（W8 挂账 ⑤——ProceduralCampaignEntry
// 数据位 T-140 已就绪，本文件是最小 UI 面）：
//
//   入口+档位选择 = 「程序关」按钮 → 面板（档位下拉=procedural.json tiers
//     行、种子输入、「生成并开局」）；入口数据位经 ProceduralEntryHost 从
//     game/content/campaign/procedural.json 装载即校验（RL7——档位/约束/
//     时带全部活注册表，宿主递交流）。
//   起局 = ProceduralLevelGenerator 生成（同 seed 同图）→ 直接从生成记录
//     组装 SimulationConfig + 波锚命令流（HandcraftedLevelAssemblyTests
//     同构组装律）→ LevelSession（战役外特殊会话，不进战役图）。
//   遥测 = ③ 程序关半边：LevelSessionConfig.Telemetry + durationTelemetry
//     双注入（T-155 留口）——窗口局 source=player 自动入账；headless 冒烟
//     source=script 沙盒分账。
//
// 承载面纪律（NetMenuUi/EndlessMenuUi 同律）：headless 自带冒烟（真注册表
// 装载+同 seed 双生成哈希全等+起局+遥测弃局行落账），打印 PROC_OK（任一
// 自检不过 Quit(1)）；窗口运行=面板选档生成开局，帧泵推进，退出=诚实弃局
// 行（SaveAndExitToMenu——LevelSession 既有结算钩，遥测随终局自动落账）。

/// <summary>
/// The T-157 procedural-entry minimal face: entry button, tier ladder
/// chooser (registry rows), seeded generation into a live
/// <see cref="LevelSession"/>, and the procedural telemetry lane.
/// </summary>
public sealed partial class ProceduralMenuUi : Control
{
    private const long SmokeSeed = 20261201;

    // —— T-162 模式卡片化版式常量（640×360 画布域） ——
    private const float ModeBannerHeight = 84f; // banner-mode-procedural 主视觉横裁带

    private readonly bool _headless;

    private Button _entryButton = null!;
    private PanelContainer _panel = null!;
    private OptionButton _tierField = null!;
    private LineEdit _seedField = null!;
    private Button _startButton = null!;
    private Button _exitButton = null!;
    private Label _statusLabel = null!;
    private Button _menuReturnButton = null!;

    // —— 活动程序关会话（窗口车道）——
    private LevelSession? _session;
    private ProceduralLevel? _level;
    private LevelDurationTelemetry? _telemetry;

    // —— T-160b 战场渲染绑定（同战役律：视口=会话泳道自持渲染件，开局
    //    Bind/泵帧，退出 Release+夹具层恢复——SimHost 夹具路径不动）——
    private SessionBattlefieldViewport? _viewport;
    private bool _fixtureHidden;

    private ProceduralMenuUi()
    {
        _headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
    }

    /// <summary>Code-built instantiation (no .tscn — NetMenuUi 同律).</summary>
    public static ProceduralMenuUi Instantiate(Control host)
    {
        var ui = new ProceduralMenuUi
        {
            Name = "ProceduralMenuUi",
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
            Text = "程序关",
            Position = new Vector2(764, 8),
            Size = new Vector2(80, 30),
        };
        _entryButton.Pressed += () => _panel.Visible = !_panel.Visible;
        AddChild(_entryButton);
        if (!_headless)
        {
            _entryButton.Hide(); // T-161 入口归拢：窗口态入口在主菜单（headless 冒烟照旧）
        }

        // T-162 模式卡片化：面板=模式卡主视觉（banner-mode-procedural 横裁带）
        // + 选项场（档位/种子/行动排），640×360 画布域。
        _panel = new PanelContainer
        {
            Position = new Vector2(28, 14),
            Size = new Vector2(584, 300),
            Visible = false,
        };
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 5);
        _panel.AddChild(rows);

        rows.AddChild(BuildModeBanner());

        rows.AddChild(new Label { Text = "档位（procedural.json tiers 行——同 seed 同图）" });
        _tierField = new OptionButton();
        ProceduralCampaignEntry entry = ProceduralEntryHost.LoadEntry();
        foreach (ProceduralTier tier in entry.Tiers)
        {
            _tierField.AddItem($"{tier.NameZh}（{tier.Id}）", _tierField.ItemCount);
        }

        int defaultIndex = Array.FindIndex(
            entry.Tiers.ToArray(), tier => tier.Id == entry.DefaultTierId);
        _tierField.Selected = Math.Max(0, defaultIndex);
        rows.AddChild(_tierField);

        var seedRow = new HBoxContainer();
        seedRow.AddChild(new Label { Text = "种子 " });
        _seedField = new LineEdit { Text = SmokeSeed.ToString(), CustomMinimumSize = new Vector2(160, 0) };
        seedRow.AddChild(_seedField);
        rows.AddChild(seedRow);

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        _startButton = new Button { Text = "生成并开局" };
        _startButton.Pressed += OnStartPressed;
        _exitButton = new Button { Text = "退出本关（记弃局）" };
        _exitButton.Pressed += OnExitPressed;
        _menuReturnButton = new Button { Text = "返回主菜单" };
        _menuReturnButton.Pressed += CloseFromMenu; // T-161：会话退出→回主菜单（承载钮）
        actionRow.AddChild(_startButton);
        actionRow.AddChild(_exitButton);
        actionRow.AddChild(_menuReturnButton);
        rows.AddChild(actionRow);

        _statusLabel = new Label
        {
            Text = "未开局",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(560, 56),
        };
        rows.AddChild(_statusLabel);

        AddChild(_panel);
    }

    /// <summary>模式卡主视觉：banner-mode-procedural 横裁带+模式名叠图（缺图=垫底带）.</summary>
    private Control BuildModeBanner()
    {
        var strip = new Control { CustomMinimumSize = new Vector2(560, ModeBannerHeight) };
        var banner = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        banner.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        banner.MouseFilter = Control.MouseFilterEnum.Ignore;
        _bannerFiles ??= Expand.ExpandAssetPaths.BannerFileNames();
        string? file = EraCore.Presentation.LevelCardDecisions.PickBannerFile(
            EraCore.Presentation.LevelCardDecisions.ProceduralModeBannerStem, _bannerFiles);
        banner.Texture = (file is null
                ? null
                : Expand.ExpandAssetPaths.LoadPng(Expand.ExpandAssetPaths.BannerPath(file)))
            ?? ModePlaceholder();
        strip.AddChild(banner);

        var scrim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.45f) };
        scrim.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        scrim.OffsetTop = -20f;
        scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
        strip.AddChild(scrim);

        var title = new Label
        {
            Text = "程序生成关卡 · 战役外入口",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        title.OffsetTop = -20f;
        title.OffsetBottom = -1;
        title.OffsetLeft = 6;
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", Colors.White);
        title.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        title.AddThemeConstantOverride("outline_size", 7);
        title.MouseFilter = Control.MouseFilterEnum.Ignore;
        strip.AddChild(title);
        return strip;
    }

    private IReadOnlyList<string>? _bannerFiles;

    /// <summary>缺图垫底带（中性深蓝回退——卡片结构不因缺图塌陷）.</summary>
    private static Godot.Texture2D ModePlaceholder()
    {
        var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(new Color(0.10f, 0.14f, 0.19f, 1f));
        return ImageTexture.CreateFromImage(image);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_headless)
        {
            return;
        }

        PumpWindowSession();
    }

    // ------------------------------------------------------ windowed wiring

    private void OnStartPressed()
    {
        if (_session is not null)
        {
            return;
        }

        ProceduralCampaignEntry entry = ProceduralEntryHost.LoadEntry();
        int tierIndex = _tierField.Selected >= 0 ? _tierField.Selected : 0;
        ProceduralTier tier = entry.Tiers[Math.Clamp(tierIndex, 0, entry.Tiers.Count - 1)];
        if (!long.TryParse(_seedField.Text.Trim(), out long seed))
        {
            seed = DateTimeOffset.UtcNow.UtcTicks; // 非法输入回落=时刻种子（每局新图）
        }

        // ③ 程序关半边：LevelSessionConfig.Telemetry + durationTelemetry 双注入
        //（T-155 留口）——真人局 source=player，D-03 dev 根账本+墙钟注入。
        _telemetry = new LevelDurationTelemetry(new DevFileStore(), new EndlessHostContent.WallClock());
        _level = ProceduralEntryHost.Generate(seed, tier, entry);
        _session = ProceduralEntryHost.StartSession(
            _level, entry, _telemetry, new LevelTelemetryScope(
                TelemetryLevelKind.Procedural, _level.Id, EraIndexOf(_level.Era), _level.Map.Seed,
                TelemetrySources.Player),
            new JsonFileSavePort(new DevFileStore(), $"procedural/{seed}"));
        _session.Machine.StartLevel();
        _entryButton.Text = "程序关·进行中";
        RefreshStatus();
    }

    private void OnExitPressed()
    {
        if (_session is null)
        {
            return;
        }

        // 诚实弃局：LevelSession 既有结算钩（Pause→SaveAndExitToMenu——
        // 流程机单终态合法性）内部落 TelemetryOutcome.Abandoned 行——遥测
        // 随终局自动入账，无需本层补调。
        _session.Pause();
        _session.SaveAndExitToMenu();
        _entryButton.Text = "程序关";
        _session = null;
        _level = null;
        _telemetry = null;
        ReleaseBattlefield(); // T-160b：退出恢复菜单态（视口释放+夹具层恢复）
        RefreshStatus();
    }

    // ------------------------------------------- T-161 主菜单入口归拢（窗口车道）

    /// <summary>主菜单「程序关卡」入口打开面板（headless 冒烟不走此路）.</summary>
    internal void OpenFromMenu() => _panel.Visible = true;

    /// <summary>
    /// 返回主菜单：局在跑=诚实弃局（既有 OnExitPressed 同一条路）后收面板
    /// ——「会话退出→回主菜单」链路的程序关半边。
    /// </summary>
    internal void CloseFromMenu()
    {
        if (_session is { Machine.State: GameFlowState.InLevel })
        {
            OnExitPressed();
        }

        _panel.Visible = false;
    }

    /// <summary>程序关会话在局位（主菜单可见性谓词探针；终局后的 Menu态残留不计）.</summary>
    internal bool SessionActive => _session is { Machine.State: GameFlowState.InLevel };

    /// <summary>程序关面板打开位（主菜单可见性谓词探针）.</summary>
    internal bool PanelOpen => _panel.Visible;

    /// <summary>「返回主菜单」承载钮真信号驱动（--shot 验收钩子——与玩家点击同一条链）.</summary>
    internal void PressMenuReturn() => _menuReturnButton.EmitSignal(Button.SignalName.Pressed);

    private void PumpWindowSession()
    {
        if (_session is not { Machine.State: GameFlowState.InLevel } || _level is null)
        {
            if (_viewport is { IsBound: true })
            {
                ReleaseBattlefield(); // 终局（胜利/弃局后续）同退出律：恢复菜单态
            }

            return;
        }

        _session.RunFor(1); // 1x 帧律：一帧一 tick（PauseClock 接线随战场宿主面）

        // T-160b 战场渲染绑定（战役同律）：每帧幂等 Bind（会话引用变化即整面
        // 重建——重开局=新图重绑）+ 脏块渲染；开局首帧隐藏夹具双层。
        _viewport ??= SessionBattlefieldViewport.Instantiate(this);
        bool wasBound = _viewport.IsBound;
        _viewport.Bind(_session, EraIndexOf(_level.Era));
        _viewport.PumpFrame();
        if (!wasBound)
        {
            SetFixtureHidden(true);
        }

        if (Engine.GetProcessFrames() % 30 == 0)
        {
            RefreshStatus();
        }
    }

    /// <summary>退出/弃局：视口释放+夹具层恢复（恢复菜单态口径）.</summary>
    private void ReleaseBattlefield()
    {
        _viewport?.Release();
        SetFixtureHidden(false);
    }

    /// <summary>夹具双层隐藏/恢复（程序关局内同层不叠画）.</summary>
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
            return; // 非主场景挂载——无夹具层可管
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

    private void RefreshStatus()
    {
        if (_session is not { } session || _level is not { } level)
        {
            _statusLabel.Text = "未开局";
            return;
        }

        _statusLabel.Text =
            $"{level.NameZh}（{level.Id}）\n" +
            $"状态={session.Machine.State} 波={session.Sim.CurrentWave}/{session.WinWaves} " +
            $"tick={session.Sim.CurrentTick}\n" +
            $"种子={level.Map.Seed} 地图={level.Map.Width}×{level.Map.Height} 遥测=player 在账";
    }

    /// <summary>era id（"era1"）→ 时代序；解析失败回落 1（era1 薄版域）.</summary>
    private static int EraIndexOf(string eraId) =>
        eraId.Length > 3 && int.TryParse(eraId[3..].Split(':')[0], out int era) && era >= 1 ? era : 1;

    // ------------------------------------------------- headless smoke lane

    /// <summary>
    /// G4 冒烟：①真注册表装载（procedural.json——档位梯/缺省档/时带在位）；
    /// ②同 seed 双生成 IdentityHash 全等（同 seed 同图的宿主面复证）；
    /// ③起局（LevelSession 组装+遥测双注入）跑 600 tick 后诚实退出——
    /// 沙盒账本断言一行 kind=Procedural/source=script/outcome=Abandoned/
    /// seed=map.seed/PlayTicks&gt;0。打印 PROC_OK；任一自检不过 Quit(1)。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        var problems = new List<string>();

        // ① 注册表装载
        ProceduralCampaignEntry entry = ProceduralEntryHost.LoadEntry();
        if (entry.Tiers.Count < 1 || entry.TryGetTier(entry.DefaultTierId) is null
            || entry.DurationBandMinutesMin < 1)
        {
            problems.Add($"注册表装载面不符：tiers={entry.Tiers.Count} default={entry.DefaultTierId}");
        }

        // ② 同 seed 同图（宿主面）
        ProceduralTier tier = entry.TryGetTier(entry.DefaultTierId)!;
        ProceduralLevel first = ProceduralEntryHost.Generate(SmokeSeed, tier, entry);
        ProceduralLevel second = ProceduralEntryHost.Generate(SmokeSeed, tier, entry);
        if (first.IdentityHash() != second.IdentityHash() || first.TerrainHash != second.TerrainHash)
        {
            problems.Add("同 seed 双生成哈希应全等（同 seed 同图）");
        }

        // ③ 起局+遥测（沙盒根，source=script）
        string smokeRoot = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", "proc-smoke");
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        var files = new DevFileStore(smokeRoot);
        var clock = new SteppedClock();
        var telemetry = new LevelDurationTelemetry(files, clock);
        var port = new JsonFileSavePort(files, "proc");
        LevelSession session = ProceduralEntryHost.StartSession(
            first, entry, telemetry,
            new LevelTelemetryScope(
                TelemetryLevelKind.Procedural, first.Id, EraIndexOf(first.Era), first.Map.Seed,
                TelemetrySources.Script),
            port);
        session.Machine.StartLevel();
        foreach (SimCommand command in ProceduralEntryHost.BuildCommands(first))
        {
            session.Enqueue(command);
        }

        const int horizon = 600;
        var viewport = SessionBattlefieldViewport.Instantiate(this); // T-160b 同律战场绑定面
        for (long tick = 0; tick < horizon; tick++)
        {
            session.RunFor(1);
            viewport.Bind(session, EraIndexOf(first.Era)); // 每帧幂等（窗口车道 PumpWindowSession 同律）
            viewport.PumpFrame();
            clock.AdvanceMs(33);
        }

        int procGroundRebuilds = viewport.GroundChunkRebuilds;
        int procWidth = viewport.BoundWidthCells;
        int procHeight = viewport.BoundHeightCells;
        viewport.Release();
        viewport.QueueFree();
        if (procGroundRebuilds <= 0 || procWidth != first.Map.Width || procHeight != first.Map.Height)
        {
            problems.Add(
                $"程序关战场绑定不符：ground={procGroundRebuilds} map={procWidth}x{procHeight}" +
                $"（level={first.Map.Width}x{first.Map.Height}）");
        }

        session.Pause();               // 流程机先入 Paused（SaveAndExit 的单终态前置）
        session.SaveAndExitToMenu();   // 诚实弃局（冒烟不打通关——遥测 Abandoned 面）

        IReadOnlyList<LevelDurationRecord> ledger = LevelDurationTelemetry.ReadAll(files);
        LevelDurationRecord? row = ledger.Count == 1 ? ledger[0] : null;
        if (row is null
            || row.Kind != TelemetryLevelKind.Procedural
            || row.LevelId != first.Id
            || row.EraIndex != 1
            || row.Seed != first.Map.Seed
            || row.Outcome != TelemetryOutcome.Abandoned
            || row.Source != TelemetrySources.Script
            || row.PlayTicks != horizon
            || row.Resumed
            || row.WallMs <= 0)
        {
            problems.Add($"遥测行不符：{row ?? (object)$"rows={ledger.Count}"}");
        }

        // 沙盒清场（dev 根 .tmp-tests 下，gitignored）
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"PROC_OK registry=tiers:{entry.Tiers.Count} default={entry.DefaultTierId} " +
                $"band={entry.DurationBandMinutesMin}~{entry.DurationBandMinutesMax}min " +
                $"seed-stable=identity+terrain level={first.Id} map={first.Map.Width}x{first.Map.Height} " +
                $"win_waves={first.WinWaves} script={first.CommandScript.Count} " +
                $"telemetry=levelsession-injected row=abandoned play_ticks={horizon} source=script " +
                $"view=bound:{procWidth}x{procHeight}:ground{procGroundRebuilds}");
        }
        else
        {
            GD.PushError("PROC_FAIL " + string.Join("; ", problems));
            GD.Print("PROC_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
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

/// <summary>
/// Host-side procedural entry lane (T-157 ⑤): parses
/// <c>game/content/campaign/procedural.json</c> into the production
/// <see cref="ProceduralCampaignEntry"/> record (装载即校验), assembles the
/// era1 content pack, generates levels and starts real
/// <see cref="LevelSession"/>s with the T-155 telemetry injection both sides
/// of the seam (<see cref="LevelSessionConfig.Telemetry"/> scope +
/// <see cref="LevelDurationTelemetry"/> service).
/// </summary>
internal static class ProceduralEntryHost
{
    /// <summary>Parses and validates the entry registry (one load per call — cheap, pure).</summary>
    public static ProceduralCampaignEntry LoadEntry()
    {
        string path = FindRepoFile(System.IO.Path.Combine("game", "content", "campaign", "procedural.json"))
            ?? throw new InvalidOperationException(
                "未找到 game/content/campaign/procedural.json（程序关宿主须在仓库内运行）");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        JsonElement entry = root.GetProperty("entry");
        JsonElement constraints = root.GetProperty("constraints");

        var tiers = new List<ProceduralTier>();
        foreach (JsonElement tierEl in root.GetProperty("tiers").EnumerateArray())
        {
            JsonElement wave = tierEl.GetProperty("wave");
            JsonElement plan = tierEl.GetProperty("tower_plan");
            JsonElement economy = tierEl.GetProperty("economy");
            JsonElement boss = tierEl.GetProperty("boss_line");
            JsonElement objectives = tierEl.GetProperty("objectives");
            tiers.Add(new ProceduralTier(
                Id: tierEl.GetProperty("id").GetString()!,
                Slot: tierEl.GetProperty("slot").GetInt32(),
                AdjacentEnemyCamps: tierEl.GetProperty("adjacent_enemy_camps").GetInt32(),
                WaveBaseCount: wave.GetProperty("base_count").GetInt32(),
                WaveBeginWave: wave.GetProperty("begin_wave").GetInt32(),
                WaveSpacing: wave.GetProperty("spacing").GetInt32(),
                WaveScaling: wave.GetProperty("scaling").GetDouble(),
                BatchesPerWave: wave.GetProperty("batches_per_wave").GetInt32(),
                ValleySeconds: wave.GetProperty("valley_seconds").GetDouble(),
                ArrowTowerId: plan.GetProperty("arrow_tower_id").GetString()!,
                ArrowTowerCount: plan.GetProperty("arrow_tower_count").GetInt32(),
                RockTowerId: plan.GetProperty("rock_tower_id").GetString()!,
                RockTowerCount: plan.GetProperty("rock_tower_count").GetInt32(),
                WaveOneTowerCount: plan.GetProperty("wave_one_tower_count").GetInt32(),
                TowerWaveStep: plan.GetProperty("tower_wave_step").GetInt32(),
                BossLineEnabled: boss.GetProperty("enabled").GetBoolean(),
                BossEnemyId: boss.GetProperty("boss_enemy_id").GetString(),
                LaborCamps: economy.GetProperty("labor_camps").GetInt32(),
                Harvesters: economy.GetProperty("harvesters").GetInt32(),
                Converters: economy.GetProperty("converters").GetInt32(),
                HarvesterLanePorters: economy.GetProperty("harvester_lane_porters").GetInt32(),
                StockMultiplier: tierEl.GetProperty("stock_multiplier").GetDouble(),
                BudgetSeconds: tierEl.GetProperty("budget_seconds").GetInt32(),
                StrategyObjectives: objectives.GetProperty("strategy").EnumerateArray()
                    .Select(o => o.GetString()!).ToArray(),
                ChallengeObjectives: objectives.GetProperty("challenge").EnumerateArray()
                    .Select(o => o.GetString()!).ToArray(),
                NameZh: tierEl.GetProperty("name_zh").GetString()!,
                NameEn: tierEl.GetProperty("name_en").GetString()!));
        }

        double[] band = entry.GetProperty("duration_band_minutes").EnumerateArray()
            .Select(v => v.GetDouble()).ToArray();
        var parsed = new ProceduralCampaignEntry(
            Id: entry.GetProperty("id").GetString()!,
            NameZh: entry.GetProperty("name_zh").GetString()!,
            NameEn: entry.GetProperty("name_en").GetString()!,
            Eras: entry.GetProperty("eras").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            DefaultTierId: entry.GetProperty("default_tier").GetString()!,
            DurationBandMinutesMin: (int)band[0],
            DurationBandMinutesMax: (int)band[1],
            Constraints: new ProceduralConstraints(
                constraints.GetProperty("width_min").GetInt32(),
                constraints.GetProperty("width_max").GetInt32(),
                constraints.GetProperty("height_min").GetInt32(),
                constraints.GetProperty("height_max").GetInt32(),
                constraints.GetProperty("forest_ratio_min").GetDouble(),
                constraints.GetProperty("forest_ratio_max").GetDouble(),
                constraints.GetProperty("rock_ratio_min").GetDouble(),
                constraints.GetProperty("rock_ratio_max").GetDouble(),
                constraints.GetProperty("water_ratio_min").GetDouble(),
                constraints.GetProperty("water_ratio_max").GetDouble(),
                constraints.GetProperty("entry_count_min").GetInt32(),
                constraints.GetProperty("entry_count_max").GetInt32(),
                constraints.GetProperty("grace_min").GetDouble(),
                constraints.GetProperty("grace_max").GetDouble(),
                constraints.GetProperty("max_rerolls").GetInt32()),
            Tiers: tiers);
        parsed.Validate();
        return parsed;
    }

    /// <summary>Generates one level (同 seed 同图——generator 确定性面).</summary>
    public static ProceduralLevel Generate(long seed, ProceduralTier tier, ProceduralCampaignEntry entry) =>
        ProceduralLevelGenerator.Generate(
            new ProceduralRequest(seed, tier, Era1Pack(), entry.Constraints),
            derived => new EndlessHostContent.SplitMix64Rng(derived));

    /// <summary>
    /// Starts one live level session from the generated record: direct record
    /// assembly (HandcraftedLevelAssemblyTests 同构组装律——JSON 往返装载面
    /// 已由 ProceduralPipelineTests 锚定，宿主车道直接消费记录零重复解析)
    /// with the T-155 telemetry injection on both seam sides.
    /// </summary>
    public static LevelSession StartSession(
        ProceduralLevel level,
        ProceduralCampaignEntry entry,
        LevelDurationTelemetry? durationTelemetry,
        LevelTelemetryScope? telemetryScope,
        ISavePort port)
    {
        _ = entry;
        var config = new LevelSessionConfig(
            BuildConfig(level),
            $"procedural.{level.Id}",
            level.Threat,
            level.Map.Seed,
            StateHashInterval: 1,
            Telemetry: telemetryScope);
        return new LevelSession(config, new EndlessHostContent.SplitMix64Rng(level.Map.Seed), port,
            campaignPool: null, durationTelemetry);
    }

    /// <summary>SimulationConfig assembled from the generated record + era1 content tables.</summary>
    public static SimulationConfig BuildConfig(ProceduralLevel level)
    {
        string eraDir = EraDir(level.Era);
        IReadOnlyList<TowerSpec> towers = TowerSpec.LoadCollection(ReadContent(eraDir, "towers.json"));
        var enemies = EnemySpecCatalog.Parse(ReadContent(eraDir, "enemies.json"));
        var entries = level.Map.EntryCells.Select(c => new CellCoord(c.X, c.Y)).ToArray();
        var stock = level.InitialStock
            .Select(line => new KeyValuePair<ResourceKey, double>(new ResourceKey(line.Resource), line.Amount))
            .ToList();
        return new SimulationConfig(
            level.Map.Width,
            level.Map.Height,
            level.Map.ForestRatio,
            level.Map.RockRatio,
            level.Map.WaterRatio,
            new CellCoord(level.Map.BaseCell.X, level.Map.BaseCell.Y),
            entries,
            towers,
            enemies,
            new WaveSpawnConfig(
                BaseCount: level.Waves.BaseCount,
                BeginWave: level.Waves.BeginWave,
                Spacing: level.Waves.Spacing,
                Scaling: level.Waves.Scaling,
                ThreatLevel: level.Threat,
                RegularRoster: level.Waves.RegularRoster,
                EliteLineEnabled: level.Waves.EliteLineEnabled,
                EliteEnemyId: level.Waves.EliteEnemyId,
                BossWave: level.Waves.BossWave ?? 20, // 终局关 Boss 波（null=默认 20 占位，BossEnemyId=null 时禁用）
                BossEnemyId: level.Waves.BossEnemyId,
                FirstWaveGraceMultiplier: level.GraceMultiplier,
                ValleySeconds: level.Waves.ValleySeconds,
                BatchesPerWave: level.Waves.BatchesPerWave,
                EntryCells: entries),
            stock,
            WarehouseCell: new CellCoord(level.Map.WarehouseCell.X, level.Map.WarehouseCell.Y),
            Costs: new BuildCostPolicy(new ResourceKey(level.InitialStock[0].Resource)),
            WarehouseLevel: WarehouseLevel(eraDir));
    }

    /// <summary>波锚命令流（BuildCommands 同律：wave k 步骤落在 k 波开场谷内；wave 1 在宽限尾）.</summary>
    public static IReadOnlyList<SimCommand> BuildCommands(ProceduralLevel level)
    {
        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * level.GraceMultiplier, MidpointRounding.AwayFromZero);
        long valleyTicks = (long)Math.Round(
            level.Waves.ValleySeconds * Simulation.TicksPerSecond, MidpointRounding.AwayFromZero);

        var commands = new List<SimCommand>();
        foreach (ProceduralScriptStep step in level.CommandScript.OrderBy(s => s.Wave))
        {
            long tick = Math.Max(0, graceTicks + ((step.Wave - 1) * intervalTicks) - valleyTicks);
            commands.Add(new SimCommand(tick, SimCommandKind.PlayerBuild, Payload(step)));
        }

        return commands;
    }

    private static string Payload(ProceduralScriptStep step) => step.Verb switch
    {
        "build-labor-camp" => CellPayload(step.Verb, step.Cell!),
        "build-harvester" => "{\"verb\":\"build-harvester\",\"resource\":\"" + step.Resource
            + "\",\"seats\":" + step.Seats + ",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "build-converter" => CellPayload(step.Verb, step.Cell!),
        "build-tower" => "{\"verb\":\"build-tower\",\"tower\":\"" + step.Tower
            + "\",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "assign-carrier" => "{\"verb\":\"assign-carrier\",\"from\":" + step.FromEntity + ",\"to\":" + step.ToEntity
            + (step.CarrierTier is null ? "" : ",\"tier\":\"" + step.CarrierTier + "\"") + "}",
        _ => throw new InvalidOperationException($"未登记命令动词 {step.Verb}（E1 薄版动词表外）"),
    };

    private static string CellPayload(string verb, CellCoord cell) =>
        "{\"verb\":\"" + verb + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}";

    // ---- era1 content pack（宿主递交流：塔表/敌册/资源/仓库等级全出自 content 表）----

    private static ProceduralEraContent Era1Pack() => new(
        EraId: "era1:era-ancient",
        RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
        Towers: TowerSpec.LoadCollection(ReadContent("era1", "towers.json")),
        CurrencyResource: "era1:res-wood",
        WarehouseLevel: WarehouseLevel("era1"),
        CarrierTier: "porter");

    private static string EraDir(string eraId) =>
        // era id 形如 "era1"（ProceduralLevel.Era——生成器 era 字段与手工关同族）
        eraId.Length > 3 ? "era" + eraId[3..].Split(':')[0] : "era1";

    private static int WarehouseLevel(string eraDir)
    {
        using JsonDocument doc = JsonDocument.Parse(ReadContent(eraDir, "era.json"));
        return doc.RootElement.GetProperty("items")[0].GetProperty("warehouse_level").GetInt32();
    }

    private static string ReadContent(string eraDir, string file)
    {
        string? full = FindRepoFile(System.IO.Path.Combine("game", "content", eraDir, file))
            ?? throw new InvalidOperationException($"未找到 game/content/{eraDir}/{file}（程序关宿主须在仓库内运行）");
        return System.IO.File.ReadAllText(full);
    }

    private static string? FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relativePath);
            if (System.IO.File.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        return null;
    }
}
