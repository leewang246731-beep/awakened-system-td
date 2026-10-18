using EraCore.Balance;
using EraCore.Checkpoint;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Units;

namespace EraCore.CampaignLoop;

/// <summary>
/// Configuration of one level session inside the campaign loop (T-059,
/// F-001): the level's <see cref="SimulationConfig"/> plus the campaign
/// coordinates it hangs off.
/// </summary>
/// <param name="LevelConfig">E1 level content the simulation is built from.</param>
/// <param name="LevelSlotId">K-7 level-domain slot id of this level (checkpoint/rolling/exit slots hang off it).</param>
/// <param name="Threat">Clamped node threat t — drives 胜波 via <see cref="Campaign.WinWaves"/> and the checkpoint wave.</param>
/// <param name="LevelSeed">Run seed (replay key #1).</param>
/// <param name="StateHashInterval">
/// Session-level hash-sampling cadence (spike1-report §7 pit#2, T-059 DEBT
/// ③): the <see cref="LevelSession.HashLedger"/> records one
/// <c>ComputeStateHash()</c> sample every N ticks. DEFAULT 1 = every tick —
/// the goldens (sim/checkpoint) and the lossless-resume assertions depend
/// on per-tick sampling, so the default MUST stay 1; a value &gt; 1 only
/// thins this observability ledger (the per-tick hash inside
/// <see cref="Simulation.Step"/> is Sim-layer and unchanged). Whether any
/// cadence above 1 is safe for the replay invariants is the T-067
/// evaluation — this config is the mechanism only, no behaviour change by
/// default.
/// </param>
/// <param name="Telemetry">
/// T-155 时长遥测声明（缺省 null=不记）：本关在六类覆盖面的身份列
/// （战役/程序关 kind、levelId、时代序）。仅当宿主同时向
/// <see cref="LevelSession"/> 注入 <see cref="LevelDurationTelemetry"/>
/// 服务时才生效——纯观测面，不进任何游戏逻辑/哈希。
/// </param>
public sealed record LevelSessionConfig(
    SimulationConfig LevelConfig,
    string LevelSlotId,
    double Threat,
    long LevelSeed,
    int StateHashInterval = 1,
    LevelTelemetryScope? Telemetry = null);

/// <summary>
/// Victory settlement of one level (T-059; v3.2 §3 占领→入库): the level's
/// leftover stock, floored to whole items, offered to the campaign pool —
/// the pool accepts up to its headroom (入池只计上限内), the refused part is
/// surfaced, and the fractional remainder stays in the ended level. The
/// 系统任务三重目标结算 (档位/转盘/宝箱) is the T-0xx follow-up task and is
/// deliberately not part of this record.
/// </summary>
/// <param name="WinWaves">胜波 the run achieved.</param>
/// <param name="Tick">Tick the victory fired on.</param>
/// <param name="Seed">Run seed (replay key #1).</param>
/// <param name="Loot">Per-resource banking lines of the settlement.</param>
public sealed record VictoryOutcome(
    int WinWaves,
    long Tick,
    long Seed,
    IReadOnlyList<ResourceGrant> Loot);

/// <summary>
/// One full level session — the C-12 campaign-loop orchestrator of a single
/// level (T-059, F-001): assembles the E1 <see cref="Simulation"/>, drives
/// the wave loop under a <see cref="GameStateMachine"/>, observes wave
/// boundaries into a <see cref="WaveCheckpointStore"/> (波界存档), detects
/// the victory predicate (胜波数 <see cref="Campaign.WinWaves"/>(t) 达成且场清)
/// and banks the settlement into the campaign pool, exposes the T-057
/// failure-tier seam on defeat, and saves/restores the mid-level exit
/// through the K-7 level domain so a 中途暂停→退出→重进 is LOSSLESS: the
/// exit snapshot is the replay key (seed + full command stream to the exit
/// tick) and the deterministic replay IS the restore (T-040 invariant).
/// <para>
/// Unit-layer collision semantics declared for the whole loop (spike2-report
/// §5 pit#1, T-059 DEBT ①, REV-20261014-T059): enemies and porters never
/// block each other on the logic layer — 同格自由叠加通过 is the DESIGN
/// semantic the loop's "互不阻塞" acceptance rests on, not an omission.
/// Squeeze/queueing VISUALS are presentation-layer debt registered against
/// the C-21 presentation work (T-07x 表现层任务行); local avoidance rules are
/// a later gameplay task. The logic loop above therefore never waits on,
/// nor simulates, unit-vs-unit collision.
/// </para>
/// <para>
/// Carrier route invalidation (spike2-report §5 pit#5, T-059 DEBT ②): the
/// formal <see cref="Logistics.RouteInvalidatedEvent"/> fires at every
/// Loading→Outbound / Unloading→Inbound departure inside
/// <see cref="Simulation.Step"/>; a host wanting wall-aware rerouting
/// subscribes through its own carrier handles and calls
/// <see cref="Logistics.Carrier.Reroute"/> — the seam this loop leaves open
/// (the E1 thin level places no mid-run walls, so no subscription is
/// wired here by default).
/// </para>
/// </summary>
public sealed class LevelSession
{
    /// <summary>Slot suffix of the mid-level exit snapshot (K-7 free-form level slot id, T-041 extension point).</summary>
    public const string ExitSlotSuffix = "#exit";

    private readonly LevelSessionConfig _config;
    private readonly ISavePort _port;
    private readonly IRng _rng;
    private readonly ResourceStock? _campaignPool;
    private readonly WaveCheckpointStore _checkpoints;
    private readonly GameStateMachine _machine = new();
    private readonly Simulation _sim;
    private readonly List<SimCommand> _issued = new();
    private readonly List<(long Tick, long Hash)> _hashLedger = new();
    private readonly long _winWaveLastEntryTick;
    private readonly LevelDurationTracker? _duration;
    private VictoryOutcome? _victory;
    private FailureScenarioKind? _failureTier;
    private NormalLevelFailureOutcome? _failureOutcome;

    /// <summary>
    /// Fresh session at <see cref="GameFlowState.Menu"/>: builds the seeded
    /// simulation (the rng is reseeded with the level seed inside).
    /// </summary>
    /// <exception cref="ArgumentNullException">config, rng or port is null.</exception>
    /// <exception cref="ArgumentException">
    /// Blank slot id, negative threat (via <see cref="Campaign.WinWaves"/>),
    /// StateHashInterval below 1, or invalid level config (via
    /// <see cref="Simulation"/> construction).
    /// </exception>
    public LevelSession(
        LevelSessionConfig config,
        IRng rng,
        ISavePort port,
        ResourceStock? campaignPool = null,
        LevelDurationTelemetry? durationTelemetry = null)
        : this(config, rng, port, campaignPool, new Simulation(BuildValidatedConfig(config), rng, config.LevelSeed), durationTelemetry, resumed: false)
    {
    }

    private LevelSession(
        LevelSessionConfig config,
        IRng rng,
        ISavePort port,
        ResourceStock? campaignPool,
        Simulation restoredSimulation,
        LevelDurationTelemetry? durationTelemetry,
        bool resumed)
    {
        Validate(config);
        _config = config;
        _rng = rng;
        _port = port;
        _campaignPool = campaignPool;
        _sim = restoredSimulation;
        WinWaves = Campaign.WinWaves(config.Threat);
        _checkpoints = new WaveCheckpointStore(port, config.LevelSlotId, config.Threat);
        _winWaveLastEntryTick = ComputeWinWaveLastEntryTick(config, WinWaves);
        // T-155 时长遥测（挂点=会话建立/重进，非 sim 热路径）：tick 基线取
        // 当前 sim tick——重进局=退出刻，纯游玩口径天然只计本坐步进。
        _duration = durationTelemetry is not null && config.Telemetry is not null
            ? durationTelemetry.Begin(config.Telemetry, restoredSimulation.CurrentTick, resumed)
            : null;
    }

    /// <summary>
    /// Restores a session from the mid-level exit snapshot
    /// (<c>&lt;slot&gt;#exit</c>): replays seed + command stream to the exit
    /// tick on a fresh simulation and lands the machine EXACTLY where the
    /// exit left it (<see cref="GameFlowState.Paused"/> with the same
    /// transition history). False (and a fresh Menu session) when no exit
    /// snapshot exists — the caller then starts the level normally.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SaveCorruptException">The stored exit payload is damaged.</exception>
    public static bool TryResume(
        LevelSessionConfig config,
        IRng rng,
        ISavePort port,
        ResourceStock? campaignPool,
        out LevelSession session,
        LevelDurationTelemetry? durationTelemetry = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(port);

        if (!TryReadExitSnapshot(port, ComposeExitSlotId(config), out RestoredWaveSnapshot snapshot))
        {
            // T-160 缺口修复（REV-20261018-T160 §引擎最小扩②）：无 #exit 快照的
            // 新开局分支此前丢弃 durationTelemetry——经 TryResume 起局的战役/程序
            // 关真人局遥测因此整面漏记（T-155「宿主注入即记」契约违例）。补传；
            // 行为面=纯观测列追加，零 sim/哈希影响。
            session = new LevelSession(config, rng, port, campaignPool, durationTelemetry);
            return false;
        }

        Simulation restored = snapshot.Replay(config.LevelConfig, rng);
        var resumed = new LevelSession(
            config, rng, port, campaignPool, restored, durationTelemetry, resumed: true);
        // Rebuild the machine history the exit froze: Menu → InLevel
        // (start) → Paused (the pause that preceded SaveAndExit).
        resumed.Machine.StartLevel();
        resumed.Machine.Pause();
        session = resumed;
        return true;
    }

    /// <summary>胜波数 of this level: <see cref="Campaign.WinWaves"/>(threat) = 10+⌊t×10⌋.</summary>
    public int WinWaves { get; }

    /// <summary>The live E1 simulation of this session.</summary>
    public Simulation Sim => _sim;

    /// <summary>The flow machine (single legality authority, F-001).</summary>
    public GameStateMachine Machine => _machine;

    /// <summary>Wave-boundary checkpoint store of this level (T-057 seam).</summary>
    public WaveCheckpointStore Checkpoints => _checkpoints;

    /// <summary>Physical slot id of the mid-level exit snapshot.</summary>
    public string ExitSlotId => ComposeExitSlotId(_config);

    /// <summary>Commands issued through this session, in issue order (replay key #2).</summary>
    public IReadOnlyList<SimCommand> IssuedCommands => _issued;

    /// <summary>
    /// Session-level hash samples (spike1 pit#2 mechanism): one
    /// <see cref="Simulation.ComputeStateHash"/> per
    /// <see cref="LevelSessionConfig.StateHashInterval"/> ticks — the
    /// lossless-resume assertions compare these against an uninterrupted
    /// twin run.
    /// </summary>
    public IReadOnlyList<(long Tick, long Hash)> HashLedger => _hashLedger;

    /// <summary>The victory settlement, once the level was won; null otherwise.</summary>
    public VictoryOutcome? Victory => _victory;

    /// <summary>The failure tier this session ended in; null while no defeat happened.</summary>
    public FailureScenarioKind? FailureTier => _failureTier;

    /// <summary>The normal-level failure outcome (T-057 executor result); null otherwise.</summary>
    public NormalLevelFailureOutcome? FailureOutcome => _failureOutcome;

    /// <summary>
    /// Enqueues a player command into the simulation (applied at its
    /// IssuedAtTick) and records it in the session stream — the recorded
    /// stream is what wave-boundary and exit snapshots persist.
    /// </summary>
    /// <exception cref="ArgumentNullException">command is null.</exception>
    /// <exception cref="InvalidOperationException">Machine not InLevel (commands only flow into a running level).</exception>
    public long Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_machine.State != GameFlowState.InLevel)
        {
            throw new InvalidOperationException(
                $"Commands can only be issued InLevel (state={_machine.State}).");
        }

        _issued.Add(command);
        return _sim.Enqueue(command);
    }

    /// <summary>
    /// Runs up to <paramref name="ticks"/> steps (stops early on the
    /// victory predicate). Every step: simulation pipeline, wave-boundary
    /// observation (auto checkpoint/rolling saves), hash-ledger sample,
    /// victory check.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">ticks negative.</exception>
    /// <exception cref="InvalidOperationException">Machine not InLevel.</exception>
    public void RunFor(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Tick count must be non-negative.");
        }

        if (_machine.State != GameFlowState.InLevel)
        {
            throw new InvalidOperationException($"Only a running level may step (state={_machine.State}).");
        }

        for (long i = 0; i < ticks && _machine.State == GameFlowState.InLevel; i++)
        {
            _sim.Step();
            _ = _checkpoints.ObserveBoundary(_sim, _issued);
            if (_sim.CurrentTick % _config.StateHashInterval == 0)
            {
                _hashLedger.Add((_sim.CurrentTick, _sim.ComputeStateHash()));
            }

            if (IsVictory())
            {
                OnVictory();
            }
        }
    }

    /// <summary>
    /// InLevel→Paused (delegates to the machine). T-155 口径注：暂停期 sim
    /// 不步进——遥测纯游玩 tick 主口径天然扣除暂停，墙钟参考口径则含暂停
    /// （双口径裁定见 <see cref="Telemetry.LevelDurationTelemetry"/> 文件头）。
    /// </summary>
    public void Pause() => _machine.Pause();

    /// <summary>Paused→InLevel (delegates to the machine).</summary>
    public void ResumePlaying() => _machine.Resume();

    /// <summary>
    /// 中途暂停→退出: writes the exit snapshot (replay key to the CURRENT
    /// tick — mid-wave included) into <c>&lt;slot&gt;#exit</c> and returns the
    /// machine to Menu. Must be called Paused (the machine's SaveAndExit
    /// edge enforces it). The campaign domain (pool) is saved separately by
    /// <see cref="CampaignState.SavePoolTo"/> — the two domains together are
    /// the complete exit state. T-155: an exit also closes the duration
    /// tracker as <see cref="TelemetryOutcome.Abandoned"/> (弃局=本坐诚实
    /// 终态；重进续关产生 Resumed=true 的新行，可拼接还原整关).
    /// </summary>
    /// <exception cref="InvalidOperationException">Machine not Paused.</exception>
    public void SaveAndExitToMenu()
    {
        _machine.SaveAndExit();
        _duration?.End(TelemetryOutcome.Abandoned, _sim.CurrentTick);
        var record = new WaveSnapshotRecord(
            _sim.Seed, _sim.CurrentWave, _sim.CurrentTick, CommandsUpTo(_issued, _sim.CurrentTick));
        _port.WriteLevel(
            ExitSlotId,
            // Wave is 1-based in the blob gate (0 = cleared tombstone): a
            // mid-grace exit (wave 0 installed) stores 1 so the snapshot
            // stays readable; the record above keeps the true wave.
            new LevelSaveBlobDto(Math.Max(1, _sim.CurrentWave), CheckpointPayload.Encode(record), Array.Empty<byte>()),
            SaveVersion.Current);
    }

    /// <summary>
    /// Clears the mid-level exit snapshot (tombstone write): called when a
    /// level ENDS (victory settlement banked or failure tiers applied), so a
    /// later re-entry of the same slot starts fresh instead of resuming a
    /// finished run.
    /// </summary>
    public void ClearExitSnapshot()
    {
        _port.WriteLevel(
            ExitSlotId,
            new LevelSaveBlobDto(WaveCheckpointStore.ClearedWaveMarker, Array.Empty<byte>(), Array.Empty<byte>()),
            SaveVersion.Current);
    }

    /// <summary>
    /// Declares the level lost and runs the T-057 normal-level failure tier
    /// (checkpoint restore + wreckage repair bill; without a stored
    /// checkpoint the level restarts from tick 0). The machine lands in
    /// <see cref="GameFlowState.Defeat"/>; acknowledge it (back to Menu)
    /// once the host has applied the outcome.
    /// </summary>
    /// <exception cref="ArgumentNullException">never (rng held from construction).</exception>
    /// <exception cref="InvalidOperationException">Machine not InLevel.</exception>
    public NormalLevelFailureOutcome DeclareDefeat(bool firstDefeat)
    {
        if (_machine.State != GameFlowState.InLevel)
        {
            throw new InvalidOperationException($"Defeat can only be declared InLevel (state={_machine.State}).");
        }

        _machine.Lose();
        _duration?.End(TelemetryOutcome.Defeat, _sim.CurrentTick);
        _failureTier = FailureScenarioKind.NormalLevel;
        _failureOutcome = FailureTiers.FailNormalLevel(_checkpoints, _config.LevelConfig, _rng, _config.LevelSeed, firstDefeat);
        return _failureOutcome;
    }

    /// <summary>Defeat→Menu after the failure outcome was applied (delegates to the machine).</summary>
    public void AcknowledgeDefeat() => _machine.AcknowledgeDefeat();

    /// <summary>Victory→Menu after the settlement was banked (delegates to the machine).</summary>
    public void CompleteLevel() => _machine.CompleteLevel();

    /// <summary>
    /// Victory predicate (v3.2 §3 守波→占领): the spawner has installed the
    /// win wave, its LAST entry tick has passed (every enemy of the win wave
    /// is on the field — no mid-wave vacuous clear), at least one enemy ever
    /// spawned, and every one of them is dead (场清).
    /// </summary>
    private bool IsVictory() =>
        _sim.CurrentWave >= WinWaves
        && _sim.CurrentTick >= _winWaveLastEntryTick
        && _sim.Units.Count > 0
        && AllUnitsDead();

    private bool AllUnitsDead()
    {
        foreach (Unit unit in _sim.Units)
        {
            if (unit.Alive)
            {
                return false;
            }
        }

        return true;
    }

    private void OnVictory()
    {
        _machine.Win();
        _duration?.End(TelemetryOutcome.Victory, _sim.CurrentTick);
        var loot = new List<ResourceGrant>();
        if (_campaignPool is not null)
        {
            foreach (ResourceKey resource in _sim.Stock.Resources)
            {
                double levelled = _sim.Stock.AmountOf(resource);
                double granted = Math.Floor(levelled);
                if (granted <= 0)
                {
                    continue;
                }

                double accepted = _campaignPool.Deposit(resource, granted);
                loot.Add(new ResourceGrant(resource, granted, accepted, granted - accepted));
            }
        }

        _victory = new VictoryOutcome(WinWaves, _sim.CurrentTick, _sim.Seed, loot);
    }

    private static string ComposeExitSlotId(LevelSessionConfig config) => config.LevelSlotId + ExitSlotSuffix;

    private static void Validate(LevelSessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.LevelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(config));
        }

        if (config.StateHashInterval < 1)
        {
            throw new ArgumentException(
                $"StateHashInterval must be at least 1 (got {config.StateHashInterval}).", nameof(config));
        }

        _ = Campaign.WinWaves(config.Threat); // negative-threat guard (throws ArgumentException)
    }

    private static SimulationConfig BuildValidatedConfig(LevelSessionConfig config)
    {
        Validate(config);
        return config.LevelConfig;
    }

    /// <summary>
    /// Last entry tick of the win wave, from a PLANNING-ONLY preview of the
    /// level's own wave schedule: <see cref="WaveSpawner.PlanWave"/> and
    /// <see cref="WaveSpawner.WaveStartTick"/> draw no randomness, so a
    /// preview spawner over the same config computes the live schedule's
    /// numbers exactly — the victory predicate gets "win wave fully
    /// entered" without any Simulation/Spawner state exposure.
    /// </summary>
    private static long ComputeWinWaveLastEntryTick(LevelSessionConfig config, int winWaves)
    {
        var preview = new WaveSpawner(config.LevelConfig.Enemies, config.LevelConfig.Waves, new PlanningRng(), new SimClock());
        long start = preview.WaveStartTick(winWaves);
        return preview.PlanWave(winWaves, start).LastEntryTick;
    }

    private static bool TryReadExitSnapshot(ISavePort port, string slotId, out RestoredWaveSnapshot snapshot)
    {
        ILevelSaveBlob blob;
        try
        {
            blob = port.ReadLevel(slotId, SaveVersion.Current);
        }
        catch (InvalidOperationException)
        {
            // No database file or no row for the slot: nothing to resume.
            snapshot = null!;
            return false;
        }

        if (blob.WaveCheckpoint <= WaveCheckpointStore.ClearedWaveMarker || blob.SimSnapshotPayload.Length == 0)
        {
            snapshot = null!;
            return false;
        }

        WaveSnapshotRecord basis = CheckpointPayload.DecodeSnapshot(blob.SimSnapshotPayload);
        WaveTailRecord? tail = blob.CommandTailPayload.Length == 0
            ? null
            : CheckpointPayload.DecodeTail(blob.CommandTailPayload);
        var commands = new List<SimCommandLog>(basis.Commands);
        if (tail is not null)
        {
            commands.AddRange(tail.Commands);
        }

        snapshot = new RestoredWaveSnapshot(
            tail?.BoundaryWave ?? basis.Wave,
            tail?.BoundaryTick ?? basis.Tick,
            basis.Seed,
            commands);
        return true;
    }

    private static IReadOnlyList<SimCommandLog> CommandsUpTo(IReadOnlyList<SimCommand> commands, long tick)
    {
        var recorded = new List<SimCommandLog>();
        foreach (SimCommand command in commands)
        {
            if (command.IssuedAtTick <= tick)
            {
                recorded.Add(new SimCommandLog(command.IssuedAtTick, command.Kind.ToString(), command.PayloadJson));
            }
        }

        return recorded;
    }

    /// <summary>
    /// Rng stand-in for the planning-only wave preview: schedule computation
    /// must never draw randomness, so any draw attempt throws instead of
    /// silently consuming a stream (defensive proof that PlanWave is
    /// rng-free).
    /// </summary>
    private sealed class PlanningRng : IRng
    {
        public int Next(int minValue, int maxValue) => throw new InvalidOperationException(
            "Wave planning must not draw randomness.");

        public double NextDouble() => throw new InvalidOperationException(
            "Wave planning must not draw randomness.");

        public void Reseed(long seed)
        {
            // Reseed is part of the IRng contract; the preview never seeds.
        }
    }
}
