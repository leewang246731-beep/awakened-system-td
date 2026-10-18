using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.IO;
using EraCore.Llm;
using EraCore.Persistence;
using EraCore.Presentation;
using EraCore.Secrets;
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
/// <item>T-102 (C-21 渲染三档): persisted preference + probe facts select a
/// capability row; the loop consumes RendererDecisionMap plans (L 按需直绘
/// + 特效密度门 + 扫描线/后期开关，能力矩阵驱动) and prints RENDER_OK.</item>
/// </list>
/// </summary>
    public sealed partial class Main : Control
{
    private const int SmokeFrames = 660;  // 22 s of sim time — the milestone stream (last tick 600) completes
    private const int UnitLayerCapacity = 128; // wave-1 budget headroom (grace 108 s ⇒ smoke sees no enemies)
    private const int HudRefreshFrames = 15;   // HUD redraw cadence (0.5 s of sim time)
    private const int AutoProbeFrames = 60;    // T-102: 首次 60 帧测帧率自适应（窗口运行；headless 无帧推进）
    private const int L2HintRefreshFrames = 45; // T-137: L2 档位提示/降级横幅刷新节奏（1.5 s of sim time）

    private SimHost _host = null!;
    private SimSnapshotBridge _bridge = null!;
    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private Label _debugLabel = null!;
    private Camera2D _camera = null!;
    private SystemPanel _panel = null!;
    private SettingsPanelUi _settingsUi = null!;
    private InfoLayersUi _infoUi = null!;

    // T-137 (F-078): L2 lane — coordinator (settings/download/runtime/route)
    // + wave-gap service (queue + async fills). Headless binds SMOKE ports
    // (fake process/probe/fill — zero real downloads, zero real processes);
    // windowed binds the real lanes.
    private L2.L2Coordinator? _l2;
    private L2.WaveGapL2Service? _waveGap;
    private L2.SmokeFillPort? _l2SmokeFill;
    private int _lastWaveSeen = -1;

    // T-102 (C-21 渲染三档): capability row + current plan the render loop
    // consumes (能力矩阵驱动——renderers never branch on the tier enum).
    private RenderTierCapabilities _renderCaps = RenderTierMatrix.Medium;
    private RenderFramePlan _renderPlan = null!;
    private readonly Dictionary<long, int> _prevUnitHp = new(); // HP-drop combat-effect requests
    private readonly HashSet<(int ChunkX, int ChunkY)> _groundDrawnChunks = new(); // L 档按需直绘已画集
    private readonly List<double> _fpsProbeSamples = new();    // windowed auto-probe (T-102)
    private int _effectRequestsTotal;

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

        // ---- 3b) T-102 render tier (C-21 渲染三档): persisted preference →
        // probe facts (headless startup has no frame pacing data — windowed
        // runs re-probe over the first 60 frames in _Process) → capability
        // row. Chunk edge, bake/draw policy, effect gate and overlay toggles
        // all read from this row (no tier ifs below).
        RenderTierSettings tierSettings = RenderTierStore.Load(_host.Store);
        _renderCaps = RenderTierMatrix.Capabilities(
            RenderTierSelector.Select(tierSettings.Preference, ProbeFacts()));
        _renderPlan = RendererDecisionMap.PlanFrame(_renderCaps, 0);

        _bridge = new SimSnapshotBridge(
            _host.SnapshotSource, _host.Framebuffer,
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells,
            chunkCells: _renderCaps.DirtyChunkCells);
        _panel.Bind(_host);
        _panel.SetScanlineEnabled(_renderPlan.ScanlinePass);

        // T-110 (F-164 能力表驱动): the host supplies the single platform
        // fact once — the order box「AI 高级语法」entry mirrors the tier row
        // (desktop shows it, Web hides it). InfoLayersUi/SettingsPanelUi read
        // the same fact inside their own Instantiate entry points.
        _panel.SetAgentTiers(WebConstraints.Availability(OS.HasFeature("web")));

        int mapWidth = _host.Framebuffer.WidthCells;
        int mapHeight = _host.Framebuffer.HeightCells;
        float cell = SimSnapshotBridge.CellSizePx;
        _camera.Position = new Vector2(mapWidth * cell / 2f, mapHeight * cell / 2f);

        _groundLayer.SetupGround(mapWidth, mapHeight, _renderCaps.DirtyChunkCells,
            TerrainAtlas.Build(), TerrainAtlas.TileIndexForColor);
        _unitLayer.SetupUnits(mapWidth, mapHeight, _renderCaps.DirtyChunkCells, capacity: UnitLayerCapacity);

        // ---- 4) dirty-chunk render loop over real ticks (headless-safe) ---
        int framesWithGroundWork = 0;
        int framesWithUnitWork = 0;
        int groundChunksRebuilt = 0;
        int unitChunksRebuilt = 0;

        for (int frame = 0; frame < SmokeFrames; frame++)
        {
            _host.StepFrame(); // T-076 (F-118): one render frame through the pause/speed clock (default 1x ⇒ tick-for-tick identical to the retired Step call)
            _bridge.SyncFrame(); // snapshot diff → dirty chunks

            // ---- T-102: combat effect gate (纯决策) — HP 掉落=受击特效请求，
            // 档位密度门决定放行集（L 减半），受击单位本帧以闪光色直绘。
            HashSet<long> impactFlashIds = CollectImpactFlashes(_bridge.Current);

            if (_renderPlan.BakeStaticGround)
            {
                var groundChunks = _bridge.TakeDirtyGroundChunks();
                if (groundChunks.Count > 0)
                {
                    _groundLayer.RebuildGroundChunks(groundChunks, _host.Framebuffer);
                    framesWithGroundWork++;
                    groundChunksRebuilt += groundChunks.Count;
                }
            }
            else
            {
                // L 档按需直绘：跳过静态烘焙（烘焙脏集直接丢弃），只直绘镜头
                // 视野内尚未画过的 chunk（ChunksInView 圈选；镜头移动自动补画）。
                _ = _bridge.TakeDirtyGroundChunks();
                RebuildOnDemandGround(ref groundChunksRebuilt, ref framesWithGroundWork);
            }

            var unitChunks = _bridge.TakeDirtyUnitChunks();
            if (unitChunks.Count > 0)
            {
                _unitLayer.RebuildUnitChunks(
                    unitChunks, _bridge.Current!, impactFlashIds.Count > 0 ? impactFlashIds : null);
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
        _settingsUi = SettingsPanelUi.Instantiate(this);

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
        _infoUi = InfoLayersUi.Instantiate(this, _host);

        // ---- 5h) T-152 visual expansion (31 号 v2.0 插入点接线) ------------
        // 视觉扩充总装（expand/VisualExpansionDirector.cs）：素材目录装载
        // （51 ogv+8 序列帧+《零的真相》+73 图+24 成就图标）+主菜单循环/
        // 全屏播片/Boss 序列帧/加载屏/记忆档案收藏链（《零的真相》解锁）/
        // 彩蛋展示/时代皮肤；headless 下自带冒烟并打印 CINE_V2 计数+
        // EXPAND_OK——smoke 视觉扩充段判据。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        Expand.VisualExpansionDirector.Instantiate(this);

        // ---- 5i) T-102 render tiers (C-21 渲染三档, F-159) ------------------
        // 三档决策表差异 + 自动探测 + L 档按需直绘执行回路 + 偏好持久化
        // round-trip——headless 冒烟判据打印 RENDER_OK（smoke 渲染三档段）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        RunRenderTierSmoke(groundChunksRebuilt, unitChunksRebuilt, framesWithGroundWork);

        // ---- 5j) T-106 Web 约束 (F-164/F-144) -------------------------------
        // 平台三档可用性表（Web=L1 恒开+L2/L3 禁）+ L3 网关平台门（最强场景
        // 零网络拒绝走真实探针文案链）+ T-100 存档探针 Web 事实回落 JSON——
        // headless 冒烟判据打印 WEB_OK（smoke Web 约束段；失败 Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        RunWebSmoke();

        // ---- 5k) T-111 战报回放 (C-30/C-22, F-121/F-174) ---------------------
        // 回放 UI 挂载（scripts/ReplayUi.cs）：reports/ 存档目录 JSONL 离线
        // 加载 + 军令/审批/执行/汇报四色时间轴（任意 tick 跳转，复用
        // JournalReplayer/ReplayTimeline）+ 回放摘要 md 导出；headless 下自带
        // 冒烟并打印 REPLAY_UI_OK files=2 load=ok seek=ok export=md——smoke
        // 回放段判据（失败 Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        ReplayUi.Instantiate(this, _host);

        // ---- 5l) T-126 四人格全量+语音 (C-13/C-25, F-109/F-137) ---------------
        // 人格台词库（4×50=200 行，16 面覆盖矩阵）+ 音色时代×人格 16 行 +
        // 三联动切换闭环（立绘+音色+台词同拍一致）——headless 冒烟判据打印
        // PERSONA_OK lines=200 tones=16 switch=linked（失败 Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        RunPersonaFullSmoke();

        // ---- 5m) T-137 L2 本地推理正式版 (C-27, F-078/F-163) ------------------
        // L2 通道总装：协调器（档位/下载/进程/路由——EraTD.L2.L2Coordinator）
        // + 波间异步服务（WaveGapL2Service）绑设置页与军师面板；headless 冒烟
        // 全程假件（假进程/假探针/假填充口/拒绝 wire——绝不真拉 llama-server、
        // 绝不下真模型），判据打印 L2_OK route/degrade/wavegap/runtime 四面
        // （失败 Quit(1)）。窗口运行走 _Process 帧循环（波次变迁开窗+逐帧泵）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        SetupL2Lane();
        RunL2SmokeAsync().Wait(); // headless synchronous drive (every inner await completes synchronously — fake seams)

        // ---- 5n) T-139 联机最小面 (F-161, scripts/net/NetMenuUi.cs) -----------
        // 右上「联机」入口+房间面板（建房/加入 IP+链路状态）。headless 自带
        // 零端口冒烟（帧格式往返+内容指纹+两实例内存回环对局含军师审批全链
        // →NET_OK，失败 Quit(1)——G4 不真开网络的纪律承载面）；窗口运行走
        // ENet 真实路径（EnetNetLink 建房=listen server/加入=client）。本 UI
        // 自持游戏实例（NetDemoLevel），不触碰上方任何单机泳道——单机路径
        // 零耦合（NetCouplingGuardTests 执法面）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        Net.NetMenuUi.Instantiate(this);

        // ---- 5o) T-157 无尽开局 UI + 策略表宿主装载 + 真人局遥测 (F-006/C-21) --
        // 无尽开局面板（scripts/EndlessMenuUi.cs）：两档词缀节奏显式选择
        // （Rolling 缺省/ClassicFixed 回切）+局内档位明示（EndlessSession.
        // AffixSchedule 只读查询位）+endless-policies.json 宿主装载（改表即
        // 改产；漏表=既有无表兜底链不变）+EndlessTelemetry 三调用（BeginRun/
        // 逐波台账/终局 End——窗口局 source=player 自动入账）；headless 自带
        // 冒烟并打印 ENDLESS_OK（失败 Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        EndlessMenuUi.Instantiate(this);

        // ---- 5p) T-157 程序关表现层入口 (F-007/C-21, scripts/ProceduralMenuUi.cs)
        // 战役外入口按钮（ProceduralCampaignEntry 数据位 T-140 已就绪）：面板=
        // 档位选择（procedural.json tiers 行）+种子+「生成并开局」——生成→
        // LevelSession 起局（LevelSessionConfig.Telemetry+durationTelemetry
        // 双注入，T-155 留口——程序关半边）；headless 自带冒烟并打印
        // PROC_OK（失败 Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        ProceduralMenuUi.Instantiate(this);

        // ---- 5q) T-160 战役宿主脸 (C-29/F-005/F-001, scripts/CampaignMenuUi.cs) --
        // 「战役」入口+章节分组选关菜单（levels/e1~e6.json 全 29 行+进度态：
        // 未解锁/可玩/已通关★）→ 生产装载器（HandcraftedLevelCatalog+
        // EraLevelAssembly，T-160 提升）→ LevelSession 真开局/续局；胜利结算
        // →解锁推进；E final 胜利→EraTransitionService 原子事务→跃迁过场播片
        // 钩子→下一时代解锁；e6-final→终章播片二选一（系统开关）+通关总结；
        // 退出到菜单=level 域 #exit 快照+campaign 域合并写（重进续玩）；遥测
        // source=player 双注入（T-155 战役半边收口）；headless 自带冒烟并打印
        // CAMPAIGN_OK（全 29 关装载矩阵+E1-01 真通关+续玩+跃迁负向探针；失败
        // Quit(1)）。
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        CampaignMenuUi.Instantiate(this);

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

        // T-119 (C-28): E3 industrial art pack load proof + belt dual-layer
        // wiring (F-127/F-133). Atlas composes in-engine (missing tiles
        // throw); the belt straight-segment material instantiates the
        // belt-scroll shader with the dual-layer textures bound and the turn
        // strip geometry is asserted live — era routing stays with the E3
        // level visuals, this marker proves the validator-gated pack loads
        // (GPU pixel output is a windowed-only fact: headless 局限, 41 号 §6).
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        _ = Era3TerrainAtlas.Build();
        _ = Era3ArtPack.BuildBeltScrollMaterial();
        Vector2 beltStrip = Era3ArtPack.LoadTurnStrip().GetSize();
        GD.Print(
            $"ART3 era=industrial atlas_tiles={Era3TerrainAtlas.TileCount} " +
            $"tile_px={Era3TerrainAtlas.TilePx} " +
            $"atlas={Era3TerrainAtlas.TileCount * Era3TerrainAtlas.TilePx}x{Era3TerrainAtlas.TilePx} " +
            $"pack_files={Era3ArtPack.ProbePackFiles().Count}/22 " +
            "source=procedural-era3-placeholders");
        GD.Print(
            "BELT shader=belt-scroll layers=2 " +
            $"body_px={Era3ArtPack.BeltBodyPx} turn_frames={Era3ArtPack.TurnFrames} " +
            $"strip={(int)beltStrip.X}x{(int)beltStrip.Y} " +
            $"grid={Era3ArtPack.TurnFramesPerRow}x{Era3ArtPack.TurnFrames / Era3ArtPack.TurnFramesPerRow} " +
            "phase=BeltRenderDecisions(locked shader-offset/frame-index)");

        // W7 E4 视觉接线 F1 (T-128/C-28, F-042/F-127/F-133): E4 modern art
        // pack load proof — 44 号 §1「时代路由」行的收口（T-128 卡明确挂出的
        // presentation 接线半边）。Atlas composes in-engine (missing tiles
        // throw); the belt skin stem comes from the pure gear decision
        // (BeltRenderDecisions.BeltSkinStem: 15 件/s = E4 传送带档 → era4
        // 皮肤, 10 件/s = E3 矿轨档 → era3 —— 同一冻结帧律仅色板换装,
        // 44 号 §3 带速不烙进纹理); tower/enemy/Boss 实体映射按
        // content-id→stem 规则组合（Era4ArtMappingTests 头less 侧镜像）。
        // Era routing stays with the render loop as with E2/E3 — this
        // marker proves the validator-gated pack loads (GPU pixel output is
        // a windowed-only fact: headless 局限, 41 号 §6).
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        _ = Era4TerrainAtlas.Build();
        _ = Era4ArtPack.BuildBeltScrollMaterial();
        Vector2 e4BeltStrip = Era4ArtPack.LoadTurnStrip().GetSize();
        _ = Era4ArtPack.EnemySpritePath("era4:enemy-boss"); // Boss（夜枭）映射解析即证明
        _ = Era4ArtPack.TowerSpritePath("era4:tower-missile", 2); // 塔升级位映射解析即证明
        GD.Print(
            $"ART4 era=modern terrain={Era4TerrainAtlas.TileCount} " +
            $"pack_files={Era4ArtPack.ProbePackFiles().Count}/30 " +
            $"belt={Era4ArtPack.BeltSkinShortStem}@{BeltRenderDecisions.Era4GearItemsPerSecond:0.#}/s " +
            $"boss={Era4ArtPack.BossStem}{Era4ArtPack.BossCanvasPx} " +
            "source=procedural-era4-placeholders");
        GD.Print(
            "BELT4 shader=belt-scroll layers=2 " +
            $"skin={Era4ArtPack.BeltSkinStem} " +
            $"switch={BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era4GearItemsPerSecond)}@{BeltRenderDecisions.Era4GearItemsPerSecond:0.#}/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era3GearItemsPerSecond)}@{BeltRenderDecisions.Era3GearItemsPerSecond:0.#}/s " +
            $"body_px={Era4ArtPack.BeltBodyPx} turn_frames={Era4ArtPack.TurnFrames} " +
            $"strip={(int)e4BeltStrip.X}x{(int)e4BeltStrip.Y} " +
            $"grid={Era4ArtPack.TurnFramesPerRow}x{Era4ArtPack.TurnFrames / Era4ArtPack.TurnFramesPerRow} " +
            "phase=BeltRenderDecisions(same frozen law, palette reskin, doc 44 §3)");

        // W8 E5 视觉接线 (T-142/C-28, F-042/F-127/F-133): E5 future art pack
        // load proof — 31 号 §1.2 E5 行（聚变青/聚变紫/纳米银白+力场弧×六边
        // 晶格）的程序合成包（tools/art/era5_placeholders.py）。Atlas composes
        // in-engine (missing tiles throw); the belt skin stem comes from the
        // pure gear decision (BeltRenderDecisions.BeltSkinStem: T-134 留口
        // 收口——20 件/s = E5 无人机档 → era5 皮肤, 15/s = E4 → era4, 10/s =
        // E3 → era3 —— 同一冻结帧律仅色板换装); tower/enemy/Boss 实体映射按
        // content-id→stem 规则组合（Era5ArtMappingTests 头less 侧镜像）。
        // pack_files=30/30：era5 内容 id 对齐（三塔 5 升级位 vs era4 四塔 6
        // 件——槽类结构同 era4 manifest，REV 登记）。Era routing stays with
        // the render loop as with E2/E3/E4 — this marker proves the
        // validator-gated pack loads (GPU pixel output is a windowed-only
        // fact: headless 局限, 41 号 §6).
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        _ = Era5TerrainAtlas.Build();
        _ = Era5ArtPack.BuildBeltScrollMaterial();
        Vector2 e5BeltStrip = Era5ArtPack.LoadTurnStrip().GetSize();
        _ = Era5ArtPack.EnemySpritePath("era5:enemy-boss"); // Boss（主宰）映射解析即证明
        _ = Era5ArtPack.TowerSpritePath("era5:tower-railgun", 2); // 塔升级位映射解析即证明
        GD.Print(
            $"ART5 era=future terrain={Era5TerrainAtlas.TileCount} " +
            $"pack_files={Era5ArtPack.ProbePackFiles().Count}/30 " +
            $"belt={Era5ArtPack.BeltSkinShortStem}@{BeltRenderDecisions.Era5GearItemsPerSecond:0.#}/s " +
            $"boss={Era5ArtPack.BossStem}{Era5ArtPack.BossCanvasPx} " +
            "source=procedural-era5-placeholders");
        GD.Print(
            "BELT5 shader=belt-scroll layers=2 " +
            $"skin={Era5ArtPack.BeltSkinStem} " +
            $"switch={BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era5GearItemsPerSecond)}@{BeltRenderDecisions.Era5GearItemsPerSecond:0.#}/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era4GearItemsPerSecond)}@{BeltRenderDecisions.Era4GearItemsPerSecond:0.#}/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era3GearItemsPerSecond)}@{BeltRenderDecisions.Era3GearItemsPerSecond:0.#}/s " +
            $"body_px={Era5ArtPack.BeltBodyPx} turn_frames={Era5ArtPack.TurnFrames} " +
            $"strip={(int)e5BeltStrip.X}x{(int)e5BeltStrip.Y} " +
            $"grid={Era5ArtPack.TurnFramesPerRow}x{Era5ArtPack.TurnFrames / Era5ArtPack.TurnFramesPerRow} " +
            "phase=BeltRenderDecisions(same frozen law, palette reskin, doc 44 §3)");

        // W8 终章视觉接线 (T-142/C-28, F-042/F-127/F-133): final-chapter
        // deep-space art pack load proof — 31 号 §1.2 终章行（星门靛蓝/
        // 暗物质紫黑/零点白青+星门环×裂缝）的程序合成包（tools/art/
        // era6_placeholders.py，内容 id 对齐 T-135 落地的 era6 五件套）。
        // Atlas composes in-engine (missing tiles throw); the belt skin stem
        // comes from the pure gear decision (BeltRenderDecisions.
        // BeltSkinStem: ladder 末位 ∞ 档 = 终章传送阵 → era6 皮肤——有限
        // 高速带仍穿 era5, 唯真无穷穿深空); tower/enemy/Boss 实体映射按
        // content-id→stem 规则组合（Era6ArtMappingTests 头less 侧镜像）。
        // pack_files=27/27：era6 内容 id 对齐（两塔均 max_tier=1 → 2 升级
        // 位文件——era4 30/era5 29 的同构异数，REV 登记）。Boss 画布=64
        // （28 §5 下限重演；设计案 §13.1「终章 Boss 96×112」升格挂账 REV）。
        // Era routing stays with the render loop — this marker proves the
        // validator-gated pack loads (GPU pixel output is a windowed-only
        // fact: headless 局限, 41 号 §6).
        // （并行纪律：本文件的接线冲突由后提交者 rebase。）
        _ = Era6TerrainAtlas.Build();
        _ = Era6ArtPack.BuildBeltScrollMaterial();
        Vector2 e6BeltStrip = Era6ArtPack.LoadTurnStrip().GetSize();
        _ = Era6ArtPack.EnemySpritePath("era6:enemy-boss"); // Boss（堕落守望者）映射解析即证明
        _ = Era6ArtPack.TowerSpritePath("era6:tower-singularity", 1); // 塔映射解析即证明
        GD.Print(
            $"ART6 era=finale terrain={Era6TerrainAtlas.TileCount} " +
            $"pack_files={Era6ArtPack.ProbePackFiles().Count}/27 " +
            $"belt={Era6ArtPack.BeltSkinShortStem}@inf/s " +
            $"boss={Era6ArtPack.BossStem}{Era6ArtPack.BossCanvasPx}x{Era6ArtPack.BossCanvasHeightPx} " +
            "source=procedural-era6-placeholders");
        GD.Print(
            "BELT6 shader=belt-scroll layers=2 " +
            $"skin={Era6ArtPack.BeltSkinStem} " +
            "switch=" +
            $"{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era6GearItemsPerSecond)}@inf/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era5GearItemsPerSecond)}@{BeltRenderDecisions.Era5GearItemsPerSecond:0.#}/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era4GearItemsPerSecond)}@{BeltRenderDecisions.Era4GearItemsPerSecond:0.#}/s" +
            $"|{BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era3GearItemsPerSecond)}@{BeltRenderDecisions.Era3GearItemsPerSecond:0.#}/s " +
            $"body_px={Era6ArtPack.BeltBodyPx} turn_frames={Era6ArtPack.TurnFrames} " +
            $"strip={(int)e6BeltStrip.X}x{(int)e6BeltStrip.Y} " +
            $"grid={Era6ArtPack.TurnFramesPerRow}x{Era6ArtPack.TurnFrames / Era6ArtPack.TurnFramesPerRow} " +
            "phase=BeltRenderDecisions(same frozen law, palette reskin, doc 44 §3)");
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

    // ------------------------------------------------- T-102 render tiers

    /// <summary>
    /// Startup probe facts (T-102): Web feature is readable immediately; FPS
    /// needs real frame pacing, which a synchronous headless _Ready has none
    /// of — windowed runs re-probe over the first 60 frames in _Process.
    /// </summary>
    private RenderProbeFacts ProbeFacts() => new(
        IsWeb: OS.HasFeature("web"),
        FirstMinuteAverageFps: 0d); // unknown at startup ⇒ auto keeps the M default

    /// <summary>
    /// Windowed auto-probe (T-102 首次 60 帧测帧率自适应): collects real
    /// Engine FPS samples for 60 frames, then re-selects the tier from the
    /// probed average and live-applies the per-frame decisions (effect gate
    /// + overlay passes); assembly-time facts (chunk granularity, bake)
    /// apply from the next launch. Headless never reaches _Process (Quit in
    /// _Ready) so smoke output stays deterministic.
    /// </summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        PumpL2Lane(); // T-137: wave-gap pump + runtime health + hint refresh (window lane)

        if (_fpsProbeSamples.Count >= AutoProbeFrames)
        {
            return;
        }

        _fpsProbeSamples.Add(Engine.GetFramesPerSecond());
        if (_fpsProbeSamples.Count < AutoProbeFrames)
        {
            return;
        }

        double average = 0f;
        foreach (double sample in _fpsProbeSamples)
        {
            average += sample;
        }

        average /= _fpsProbeSamples.Count;
        var facts = new RenderProbeFacts(OS.HasFeature("web"), average);
        RenderTier probed = RenderTierSelector.Select(RenderTierPreference.Auto, facts);
        _renderCaps = RenderTierMatrix.Capabilities(probed);
        _renderPlan = RendererDecisionMap.PlanFrame(_renderCaps, 0);
        _panel.SetScanlineEnabled(_renderPlan.ScanlinePass);
        GD.Print(
            $"RENDER_PROBE frames={AutoProbeFrames} avg_fps={average:F1} " +
            $"tier={RenderTierMatrix.TierId(probed)} live_applied=fx-gate+scan+post " +
            "assembly=next-launch");
    }

    // ------------------------------------------------ T-137 L2 lane wiring

    /// <summary>
    /// Builds and binds the L2 lane (T-137). Headless binds SMOKE ports —
    /// the acceptance run must never download the 1.93 GB model nor spawn a
    /// real llama-server (G4 纪律); windowed binds the real wire/process/
    /// probe/warmup seams over the shared D-03 store root.
    /// </summary>
    private void SetupL2Lane()
    {
        var platform = WebConstraints.Availability(OS.HasFeature("web"));
        bool headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";

        var files = new L2.DataRootDownloadFilePort(_host.Store.Root);
        L2.L2Coordinator coordinator;
        if (headless)
        {
            _l2SmokeFill = new L2.SmokeFillPort();
            coordinator = new L2.L2Coordinator(
                _host.Store,
                files,
                () => new L2.UnreachableDownloadWirePort(),
                () => new L2.L2RuntimeHost(
                    new L2.SmokeServerProcess(), new L2.SmokeHealthProbe(), (_, _) => Task.FromResult(true)),
                platform);
            _waveGap = new L2.WaveGapL2Service(
                () => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                new EraCore.Llm.L2.WaveGapFillQueue(
                    () => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond),
                () => _l2SmokeFill);
        }
        else
        {
            coordinator = new L2.L2Coordinator(
                _host.Store,
                files,
                () => new L2.GodotDownloadWirePort(this),
                () => new L2.L2RuntimeHost(
                    new L2.RealLlamaServerProcess(),
                    new L2.GodotHealthProbePort(this),
                    (port, ct) => WarmupLocalServerAsync(port, ct)),
                platform);
            _waveGap = new L2.WaveGapL2Service(
                () => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                new EraCore.Llm.L2.WaveGapFillQueue(
                    () => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond),
                () => L2.GatewayIntentFillPort.ForLocalLane(this, coordinator.Settings.Port));
        }

        _l2 = coordinator;
        _settingsUi.BindL2(coordinator);
        _panel.BindL2(coordinator, _waveGap);
        _lastWaveSeen = _host.Simulation.CurrentWave;
    }

    /// <summary>
    /// Frame pump (window lane, T-137): wave transitions open the wave-gap
    /// window (async fills — the sim tick never waits on them); every frame
    /// pumps completions/timeouts, refreshes runtime health (a dead process
    /// flips the snapshot → 降级矩阵) and mirrors the tier hint on cadence.
    /// </summary>
    private void PumpL2Lane()
    {
        if (_l2 is null || _waveGap is null)
        {
            return;
        }

        int wave = _host.Simulation.CurrentWave;
        if (wave != _lastWaveSeen)
        {
            _lastWaveSeen = wave;
            _ = _waveGap.OnWaveGapOpened(); // 波间开窗：派发异步填充（不阻塞帧循环）
        }

        _waveGap.PumpFrame(); // 完成回投/超时放弃（主线统一落地）
        _l2.RefreshRuntimeHealth();
        if (Engine.GetProcessFrames() % L2HintRefreshFrames == 0)
        {
            _panel.RefreshL2Surfaces();
        }
    }

    /// <summary>One minimal chat request against the local OpenAI endpoint (47-t129 §3.2① warm-up).</summary>
    private async Task<bool> WarmupLocalServerAsync(int port, CancellationToken cancellationToken)
    {
        var request = new Godot.HttpRequest
        {
            MaxRedirects = 0,
            Timeout = 120d,
            UseThreads = true,
        };
        AddChild(request);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        request.RequestCompleted += (result, code, headers, body) =>
            completion.TrySetResult(
                result == (long)Godot.HttpRequest.Result.Success && code is >= 200 and <= 299);
        cancellationToken.Register(() =>
        {
            request.CancelRequest();
            completion.TrySetResult(false);
        });

        Error sent = request.Request(
            $"http://127.0.0.1:{port}/v1/chat/completions",
            ["Content-Type: application/json"],
            Godot.HttpClient.Method.Post,
            """{"messages":[{"role":"user","content":"ping"}],"max_tokens":1}""");
        if (sent != Error.Ok)
        {
            request.QueueFree();
            return false;
        }

        bool ok = await completion.Task.ConfigureAwait(true);
        request.QueueFree();
        return ok;
    }

    /// <summary>
    /// Combat effect requests this frame (T-102): units whose HP dropped
    /// since the previously observed snapshot — real combat events, never
    /// synthesized. The tier density gate (纯决策 RendererDecisionMap) picks
    /// which of them flash; the surviving set drives the unit-layer flash
    /// color in RebuildUnitChunks.
    /// </summary>
    private HashSet<long> CollectImpactFlashes(SimSnapshot? snapshot)
    {
        var flashes = new HashSet<long>();
        if (snapshot is null)
        {
            return flashes;
        }

        var requests = new List<long>();
        foreach (IUnit unit in snapshot.Units)
        {
            if (_prevUnitHp.TryGetValue(unit.Id.Value, out int before) && unit.Hp < before)
            {
                requests.Add(unit.Id.Value);
            }
        }

        _effectRequestsTotal += requests.Count;
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(_renderCaps, requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            if (RendererDecisionMap.AllowEffectSpawn(plan, i))
            {
                flashes.Add(requests[i]);
            }
        }

        _prevUnitHp.Clear();
        foreach (IUnit unit in snapshot.Units)
        {
            _prevUnitHp[unit.Id.Value] = unit.Hp;
        }

        return flashes;
    }

    /// <summary>
    /// L 档按需直绘 (T-102): rebuilds only camera-view chunks that have not
    /// been drawn yet — the static bake is skipped, so the ground buffer
    /// fills lazily as chunks become visible (camera pans draw new chunks).
    /// </summary>
    private void RebuildOnDemandGround(ref int chunksRebuilt, ref int framesWithWork)
    {
        (int x0, int y0, int x1, int y1) = CameraCellRect();
        IReadOnlyList<(int ChunkX, int ChunkY)> view = RendererDecisionMap.ChunksInView(
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells,
            _renderCaps.DirtyChunkCells, x0, y0, x1, y1);

        var pending = new List<(int ChunkX, int ChunkY)>();
        foreach ((int chunkX, int chunkY) in view)
        {
            if (_groundDrawnChunks.Add((chunkX, chunkY)))
            {
                pending.Add((chunkX, chunkY));
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        _groundLayer.RebuildGroundChunks(pending, _host.Framebuffer);
        chunksRebuilt += pending.Count;
        framesWithWork++;
    }

    /// <summary>Camera view rect in cells (main lens sits centered on the map; 32 px = 1 cell).</summary>
    private (int X0, int Y0, int X1, int Y1) CameraCellRect()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        Vector2 center = _camera.Position;
        float cell = SimSnapshotBridge.CellSizePx;
        return (
            (int)Math.Floor((center.X - size.X / 2f) / cell),
            (int)Math.Floor((center.Y - size.Y / 2f) / cell),
            (int)Math.Ceiling((center.X + size.X / 2f) / cell),
            (int)Math.Ceiling((center.Y + size.Y / 2f) / cell));
    }

    /// <summary>
    /// T-102 acceptance smoke (I 级): proves headless that (1) the three-tier
    /// decision tables are pairwise distinct over the OBSERVED live load,
    /// (2) auto-probe selects M under headless facts (no fps data ⇒ default
    /// unchanged), (3) the L tier executes for real — 60 frames through an
    /// L-configured bridge with the static bake skipped and only view chunks
    /// direct-drawn (each exactly once), and (4) the tier preference
    /// round-trips through the D-03 store. Prints RENDER_OK / RENDER_FAIL.
    /// </summary>
    private void RunRenderTierSmoke(int groundChunksRebuilt, int unitChunksRebuilt, int framesWithGroundWork)
    {
        var problems = new List<string>();

        // (1) three-tier decision diff over the real observed load (三档差异可测)
        var plans = new Dictionary<RenderTier, RenderFramePlan>();
        foreach (RenderTier tier in RenderTierMatrix.AllTiers)
        {
            plans[tier] = RendererDecisionMap.PlanFrame(
                RenderTierMatrix.Capabilities(tier), _effectRequestsTotal);
        }

        bool pairwiseDiff = plans[RenderTier.Low] != plans[RenderTier.Medium]
            && plans[RenderTier.Medium] != plans[RenderTier.High]
            && plans[RenderTier.Low] != plans[RenderTier.High];
        if (!pairwiseDiff)
        {
            problems.Add("三档决策表未两两不同");
        }

        // (2) auto probe under headless facts: no fps data ⇒ M default
        RenderProbeFacts facts = ProbeFacts();
        RenderTier probed = RenderTierSelector.Select(RenderTierPreference.Auto, facts);
        if (probed != RenderTier.Medium)
        {
            problems.Add($"headless 探测应保持 M 缺省，实选 {probed}");
        }

        // (3) L execution: 60 frames through an L-configured bridge on the
        // REAL host — bake skipped, only camera-view chunks direct-drawn.
        var lowCaps = RenderTierMatrix.Low;
        var lowBridge = new SimSnapshotBridge(
            _host.SnapshotSource, _host.Framebuffer,
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells,
            chunkCells: lowCaps.DirtyChunkCells);
        var drawn = new HashSet<(int, int)>();
        (int vx0, int vy0, int vx1, int vy1) = CameraCellRect();
        IReadOnlyList<(int ChunkX, int ChunkY)> viewChunks = RendererDecisionMap.ChunksInView(
            _host.Framebuffer.WidthCells, _host.Framebuffer.HeightCells,
            lowCaps.DirtyChunkCells, vx0, vy0, vx1, vy1);
        int lowFramesWithUnitWork = 0;
        for (int frame = 0; frame < AutoProbeFrames; frame++)
        {
            _host.StepFrame();
            lowBridge.SyncFrame();
            _ = lowBridge.TakeDirtyGroundChunks(); // L 档丢弃烘焙脏集（按需直绘替代）
            var unitChunks = lowBridge.TakeDirtyUnitChunks();
            if (unitChunks.Count > 0)
            {
                _unitLayer.RebuildUnitChunks(unitChunks, lowBridge.Current!);
                lowFramesWithUnitWork++; // 单位层不受烘焙策略影响（密度门见决策表）
            }

            var pending = new List<(int ChunkX, int ChunkY)>();
            foreach ((int chunkX, int chunkY) in viewChunks)
            {
                if (drawn.Add((chunkX, chunkY)))
                {
                    pending.Add((chunkX, chunkY));
                }
            }

            if (pending.Count > 0)
            {
                _groundLayer.RebuildGroundChunks(pending, _host.Framebuffer);
            }
        }

        if (drawn.Count != viewChunks.Count || drawn.Count == 0)
        {
            problems.Add($"L 档按需直绘应恰好画完视野 chunk 集（view={viewChunks.Count} drawn={drawn.Count}）");
        }

        if (lowFramesWithUnitWork > AutoProbeFrames)
        {
            problems.Add("L 档执行回路帧数超界");
        }

        // (4) preference persistence round-trip on the D-03 store (restore Auto)
        RenderTierStore.Save(_host.Store, new RenderTierSettings { Preference = RenderTierPreference.Low });
        bool persistedLow = RenderTierStore.Load(_host.Store).Preference == RenderTierPreference.Low;
        RenderTierStore.Save(_host.Store, RenderTierSettings.Default);
        bool restoredAuto = RenderTierStore.Load(_host.Store).Preference == RenderTierPreference.Auto;
        if (!persistedLow || !restoredAuto)
        {
            problems.Add("档位偏好持久化 round-trip 失败");
        }

        string tierSummaries = string.Join(" ", RenderTierMatrix.AllTiers.Select(tier =>
        {
            RenderFramePlan plan = plans[tier];
            return $"{RenderTierMatrix.TierId(tier)}[bake={(plan.BakeStaticGround ? "on" : "off")}" +
                   $",chunk={plan.ChunkCells},fx={plan.EffectSpawnsAllowed}/{plan.EffectSpawnRequests}" +
                   $",scan={(plan.ScanlinePass ? "on" : "off")},post={(plan.PostProcessPass ? "on" : "off")}]";
        }));

        if (problems.Count == 0)
        {
            GD.Print(
                "RENDER_OK tier=L|M|H decisions=diff auto=probe" +
                $"(sel={RenderTierMatrix.TierId(probed)} web={facts.IsWeb} fps=unknown-headless) " +
                $"{tierSummaries} wired={RenderTierMatrix.TierId(_renderCaps.Tier)} " +
                $"m_loop ground_chunks={groundChunksRebuilt} unit_chunks={unitChunksRebuilt} " +
                $"l_loop frames={AutoProbeFrames} view_chunks={viewChunks.Count} " +
                $"effect_requests={_effectRequestsTotal} persist=roundtrip-ok");
        }
        else
        {
            GD.PushError("RENDER_FAIL " + string.Join("; ", problems));
            GD.Print("RENDER_FAIL " + string.Join("; ", problems));
        }
    }

    // ------------------------------------------- T-126 四人格全量+语音

    /// <summary>
    /// T-126 四人格全量+语音 headless acceptance (F-109/F-137): the persona
    /// line bank loads at its frozen volume (4×50=200 rows, 16 faces per
    /// persona), the timbre table carries the sixteen era×persona rows, and
    /// one live persona switch flips all THREE arms in the same beat —
    /// wording (director persona + switch-in greeting), timbre (era:persona
    /// resolution) and portrait (P4 key + texture). Prints
    /// <c>PERSONA_OK lines=200 tones=16 switch=linked</c> or fails the run.
    /// </summary>
    private void RunPersonaFullSmoke()
    {
        var problems = new List<string>();

        // (1) 台词库全量：4×50=200 行，16 面×4 人格覆盖矩阵（解析器已在装载
        //     时执法行数与面完备性——这里复核冻结量级并驱动音效位一次）
        EraCore.Narrative.PersonaLineBank? bank = null;
        try
        {
            bank = EraCore.Narrative.PersonaLineBank.Parse(
                ReadRepoContent("narrative", "persona-lines.json"), "narrative/persona-lines.json");
        }
        catch (Exception ex)
        {
            problems.Add($"persona bank load: {ex.Message}");
        }

        int lines = bank?.Rows.Count ?? 0;
        int faces = bank is null ? 0 : Enum.GetValues<EraCore.Narrative.PersonaLineFace>().Length;
        if (bank is not null)
        {
            if (lines != 200)
            {
                problems.Add($"bank rows {lines} != 200");
            }

            foreach (EraCore.Narrative.SystemPersona persona in Enum.GetValues<EraCore.Narrative.SystemPersona>())
            {
                if (bank.RowsOf(persona).Count != EraCore.Narrative.PersonaLineBank.RowsPerPersona)
                {
                    problems.Add($"persona {persona} rows != 50");
                }
            }

            // 能力等价冒烟面：sys 槽位语法与中性库一致（L1 填充共用槽位解析；
            // 键约定 {persona}.{中性键 '.'分隔}，如 advisor.sys.alarm-integrity
            // ↔ 中性模板 sys-alarm-integrity）
            EraCore.Narrative.SystemLineLibrary neutral = EraCore.Narrative.SystemLineLibrary.Parse(
                ReadRepoContent("narrative", "system-lines.json"), "narrative/system-lines.json");
            foreach (EraCore.Narrative.PersonaLineRow row in bank.RowsOf(EraCore.Narrative.SystemPersona.Snarky,
                EraCore.Narrative.PersonaLineFace.Sys))
            {
                string situation = row.Key[(row.Key.IndexOf('.') + 1)..];
                EraCore.Narrative.SystemLineTemplate? template = neutral.Find(situation.Replace('.', '-'));
                if (template is null
                    || SlotNames(row.Text) is not { } slots
                    || !slots.SequenceEqual(template.Slots))
                {
                    problems.Add($"sys slot drift: {row.Key}");
                }
            }
        }

        // (2) 音色 16 行：时代×人格表（AudioDirector 已装载并过 AUDIO_OK——
        //     这里断言人格行数与 era 解析）
        Audio.AudioDirector? audio = GetNodeOrNull<Audio.AudioDirector>("AudioDirector");
        int tones = audio?.PersonaToneRowCount ?? 0;
        string voiceEra = audio?.VoiceEra ?? "-";
        if (tones != 16)
        {
            problems.Add($"persona tone rows {tones} != 16");
        }

        foreach (string era in EraCore.Audio.PersonaTimbreTable.Eras)
        {
            audio?.SetVoiceEra(era); // 语音时代全合法（BGM 六行齐——T-157 补 era4）
        }

        audio?.SetVoiceEra("era1");

        // (3) 三联动：一次切换同拍翻三臂——台词（导演+切入自报行）、音色
        //     （{era}:{persona} 解析）、立绘（P4 键+贴图装载）
        var narrative = GetNodeOrNull<Narrative.NarrativeHud>("NarrativeHud");
        var visual = GetNodeOrNull<Expand.VisualExpansionDirector>("VisualExpansionDirector");
        string personaFrom = _infoUi.CurrentPersonaId;
        const string probe = "goofy";
        bool switched = _infoUi.SwitchPersona(probe);
        bool linesArm = narrative?.Director?.Persona == EraCore.Narrative.SystemPersona.Goofy;
        bool timbreArm = audio is not null
            && audio.VoiceTimbreIdFor(EraCore.Narrative.SystemPersona.Goofy, systemEnabled: true) == $"{voiceEra}:goofy"
            && audio.VoiceTimbreIdFor(EraCore.Narrative.SystemPersona.Goofy, systemEnabled: false) == EraCore.Audio.PersonaTimbreTable.NarratorId;
        bool portraitArm = visual is not null
            && visual.ShownPersona == EraCore.Narrative.SystemPersona.Goofy
            && Expand.VisualExpansionDirector.PersonaPortraitKey(EraCore.Narrative.SystemPersona.Goofy)
                == EraCore.Presentation.PersonaChoice.PortraitKey(probe)
            && visual.PortraitTextureLoaded; // repo 内置 P4 立绘轨真实装载（D21-5）
        int cues = narrative?._cuesPlayed ?? 0;
        bool restored = _infoUi.SwitchPersona(personaFrom) && _infoUi.CurrentPersonaId == personaFrom
            && EraCore.Presentation.PersonaChoice.TryParse(personaFrom) is not null;

        if (!switched || !linesArm || !timbreArm || !portraitArm || !restored)
        {
            problems.Add(
                $"tri-link switched={switched} lines={linesArm} timbre={timbreArm} " +
                $"portrait={portraitArm} restored={restored}");
        }

        if (cues < 1)
        {
            problems.Add($"cue slots driven {cues} < 1（台词触发接音效位未走到）");
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"PERSONA_OK lines={lines} tones={tones} switch=linked " +
                $"bank=4x{EraCore.Narrative.PersonaLineBank.RowsPerPersona} faces={faces} " +
                $"eras={EraCore.Audio.PersonaTimbreTable.Eras.Count} voice_era={voiceEra} " +
                $"cues={cues} portrait=loaded restored={personaFrom} " +
                "source=persona-lines+persona-timbres-v2");
        }
        else
        {
            GD.PushError($"persona acceptance failed: {string.Join("; ", problems)}");
            GD.Print($"PERSONA_FAIL lines={lines} tones={tones} problems={problems.Count}");
            GetTree().Quit(1);
        }
    }

    /// <summary>【槽位名】集 of one template text (slot-syntax probe, sorted in-text order).</summary>
    private static List<string>? SlotNames(string text)
    {
        var names = new List<string>();
        int i = text.IndexOf('【');
        while (i >= 0)
        {
            int j = text.IndexOf('】', i);
            if (j < 0)
            {
                return null;
            }

            names.Add(text[(i + 1)..j]);
            i = text.IndexOf('【', j);
        }

        return names;
    }

    /// <summary>Repo walk-up content read (same convention as the mounted HUDs' loaders).</summary>
    private static string ReadRepoContent(string subDir, string file)
    {
        string relative = System.IO.Path.Combine("game", "content", subDir);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return System.IO.File.ReadAllText(System.IO.Path.Combine(probe, file));
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{subDir}（宿主须在仓库内运行）");
    }

    // ------------------------------------------------- T-106 Web 约束

    /// <summary>
    /// T-106 acceptance smoke (I 级, F-164/F-144): proves the Web-constraint
    /// code paths inside THIS engine process — (1) the platform tier table
    /// (<see cref="WebConstraints"/>: Web 行=L1 恒开+L2/L3 禁，桌面行=三档
    /// 全开，且 live 平台事实与 L3 门一致); (2) the L3 gateway platform gate
    /// refusing the STRONGEST case (enabled row + key stored + whitelisted
    /// domain) with the <c>web-tier-disabled</c> key, zero network, through
    /// the real <see cref="LlmConnectionProbe"/> copy chain; (3) the T-100
    /// save probe under the Web fact (e_sqlite3 unavailable) falling back to
    /// <see cref="JsonFileSavePort"/> with a Warn record; (4) the T-110 UI
    /// adaptation feature simulation (D-34 桌面口径) — the three real panels
    /// re-bind the Web tier row through <see cref="WebUiSurfaces"/> and the
    /// surfaces mirror: 军令框 AI 高级语法入口隐藏, 设置页 BYOK 表单隐藏+
    /// 对比条/画质档/AI 标注保留+Web 行文案, 词汇表 desktop_only 词条标注,
    /// then the desktop row restores with zero residue. The desktop real
    /// probe outcome is printed for observability but NOT gated (its native
    /// resolution inside the Godot mono host is environment-dependent).
    /// Prints WEB_OK / WEB_FAIL; a failure quits non-zero BEFORE SMOKE_OK.
    /// </summary>
    private void RunWebSmoke()
    {
        var problems = new List<string>();

        // (1) 平台三档表：Web 行与桌面行都断言（纯表离线全验）
        AgentTierAvailability webRow = WebConstraints.Availability(isWeb: true);
        AgentTierAvailability desktopRow = WebConstraints.Availability(isWeb: false);
        if (webRow is not { L1Grammar: true, L2LocalModel: false, L3CloudByok: false })
        {
            problems.Add("Web 档位行应 L1=on L2=off L3=off");
        }

        if (desktopRow is not { L1Grammar: true, L2LocalModel: true, L3CloudByok: true })
        {
            problems.Add("桌面档位行应三档全开");
        }

        bool liveWeb = OS.HasFeature("web");
        if (WebConstraints.Availability(liveWeb).L3CloudByok == liveWeb)
        {
            problems.Add("live 平台事实与 L3 门不一致（Web 必须 L3=off）");
        }

        // (2) L3 网关平台门：最强场景（默认模板激活行 enable=true + Key 在库）
        // 仍被 web-tier-disabled 拒绝——探针同步完成（零网络，wire 端口不可达）
        string smokeRoot = System.IO.Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", "web-smoke");
        if (System.IO.Directory.Exists(smokeRoot))
        {
            System.IO.Directory.Delete(smokeRoot, recursive: true);
        }

        var gateFiles = new DevFileStore(smokeRoot);
        var gateLog = new CollectingLogSink();
        var gateSecrets = SecretStoreFactory.Create(gateFiles, gateLog);
        LlmConfig config = LlmProviderPresets.DefaultConfig();
        gateSecrets.Store(config.ActiveProvider, "synthetic-web-smoke-key-000222");
        var gatedGateway = new LlmGateway(
            config, new UnreachableWirePort(), gateSecrets,
            cloudTierEnabled: WebConstraints.Availability(isWeb: true).L3CloudByok);
        LlmProbeResult probed = new LlmConnectionProbe(gatedGateway)
            .ProbeAsync(config.ActiveProvider).GetAwaiter().GetResult();
        if (probed.Ok || probed.ErrorKey != LlmErrorKeys.WebTierDisabled
            || probed.MessageZh.Length == 0 || probed.MessageEn.Length == 0)
        {
            problems.Add($"L3 平台门拒绝未按预期：ok={probed.Ok} key={probed.ErrorKey}");
        }

        // (3) T-100 存档探针 Web 事实：e_sqlite3 不可用 → JsonFileSavePort 回落
        // + Warn 记录（桌面真探针结果只观测不断言——Godot mono 宿主内原生库
        // 解析依环境而异，探针语义本就是"运行时决定"）
        var saveLog = new CollectingLogSink();
        ISavePort webSave = SavePortFactory.Create(gateFiles, saveLog, sqliteProbe: () => false);
        if (webSave is not JsonFileSavePort)
        {
            problems.Add($"Web 探针应回落 JsonFileSavePort，实得 {webSave.GetType().Name}");
        }

        if (!saveLog.Lines.Exists(line => line.Contains("json-fallback", StringComparison.Ordinal)))
        {
            problems.Add("Web 存档回落未记录 json-fallback");
        }

        ISavePort liveSave = SavePortFactory.Create(gateFiles, saveLog);
        string liveBackend = liveSave is JsonFileSavePort ? "json" : "sqlite";

        // (4) T-110 UI 禁用适配（feature 模拟，D-34 桌面口径——引擎侧 Web 导出
        // 被阻断，断言走与真实 Web 导出同一条 SetAgentTiers/WebUiSurfaces 镜像
        // 路径，Web 启用日同代码生效）：①军令框「AI 高级语法」入口 Web 隐藏；
        // ②设置页 BYOK 表单隐藏+T-109 对比条/画质档/AI 标注保留+Web 行文案；
        // ③词汇表 desktop_only 词条 Web 下标注「桌面版功能」；④桌面行恢复无残留。
        bool desktopAiHint = _panel.AiSyntaxHintVisible;
        _panel.SetAgentTiers(webRow);
        bool webAiHintHidden = !_panel.AiSyntaxHintVisible;

        bool desktopByokVisible = _settingsUi.IsFormRowVisible("KeyRow");
        _settingsUi.SetAgentTiers(webRow);
        bool webByokHidden = !_settingsUi.IsFormRowVisible("KeyRow")
            && !_settingsUi.IsFormRowVisible("ActionRow")
            && !_settingsUi.IsFormRowVisible("TemplateRow");
        bool webDisclosuresKept = _settingsUi.IsFormRowVisible("CompareHeading")
            && _settingsUi.IsFormRowVisible("L1Row")
            && _settingsUi.IsFormRowVisible("L2Row")
            && _settingsUi.IsFormRowVisible("L3Row")
            && _settingsUi.IsFormRowVisible("RenderTierRow")
            && _settingsUi.IsFormRowVisible("AiAssetLabel");
        bool webNoticeShown = _settingsUi.WebTierNoticeVisible;
        string webL3Copy = _settingsUi.CompareL3RowText;
        bool webL3CopyOk = webL3Copy.Contains("Web 版禁用", StringComparison.Ordinal)
            && webL3Copy.Contains("不发送任何数据", StringComparison.Ordinal);

        bool desktopNoAnnotation = !_infoUi.HelpRowTexts()
            .Any(text => text.Contains(WebUiSurfaces.DesktopOnlySuffixZh, StringComparison.Ordinal));
        _infoUi.SetAgentTiers(webRow);
        IReadOnlyList<string> webTermRows = _infoUi.HelpRowTexts();
        bool byokRowAnnotated = webTermRows.Any(text =>
            text.Contains("BYOK", StringComparison.Ordinal)
            && text.Contains(WebUiSurfaces.DesktopOnlySuffixZh, StringComparison.Ordinal));
        GlossaryEntry? byokTerm = _infoUi.ShowTerm("byok");
        bool byokDefAnnotated = byokTerm is not null
            && _infoUi.HelpDefinitionText.Contains(WebUiSurfaces.DesktopOnlySuffixZh, StringComparison.Ordinal)
            && _infoUi.HelpDefinitionText.Contains(WebUiSurfaces.DesktopOnlySuffixEn, StringComparison.Ordinal);

        if (!desktopAiHint || !webAiHintHidden)
        {
            problems.Add("军令框「AI 高级语法」入口未按 Web 档位行隐藏");
        }

        if (!desktopByokVisible || !webByokHidden || !webDisclosuresKept || !webNoticeShown)
        {
            problems.Add("设置页 Web 表面矩阵未按预期（BYOK 隐藏/披露保留/提示在位）");
        }

        if (!webL3CopyOk)
        {
            problems.Add($"对比条 Web 行文案缺失：{webL3Copy}");
        }

        if (!desktopNoAnnotation || !byokRowAnnotated || !byokDefAnnotated)
        {
            problems.Add("词汇表 desktop_only 标注未按 Web 档位行激活");
        }

        // 桌面行恢复（feature 模拟不得污染后续段/真桌面行为）
        _panel.SetAgentTiers(desktopRow);
        _settingsUi.SetAgentTiers(desktopRow);
        _infoUi.SetAgentTiers(desktopRow);
        bool desktopRestored = _panel.AiSyntaxHintVisible
            && _settingsUi.IsFormRowVisible("KeyRow")
            && !_settingsUi.WebTierNoticeVisible
            && !_infoUi.HelpRowTexts()
                .Any(text => text.Contains(WebUiSurfaces.DesktopOnlySuffixZh, StringComparison.Ordinal));
        if (!desktopRestored)
        {
            problems.Add("桌面档位行恢复失败（feature 模拟残留）");
        }

        if (problems.Count == 0)
        {
            GD.Print(
                "WEB_OK tier-table=l1-only-on-web l3-gate=" + LlmErrorKeys.WebTierDisabled +
                " models-gate=code-path(T-110) save-web-fallback=json warn=logged live_save_probe=" + liveBackend +
                " ui-adaptation=order-ai-syntax-hidden+byok-form-hidden+compare-bar-kept+glossary-desktop-only" +
                " render-tier-web=auto-L(T-102) coep=not-required(threads-off)");
        }
        else
        {
            GD.PushError("WEB_FAIL " + string.Join("; ", problems));
            GD.Print("WEB_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
    }

    // ------------------------------------------------- T-137 L2 acceptance

    /// <summary>
    /// T-137 headless acceptance (I 级, F-078/F-163): proves the L2 lane
    /// inside THIS engine process with SMOKE fakes only — no real model
    /// download, no real llama-server process (G4 纪律):
    /// <list type="number">
    /// <item>route matrix mirrors (L1 floor / L2 ready / L3 desktop / Web 行);</item>
    /// <item>degradation regression matrix（验收判据）: 模型缺 → L1 横幅；
    /// 进程死（真实状态机路径：假进程先 Ready 再退出 → RefreshRuntimeHealth
    /// 翻 ProcessDead）→ L1 横幅；波间超时（假时钟推过 25 s 预算）→ 放弃本轮
    /// 横幅——三路全部落 L1 且玩家可见；</item>
    /// <item>wave-gap happy lane: L1 澄清军令入队 → 波间开窗 → 异步填充
    /// （LookAround 候选）→ 泵回主线 → 五段管线 HandleSystemAction → 军师
    /// 气泡出 [L2 波间] 报告；开窗派发不等填充（不阻塞断言）；</item>
    /// <item>runtime state machine: spawn→/health→warm-up→Ready→死亡→降级。</item>
    /// </list>
    /// Prints L2_OK / L2_FAIL; failure quits non-zero BEFORE SMOKE_OK.
    /// </summary>
    private async Task RunL2SmokeAsync()
    {
        if (_l2 is null || _waveGap is null || _l2SmokeFill is null)
        {
            GD.PushError("L2_FAIL lane not initialized");
            GetTree().Quit(1);
            return; // synchronous headless caller (.Wait()) — no task leaks
        }

        var problems = new List<string>();
        var desktop = WebConstraints.Availability(isWeb: false);
        var web = WebConstraints.Availability(isWeb: true);

        // ---- (1) 路由矩阵镜像（纯表——与面板每次下令同源同判） -----------------
        var readySnapshot = new EraCore.Llm.L2.L2RuntimeSnapshot(
            EraCore.Llm.L2.L2RuntimePhase.Ready, ModelPresent: true, RuntimePresent: true);
        if (EraCore.Llm.L2.L2RouteDecider.Decide(EraCore.Llm.L2.LlmTierKind.L1Grammar, readySnapshot, desktop)
                is not { EffectiveTier: EraCore.Llm.L2.LlmTierKind.L1Grammar, Downgraded: false }
            || EraCore.Llm.L2.L2RouteDecider.Decide(EraCore.Llm.L2.LlmTierKind.L2Local, readySnapshot, desktop)
                is not { EffectiveTier: EraCore.Llm.L2.LlmTierKind.L2Local, Downgraded: false }
            || EraCore.Llm.L2.L2RouteDecider.Decide(EraCore.Llm.L2.LlmTierKind.L3Cloud, readySnapshot, desktop)
                is not { EffectiveTier: EraCore.Llm.L2.LlmTierKind.L3Cloud, Downgraded: false })
        {
            problems.Add("路由矩阵桌面行镜像不符（L1/L2/L3 三选直通）");
        }

        EraCore.Llm.L2.L2RouteDecision l2Web = EraCore.Llm.L2.L2RouteDecider.Decide(
            EraCore.Llm.L2.LlmTierKind.L2Local, readySnapshot, web);
        EraCore.Llm.L2.L2RouteDecision l3Web = EraCore.Llm.L2.L2RouteDecider.Decide(
            EraCore.Llm.L2.LlmTierKind.L3Cloud, readySnapshot, web);
        if (l2Web is not { Downgraded: true, Reason: EraCore.Llm.L2.L2DegradationReason.WebTier }
            || l3Web is not { Downgraded: true, Reason: EraCore.Llm.L2.L2DegradationReason.WebTier })
        {
            problems.Add($"Web 行未降级 L2/L3：l2={l2Web} l3={l3Web}");
        }

        // ---- (2) 降级回归矩阵：沙盒协调器（.tmp-tests 根——绝不写真模型进真根） --
        string l2Root = System.IO.Path.Combine(
            EraCore.IO.DevFileStore.DefaultDevRoot, ".tmp-tests", "l2-smoke");
        if (System.IO.Directory.Exists(l2Root))
        {
            System.IO.Directory.Delete(l2Root, recursive: true);
        }

        var l2Store = new EraCore.IO.DevFileStore(l2Root);
        var smokeProcess = new L2.SmokeServerProcess();
        var smokeProbe = new L2.SmokeHealthProbe();
        var sandbox = new L2.L2Coordinator(
            l2Store,
            new L2.DataRootDownloadFilePort(l2Root),
            () => new L2.UnreachableDownloadWirePort(),
            () => new L2.L2RuntimeHost(smokeProcess, smokeProbe, (_, _) => Task.FromResult(true)),
            desktop);

        long clockNow = 0;
        var fillPort = new L2.SmokeFillPort();
        var sandboxWaveGap = new L2.WaveGapL2Service(
            () => clockNow,
            new EraCore.Llm.L2.WaveGapFillQueue(() => clockNow),
            () => fillPort);
        _panel.BindL2(sandbox, sandboxWaveGap);

        // (2a) 模型缺：L2 选中但无模型文件 → 降 L1 + 中性横幅（玩家可见）
        sandbox.SelectTier(EraCore.Presentation.L2TierSelection.L2);
        EraCore.Llm.L2.L2RouteDecision modelMissing = sandbox.DecideRoute();
        if (modelMissing is not
            {
                EffectiveTier: EraCore.Llm.L2.LlmTierKind.L1Grammar,
                Reason: EraCore.Llm.L2.L2DegradationReason.ModelMissing,
                Downgraded: true,
            })
        {
            problems.Add($"模型缺路径未降 L1/ModelMissing：{modelMissing}");
        }

        _panel.RefreshL2Surfaces();
        if (_panel.L2BannerText != EraCore.Presentation.L2DegradationNotices.BannerZh(
                EraCore.Llm.L2.L2DegradationReason.ModelMissing))
        {
            problems.Add($"模型缺横幅文案不符：{_panel.L2BannerText}");
        }

        PipelineOutcomeKind? clarified = SubmitSmokeOrder("整个银河系帮我搬砖头"); // L1 澄清军令
        if (clarified != PipelineOutcomeKind.Clarification)
        {
            problems.Add($"L2 降级态军令未走 L1 澄清卡：{clarified}");
        }

        if (sandboxWaveGap.Queue.Count != 0)
        {
            problems.Add("降级态军令不应入波间队列");
        }

        // (2b) 就绪全链：哑模型/运行时文件（沙盒根内，仅满足路径存在性探测）→ 选 L2 自动拉起（假
        // 进程+假探针）→ Ready；档位提示切 L2；澄清军令入队 → 波间开窗 →
        // 异步填充 → 泵回 → 五段管线 → [L2 波间] 报告；开窗不阻塞断言。
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(l2Root, "models"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(l2Root, "runtime", "unpacked"));
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(l2Root, "models", EraCore.Llm.L2.L2Facts.Model.FileName), "smoke-dummy-model");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(l2Root, "runtime", "unpacked", "llama-server.exe"), "smoke-dummy-exe");

        sandbox.SelectTier(EraCore.Presentation.L2TierSelection.L2);
        L2.L2RuntimeStartResult start = sandbox.RuntimeStartTask is { } starting
            ? await starting.ConfigureAwait(true)
            : new L2.L2RuntimeStartResult(false, "auto-start missing");
        if (!start.Ok || sandbox.Snapshot.Phase != EraCore.Llm.L2.L2RuntimePhase.Ready)
        {
            problems.Add($"假进程启动未到 Ready：{start.Detail} phase={sandbox.Snapshot.Phase}");
        }

        if (smokeProbe.Calls == 0)
        {
            problems.Add("健康探针未被调用（探测循环未执行）");
        }

        _panel.RefreshL2Surfaces();
        if (!_panel.L2TierHintText.Contains("L2 本地模型"))
        {
            problems.Add($"就绪档位提示未切 L2：{_panel.L2TierHintText}");
        }

        fillPort.Enqueue(EraCore.Llm.L2.L2IntentFillResult.Succeeded("""{"action":"LookAround"}"""));
        PipelineOutcomeKind? queuedOutcome = SubmitSmokeOrder("北边那坨啥情况给我瞅瞅"); // L1 澄清 → 入队
        if (queuedOutcome != PipelineOutcomeKind.Clarification || sandboxWaveGap.Queue.Count != 1)
        {
            problems.Add($"就绪态澄清军令未入波间队列：outcome={queuedOutcome} queue={sandboxWaveGap.Queue.Count}");
        }

        clockNow = 1_000;
        EraCore.Llm.L2.L2QueuedFill? opened = sandboxWaveGap.OnWaveGapOpened();
        if (opened is null)
        {
            problems.Add("波间开窗未派发填充");
        }

        if (_panel.ReportText.Contains("[L2 波间]"))
        {
            problems.Add("开窗即同步渲染（填充不得阻塞开窗调用）");
        }

        sandboxWaveGap.PumpFrame(); // 填充已同步落 _landed → 主线泵回五段管线
        if (!_panel.ReportText.Contains("[L2 波间]"))
        {
            problems.Add($"波间填充未回投五段管线：{_panel.ReportText}");
        }

        // (2c) 波间超时：悬挂填充口（队列空=永不返回）→ 开窗 → 时钟推过 25 s
        // 预算 → 泵 → 放弃本轮（横幅+不入管线）。
        EraCore.Llm.L2.L2QueuedFill? timeoutOrder = SubmitForWaveGap(sandboxWaveGap, "这道令你慢慢想不着急");
        clockNow += 1_000;
        _ = sandboxWaveGap.OnWaveGapOpened();
        clockNow += EraCore.Llm.L2.WaveGapFillQueue.BudgetMs; // 推过预算线
        bool stillInFlight = sandboxWaveGap.Queue.InFlight is not null;
        sandboxWaveGap.PumpFrame();
        if (sandboxWaveGap.Queue.InFlight is not null || !stillInFlight)
        {
            problems.Add("超时未放弃在飞单（放弃本轮不卡波）");
        }

        if (_panel.L2BannerText != EraCore.Presentation.L2DegradationNotices.BannerZh(
                EraCore.Llm.L2.L2DegradationReason.FillTimeout))
        {
            problems.Add($"超时横幅文案不符：{_panel.L2BannerText}");
        }

        // (2d) 进程死（真实状态机路径）：Ready 后假进程退出 → RefreshRuntimeHealth
        // 翻 ProcessDead → 降 L1 横幅。
        smokeProcess.ExitNow();
        sandbox.RefreshRuntimeHealth();
        EraCore.Llm.L2.L2RouteDecision dead = sandbox.DecideRoute();
        if (dead is not
            {
                EffectiveTier: EraCore.Llm.L2.LlmTierKind.L1Grammar,
                Reason: EraCore.Llm.L2.L2DegradationReason.ProcessDead,
                Downgraded: true,
            })
        {
            problems.Add($"进程死路径未降 L1/ProcessDead：{dead}");
        }

        _panel.RefreshL2Surfaces();
        if (!_panel.L2BannerText.Contains("进程已退出"))
        {
            problems.Add($"进程死横幅文案不符：{_panel.L2BannerText}");
        }

        // 停机清理：Stop 后快照回 Idle（文件仍在）——不再占用进程位。
        sandbox.StopRuntime();
        if (sandbox.Snapshot.Phase != EraCore.Llm.L2.L2RuntimePhase.Idle)
        {
            problems.Add($"停机后快照未回 Idle：{sandbox.Snapshot.Phase}");
        }

        // 沙盒清场（dev 根 .tmp-tests 下，gitignored）
        sandbox.Dispose();
        if (System.IO.Directory.Exists(l2Root))
        {
            System.IO.Directory.Delete(l2Root, recursive: true);
        }

        // 还原绑定（headless 随后退出——保持接线一致性）
        _panel.BindL2(_l2, _waveGap);

        if (problems.Count == 0)
        {
            GD.Print(
                "L2_OK route=matrix-desktop+web degrade=model-missing+process-dead+fill-timeout" +
                " wavegap=enqueue-open-pump-nonblocking runtime=spawn-health-warmup-ready-death-idle" +
                " ui=tier-hint+neutral-banner dataflow=compare-sync tier=persisted-l2-tier-json" +
                " download=engine-g1-tested(fake-wire) real-model=absent-by-design");
        }
        else
        {
            GD.PushError("L2_FAIL " + string.Join("; ", problems));
            GD.Print("L2_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
    }

    /// <summary>Submits one order through the REAL panel signal path (smoke helper).</summary>
    private PipelineOutcomeKind? SubmitSmokeOrder(string order)
    {
        _panel.OrderInput.Text = order;
        _panel.OrderInput.EmitSignal(LineEdit.SignalName.TextSubmitted, order);
        return _panel.LastOutcome;
    }

    /// <summary>Enqueues one order straight into a wave-gap queue without the panel gate (timeout-lane helper).</summary>
    private static EraCore.Llm.L2.L2QueuedFill? SubmitForWaveGap(L2.WaveGapL2Service service, string order) =>
        service.Enqueue(order);

    /// <summary>
    /// ILogSink collector (K-1): captures lines so smoke assertions can read
    /// the degradation records (同面板 PanelLogSink 私有纪律——按文件私有).
    /// </summary>
    private sealed class CollectingLogSink : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Write(LogLevel level, long tick, string message) =>
            Lines.Add($"[{level}] {message}");
    }

    /// <summary>
    /// IHttpPort that must never be reached: the T-106 platform gate refuses
    /// BEFORE the wire port exists in the call path — any SendAsync call
    /// throws, which the probe classifies as <c>network</c> and the smoke
    /// then fails loudly on the expected <c>web-tier-disabled</c> key.
    /// </summary>
    private sealed class UnreachableWirePort : IHttpPort
    {
        public Task<HttpPortResponse> SendAsync(
            HttpPortRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "platform gate leaked: the wire port must be unreachable on the Web tier");
    }
}
