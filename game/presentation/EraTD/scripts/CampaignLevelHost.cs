using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Agent.Grammar;
using EraCore.Agent.Pipeline;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Checkpoint;
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
using EraCore.Supply;
using EraCore.Telemetry;
using EraCore.Units;
using Persona = EraCore.Cinematics.SystemPersona;

namespace EraTD;

// T-160 战役宿主核心（ProceduralEntryHost 同律的表现层编排——纯 C#，无 Godot
// 节点引用）：把既有引擎件（CampaignState/CampaignProgression/LegacyLedger/
// EraTransitionService/LevelSession/VictorySettlementStore/LevelDurationTelemetry）
// 组装成「从 UI 玩全战役」的宿主面。引擎零改动——装载与组装走 T-160 提升的
// 生产面（HandcraftedLevelCatalog + EraLevelAssembly）。
//
// 进度态三源：①时代位=CampaignState（K-7 campaign 文档 EraIndex）②占领/★=
// VictorySettlementStore 档案（★=三重目标通过数，0~3）③奇观位=LegacyLedger
// （e6-final 的 requires_wonder 通关之门数据行）。解锁律（章内顺序推进）：
// 章首恒可玩；slot n 需同章 slot<n 全部已通关；e6-final 额外需纪元之门奇观
// 位（=e6 非终局四关全清后落位——V10 章末奇观位同律的终章形态）。
//
// T-160c 增两面：①败北面——引擎调用约定=宿主检测（基地陷落：仓库 HP 归零）
// +DeclareDefeat 声明→普通关分档（检查点恢复+残骸修复账）+失败面板三行动
// （从检查点继续=TryResumeCheckpoint 重放续局/重开本关=清本关存档域从头开/
// 退回菜单）+遥测 Defeat 行（T-155 终态三态收口）；②演出钩子——终局关胜
// 利拍发射序=收束演出（e3/e4/e5-finale，文件存在性驱动）→跃迁过场；剧情钩
// 子路由（CinematicHookRouter）随当前关的 cinematic_hooks 行构造（e1-01 断
// 弹波始拍/e2-01 觉醒开局拍——level_start+wave_start 两拍，演出经显式通道
// 入队，进度门=已看过不重播）。

/// <summary>
/// The campaign host core: level loading through the production assembly,
/// victory settlement, era transitions, the finale pick, campaign save/resume
/// and the telemetry lane. One instance per UI mount (windowed = the player's
/// D-03 root; headless smoke = a sandbox root).
/// </summary>
internal sealed class CampaignLevelHost
{
    private const long WorldSeed = 20261013; // ci-allow: R7 K-7 WorldSeed 锚（CampaignLoopFixture.WorldSeed 同族——战役布局键，非平衡数值）
    private const int GridWidth = 6;        // ci-allow: R7 选图大陆网格（CampaignLoopFixture 同锚，结构域常量）
    private const int GridHeight = 5;
    private const int CounterSampleTicks = 600; // 审计计数采样节奏（V10 链 600 tick 同锚）

    private readonly string _contentRoot;
    private readonly IFileStore _files;
    private readonly ISavePort _port;
    private readonly LevelDurationTelemetry? _telemetry;
    private readonly string _telemetrySource;
    private readonly Dictionary<int, HandcraftedLevelSet> _sets = new();
    private readonly ChestTable _chestTable;
    private readonly int _wheelSegments;
    private readonly IReadOnlyList<WonderSpec> _wonders;
    private readonly RelicMappingParams _mapping;
    private readonly EraTransitionSpec _transition;

    private CampaignState _campaign = null!;
    private TechTree _tech = null!;
    private LegacyLedger _ledger = null!;
    private CampaignProgression _progression = null!;
    private ICampaignMeta _meta = null!;
    private VictorySettlementArchive _victoryArchive = VictorySettlementArchive.Default;

    private CampaignRun? _current;
    private bool _victoryHandled;
    private readonly List<CinematicLaunch> _pendingLaunches = new();
    private string _completionSummary = string.Empty;

    // —— T-160c 败全面（引擎调用约定=宿主检测+DeclareDefeat 声明，见 PumpWindowFrame）——
    private readonly HashSet<string> _defeatSeen = new(StringComparer.Ordinal); // 首败折扣键（本坐口径）
    private DefeatPanelFacts? _pendingDefeat;

    // —— T-160c 战役泳道剧情钩子（路由随当前关构造——REV-20261019-T160c §断点二）——
    private readonly IReadOnlyList<CinematicScript> _hookScripts; // 带钩子键的脚本全集（内容侧三件）
    private CinematicHookRouter _hookRouter = new(Array.Empty<CinematicHookRow>(), Array.Empty<CinematicScript>());
    private CinematicProgress _cinematicProgress = CinematicProgress.Default;
    private int _lastSeenWave = -1;

    // 审计计数累计（HostRunCounters 的活体输入——V10 采样律）
    private double _integrityMin = 1.0;
    private double _starvedSeconds;
    private double _waveEndEfficiencyMin = 1.0;
    private long _lastSampleTick;

    private CampaignLevelHost(
        string contentRoot,
        IFileStore files,
        ISavePort port,
        LevelDurationTelemetry? telemetry,
        string telemetrySource)
    {
        _contentRoot = contentRoot;
        _files = files;
        _port = port;
        _telemetry = telemetry;
        _telemetrySource = telemetrySource;

        for (int era = 1; era <= CampaignState.MaxHostedEraIndex; era++)
        {
            string path = System.IO.Path.Combine(contentRoot, "levels", $"e{era}.json");
            _sets[era] = HandcraftedLevelCatalog.Parse(System.IO.File.ReadAllText(path));
        }

        _chestTable = ChestRewardsData.Parse(ReadContent("campaign", "chests.json"));
        _wheelSegments = WheelSegmentsOfVictoryScript();
        _wonders = LegacyDataLoader.ParseWonders(ReadContent("legacy", "wonders.json"));
        _mapping = LegacyDataLoader.ParseRelicMapping(ReadContent("legacy", "relic_mapping.json"));
        _transition = LegacyDataLoader.ParseEraTransition(ReadContent("legacy", "era_transition.json"));

        _campaign = new CampaignState(WorldSeed, new EndlessHostContent.SplitMix64Rng(WorldSeed), GridWidth, GridHeight);
        _ledger = new LegacyLedger(
            LegacyDataLoader.ParseArchaeology(ReadContent("legacy", "archaeology.json")),
            LegacyDataLoader.ParseHeritageShop(ReadContent("legacy", "heritage_shop.json")));
        _victoryArchive = VictorySettlementStore.Load(files);

        // T-160c 剧情钩子脚本全集：内容侧带 hook 键的脚本恰三件（e1-aha 断弹/
        // e2-awaken 觉醒/finale-zero 终章真相——CinematicHookRouterTests+
        // FinaleCinematicsTests 钉死该集）。路由绑定按当前关的钩子行过滤（见
        // BuildHookRouter），非本关声明集的脚本不进会话路由。
        _hookScripts = new[]
        {
            CinematicScript.Parse(ReadContent("cinematics", "e1-aha.json"), "cinematics/e1-aha.json"),
            CinematicScript.Parse(ReadContent("cinematics", "e2-awaken.json"), "cinematics/e2-awaken.json"),
            CinematicScript.Parse(ReadContent("cinematics", "finale-zero.json"), "cinematics/finale-zero.json"),
        };

        bool restored = false;
        try
        {
            _ = _campaign.LoadPoolFrom(port);
            restored = true;
        }
        catch (InvalidOperationException)
        {
            // 无 campaign 文档（或种子不符）：全新战役——E1 起步。
            _campaign = new CampaignState(WorldSeed, new EndlessHostContent.SplitMix64Rng(WorldSeed), GridWidth, GridHeight);
        }

        _meta = restored ? port.ReadCampaign(SaveVersion.Current) : EmptyMeta();
        if (restored)
        {
            RestoreLedger(_meta);
        }

        _tech = new TechTree(TechTreeData.Parse(ReadContent("campaign", "techtree.json")), _campaign.Pool, _campaign.EraIndex);
        _progression = NewProgression();
        foreach (string levelId in _victoryArchive.Records.Select(record => record.LevelId).Distinct())
        {
            _tech.RegisterCapture(levelId); // 占领恢复（跃迁 Precheck 的树面输入）
        }
    }

    /// <summary>Factory（装载即校验：六个关卡文件+五张 legacy 表+结算三件全数到位）.</summary>
    public static CampaignLevelHost Create(
        IFileStore files, ISavePort port, LevelDurationTelemetry? telemetry, string telemetrySource)
    {
        string contentRoot = FindContentRoot();
        return new CampaignLevelHost(contentRoot, files, port, telemetry, telemetrySource);
    }

    // ------------------------------------------------------------ 面向外界面

    /// <summary>当前战役时代位（1..6）.</summary>
    public int EraIndex => _campaign.EraIndex;

    /// <summary>活动关卡绑定（无则 null）.</summary>
    public CampaignRun? Current => _current;

    /// <summary>是否正在局内（帧泵推进的门槛）.</summary>
    public bool InLevel => _current is { } run
        && run.Session.Machine.State is GameFlowState.InLevel or GameFlowState.Paused;

    /// <summary>待开演队列（胜利结算=带 decided；跃迁/终章=纯脚本 id）.</summary>
    public IReadOnlyList<CinematicLaunch> PendingLaunches => _pendingLaunches;

    /// <summary>终章通关总结（e6-final 胜利后置位——制作组/全清面）.</summary>
    public string CompletionSummary => _completionSummary;

    /// <summary>章节分组菜单（e1~e6 每章按 slot 排序+进度态）.</summary>
    public IReadOnlyList<CampaignChapter> Chapters()
    {
        var chapters = new List<CampaignChapter>();
        foreach (int era in _sets.Keys.Order())
        {
            string eraName = EraNameOf(era);
            var rows = _sets[era].Items
                .OrderBy(level => level.Slot)
                .Select(LevelRow)
                .ToList();
            chapters.Add(new CampaignChapter(era, $"第 {era} 章 · {eraName}（时代 {era}）", rows));
        }

        return chapters;
    }

    /// <summary>构造一关的会话（装载矩阵面：Menu 态构造即证明，不开局）.</summary>
    public LevelSessionProbe ProbeSession(string levelId)
    {
        HandcraftedLevel level = LevelById(levelId);
        var config = BuildSessionConfig(level);
        var session = new LevelSession(config, new EndlessHostContent.SplitMix64Rng(level.Map.Seed), _port, _campaign.Pool);
        return new LevelSessionProbe(level, session, EraLevelAssembly.BuildCommands(level, _contentRoot));
    }

    /// <summary>
    /// 开局/续局一关：#exit 快照在位即 TryResume 无损续（Paused 冻结位回
    /// InLevel），否则新开局（命令流=该关脚本全量入队）。
    /// </summary>
    public void BeginLevel(string levelId) => BeginLevel(levelId, fromCheckpoint: false);

    /// <summary>
    /// T-160c 检查点续局入口（失败面板「从检查点继续」）：从本关检查点波快照
    /// 重放续局（<see cref="LevelSession.TryResumeCheckpoint"/>）；检查点不在位
    /// （被清/未跨）时诚实回退从头开局。
    /// </summary>
    private void BeginLevel(string levelId, bool fromCheckpoint)
    {
        HandcraftedLevel level = LevelById(levelId);
        var config = BuildSessionConfig(level);
        var rng = new EndlessHostContent.SplitMix64Rng(level.Map.Seed);
        bool resumedExit = false;
        bool resumedCheckpoint = false;
        LevelSession session;
        if (fromCheckpoint)
        {
            resumedCheckpoint = LevelSession.TryResumeCheckpoint(
                config, rng, _port, _campaign.Pool, out session, _telemetry);
        }
        else
        {
            resumedExit = LevelSession.TryResume(
                config, rng, _port, _campaign.Pool, out session, _telemetry);
        }

        if (!resumedExit && !resumedCheckpoint)
        {
            session.Machine.StartLevel();
            foreach (SimCommand command in EraLevelAssembly.BuildCommands(level, _contentRoot))
            {
                session.Enqueue(command);
            }
        }
        else
        {
            session.ResumePlaying(); // #exit/#ckpt 冻结在 Paused——回到 InLevel 续走
        }

        _current = new CampaignRun(session, level, resumedExit, config, resumedCheckpoint);
        _victoryHandled = false;
        _pendingDefeat = null;          // 开新局即撤下失败面板（旧分档事实已入遥测）
        _integrityMin = 1.0;
        _starvedSeconds = 0.0;
        _waveEndEfficiencyMin = 1.0;
        _lastSampleTick = session.Sim.CurrentTick;

        // T-160c 剧情钩子路由：随当前关构造（会话内单发）；进度门=已看过的
        // 演出不重播（CinematicDirector.LaunchByHook 同律——本宿主走显式通道，
        // 门在入队侧）。level_start 拍只在新开局发射（续局=同一局的延续，不重演）。
        _cinematicProgress = CinematicProgressStore.Load(_files);
        _hookRouter = BuildHookRouter(level);
        _lastSeenWave = session.Sim.CurrentWave;
        if (!resumedExit && !resumedCheckpoint)
        {
            foreach (string scriptId in _hookRouter.OnLevelStart())
            {
                EnqueueLaunch(scriptId);
            }
        }
    }

    /// <summary>
    /// 当前关的钩子路由：行=<see cref="HandcraftedLevel.CinematicHooks"/>（逐关
    /// 数据位，非整文件混装）；可路由脚本=钩子键在本关声明集内者（e1 关不绑
    /// e2-awaken——绑死夹具 e1+e2 混装装载的收口，beat 表复用引擎封闭表）。
    /// </summary>
    private CinematicHookRouter BuildHookRouter(HandcraftedLevel level)
    {
        string source = $"levels/e{EraLevelAssembly.EraIndexOf(level)}.json:{level.Id}";
        var rows = new List<CinematicHookRow>();
        foreach (CinematicHook hook in level.CinematicHooks)
        {
            rows.Add(new CinematicHookRow(
                hook.Id, CinematicHookRouter.ParseBeat(hook.Beat, source, hook.Id), hook.Wave));
        }

        var routable = new List<CinematicScript>();
        foreach (CinematicScript script in _hookScripts)
        {
            if (script.HookId is not null && rows.Any(row => row.HookId == script.HookId))
            {
                routable.Add(script);
            }
        }

        return new CinematicHookRouter(rows, routable);
    }

    /// <summary>
    /// 剧情钩子演出入队（beat 路由→显式发射通道）：已在本待演队列=单发幂等；
    /// 进度标记已看过=不重播。终章二选一的互斥由显式发射位保证（e6-final 胜利
    /// 拍=FinalePlayback.ScriptIdFor 开关二选一，钩子路由不抢该拍——见 REV）。
    /// </summary>
    private void EnqueueLaunch(string scriptId)
    {
        if (_pendingLaunches.Any(launch => launch.ScriptId == scriptId))
        {
            return;
        }

        if (CinematicProgressStore.IsCompleted(_cinematicProgress, scriptId))
        {
            return;
        }

        _pendingLaunches.Add(new CinematicLaunch(scriptId, Settlement: null));
    }

    /// <summary>
    /// T-160b（战役军师面板可用面）：为活动关构造军师面板宿主——真五段管线
    /// over 该关 LevelSession 的 sim（时代治理=关卡时代序，E1 关系统点不启
    /// 用=设计律；命令经引擎观察缝记入会话命令流，重放键完整）。会话结束由
    /// 调用方 Dispose（战报文件句柄）。
    /// </summary>
    public CampaignAdvisorBoard BuildAdvisorBoard(CampaignRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        string eraDir = $"era{EraLevelAssembly.EraIndexOf(run.Level)}";
        var parser = L1IntentParser.FromJson(
            ReadContent("grammar", "patterns-zh.json"),
            ReadContent("grammar", "patterns-en.json"),
            ReadContent("grammar", "lexicon.json"),
            ReadContent(eraDir, "towers.json"));

        // 战报=K-8 JSONL（GUID 文件名——并行局/连打多关不互撞，NetDemoLevel 同
        // 律）；根=宿主自己的 store 根（玩家局落 D-03 玩家根，冒烟局落沙盒根）。
        string journalPath = System.IO.Path.Combine(
            _files.Root, "reports", $"t160b-campaign-{run.Level.Id}-{System.Guid.NewGuid():N}.jsonl");
        var journal = new JsonlJournalWriter(journalPath);

        var pipeline = AgentPipeline.OverSimulation(
            run.Session.Sim,
            run.Config.LevelConfig,
            parser,
            EraLevelAssembly.EraIndexOf(run.Level),
            journal,
            new QuietLogSink(),
            new Dictionary<string, CellCoord>()); // 手工关卡数据无命名分区：分区军令诚实澄清/拒绝（无伪造锚点）
        pipeline.Ledger.BeginWave(Math.Max(1, run.Session.Sim.CurrentWave), isBossWave: false);

        return new CampaignAdvisorBoard(run, pipeline, _files, journal, journalPath);
    }

    /// <summary>
    /// 窗口帧泵：1x 帧律一帧一 tick；胜利/败北落地时返回 true（UI 刷新面）。
    /// T-160c 败北判定（引擎调用约定：引擎无自动败北谓词——<see cref=
    /// "LevelSession.DeclareDefeat"/> 由宿主在检测到败北条件时调用）：步进后
    /// 检测基地陷落（仓库=本关唯一基地建筑，HP 归零即陷落——真实 sim 状态读
    /// 面）。生产内容当前无建筑伤害路径（践踏脉冲=引擎数据位先行、宿主未驱动
    /// ——REV 登记），冒烟经 <see cref="DeclareDefeat"/> 声明入口构造败北（同
    /// 一条结算链；无尽 Die() 的声明式终局同律）。
    /// </summary>
    public bool PumpWindowFrame()
    {
        if (_current is not { } run)
        {
            return false;
        }

        if (run.Session.Machine.State == GameFlowState.InLevel)
        {
            run.Session.RunFor(1);
            SampleCounters(run);
        }

        if (run.Session.Machine.State == GameFlowState.Victory && !_victoryHandled)
        {
            HandleVictory(run);
            return true;
        }

        if (run.Session.Machine.State == GameFlowState.InLevel && IsBaseFallen(run))
        {
            HandleDefeat(run, "基地陷落（仓库 HP 归零）");
            return true;
        }

        // T-160c 剧情钩子·波始拍：CurrentWave 前进=新波开打（锚波精确匹配——
        // e1-01 第 2 波断弹位）。首见波=基线不发射（开局/续局的当前波非新拍）。
        if (run.Session.Machine.State == GameFlowState.InLevel)
        {
            int wave = run.Session.Sim.CurrentWave;
            if (wave != _lastSeenWave)
            {
                if (wave > _lastSeenWave)
                {
                    foreach (string scriptId in _hookRouter.OnWaveStart(wave))
                    {
                        EnqueueLaunch(scriptId);
                    }
                }

                _lastSeenWave = wave;
            }
        }

        return false;
    }

    /// <summary>基地陷落判定：仓库（本关唯一基地建筑）HP 归零.</summary>
    private static bool IsBaseFallen(CampaignRun run) =>
        run.Session.Sim.Buildings.Any(
            building => building.Kind == SimBuildingKind.Warehouse && building.Hp <= 0);

    /// <summary>
    /// 宿主声明式败北入口：活局（InLevel）上调用即走完整败北结算（检查点分档
    /// +遥测 Defeat 行+失败面板事实）。判定命中（IsBaseFallen）与冒烟构造走
    /// 同一条 <see cref="HandleDefeat"/> 链——引擎 DeclareDefeat 的调用约定
    /// （宿主检测→声明→Acknowledge）在此收口。
    /// </summary>
    /// <returns>False=无活局可声明（已 Victory/Defeat/Menu）.</returns>
    public bool DeclareDefeat(string reason)
    {
        if (_current is not { } run || run.Session.Machine.State != GameFlowState.InLevel)
        {
            return false;
        }

        HandleDefeat(run, reason);
        return true;
    }

    /// <summary>
    /// 败北结算（v3.2 §3/§7 失败分档的宿主面）：引擎 DeclareDefeat=机器
    /// InLevel→Defeat+遥测 Defeat 行+T-057 普通关分档（检查点恢复+残骸修复
    /// 账）；#exit 快照随关终局清档（重开不得误续死局）；分档事实提取入
    /// <see cref="PendingDefeat"/>（失败面板）后机器回 Menu。
    /// </summary>
    private void HandleDefeat(CampaignRun run, string reason)
    {
        bool firstDefeat = _defeatSeen.Add(run.Level.Id); // 首败折扣键（本坐口径，REV 登记）
        NormalLevelFailureOutcome outcome = run.Session.DeclareDefeat(firstDefeat);
        run.Session.ClearExitSnapshot();
        run.Session.AcknowledgeDefeat();
        _pendingDefeat = new DefeatPanelFacts(
            run.Level.Id,
            run.Level.NameZh,
            reason,
            outcome.RestoredFromWave,
            outcome.RestoredAtTick,
            run.Session.Checkpoints.CheckpointWave,
            outcome.Wreckage.Count,
            outcome.TotalRepairCostPoints,
            outcome.FirstDefeat,
            // 检查点波恒 ≥10（Campaign.Checkpoint 下限）——恢复波>0 ⟺ 已跨
            // 检查点波（面板有「继续」档；0=未跨=仅重开）。
            ContinueAvailable: outcome.RestoredFromWave > 0);
    }

    /// <summary>失败面板事实（败绩分档呈现——PendingDefeat 非空即面板在位）.</summary>
    public DefeatPanelFacts? PendingDefeat => _pendingDefeat;

    /// <summary>
    /// 失败面板「从检查点继续」：检查点续局（TryResumeCheckpoint 重放到检查
    /// 点波边界续走）。无检查点（面板无此档）返回 False。
    /// </summary>
    public bool ContinueFromCheckpoint()
    {
        if (_pendingDefeat is not { ContinueAvailable: true } defeat)
        {
            return false;
        }

        string levelId = defeat.LevelId;
        _pendingDefeat = null;
        BeginLevel(levelId, fromCheckpoint: true);
        return _current is { ResumedFromCheckpoint: true };
    }

    /// <summary>
    /// 失败面板「重开本关」：清本关存档域（检查点/滚动/#exit——终局关清档
    /// WaveCheckpointStore.Clear 同律）后从头开局（玩家明确放弃检查点进度）。
    /// </summary>
    public void RestartLevel()
    {
        if (_pendingDefeat is not { } defeat)
        {
            return;
        }

        _pendingDefeat = null;
        if (_current is { Session.Checkpoints: { } store })
        {
            store.Clear();
        }

        BeginLevel(defeat.LevelId);
    }

    /// <summary>失败面板「退回菜单」：分档已入面板/遥测，检查点域保留（下次进关可续）.</summary>
    public void DismissDefeatToMenu()
    {
        _pendingDefeat = null;
    }

    /// <summary>冒烟泵：与 <see cref="PumpWindowFrame"/> 同律（headless 直驱）.</summary>
    public void PumpOneTick()
    {
        _ = PumpWindowFrame();
    }

    /// <summary>
    /// 退出到菜单（诚实存档续玩面）：level 域 #exit 快照（SaveAndExitToMenu
    /// ——遥测随终态自动落 Abandoned 行）+ campaign 域合并写。
    /// </summary>
    public void ExitToMenu()
    {
        if (_current is not { } run || run.Session.Machine.State is not (GameFlowState.InLevel or GameFlowState.Paused))
        {
            return;
        }

        if (run.Session.Machine.State == GameFlowState.InLevel)
        {
            run.Session.Pause();
        }

        run.Session.SaveAndExitToMenu();
        SaveCampaignDocument();
        _current = null;
    }

    /// <summary>排空待开演队列（窗口 UI 消费后调用）.</summary>
    public IReadOnlyList<CinematicLaunch> DrainPendingLaunches()
    {
        var drained = _pendingLaunches.ToList();
        _pendingLaunches.Clear();
        return drained;
    }

    /// <summary>清空待开演队列（headless 冒烟断言后调用）.</summary>
    public void ClearPendingLaunches() => _pendingLaunches.Clear();

    /// <summary>
    /// 跃迁负向探针：以当前活体状态构造 era→era+1 事务请求并执行——终局关
    /// 未占领/奇观未建即 <see cref="EraTransitionRejectedException"/>（返回拒
    /// 绝消息；前置已满足则事务照常提交并返回 null）。
    /// </summary>
    public string? ProbeTransitionRefusal(int fromEra)
    {
        try
        {
            _ = new EraTransitionService().Execute(BuildTransitionRequest(fromEra, cityFacts: null));
            return null;
        }
        catch (EraTransitionRejectedException refused)
        {
            return refused.Message;
        }
    }

    // ---------------------------------------------------------------- 内部

    private CampaignLevelRow LevelRow(HandcraftedLevel level)
    {
        VictorySettlementRecord? record = VictorySettlementStore.LatestFor(_victoryArchive, level.Id);
        if (record is not null)
        {
            int stars = record.Decided.VerdictRows.Count(row => row.Passed);
            return new CampaignLevelRow(level.Id, level.NameZh, CampaignLevelState.Cleared, stars);
        }

        bool unlocked = EraLevelAssembly.EraIndexOf(level) <= _campaign.EraIndex
            && LowerSlotsCleared(level)
            && (level.RequiresWonder is null || _ledger.IsWonderBuilt(level.RequiresWonder));
        return new CampaignLevelRow(
            level.Id, level.NameZh,
            unlocked ? CampaignLevelState.Playable : CampaignLevelState.Locked,
            Stars: 0);
    }

    private bool LowerSlotsCleared(HandcraftedLevel level) =>
        _sets[EraLevelAssembly.EraIndexOf(level)].Items
            .Where(other => other.Slot < level.Slot)
            .All(other => VictorySettlementStore.LatestFor(_victoryArchive, other.Id) is not null);

    private void HandleVictory(CampaignRun run)
    {
        _victoryHandled = true;
        HandcraftedLevel level = run.Level;
        int era = EraLevelAssembly.EraIndexOf(level);

        // —— 胜利结算：判分/开箱/占领（引擎既有链）+ 先定后演决定入档（★面）——
        var counters = new HostRunCounters(
            StationsLost: run.Session.Sim.Buildings.Count(building => building.Hp <= 0),
            DefenseIntegrityMin: _integrityMin,
            TowersSoldCount: 0, // 战役命令流无出售动词（V10 同口径）
            AmmoStarvedSeconds: _starvedSeconds,
            WaveEndEfficiencyMin: _waveEndEfficiencyMin,
            KeyBuildingsDamagedCount: run.Session.Sim.Buildings.Count(
                building => building.Hp < building.MaxHpResolved),
            LogisticsDeadlockCount: 0, // 命令流无死锁注入动词
            SystemBuildPlanCount: 0,   // 军师局另接（战役命令流零军令——V10 关闭局同口径）
            ScrollTriggerCount: 0,
            FirstWaveLeaks: FirstWaveLeaks(run));
        LevelRunFacts facts = CampaignProgression.ObserveFacts(run.Session, level.BudgetSeconds, 0, counters);
        CampaignSettlement settlement = _progression.SettleVictory(run.Session, level.Id, level.Threat, facts);
        DecidedVictoryPerformance decided = VictorySettlement.Decide(
            settlement, _chestTable, new EndlessHostContent.SplitMix64Rng(level.Map.Seed), _wheelSegments,
            run.Session.Victory!.Seed, Persona.Advisor, TrustBadge.Trustworthy, TrustBadge.Trustworthy);
        _victoryArchive = VictorySettlementStore.Upsert(
            _victoryArchive, new VictorySettlementRecord { LevelId = level.Id, Decided = decided, Viewed = false });
        VictorySettlementStore.Save(_files, _victoryArchive);
        _pendingLaunches.Add(new CinematicLaunch(Cinematics.CinematicDirector.VictoryScriptId, decided));

        run.Session.CompleteLevel();
        run.Session.ClearExitSnapshot();

        // —— 章末推进：E1~E5 final → 奇观位+跃迁事务+下一时代；e6-final → 终章 ——
        if (IsEraFinal(level))
        {
            if (era < CampaignState.MaxHostedEraIndex)
            {
                ExecuteTransition(era, run);
            }
            else
            {
                bool systemEnabled = PanelSettingsStore.Load(_files).SystemEnabled;
                _pendingLaunches.Add(new CinematicLaunch(FinalePlayback.ScriptIdFor(systemEnabled), Settlement: null));
                int totalCleared = _victoryArchive.Records.Count;
                _completionSummary =
                    "六时代全清——纪元之门合拢。《我在游戏觉醒系统》通关总结：\n" +
                    $"已通关 {totalCleared} 关（六时代集齐）；制作组：EraTD 项目组（全免费开源，代码 MIT/资产 CC BY-NC）。\n" +
                    "感谢游玩——零的真相已向你完整揭示。";
                SaveCampaignDocument();
            }
        }
        else
        {
            // 终章通关之门：e6 非终局四关全清 → 纪元之门奇观位落账（e6-final 解锁）
            if (era == CampaignState.MaxHostedEraIndex
                && _sets[era].Items.Where(other => other.Slot < MaxSlotOf(era))
                    .All(other => VictorySettlementStore.LatestFor(_victoryArchive, other.Id) is not null))
            {
                string? gate = _sets[era].Items.FirstOrDefault(other => other.RequiresWonder is not null)?.RequiresWonder;
                if (gate is not null && !_ledger.IsWonderBuilt(gate))
                {
                    _ledger.MarkWonderBuilt(gate);
                }
            }

            SaveCampaignDocument();
        }
    }

    private void ExecuteTransition(int fromEra, CampaignRun finalRun)
    {
        WonderSpec wonder = _wonders.Single(row => row.EraIndex == fromEra);
        _ledger.MarkWonderBuilt(wonder.Id); // 章末奇观位（V10 同律：跃迁硬前置）

        var facts = finalRun.Session.Sim.Buildings
            .Where(building => building.Kind != SimBuildingKind.Warehouse)
            .Select(building => new RelicCityBuildingFact(
                building.Id.Value, building.ContentId, building.Tier, Orientation: 0, building.Cell))
            .ToList();
        EraTransitionResult result = new EraTransitionService().Execute(BuildTransitionRequest(fromEra, facts));
        _campaign.AdvanceEra(result);       // 时代位+新池（仓库+1）+新大陆网格
        _meta = result.WrittenMeta;         // 事务的 campaign 域一次写=权威文档
        foreach (string scriptId in EraFinaleLaunchIds(fromEra, _contentRoot))
        {
            _pendingLaunches.Add(new CinematicLaunch(scriptId, Settlement: null));
        }
    }

    /// <summary>
    /// 章末胜利拍的演出发射序（T-160c 断点二①，裁定依据）：①收束演出
    /// （<c>cinematics/eX-finale.json</c> 存在即发射——F-107 时代收束段 3/4/5，
    /// 数据注释明言「由战役宿主在本时代终局关胜利拍显式发射」；E1/E2 无收束
    /// 段文件=数据现实，43 号 §5 段表 1/2 归 T-089 结算演出域）；②跃迁过场
    /// （<c>transition-eX-eY</c>，F-003 事务点）。顺序=§12 叙事序：本关结算
    /// （victory.json 先定后演，已先入队）→时代落幕收束→新时代跃迁开场；两场
    /// 独立可单跳（eX-finale 注释）。文件存在性驱动=数据面说了算（后续补
    /// e1/e2-finale 段即自动入序，零代码改动）。
    /// <para>
    /// 注：跃迁 id=<c>transition-e{X}-e{Y}</c>（两端皆 e 前缀——脚本 id 命名
    /// 约定）。T-160 原实现误拼 <c>e{X}-{Y}</c>（缺第二端 e 前缀）——入队 id
    /// 与装载脚本 id 不符，窗口车道 Launch 即抛；本任务收口（冒烟发射序探针
    /// 执法）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> EraFinaleLaunchIds(int fromEra, string contentRoot)
    {
        var ids = new List<string>();
        if (System.IO.File.Exists(
                System.IO.Path.Combine(contentRoot, "cinematics", $"e{fromEra}-finale.json")))
        {
            ids.Add($"cinematic:e{fromEra}-finale");
        }

        ids.Add($"cinematic:transition-e{fromEra}-e{fromEra + 1}");
        return ids;
    }

    private EraTransitionRequest BuildTransitionRequest(int fromEra, IReadOnlyList<RelicCityBuildingFact>? cityFacts)
    {
        var anchor = new CellCoord(24, 24); // ci-allow: R7 跃迁快照锚（遗迹城核心带中心——FinalLevelTransitionReachability 城市生成带同族结构位，非平衡数值）
        var facts = cityFacts ?? new List<RelicCityBuildingFact>();
        IReadOnlySet<ResourceKey> newEraResources = CrossEraResourcePolicy.Parse(
            ReadContent($"era{fromEra + 1}", "era.json")).NewEraResources;
        return new EraTransitionRequest(
            WorldSeed,
            FromEraIndex: fromEra,
            _campaign.Pool,
            _tech,
            _ledger,
            facts,
            anchor,
            newEraResources,
            new SimClock(),
            new EndlessHostContent.SplitMix64Rng(WorldSeed + fromEra),
            _port,
            _meta,
            _mapping,
            _transition,
            _wonders);
    }

    /// <summary>
    /// campaign 域合并写：保留既有文档的载体族（legacy ledger 载荷/遗迹城快照/
    /// 遗迹登记/已解锁科技），只刷新时代位/池/ledger 编码——<see cref=
    /// "CampaignState.SavePoolTo"/> 的薄面（清空载体族）不适用于跃迁后的战役，
    /// 故宿主侧走合并口径（REV-20261018-T160 §存档边界）。
    /// </summary>
    private void SaveCampaignDocument()
    {
        var pool = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ResourceKey resource in _campaign.Pool.Resources)
        {
            double amount = _campaign.Pool.AmountOf(resource);
            long whole = (long)Math.Floor(amount);
            if (whole != amount)
            {
                throw new ArgumentException(
                    $"Campaign pool amount of {resource.Value} is {amount} — banking must deposit whole items (K-7 long).");
            }

            pool[resource.Value] = whole;
        }

        var layouts = _meta.LevelLayoutSnapshots
            .Where(layout => layout.LevelId != LegacyLedger.LedgerSlotId)
            .Select(layout => new LevelLayoutSnapshotDto(layout.LevelId, layout.LayoutPayload))
            .ToList();
        layouts.Add(new LevelLayoutSnapshotDto(LegacyLedger.LedgerSlotId, _ledger.Encode()));

        var unlocked = new List<string>(_meta.UnlockedTechs);
        unlocked.AddRange(_tech.UnlockedIds.Where(id => !unlocked.Contains(id)));

        _meta = new CampaignMetaDto(
            _campaign.WorldSeed, _campaign.EraIndex, pool, unlocked,
            new List<string>(_meta.RelicRegistry), layouts);
        _port.WriteCampaign(_meta, SaveVersion.Current);
    }

    private void RestoreLedger(ICampaignMeta meta)
    {
        ILevelLayoutSnapshot? slot = meta.LevelLayoutSnapshots
            .FirstOrDefault(layout => layout.LevelId == LegacyLedger.LedgerSlotId);
        if (slot is { LayoutPayload.Length: > 0 })
        {
            _ledger.DecodeInto(slot.LayoutPayload);
        }
    }

    private void SampleCounters(CampaignRun run)
    {
        long tick = run.Session.Sim.CurrentTick;
        if (tick - _lastSampleTick < CounterSampleTicks)
        {
            return;
        }

        double seconds = (tick - _lastSampleTick) / (double)Simulation.TicksPerSecond;
        _lastSampleTick = tick;

        HudSnapshot hud = HudMetrics.Compute(
            run.Session.Sim.CurrentWave,
            0.0,
            run.Session.Sim.Buildings
                .Where(building => building.Kind == SimBuildingKind.Tower)
                .Select(building => (building.Hp, building.MaxHpResolved))
                .ToList());
        _integrityMin = Math.Min(_integrityMin, hud.DefenseIntegrity);

        var efficiencies = new List<double>();
        double gap = 0.0;
        foreach (SimTower tower in run.Session.Sim.Towers)
        {
            if (run.Session.Sim.TryGetFirePacer(tower.Id, out SuppliedWeaponFire pacer))
            {
                efficiencies.Add(pacer.Efficiency);
                gap += Math.Max(0.0, 1.0 - pacer.Efficiency);
            }
        }

        _starvedSeconds += gap * seconds;
        _waveEndEfficiencyMin = Math.Min(
            _waveEndEfficiencyMin, efficiencies.Count == 0 ? 1.0 : efficiencies.Average());
    }

    private int FirstWaveLeaks(CampaignRun run)
    {
        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(run.Level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * run.Level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return run.Session.Sim.Units.Count(
            unit => unit.ArrivedAtTarget && unit.SpawnTick < graceTicks + intervalTicks);
    }

    private LevelSessionConfig BuildSessionConfig(HandcraftedLevel level) => new(
        EraLevelAssembly.BuildConfig(level, _contentRoot),
        $"campaign.{level.Id}",
        level.Threat,
        level.Map.Seed,
        StateHashInterval: 1,
        Telemetry: _telemetry is null
            ? null
            : new LevelTelemetryScope(
                TelemetryLevelKind.Campaign, level.Id, EraLevelAssembly.EraIndexOf(level), level.Map.Seed,
                _telemetrySource));

    private CampaignProgression NewProgression() => new(
        _campaign.Pool,
        _tech,
        new TripleObjectiveJudge(
            ObjectivesData.Parse(ReadContent("campaign", "objectives.json")),
            new EndlessHostContent.SplitMix64Rng(WorldSeed)),
        new ChestRewards(
            ChestRewardsData.Parse(ReadContent("campaign", "chests.json")),
            new EndlessHostContent.SplitMix64Rng(WorldSeed)));

    private HandcraftedLevel LevelById(string levelId)
    {
        foreach (HandcraftedLevelSet set in _sets.Values)
        {
            if (set.TryGet(levelId) is { } level)
            {
                return level;
            }
        }

        throw new InvalidOperationException($"未找到战役关卡行 {levelId}");
    }

    private bool IsEraFinal(HandcraftedLevel level) => level.Slot == MaxSlotOf(EraLevelAssembly.EraIndexOf(level));

    private int MaxSlotOf(int era) => _sets[era].Items.Max(level => level.Slot);

    private string EraNameOf(int era)
    {
        using var doc = JsonDocument.Parse(ReadContent($"era{era}", "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("name_zh").GetString() ?? $"era{era}";
    }

    /// <summary>结算转盘段数（RL7：从 victory.json 的 wheel_spin 步骤读，禁宿主硬编码）.</summary>
    private int WheelSegmentsOfVictoryScript()
    {
        CinematicScript script = CinematicScript.Parse(
            ReadContent("cinematics", "victory.json"), "cinematics/victory.json");
        foreach (CinematicStep step in script.Steps)
        {
            if (step is CinematicStep.WheelSpin wheel)
            {
                return wheel.Segments;
            }
        }

        throw new InvalidOperationException("cinematics/victory.json 缺 wheel_spin 步骤（结算转盘段数来源）");
    }

    private static CampaignMetaDto EmptyMeta() => new(
        WorldSeed, CampaignState.E1EraIndex, new Dictionary<string, long>(),
        new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>());

    private string ReadContent(string subDir, string file)
    {
        string path = System.IO.Path.Combine(_contentRoot, subDir, file);
        return System.IO.File.Exists(path)
            ? System.IO.File.ReadAllText(path)
            : throw new InvalidOperationException($"内容文件缺失：content/{subDir}/{file}（战役宿主须在仓库内运行）");
    }

    /// <summary>Repo walk-up to game/content（RL9：相对探测，无绝对路径；冒烟的收束发射序探针复用）.</summary>
    internal static string FindContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, "game", "content");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content（战役宿主须在仓库内运行）");
    }
}

// T-160b 战役军师面板宿主（SystemPanel.IAdvisorBoardHost 的战役实现——纯 C#，
// 无 Godot 节点引用）：五段管线 over 活动关 LevelSession 的 sim；HUD 三指标
// （含第三指标的构成/预览行）从会话 sim 活体计算（SimHost.BuildHudSnapshot/
// BuildWaveCompositionView 同律镜像——读面独立成对）；确认的 BuildPlan 等命
// 令经引擎观察缝（Simulation.CommandEnqueued→LevelSession._issued）记入会话
// 命令流，波界/退出快照的重放键覆盖军令来源（无损续玩不变量）。

/// <summary>
/// The campaign-level advisor board host: the panel-facing seam of ONE live
/// campaign level. Disposable — owns the run's JSONL war-report writer.
/// </summary>
internal sealed class CampaignAdvisorBoard : SystemPanel.IAdvisorBoardHost, IDisposable
{
    private readonly CampaignRun _run;
    private readonly JsonlJournalWriter _journal;

    internal CampaignAdvisorBoard(
        CampaignRun run,
        AgentPipeline pipeline,
        IFileStore store,
        JsonlJournalWriter journal,
        string journalPath)
    {
        _run = run;
        Pipeline = pipeline;
        Store = store;
        _journal = journal;
        JournalPath = journalPath;
    }

    /// <summary>The run this board fronts.</summary>
    public CampaignRun Run => _run;

    /// <summary>War-report JSONL path (K-8 evidence of the advisor lane).</summary>
    public string JournalPath { get; }

    /// <inheritdoc />
    public AgentPipeline Pipeline { get; }

    /// <inheritdoc />
    public IFileStore Store { get; }

    /// <inheritdoc />
    public HudSnapshot BuildHudSnapshot()
    {
        double production = 0d;
        foreach (EraCore.Economy.Producer producer in _run.Session.Sim.Producers)
        {
            production += producer.OutputItemsPerSecond * producer.Efficiency;
        }

        var towerHealths = new List<(int Hp, int MaxHp)>();
        foreach (SimTower tower in _run.Session.Sim.Towers)
        {
            if (_run.Session.Sim.TryGetBuilding(tower.Id, out SimBuilding building))
            {
                towerHealths.Add((building.Hp, tower.Spec.Hp));
            }
        }

        return HudMetrics.Compute(_run.Session.Sim.CurrentWave, production, towerHealths);
    }

    /// <summary>Chinese display name of an enemy content id (falls back to the id).</summary>
    public string NameOfEnemy(string id) =>
        _run.Config.LevelConfig.Enemies.TryGet(id, out EnemySpec? spec) ? spec.NameZh : id;

    /// <summary>Releases the war-report writer (file handle).</summary>
    public void Dispose() => _journal.Dispose();
}

/// <summary>Quiet K-1 sink（纯 C# 宿主侧）：注册表诊断静默丢弃——K-8 JSONL 战报才是记录（RL10）.</summary>
internal sealed class QuietLogSink : ILogSink
{
    public void Write(LogLevel level, long tick, string message)
    {
    }
}
