using System.Text.Json;
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
    public void BeginLevel(string levelId)
    {
        HandcraftedLevel level = LevelById(levelId);
        var config = BuildSessionConfig(level);
        bool resumed = LevelSession.TryResume(
            config, new EndlessHostContent.SplitMix64Rng(level.Map.Seed), _port, _campaign.Pool,
            out LevelSession session, _telemetry);
        if (!resumed)
        {
            session.Machine.StartLevel();
            foreach (SimCommand command in EraLevelAssembly.BuildCommands(level, _contentRoot))
            {
                session.Enqueue(command);
            }
        }
        else
        {
            session.ResumePlaying(); // #exit 冻结在 Paused——回到 InLevel 续走
        }

        _current = new CampaignRun(session, level, resumed);
        _victoryHandled = false;
        _integrityMin = 1.0;
        _starvedSeconds = 0.0;
        _waveEndEfficiencyMin = 1.0;
        _lastSampleTick = session.Sim.CurrentTick;
    }

    /// <summary>窗口帧泵：1x 帧律一帧一 tick；胜利落地时返回 true（UI 刷新面）.</summary>
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

        return false;
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
        _pendingLaunches.Add(new CinematicLaunch(
            $"cinematic:transition-e{fromEra}-{fromEra + 1}", Settlement: null));
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

    /// <summary>Repo walk-up to game/content（RL9：相对探测，无绝对路径）.</summary>
    private static string FindContentRoot()
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
