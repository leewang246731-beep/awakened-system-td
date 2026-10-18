using System.Text;
using EraCore.Agent.Autopilot;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;

namespace EraCore.Endless;

/// <summary>
/// The settled outcome of one endless run (T-080 死亡终局 + T-118 整局锁档):
/// the SP-7c score breakdown, the board the run was routed to
/// (<see cref="EndlessParticipationMode">开局档</see> — 三榜分记互不混排的
/// 唯一路由口径) and the channel id — the autopilot board carries the T-081
/// reserved channel and additionally lands in the injected
/// <see cref="IAutopilotScoreSink"/>.
/// </summary>
/// <param name="Board">Board the run was routed to (T-118 开局档锁定；= <see cref="EndlessParticipation.BoardOf"/>(<paramref name="LockedParticipation"/>)).</param>
/// <param name="ChannelId">Channel of the board (<see cref="EndlessBoardChannels.Of"/>).</param>
/// <param name="WavesSurvived">Completed waves at death (存活波数).</param>
/// <param name="EliteKills">Elite kills of the whole run.</param>
/// <param name="BossKills">Boss kills of the whole run.</param>
/// <param name="AffixBonusPoints">词缀加权分 (T-080 [NEW-原型校准]).</param>
/// <param name="Score">Total score (SP-7c + 词缀加权).</param>
/// <param name="ActionsTotal">Actions (manual + autopilot) recorded over the run.</param>
/// <param name="AutopilotActions">Actions issued by the autopilot among them.</param>
/// <param name="AutopilotShare">Autopilot action share in [0,1] (0 when no actions).</param>
/// <param name="LockedParticipation">开局参与档（记档整局的口径——结算榜别=该档）.</param>
/// <param name="CurrentParticipation">死亡时的当前档（中途换档后的末值；≠ LockedMode 只说明换过档，不影响榜别）.</param>
/// <param name="SwitchCount">中途换档次数（审计）.</param>
/// <param name="ShareImpliedBoard">SP-7c 占比式判定的审计列（0%→手动/1~89%→辅助/≥90%→全托管）——T-118 裁定占比不参与路由，仅留痕口径漂移.</param>
/// <param name="AdvisorParticipated">军师参与位（T-081 口径：军师实际执行过 ≥1 动作）.</param>
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
    EndlessParticipationMode LockedParticipation,
    EndlessParticipationMode CurrentParticipation,
    int SwitchCount,
    EndlessBoardKind ShareImpliedBoard,
    bool AdvisorParticipated,
    EndlessScoreRecord Record,
    bool Persisted);

/// <summary>
/// One endless run (T-080, F-006; SP-7 + T-118 三榜分记): perpetual wave
/// generation (<see cref="BeginWave"/> keeps planning waves forever — there
/// is no victory wave), the ONLY terminal is death (<see cref="Defeat"/> —
/// 死亡终局), and settling routes the score onto exactly one of the three
/// boards by the LOCKED OPENING participation mode
/// (<see cref="EndlessRunConfig.Participation"/> — T-118 整局锁档：中途换档
/// [<see cref="SwitchParticipation"/>] 只改当前档，绝不换榜；SP-7c 占比降为
/// 审计列), persists it through the K-7 campaign domain (落账) and — for the
/// full-autopilot board — hands the <see cref="AutopilotScoreEntry"/> to the
/// T-081 sink.
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
    private readonly List<EndlessParticipationSwitch> _participationSwitches = new();

    private EndlessWavePlan? _active;
    private EndlessParticipationMode _currentParticipation;

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
        _currentParticipation = config.Participation;
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
    /// Board the run will settle on (T-118 整局锁档): <b>the LOCKED opening
    /// participation mode</b> — <see cref="EndlessParticipation.BoardOf"/>(
    /// <see cref="EndlessRunConfig.Participation"/>) — never re-judged from the
    /// share and never changed by <see cref="SwitchParticipation"/> (中途换档
    /// 不换榜). The SP-7c share rule survives as the audit column
    /// <see cref="EndlessSettlement.ShareImpliedBoard"/>.
    /// </summary>
    public EndlessBoardKind Board => EndlessParticipation.BoardOf(_config.Participation);

    /// <summary>开局参与档（随配置冻结的记档口径——整局不变）.</summary>
    public EndlessParticipationMode LockedParticipation => _config.Participation;

    /// <summary>
    /// 当前参与档（T-118 局面参与状态机的活档）：开局=开局档，
    /// <see cref="SwitchParticipation"/> 后随之变化——只影响 HUD/审计，
    /// 不影响 <see cref="Board"/>（记档整局）。
    /// </summary>
    public EndlessParticipationMode CurrentParticipation => _currentParticipation;

    /// <summary>中途换档审计行（按发生序；空=整局未换档）.</summary>
    public IReadOnlyList<EndlessParticipationSwitch> ParticipationSwitches => _participationSwitches;

    /// <summary>
    /// 局内参与状态实时查询快照（T-118 HUD 位）：开局锁档+当前档+动作审计+
    /// 换档计数，一次只读取值——表现层每帧可查，不参与任何路由。
    /// </summary>
    public EndlessParticipationSnapshot Participation => new(
        LockedParticipation,
        _currentParticipation,
        CurrentWave,
        ActionsTotal,
        AutopilotActions,
        AutopilotShare,
        _participationSwitches.Count,
        AutopilotActions > 0,
        IsOver);

    /// <summary>
    /// 中途换档（T-118——换档自由、记档不换）：把<b>当前档</b>切到
    /// <paramref name="mode"/> 并记一条审计行（波次号+迁移+机读原因）。
    /// 榜别（<see cref="Board"/>）与开局档（<see cref="LockedParticipation"/>）
    /// 永不因此变化。T-081「当波降级」对齐：接管降级=切到 Assisted（罚则
    /// 当波已生效不回退——那是 T-081 罚则侧语义；本方法只管档位与审计）。
    /// </summary>
    /// <returns>True 当发生了真实换档；同档调用是 no-op（不记审计行）返回 false.</returns>
    /// <exception cref="ArgumentException">mode is undefined or reason is null/blank.</exception>
    /// <exception cref="InvalidOperationException">The run is over.</exception>
    public bool SwitchParticipation(EndlessParticipationMode mode, string reason)
    {
        ThrowIfOver();
        if (!EndlessParticipation.IsDefined(mode))
        {
            throw new ArgumentException(
                $"Participation mode must be a defined three-board mode (got {mode}).", nameof(mode));
        }

        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (mode == _currentParticipation)
        {
            return false;
        }

        _participationSwitches.Add(new EndlessParticipationSwitch(
            CurrentWave, _currentParticipation, mode, reason));
        _currentParticipation = mode;
        return true;
    }

    /// <summary>
    /// SP-7c share rule as a pure mapping (matrix-testable): <c>total == 0</c>
    /// → Manual; <c>autopilot × 100 ≥ total × 90</c> → Autopilot; else
    /// Assisted. Since T-118 this is the AUDIT column
    /// (<see cref="EndlessSettlement.ShareImpliedBoard"/>) — the score's
    /// destination is the locked opening mode (<see cref="Board"/>), never
    /// this mapping.
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
    /// score of the COMPLETED waves, routes the board BY THE LOCKED OPENING
    /// MODE (T-118 整局锁档——中途换档不换榜), persists the record through the
    /// campaign domain when a port was injected (落账 — a wave still in
    /// progress simply dies uncounted) and hands the T-081
    /// <see cref="AutopilotScoreEntry"/> to the sink for the autopilot
    /// board. Double settle is refused — a dead run never re-scores.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is already over.</exception>
    public EndlessSettlement Defeat()
    {
        ThrowIfOver();
        IsOver = true;
        _active = null;

        long affixBonus = _config.AffixRolls is null
            ? EndlessRules.AffixBonusPoints((int)WavesSurvived, _config.Scores)
            : EndlessRules.AffixBonusPoints((int)WavesSurvived, _config.Seed, _config.Scores, _config.AffixRolls);
        long score = EndlessRules.Score(
            (int)WavesSurvived, EliteKills, BossKills, _config.Scores, _config.Seed, _config.AffixRolls);
        EndlessBoardKind board = Board;
        string channelId = EndlessBoardChannels.Of(board);
        EndlessBoardKind shareImplied = RouteBoard(AutopilotActions, ActionsTotal);
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
            LockedParticipation,
            _currentParticipation,
            _participationSwitches.Count,
            shareImplied,
            AdvisorParticipated: AutopilotActions > 0,
            record,
            persisted);
    }

    /// <summary>
    /// FNV-1a hash over the run's whole generated history and tallies
    /// (determinism ledger): config digest (incl. the locked participation
    /// mode), the participation switch log (T-118), every planned wave
    /// (counts, interval, modifiers, full roster in draw order), then the
    /// tallies, the CURRENT participation mode and the terminal flag. Same
    /// seed + same wave/action/switch stream ⇒ identical hash at any point
    /// — the 100-wave determinism acceptance test.
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
        writer.Write((int)_config.Participation);
        WriteBaseline(writer, _config.StartEraBaseline);
        WriteMixPolicy(writer, _config.MixPolicy);
        WriteRollPolicy(writer, _config.AffixRolls);
        writer.Write(_config.ProceduralMap); // T-140 程序图开关入身份哈希（只读消费）

        writer.Write(_participationSwitches.Count);
        foreach (EndlessParticipationSwitch participationSwitch in _participationSwitches)
        {
            writer.Write(participationSwitch.Wave);
            writer.Write((int)participationSwitch.From);
            writer.Write((int)participationSwitch.To);
            WriteString(writer, participationSwitch.Reason);
        }

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
        writer.Write((int)_currentParticipation);
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

    /// <summary>T-143 起始基准入账: era + 初始资源行 + 解锁面 (null = 0 行).</summary>
    private static void WriteBaseline(BinaryWriter writer, EndlessStartEraBaseline? baseline)
    {
        writer.Write(baseline is not null);
        if (baseline is null)
        {
            return;
        }

        writer.Write(baseline.EraIndex);
        writer.Write(baseline.InitialResources.Count);
        foreach (EndlessResourceStock row in baseline.InitialResources)
        {
            WriteString(writer, row.ResourceId);
            writer.Write(row.Amount);
        }

        WriteStringList(writer, baseline.UnlockedTowers);
    }

    /// <summary>
    /// T-143 混编档入账 (null = T-118 语义全池混编)；T-154 渐升参数随档入账
    /// (渐升档携带终值波与终值权重——不同渐升参数=不同运行身份，分账纪律)。
    /// </summary>
    private static void WriteMixPolicy(BinaryWriter writer, EndlessMixPolicy? mix)
    {
        writer.Write(mix is not null);
        if (mix is null)
        {
            return;
        }

        writer.Write(mix.MixFromWave);
        writer.Write(mix.StartEraWeight);
        writer.Write(mix.OtherEraWeight);
        writer.Write(mix.RampEndWave is not null);
        if (mix.RampEndWave is int rampEnd)
        {
            writer.Write(rampEnd);
            writer.Write(mix.EndStartEraWeight ?? mix.StartEraWeight);
            writer.Write(mix.EndOtherEraWeight ?? mix.OtherEraWeight);
        }
    }

    /// <summary>T-143 滚词缀档入账 (null = SP-7b 冻结激活表).</summary>
    private static void WriteRollPolicy(BinaryWriter writer, EndlessAffixRollPolicy? rolls)
    {
        writer.Write(rolls is not null);
        if (rolls is null)
        {
            return;
        }

        writer.Write(rolls.RollEveryWaves);
        writer.Write(rolls.RollStartWave);
        writer.Write(rolls.KindsPerRoll);
        writer.Write(rolls.AllowConsecutiveRepeat);
        writer.Write(rolls.RepeatIntensityGain);
    }
}
