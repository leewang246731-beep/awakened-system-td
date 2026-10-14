using System.Text;
using EraCore.Agent.Autopilot;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;

namespace EraCore.Endless;

/// <summary>
/// The settled outcome of one endless run (T-080 死亡终局): the SP-7c
/// score breakdown, the board the run was routed to (三榜分记) and the
/// channel id — the autopilot board carries the T-081 reserved channel and
/// additionally lands in the injected <see cref="IAutopilotScoreSink"/>.
/// </summary>
/// <param name="Board">Board the run was routed to (SP-7c 榜别判定).</param>
/// <param name="ChannelId">Channel of the board (<see cref="EndlessBoardChannels.Of"/>).</param>
/// <param name="WavesSurvived">Completed waves at death (存活波数).</param>
/// <param name="EliteKills">Elite kills of the whole run.</param>
/// <param name="BossKills">Boss kills of the whole run.</param>
/// <param name="AffixBonusPoints">词缀加权分 (T-080 [NEW-原型校准]).</param>
/// <param name="Score">Total score (SP-7c + 词缀加权).</param>
/// <param name="ActionsTotal">Actions (manual + autopilot) recorded over the run.</param>
/// <param name="AutopilotActions">Actions issued by the autopilot among them.</param>
/// <param name="AutopilotShare">Autopilot action share in [0,1] (0 when no actions).</param>
/// <param name="Record">The stamped leaderboard row actually stored.</param>
/// <param name="Persisted">True when the record reached the campaign domain (port injected).</param>
public sealed record EndlessSettlement(
    EndlessBoardKind Board,
    string ChannelId,
    long WavesSurvived,
    long EliteKills,
    long BossKills,
    long AffixBonusPoints,
    long Score,
    long ActionsTotal,
    long AutopilotActions,
    double AutopilotShare,
    EndlessScoreRecord Record,
    bool Persisted);

/// <summary>
/// One endless run (T-080, F-006; SP-7): perpetual wave generation
/// (<see cref="BeginWave"/> keeps planning waves forever — there is no
/// victory wave), the ONLY terminal is death (<see cref="Defeat"/> — 死亡
/// 终局), and settling routes the score onto exactly one of the three
/// boards by the SP-7c share rule, persists it through the K-7 campaign
/// domain (落账) and — for the full-autopilot board — hands the
/// <see cref="AutopilotScoreEntry"/> to the T-081 sink.
/// Determinism: all randomness flows through the injected
/// <see cref="IRng"/> with a fixed draw order, and
/// <see cref="ComputeStateHash"/> digests the whole generated history —
/// same seed + same action stream ⇒ same hash at every point of the run.
/// </summary>
public sealed class EndlessRun
{
    /// <summary>
    /// 全托管榜门槛（SP-7c「≥90%」）的百分基数 — the share comparison runs
    /// on integer cross-multiplication, never on double rounding.
    /// </summary>
    public const int SharePercentBase = 100; // ci-allow: R7 T-080 SP-7c 榜别判定的百分基数（口径换算，非平衡数值）

    /// <summary>系统执行动作占比 ≥90% → 全托管榜（SP-7c 榜别判定）.</summary>
    public const int FullAutopilotSharePercent = 90; // ci-allow: R7 T-080 11-spec-tables SP-7c「≥90%→全托管榜」（冻结表行）

    private readonly EndlessRunConfig _config;
    private readonly IRng _rng;
    private readonly ISavePort? _port;
    private readonly ICampaignMeta? _campaignMeta;
    private readonly IAutopilotScoreSink? _scoreSink;
    private readonly List<EndlessWavePlan> _history = new();

    private EndlessWavePlan? _active;

    private int _lastWave;

    /// <summary>
    /// Wires the run. <paramref name="port"/>, <paramref name="campaignMeta"/> and
    /// <paramref name="scoreSink"/> are optional: with a port the settle
    /// path persists (落账) into the K-7 campaign domain; with a sink the
    /// full-autopilot board additionally reports to T-081. Null deps keep
    /// the run pure (tests, host-managed settle).
    /// </summary>
    /// <exception cref="ArgumentNullException">config or rng is null.</exception>
    public EndlessRun(
        EndlessRunConfig config,
        IRng rng,
        ISavePort? port = null,
        ICampaignMeta? campaignMeta = null,
        IAutopilotScoreSink? scoreSink = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);
        _config = config;
        _rng = rng;
        _port = port;
        _campaignMeta = campaignMeta;
        _scoreSink = scoreSink;
    }

    /// <summary>Run configuration (frozen at construction).</summary>
    public EndlessRunConfig Config => _config;

    /// <summary>
    /// Last wave BEGUN (0 before the first <see cref="BeginWave"/>); stays at
    /// its value through completion and death — the next wave is always
    /// <c>CurrentWave + 1</c>, never a replay.
    /// </summary>
    public int CurrentWave => _lastWave;

    /// <summary>Completed waves (the 存活波数 of the eventual settlement).</summary>
    public long WavesSurvived { get; private set; }

    /// <summary>Elite kills of the whole run.</summary>
    public long EliteKills { get; private set; }

    /// <summary>Boss kills of the whole run.</summary>
    public long BossKills { get; private set; }

    /// <summary>Actions recorded so far (manual + autopilot).</summary>
    public long ActionsTotal { get; private set; }

    /// <summary>Actions issued by the autopilot among them.</summary>
    public long AutopilotActions { get; private set; }

    /// <summary>True once <see cref="Defeat"/> settled the run (terminal).</summary>
    public bool IsOver { get; private set; }

    /// <summary>True while a wave is in progress (between Begin/Complete).</summary>
    public bool WaveActive => _active is not null;

    /// <summary>
    /// Plans the next wave (永续生成: any number of cycles is legal — counts
    /// clamp at <see cref="EndlessWaveFormula.MaxCountClamp"/>, the rng
    /// keeps streaming). Requires the previous wave to be completed; a wave
    /// in progress or a settled run is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">A wave is active or the run is over.</exception>
    public EndlessWavePlan BeginWave()
    {
        ThrowIfOver();
        if (_active is not null)
        {
            throw new InvalidOperationException($"Wave {_active.Wave} is still in progress — complete it first.");
        }

        EndlessWavePlan plan = EndlessRules.PlanWave(_lastWave + 1, _config, _rng);
        _active = plan;
        _lastWave = plan.Wave;
        _history.Add(plan);
        return plan;
    }

    /// <summary>
    /// Closes the active wave and banks the kill tallies. Kills are
    /// clamped to what the wave actually spawned (a tally can never exceed
    /// the plan's line counts).
    /// </summary>
    /// <exception cref="InvalidOperationException">No wave is active or the run is over.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tally is negative.</exception>
    public void CompleteWave(long eliteKills = 0, long bossKills = 0)
    {
        ThrowIfOver();
        if (_active is null)
        {
            throw new InvalidOperationException("No wave is in progress.");
        }

        if (eliteKills < 0 || bossKills < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eliteKills), "Kill tallies are never negative.");
        }

        WavesSurvived++;
        EliteKills += Math.Min(eliteKills, _active.EliteCount);
        BossKills += Math.Min(bossKills, _active.BossCount);
        _active = null;
    }

    /// <summary>
    /// Records one action of the run for the SP-7c 榜别判定 share (占比 by
    /// JSONL 战报统计 in the spec — the prototype counts here).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">pointsSpent is negative.</exception>
    /// <exception cref="InvalidOperationException">The run is over.</exception>
    public void RecordAction(bool byAutopilot, long pointsSpentByAutopilot = 0)
    {
        ThrowIfOver();
        if (pointsSpentByAutopilot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointsSpentByAutopilot), "Points spent are never negative.");
        }

        ActionsTotal++;
        if (byAutopilot)
        {
            AutopilotActions++;
            AutopilotPoints += pointsSpentByAutopilot;
        }
    }

    /// <summary>System points spent by the autopilot (audit figure for the T-081 entry).</summary>
    public long AutopilotPoints { get; private set; }

    /// <summary>
    /// The run's autopilot share in [0,1] (0 when no action was recorded).
    /// Integer-typed tallies — no float drift.
    /// </summary>
    public double AutopilotShare => ActionsTotal == 0 ? 0.0 : (double)AutopilotActions / ActionsTotal;

    /// <summary>
    /// Board the run would settle on (SP-7c 榜别判定): share 0% → 手动榜,
    /// 1%~89% → 系统辅助榜, ≥90% → 全托管榜. Integer cross-multiplication —
    /// the 90% boundary never rides on double rounding.
    /// </summary>
    public EndlessBoardKind Board => RouteBoard(AutopilotActions, ActionsTotal);

    /// <summary>
    /// Pure routing rule (matrix-testable): <c>total == 0</c> → Manual;
    /// <c>autopilot × 100 ≥ total × 90</c> → Autopilot; else Assisted.
    /// </summary>
    public static EndlessBoardKind RouteBoard(long autopilotActions, long actionsTotal)
    {
        if (autopilotActions < 0 || actionsTotal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actionsTotal), "Action tallies are never negative.");
        }

        if (actionsTotal == 0)
        {
            return EndlessBoardKind.Manual;
        }

        if (autopilotActions > actionsTotal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(autopilotActions), autopilotActions, "Autopilot actions cannot exceed total actions.");
        }

        return (autopilotActions * SharePercentBase) >= (actionsTotal * FullAutopilotSharePercent)
            ? EndlessBoardKind.Autopilot
            : autopilotActions == 0
                ? EndlessBoardKind.Manual
                : EndlessBoardKind.Assisted;
    }

    /// <summary>
    /// 死亡终局: settles the run (terminal). Computes the SP-7c + 词缀加权
    /// score of the COMPLETED waves, routes the board, persists the record
    /// through the campaign domain when a port was injected (落账 — a wave
    /// still in progress simply dies uncounted) and hands the T-081
    /// <see cref="AutopilotScoreEntry"/> to the sink for the autopilot
    /// board. Double settle is refused — a dead run never re-scores.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is already over.</exception>
    public EndlessSettlement Defeat()
    {
        ThrowIfOver();
        IsOver = true;
        _active = null;

        long affixBonus = EndlessRules.AffixBonusPoints((int)WavesSurvived, _config.Scores);
        long score = EndlessRules.Score((int)WavesSurvived, EliteKills, BossKills, _config.Scores);
        EndlessBoardKind board = Board;
        string channelId = EndlessBoardChannels.Of(board);
        var record = new EndlessScoreRecord(
            board,
            Arrival: 0,
            WavesSurvived,
            EliteKills,
            BossKills,
            affixBonus,
            score);

        bool persisted = false;
        if (_port is not null)
        {
            // 合并基优先级：注入的 campaignMeta ＞ port 里已有的战役文档 ＞ 最小壳。
            // （Load 的"无文档→空榜"只解决读路径；写路径必须拿真文档做基，否则
            // 会用壳文档覆盖已有的池/科技/遗迹/他槽数据。）
            ICampaignMeta? previous = _campaignMeta ?? TryReadCampaign(_port);
            EndlessBoards boards = previous is not null
                ? EndlessBoardStore.FromMeta(previous)
                : new EndlessBoards();
            EndlessScoreRecord stamped = EndlessBoardStore.Commit(boards, record, _port, previous);
            record = stamped;
            persisted = true;
        }

        if (board == EndlessBoardKind.Autopilot && _scoreSink is not null)
        {
            _scoreSink.Record(new AutopilotScoreEntry(
                channelId,
                Wave: WavesSurvived,
                ActionsIssued: AutopilotActions,
                PointsSpentByAutopilot: AutopilotPoints));
        }

        return new EndlessSettlement(
            board,
            channelId,
            WavesSurvived,
            EliteKills,
            BossKills,
            affixBonus,
            score,
            ActionsTotal,
            AutopilotActions,
            AutopilotShare,
            record,
            persisted);
    }

    /// <summary>
    /// FNV-1a hash over the run's whole generated history and tallies
    /// (determinism ledger): config digest, then every planned wave (counts,
    /// interval, modifiers, full roster in draw order), then the tallies
    /// and terminal flag. Same seed + same wave/action stream ⇒ identical
    /// hash at any point — the 100-wave determinism acceptance test.
    /// </summary>
    public long ComputeStateHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(_config.Seed);
        writer.Write(_config.StartEraIndex);
        writer.Write(_config.ThreatLevel);
        writer.Write(_config.WaveBaseCount);
        writer.Write(_config.WaveBegin);
        writer.Write(_config.WaveSpacing);
        writer.Write(_config.WaveScaling);
        writer.Write(_config.BossEveryWaves);
        WriteStringList(writer, _config.WonderIds);

        writer.Write(_history.Count);
        foreach (EndlessWavePlan plan in _history)
        {
            writer.Write(plan.Wave);
            writer.Write(plan.RegularCount);
            writer.Write(plan.EliteCount);
            writer.Write(plan.BossCount);
            writer.Write(plan.IntervalSeconds);
            writer.Write(plan.HpMultiplier);
            EndlessEnemyModifiers m = plan.Modifiers;
            writer.Write(m.Wave);
            writer.Write(m.Stacks);
            writer.Write(m.SpeedMultiplier);
            writer.Write(m.ArmorMultiplier);
            writer.Write(m.HpMultiplier);
            writer.Write(m.PhasePeriodTicks);
            writer.Write(m.PhaseInvulnerableTicks);
            writer.Write(m.SuicideRadiusMultiplier);
            writer.Write(m.SuicideDamageMultiplier);
            writer.Write(m.SuicideBaseRadiusCells);
            writer.Write(m.SuicideBaseDamage);
            writer.Write(m.ActiveKinds.Count);
            foreach (EndlessAffixKind kind in m.ActiveKinds)
            {
                writer.Write((int)kind);
            }

            writer.Write(plan.Roster.Count);
            foreach (EndlessSpawn spawn in plan.Roster)
            {
                writer.Write((int)spawn.Role);
                writer.Write(spawn.EraIndex);
                WriteString(writer, spawn.EnemyId);
            }
        }

        writer.Write(WavesSurvived);
        writer.Write(EliteKills);
        writer.Write(BossKills);
        writer.Write(ActionsTotal);
        writer.Write(AutopilotActions);
        writer.Write(AutopilotPoints);
        writer.Write(IsOver);
        writer.Flush();
        byte[] payload = stream.ToArray();

        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (byte b in payload)
            {
                hash ^= b;
                hash *= prime;
            }

            return unchecked((long)hash);
        }
    }

    private void ThrowIfOver()
    {
        if (IsOver)
        {
            throw new InvalidOperationException("The run already settled — 死亡终局 is terminal.");
        }
    }

    /// <summary>Reads the campaign document, null when the store has none yet.</summary>
    private static Contracts.Persistence.ICampaignMeta? TryReadCampaign(ISavePort port)
    {
        try
        {
            return port.ReadCampaign(Contracts.Persistence.SaveVersion.Current);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteStringList(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (string value in values)
        {
            WriteString(writer, value);
        }
    }
}
