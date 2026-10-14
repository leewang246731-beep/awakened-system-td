using EraCore.Agent.Pipeline;
using EraCore.Contracts.Sim;
using EraCore.Presentation;
using Godot;

namespace EraTD;

/// <summary>
/// T-042 scene entry (C-21 skeleton), T-055 panel wiring (C-22). The
/// headless 60→N-frame driver loop runs synchronously inside _Ready because
/// headless <c>--quit</c> executes a single frame iteration.
/// <list type="number">
/// <item>integer-scale init: the pure math lives in
/// <see cref="ViewportScale"/> (EraCore), the engine-side letterboxing in
/// project.godot — both decisions agree; the computed factor feeds the
/// DebugLabel and the smoke line;</item>
/// <item>snapshot adapter — REAL PRODUCER (T-055 mock→real swap):
/// <see cref="SimHost"/> builds the live C-12 Simulation (E1 content) plus
/// the T-053 AgentPipeline, and the bridge consumes its two adapters
/// (ISimSnapshotSource → Simulation.Snapshots.Latest; IFramebufferProvider
/// → the terrain map). The retired Wave-1 fixture
/// DeterministicMockSnapshotSource is deleted — this call site is the
/// second replacement note (the first lives in SimSnapshotBridge.cs);</item>
/// <item>MultiMesh dirty rebuild: both layers rewrite only the chunks the
/// bridge drained this frame;</item>
/// <item>C-22 panel: HUD three metrics refresh from the same real
/// snapshots; the panel smoke drives the order box signal, the plan-card
/// confirm and the off-switch, then prints the PANEL_OK markers.</item>
/// </list>
/// </summary>
public sealed partial class Main : Control
{
    private const int SmokeFrames = 660;  // 22 s of sim time — the milestone stream (last tick 600) completes
    private const int UnitLayerCapacity = 128; // wave-1 budget headroom (grace 108 s ⇒ smoke sees no enemies)
    private const int HudRefreshFrames = 15;   // HUD redraw cadence (0.5 s of sim time)

    private SimHost _host = null!;
    private SimSnapshotBridge _bridge = null!;
    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private Label _debugLabel = null!;
    private Camera2D _camera = null!;
    private SystemPanel _panel = null!;

    public override void _Ready()
    {
        // ---- 1) integer-scale init (F-125) --------------------------------
        // Headless runs report a dummy window size; fall back to the design
        // resolution so the smoke output stays deterministic (scale=1).
        Vector2I window = DisplayServer.WindowGetSize();
        if (window.X < ViewportScale.BaseWidth || window.Y < ViewportScale.BaseHeight)
        {
            window = new Vector2I(ViewportScale.BaseWidth, ViewportScale.BaseHeight);
        }

        ViewportFit fit = ViewportScale.Fit(window.X, window.Y);

        // ---- 2) scene wiring ----------------------------------------------
        _camera = GetNode<Camera2D>("WorldCamera");
        _debugLabel = GetNode<Label>("DebugLabel");
        _groundLayer = GetNode<MultiMeshRenderer>("GroundLayer");
        _unitLayer = GetNode<MultiMeshRenderer>("UnitLayer");
        _panel = GetNode<SystemPanel>("SystemPanel");

        // ---- 3) REAL sim host + snapshot adapter (mock→real swap, T-055) --
        _host = new SimHost();
        _bridge = new SimSnapshotBridge(
            _host.SnapshotSource, _host.Framebuffer,
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells);
        _panel.Bind(_host);

        int mapWidth = _host.Framebuffer.WidthCells;
        int mapHeight = _host.Framebuffer.HeightCells;
        float cell = SimSnapshotBridge.CellSizePx;
        _camera.Position = new Vector2(mapWidth * cell / 2f, mapHeight * cell / 2f);

        _groundLayer.SetupGround(mapWidth, mapHeight, DirtyRegionSet.DefaultChunkCells,
            TerrainAtlas.Build(), TerrainAtlas.TileIndexForColor);
        _unitLayer.SetupUnits(mapWidth, mapHeight, DirtyRegionSet.DefaultChunkCells, capacity: UnitLayerCapacity);

        // ---- 4) dirty-chunk render loop over real ticks (headless-safe) ---
        int framesWithGroundWork = 0;
        int framesWithUnitWork = 0;
        int groundChunksRebuilt = 0;
        int unitChunksRebuilt = 0;

        for (int frame = 0; frame < SmokeFrames; frame++)
        {
            _host.StepFrame(); // T-076 (F-118): one render frame through the pause/speed clock (default 1x ⇒ tick-for-tick identical to the retired Step call)
            _bridge.SyncFrame(); // snapshot diff → dirty chunks

            var groundChunks = _bridge.TakeDirtyGroundChunks();
            if (groundChunks.Count > 0)
            {
                _groundLayer.RebuildGroundChunks(groundChunks, _host.Framebuffer);
                framesWithGroundWork++;
                groundChunksRebuilt += groundChunks.Count;
            }

            var unitChunks = _bridge.TakeDirtyUnitChunks();
            if (unitChunks.Count > 0)
            {
                _unitLayer.RebuildUnitChunks(unitChunks, _bridge.Current!);
                framesWithUnitWork++;
                unitChunksRebuilt += unitChunks.Count;
            }

            if (frame % HudRefreshFrames == 0)
            {
                _panel.RefreshHud(_host.BuildHudSnapshot()); // real metrics (F-111)
                _panel.RefreshComposition( // T-075: full third metric (F-111 全口径)
                    _host.BuildWaveCompositionView(), _host.NameOfEnemy);
                _panel.ObserveTrustScore(_host.Trust.Score); // badge tier follows the live model (§11.4)
            }

            UpdateDebugLabel(fit);
        }

        // Reverse-seam sanity: the command queue round-trips SimCommands.
        _bridge.EnqueueCommand(new SimCommand(
            _bridge.Current!.Tick, SimCommandKind.TimeControl, "{\"speed\":1}"));

        // ---- 5) panel flow smoke (F-115/F-116 + off-switch) ---------------
        RunPanelSmoke();

        // ---- 5b) T-056 build interaction (C-23 thin, F-036/F-119) ---------
        // 拖放建造薄版：实例化 build_ui.tscn（EraCore 接线全部在
        // BuildInteractionUi 脚本内，自带演示用 C-12 实例；headless 下自动
        // 跑脚本化拖放/取消/红格/拆除序列并打印 BUILD_OK——smoke 建造段
        // 判据。与其它泳道在本文件的接线冲突由后提交者 rebase。）
        BuildInteractionUi.Instantiate(this);

        // ---- 5c) T-077 BYOK settings panel (C-26, F-081) -------------------
        // 设置页场景挂载（settings_panel.tscn）：headless 下脚本内自带冒烟
        // 序列并打印 SETTINGS_OK providers=4 mask=on declare=on——smoke 设置
        // 段判据。（并行纪律：本文件的接线冲突由后提交者 rebase。）
        SettingsPanelUi.Instantiate(this);

        // ---- 5d) T-086 cinematics (C-24, F-113/F-114) -----------------------
        // 演出导演挂载（cinematics/CinematicDirector.cs）：E1 啊哈+E2 觉醒
        // 两脚本接真模拟；headless 下自带冒烟序列并打印
        // CINE_OK aha=skip-safe awaken=3choice——smoke 演出段判据。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        Cinematics.CinematicDirector.Instantiate(this, _host);

        // ---- 5e) T-089 narrative HUD (C-24 叙事薄版, F-098~F-109) ----------
        // 叙事 HUD 挂载（narrative/NarrativeHud.cs）：E1 章叙事导演接波拍
        // （幕推进+波间闲聊+打脸嘲讽拍→气泡）；headless 下自带冒烟序列并
        // 打印 NARR_OK acts=3 loop=720s——smoke 叙事段判据。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        Narrative.NarrativeHud.Instantiate(this, _host);

        // ---- 5f) T-092 audio full (C-25, F-112/F-135~F-139) -----------------
        // 音频导演挂载（audio/AudioDirector.cs）：三态 BGM 循环（代码合成+
        // 等功率淡入淡出）+四人格音色+一次性旁白（CC0 短乐）+三通道混音总线
        // （BGM/音效/军师语音，叮通道回归）；headless 下自带冒烟序列并打印
        // AUDIO_OK bgm=3states persona=4 narrated=2 bus=3vol——smoke 音频段判据。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        Audio.AudioDirector.Instantiate(this, _host);

        // ---- 5g) T-093 info layers (C-22/C-23 UI/UX polish) ----------------
        // 信息层总装（scripts/InfoLayersUi.cs）：三层信息（塔详情卡+塔头状态
        // 条+断流红圈灰线）+分区命名标注（镜头靠近浮显+chip 点击定位）+F1
        // 词汇表（术语搜索+提示互链）+谓词驱动提示队列（优先级+冷静期+已读
        // 持久化）；headless 下自带冒烟并打印 UI_OK layers=3——smoke 信息层
        // 判据，另存关键 UI 状态截图（D-03 根 ui-snapshots/，口径见 SNAP 行）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        InfoLayersUi.Instantiate(this, _host);

        // ---- 6) markers (smoke_godot.sh asserts SMOKE_OK) ------------------
        // Canonical table recomputed LIVE inside the engine process so the
        // smoke log proves the EraCore math is what the Godot side runs.
        GD.Print(
            "SCALE_TABLE " +
            $"1280x720={ViewportScale.Fit(1280, 720).Scale} " +
            $"1920x1080={ViewportScale.Fit(1920, 1080).Scale} " +
            $"2560x1440={ViewportScale.Fit(2560, 1440).Scale} " +
            $"1600x900={ViewportScale.Fit(1600, 900).Scale} " +
            $"offset1600x900=({ViewportScale.Fit(1600, 900).OffsetX},{ViewportScale.Fit(1600, 900).OffsetY})");
        GD.Print(
            $"ART atlas_tiles={TerrainAtlas.TileCount} tile_px={TerrainAtlas.TilePx} " +
            $"atlas={TerrainAtlas.TileCount * TerrainAtlas.TilePx}x{TerrainAtlas.TilePx} " +
            "scale=2x-nearest source=kenney-tiny-dungeon-cc0");

        // T-078 (C-28): E2 classical art pack load proof. The era2 atlas is
        // composed in-engine (missing tiles throw above) but NOT wired into
        // the ground layer yet — era routing lands with the E2 level work;
        // this marker only proves the validator-gated pack loads.
        _ = Era2TerrainAtlas.Build();
        GD.Print(
            $"ART2 era=classical atlas_tiles={Era2TerrainAtlas.TileCount} " +
            $"tile_px={Era2TerrainAtlas.TilePx} " +
            $"atlas={Era2TerrainAtlas.TileCount * Era2TerrainAtlas.TilePx}x{Era2TerrainAtlas.TilePx} " +
            $"pack_files={Era2TerrainAtlas.ProbePackFiles().Count}/15 " +
            "source=kenney-tiny-town+medieval-rts-cc0+procedural-standin");
        GD.Print(
            $"BRIDGE frames={SmokeFrames} ticks={_bridge.Current.Tick} " +
            $"units={_bridge.Current.Units.Count} buildings={_bridge.Current.Buildings.Count} " +
            $"ground_chunk_rebuilds={groundChunksRebuilt} unit_chunk_rebuilds={unitChunksRebuilt} " +
            $"frames_with_ground_work={framesWithGroundWork} frames_with_unit_work={framesWithUnitWork} " +
            $"wave_grace_note=no-enemies-before-tick-3240 " +
            $"pending_commands={_bridge.PendingCommandCount} state_hash={_bridge.Current.StateHash} " +
            "source=real-c12-simulation");
        GD.Print($"PANEL journal {_host.JournalDigest()}");
        GD.Print($"SMOKE_OK core={EraCore.VersionInfo.Current} scale={fit.Scale}");

        // T-093：显式 Quit 仅 headless（--quit 本就在首次迭代后退出，显式
        // 调用只为钉住退出码）；窗口运行让主循环迭代真实帧——信息层截图
        // 留档（ForceDraw 在窗口映射前只画出清屏色，取不到首帧像素）。
        // 窗口跑法：无参运行=活机窗口；--quit-after N=N 帧后自动退。
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            GetTree().Quit(0);
        }
    }

    /// <summary>
    /// C-22 thin-panel acceptance run (I 级 smoke): expand → real order
    /// through the LineEdit signal → plan card → confirm executes in the
    /// real simulation → off-switch hides every system surface while the
    /// HUD metrics survive as a neutral banner. Prints the two PANEL_OK
    /// markers (mode=on / mode=off) the acceptance gate greps for.
    /// </summary>
    private void RunPanelSmoke()
    {
        int towersBefore = _host.Simulation.Towers.Count;

        // Expand via the panel's own path (bubble press equivalent).
        _panel.Expand();

        // Order box: type the standing example and press Enter — the real
        // TextSubmitted signal drives the same handler a live player hits.
        _panel.OrderInput.Text = SystemPanel.ExampleOrder;
        _panel.OrderInput.EmitSignal(LineEdit.SignalName.TextSubmitted, SystemPanel.ExampleOrder);
        bool cardShown = _panel.PlanCardVisible;
        PipelineOutcomeKind? confirmed = _panel.ConfirmPending();
        int towersAfter = _host.Simulation.Towers.Count;
        _panel.RefreshHud(_host.BuildHudSnapshot()); // the confirm built towers — HUD catches up now
        string hud = _panel.HudText();
        string report = _panel.ReportText;

        GD.Print(
            $"PANEL_OK mode=on state={_panel.Machine.State} card={cardShown} " +
            $"confirm={confirmed} towers={towersBefore}->{towersAfter} hud=\"{hud}\" " +
            $"report=\"{report}\"");

        // T-075: trust tier crossing through the LIVE model (F-111/§11.4) —
        // advice outcomes carry no per-wave cap (false alarms do, so they
        // cannot drive a crossing inside one wave): one lost advice stays
        // inside 可托付 (50→47), four land in 将信将疑 (38), one held climbs
        // back (41). Each ObserveTrustScore speaks only on a real crossing.
        _host.Trust.RecordAdviceOutcome(held: false);
        TrustBadgeChange? sameTier = _panel.ObserveTrustScore(_host.Trust.Score); // 47 — tier holds
        _host.Trust.RecordAdviceOutcome(held: false);
        _host.Trust.RecordAdviceOutcome(held: false);
        _host.Trust.RecordAdviceOutcome(held: false);
        TrustBadgeChange? tierDown = _panel.ObserveTrustScore(_host.Trust.Score); // 38 → 将信将疑
        _host.Trust.RecordAdviceOutcome(held: true);
        TrustBadgeChange? tierUp = _panel.ObserveTrustScore(_host.Trust.Score); // 41 → 可托付
        GD.Print(
            $"PANEL_V2 comp=on badge={_panel.BadgeLabelText} next_wave=\"{_panel.NextWaveText}\" " +
            $"comp_text=\"{_panel.CompText}\" same_tier_silent={sameTier is null} " +
            $"tier_change_down=\"{tierDown?.Line ?? "none"}\" tier_change_up=\"{tierUp?.Line ?? "none"}\"");

        // Off-switch (pillar 4): input/wheel/panel hidden, HUD kept neutral.
        _panel.SetSystemEnabled(false);
        bool inputHidden = !_panel.OrderInput.IsVisibleInTree();
        bool metricsKept = _panel.HudText().Length > 0 && _panel.Machine.HudNeutral;

        // Off-mode full-downgrade probe (T-075): the badge is a persona
        // element and hides; a below-line integrity reading proves the
        // alarm still fires through the NEUTRAL channel (警报不缺失, §10.7).
        bool badgeHidden = !_panel.BadgeMarkVisible;
        HudSnapshot realSnapshot = _host.BuildHudSnapshot();
        _panel.RefreshHud(new HudSnapshot(
            realSnapshot.Wave, realSnapshot.ProductionPerSecond, 0.35d, realSnapshot.TowerCount));
        bool alarmFires = _panel.AlarmText.Length > 0;
        string neutralAlarm = _panel.AlarmText;
        GD.Print(
            $"PANEL_OK mode=off state={_panel.Machine.State} " +
            $"input_hidden={inputHidden} panel_hidden={ !_panel.Machine.PanelVisible} " +
            $"hud_neutral_banner=\"{_panel.HudText()}\" metrics_kept={metricsKept}");
        GD.Print(
            $"PANEL_V2 comp=off badge={_panel.BadgeLabelText} badge_hidden={badgeHidden} " +
            $"next_wave=\"{_panel.NextWaveText}\" alarm_fires={alarmFires} alarm=\"{neutralAlarm}\"");
        _panel.RefreshHud(realSnapshot); // restore the true reading

        if (!cardShown || confirmed != PipelineOutcomeKind.Completed || towersAfter <= towersBefore)
        {
            GD.PushError("panel flow broken: card/confirm/tower evidence failed");
            GetTree().Quit(1);
        }

        if (!inputHidden || !metricsKept)
        {
            GD.PushError("off-switch broken: input surfaces or HUD metrics did not degrade correctly");
            GetTree().Quit(1);
        }

        if (sameTier is not null || tierDown is null || tierUp is null)
        {
            GD.PushError("trust badge broken: tier crossings did not fire exactly on the boundaries");
            GetTree().Quit(1);
        }

        if (!badgeHidden || !alarmFires)
        {
            GD.PushError("off-mode downgrade broken: badge visible or neutral alarm missing");
            GetTree().Quit(1);
        }

        // Switch back on — the remembered state (Expanded) must return, and
        // the persisted position must read enabled again (F-097 开关持久化).
        _panel.SetSystemEnabled(true);
        bool persistedEnabled = PanelSettingsStore.Load(_host.Store).SystemEnabled;
        GD.Print($"PANEL_V2 settings_persisted={persistedEnabled} path={PanelSettingsStore.RelativePath}");
        if (!persistedEnabled)
        {
            GD.PushError("panel settings store broken: re-enable did not persist");
            GetTree().Quit(1);
        }
    }

    private void UpdateDebugLabel(in ViewportFit fit) =>
        _debugLabel.Text =
            $"scale={fit.Scale} off=({fit.OffsetX},{fit.OffsetY}) " +
            $"fps={Engine.GetFramesPerSecond()} tick={_bridge.Current?.Tick.ToString() ?? "-"} " +
            $"wave={_host.Simulation.CurrentWave}";
}
