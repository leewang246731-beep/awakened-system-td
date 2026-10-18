using EraCore.Agent.Pipeline;
using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;

namespace EraNet;

// T-139 (F-161) 宿主权威会话（48 §2 listen server）：宿主进程持有唯一权威
// Simulation、唯一 SimCommandQueue 排序权与（可选）唯一 AgentPipeline。
// 一切命令——宿主本地输入（listen 语义：宿主玩家与其他客户端同权同路）、
// 客户端上行建议（盖章=宿主接收 tick+1，48 §2.2）、审批通过的军师动作
// （管线经同一队列落子）——全部经过 Simulation.Enqueue 咽喉；队列 tap
// （SimCommandQueue.Enqueued，本任务加的宿主观测缝）按权威入队序镜像进
// 广播期刊。客户端镜像 sim 消费同一广播流 ⇒ (IssuedAtTick, seq) 两端一致
// ⇒ 同 seed 同命令流同哈希（回放不变式的网络搬运，48 §1.2）。

/// <summary>
/// The listen-server session orchestrator (T-139, F-161): stamps every
/// submission with the host receive tick + 1, sequences everything through
/// the ONE authority <see cref="SimCommandQueue"/>, broadcasts the journal
/// in exact enqueue order, and publishes one <see cref="SimHashSample"/>
/// digest every <see cref="NetTiming.SnapshotCadenceTicks"/> ticks
/// (08 acceptance: 快照 200ms = 6 tick @ 30 Hz).
/// <para>
/// Advisor orders ride <see cref="IAdvisorOrderChannel"/>: order TEXT goes
/// upstream, the host runs the full K-5 five-stage pipeline (approval gate
/// and system-point ledger settle host-side only — 48 §4.2), and the reply
/// digest goes downstream. Approved actions enter the sim through the
/// pipeline's own <c>SimCommand</c> lane and reach clients like any other
/// broadcast command — a forged client-side action JSON has no consumption
/// path.
/// </para>
/// </summary>
public sealed class NetGameHost
{
    private readonly Simulation _sim;
    private readonly NetHostWire _wire;
    private readonly ISnapshotChannel _snapshots;
    private readonly IAdvisorOrderChannel _advisor;
    private readonly AgentPipeline? _pipeline;
    private readonly int _snapshotEveryTicks;
    private readonly Queue<SimCommand> _pendingBroadcast = new();
    private readonly List<SimCommandLog> _broadcastJournal = new();
    private long _lastDigestTick;

    /// <param name="authoritySim">The one authority simulation (host-owned).</param>
    /// <param name="commands">Host end of the command lane.</param>
    /// <param name="snapshots">Host end of the digest channel.</param>
    /// <param name="advisor">Host end of the advisor order channel.</param>
    /// <param name="pipeline">
    /// The host-side agent pipeline (server approval). Null = this room runs
    /// without an advisor (order requests are refused honestly, never faked).
    /// </param>
    /// <param name="snapshotEveryTicks">Digest cadence; default 200 ms worth of ticks.</param>
    public NetGameHost(
        Simulation authoritySim,
        NetHostWire commands,
        ISnapshotChannel snapshots,
        IAdvisorOrderChannel advisor,
        AgentPipeline? pipeline = null,
        int snapshotEveryTicks = NetTiming.SnapshotCadenceTicks)
    {
        ArgumentNullException.ThrowIfNull(authoritySim);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(advisor);
        if (snapshotEveryTicks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshotEveryTicks), snapshotEveryTicks, "Digest cadence must be at least 1 tick.");
        }

        _sim = authoritySim;
        _wire = commands;
        _snapshots = snapshots;
        _advisor = advisor;
        _pipeline = pipeline;
        _snapshotEveryTicks = snapshotEveryTicks;
        _sim.Commands.Enqueued += OnAuthorityCommandEnqueued;
    }

    /// <summary>The authority simulation (host-owned; clients never see it directly).</summary>
    public Simulation Sim => _sim;

    /// <summary>Number of commands in the broadcast journal so far.</summary>
    public int BroadcastCount => _broadcastJournal.Count;

    /// <summary>Last tick a digest was published at (0 = none yet).</summary>
    public long LastDigestTick => _lastDigestTick;

    /// <summary>
    /// Host player input (listen semantics, 48 §2.1: 宿主「听着」自己的服务
    /// 器——宿主本地命令与客户端命令走同一盖章/入队/广播环路).
    /// </summary>
    public void SubmitLocal(SimCommand command) => StampAndEnqueue(command);

    /// <summary>
    /// One host-loop iteration: drain client submissions (stamp → authority
    /// queue → broadcast), then drain advisor requests through the pipeline
    /// (execution enqueues through the same throat; the tap captures), then
    /// flush the broadcast and publish any due digest.
    /// </summary>
    public void PumpIncoming()
    {
        foreach (SimCommand submission in _wire.DrainSubmissions())
        {
            StampAndEnqueue(submission);
        }

        FlushBroadcast();

        foreach (AdvisorOrderRequest request in _advisor.DrainRequests())
        {
            HandleAdvisorRequest(request);
        }

        FlushBroadcast();
        PublishDueDigest();
    }

    /// <summary>
    /// Steps the authority simulation exactly one tick and publishes the due
    /// digest afterwards. Externally driven sims (e.g. a LevelSession that
    /// steps the same Simulation itself) must NOT call this — they call
    /// <see cref="PublishDueDigest"/> from their own loop instead.
    /// </summary>
    public void AdvanceTick()
    {
        _sim.Step();
        PublishDueDigest();
    }

    /// <summary>
    /// Publishes one digest when the current tick sits exactly on the cadence
    /// grid and is fresh (best-effort: an externally stepped multi-tick run
    /// may skip grid ticks entirely — 48 §3.1 losing edge samples is cheaper
    /// than buffering them; the grid alignment guarantees every published
    /// tick is one the client also self-samples at, so matches are real
    /// comparisons, never accidental hits).
    /// </summary>
    public void PublishDueDigest()
    {
        if (_sim.CurrentTick % _snapshotEveryTicks == 0 && _sim.CurrentTick > _lastDigestTick)
        {
            _snapshots.Publish(new SimHashSample(_sim.CurrentTick, _sim.ComputeStateHash()));
            _lastDigestTick = _sim.CurrentTick;
        }
    }

    /// <summary>
    /// Late-join / reconnect / resync payload (48 §3.2): seed + the FULL
    /// broadcast journal + current tick — the K-7 「恢复=重放」 medium over
    /// the wire. The receiving client rebuilds a fresh simulation from the
    /// same content, replays the journal to the tick and resumes live
    /// consumption.
    /// </summary>
    public NetSessionSnapshot BuildSessionSnapshot() => new(
        _sim.Seed,
        _sim.CurrentTick,
        _snapshotEveryTicks,
        _broadcastJournal.ToList());

    private void StampAndEnqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // 盖章（48 §2.2）：客户端的 IssuedAtTick 只是建议——宿主改写为接收
        // tick+1，使全房间 (IssuedAtTick, seq) 只由宿主到达序决定。
        var stamped = command with { IssuedAtTick = _sim.CurrentTick + 1 };
        _sim.Enqueue(stamped);
    }

    private void OnAuthorityCommandEnqueued(long seq, SimCommand command) => _pendingBroadcast.Enqueue(command);

    private void FlushBroadcast()
    {
        while (_pendingBroadcast.Count > 0)
        {
            SimCommand command = _pendingBroadcast.Dequeue();
            _broadcastJournal.Add(new SimCommandLog(command.IssuedAtTick, command.Kind.ToString(), command.PayloadJson));
            _wire.Broadcast(command);
        }
    }

    private void HandleAdvisorRequest(AdvisorOrderRequest request)
    {
        if (_pipeline is null)
        {
            _advisor.Push(new AdvisorOrderReply(
                request.RequestId, 0, request.OrderText ?? string.Empty,
                PipelineOutcomeKind.Refused, "agent.pipeline.absent", -1,
                "本房间未装配军师管线（宿主无 K-5 审批面）。"));
            return;
        }

        OrderOutcome outcome = request.Kind switch
        {
            AdvisorOrderRequestKind.Order => _pipeline.Handle(request.OrderText!),
            AdvisorOrderRequestKind.Confirm => _pipeline.Confirm(request.PendingId),
            AdvisorOrderRequestKind.Refuse => _pipeline.Refuse(request.PendingId),
            _ => throw new InvalidOperationException($"Unknown advisor request kind {request.Kind}."),
        };

        _advisor.Push(ToReply(request.RequestId, outcome));
    }

    private static AdvisorOrderReply ToReply(long requestId, OrderOutcome outcome) => new(
        requestId,
        outcome.Order,
        outcome.OrderText,
        outcome.Kind,
        outcome.ClarifyingKey,
        outcome.PendingId,
        outcome.Report?.Summary ?? string.Empty);
}
