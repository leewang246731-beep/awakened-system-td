using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;

namespace EraNet;

// T-139 (F-161) 客户端镜像会话（48 §2.1）：客户端跑镜像 Simulation（同 seed、
// 同命令流、逐 tick 同哈希）。v1 简化（48 §2.3 英雄预测登记不预留缝）：
// 本地输入<strong>不做预测</strong>——上行只是「建议」，镜像 sim 的唯一状态源
// 是宿主广播流；本地输入以回显列表供 UI 展示。对账=既有回放哈希时间线的
// 网络搬运：客户端按宿主节奏自采样，宿主摘要到达同 tick 后逐行比对
// （偏差 → Divergences 记录，处置=重同步/退出由调用方决策，48 §3.3 选项
// 登记口径）。迟到加入/断线重连/偏差重同步走同一条 RestoreFrom：检查点
// 重放（K-7 体系网络化）。

/// <summary>One detected hash divergence between the mirror and the host digest.</summary>
/// <param name="Tick">Tick the mismatch was detected at.</param>
/// <param name="LocalHash">The mirror's locally recomputed hash at that tick.</param>
/// <param name="HostHash">The host digest's hash at that tick.</param>
public sealed record NetDivergence(long Tick, long LocalHash, long HostHash);

/// <summary>
/// The client mirror session (T-139, F-161): consumes the host broadcast
/// stream verbatim into its own <see cref="SimCommandQueue"/> (arrival order
/// = enqueue order ⇒ identical per-queue seq ⇒ identical hashes), submits
/// local commands as upstream suggestions (no prediction — v1 renders the
/// host truth plus a local input echo), reconciles periodic digests against
/// its own recomputed hash timeline, and restores from a
/// <see cref="NetSessionSnapshot"/> on join/reconnect/resync by deterministic
/// replay.
/// </summary>
public sealed class NetGameClient
{
    private readonly ICommandTransport _commands;
    private readonly ISnapshotChannel _snapshots;
    private readonly IAdvisorOrderChannel _advisor;
    private readonly Queue<SimHashSample> _pendingHostSamples = new();
    private readonly Dictionary<long, long> _ownTimeline = new();
    private readonly List<NetDivergence> _divergences = new();
    private readonly List<AdvisorOrderReply> _replies = new();
    private readonly List<SimCommand> _submittedEcho = new();
    private Simulation _mirror;
    private int _snapshotEveryTicks;
    private long _requestSeq;

    /// <param name="mirror">Fresh mirror simulation (same config + seed as the host level).</param>
    /// <param name="commands">Client end of the command lane.</param>
    /// <param name="snapshots">Client end of the digest channel.</param>
    /// <param name="advisor">Client end of the advisor order channel.</param>
    /// <param name="snapshotEveryTicks">Own sampling cadence; must match the host's.</param>
    public NetGameClient(
        Simulation mirror,
        ICommandTransport commands,
        ISnapshotChannel snapshots,
        IAdvisorOrderChannel advisor,
        int snapshotEveryTicks = NetTiming.SnapshotCadenceTicks)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(advisor);
        if (snapshotEveryTicks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshotEveryTicks), snapshotEveryTicks, "Digest cadence must be at least 1 tick.");
        }

        _mirror = mirror;
        _commands = commands;
        _snapshots = snapshots;
        _advisor = advisor;
        _snapshotEveryTicks = snapshotEveryTicks;
    }

    /// <summary>The mirror simulation (replaced wholesale by <see cref="RestoreFrom"/>).</summary>
    public Simulation Mirror => _mirror;

    /// <summary>Detected divergences in arrival order (empty while in sync).</summary>
    public IReadOnlyList<NetDivergence> Divergences => _divergences;

    /// <summary>Advisor replies received so far, in push order.</summary>
    public IReadOnlyList<AdvisorOrderReply> AdvisorReplies => _replies;

    /// <summary>Local input echo (v1: suggestions are NOT predicted — display only).</summary>
    public IReadOnlyList<SimCommand> SubmittedEcho => _submittedEcho;

    /// <summary>Digests matched so far (tick + hash both equal).</summary>
    public long MatchedDigests { get; private set; }

    /// <summary>
    /// Host digests skipped as uncomparable: the host published at a tick the
    /// client never self-sampled at (externally thinned cadence). Counted
    /// honestly — never silently folded into matches (反造假纪律).
    /// </summary>
    public long SkippedDigests { get; private set; }

    /// <summary>
    /// Upstreams one local command as a suggestion (v1: no prediction — the
    /// mirror's only state source is the broadcast stream; the command echoes
    /// locally for UI display and takes effect when the host-stamped
    /// broadcast comes back).
    /// </summary>
    public bool SubmitCommand(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.Submit(command))
        {
            return false;
        }

        _submittedEcho.Add(command);
        return true;
    }

    /// <summary>Upstreams one advisor order text (approval happens host-side).</summary>
    public bool SubmitAdvisorOrder(string orderText)
    {
        if (string.IsNullOrWhiteSpace(orderText))
        {
            return false;
        }

        return _advisor.Submit(new AdvisorOrderRequest(
            ++_requestSeq, AdvisorOrderRequestKind.Order, orderText, PendingId: -1));
    }

    /// <summary>Upstreams a confirmation of a pending plan card.</summary>
    public bool ConfirmPending(long pendingId) => _advisor.Submit(new AdvisorOrderRequest(
        ++_requestSeq, AdvisorOrderRequestKind.Confirm, OrderText: null, PendingId: pendingId));

    /// <summary>Upstreams a refusal of a pending plan card.</summary>
    public bool RefusePending(long pendingId) => _advisor.Submit(new AdvisorOrderRequest(
        ++_requestSeq, AdvisorOrderRequestKind.Refuse, OrderText: null, PendingId: pendingId));

    /// <summary>
    /// One client-loop iteration BEFORE stepping: drains delivered broadcasts
    /// into the mirror queue verbatim (arrival order = enqueue order), queues
    /// host digests for deferred comparison, and collects advisor replies.
    /// </summary>
    public void PumpIncoming()
    {
        foreach (SimCommand command in _commands.DrainIncoming())
        {
            _mirror.Enqueue(command);
        }

        foreach (SimHashSample sample in _snapshots.DrainIncoming())
        {
            _pendingHostSamples.Enqueue(sample);
        }

        _replies.AddRange(_advisor.DrainReplies());
    }

    /// <summary>
    /// Steps the mirror exactly one tick, self-samples at the digest cadence,
    /// then reconciles every host sample whose tick the mirror has reached.
    /// </summary>
    public void StepMirror()
    {
        _mirror.Step();
        if (_mirror.CurrentTick % _snapshotEveryTicks == 0)
        {
            _ownTimeline[_mirror.CurrentTick] = _mirror.ComputeStateHash();
        }

        ReconcileDueSamples();
    }

    /// <summary>
    /// Late-join / reconnect / resync (48 §3.2): rebuilds the mirror from the
    /// host session snapshot by deterministic replay — fresh simulation from
    /// the same content factory, journal enqueued in order, replayed to the
    /// snapshot tick. Clears the desync STATE (pending samples, own timeline);
    /// the divergence LEDGER survives as the audit trail.
    /// </summary>
    /// <param name="snapshot">The host-built session snapshot.</param>
    /// <param name="simFactory">Rebuilds a fresh simulation for the snapshot seed (same content).</param>
    public void RestoreFrom(NetSessionSnapshot snapshot, Func<long, Simulation> simFactory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(simFactory);
        if (snapshot.Tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Tick, "Snapshot tick must be non-negative.");
        }

        Simulation rebuilt = simFactory(snapshot.Seed);
        foreach (SimCommandLog entry in snapshot.Commands)
        {
            rebuilt.Enqueue(new SimCommand(
                entry.IssuedAtTick,
                Enum.Parse<SimCommandKind>(entry.Kind, ignoreCase: false),
                entry.PayloadJson));
        }

        if (snapshot.Tick > rebuilt.CurrentTick)
        {
            rebuilt.Run(snapshot.Tick - rebuilt.CurrentTick);
        }

        _mirror = rebuilt;
        _snapshotEveryTicks = Math.Max(1, snapshot.SnapshotEveryTicks);
        _ownTimeline.Clear();
        _pendingHostSamples.Clear();
        // Divergences 刻意保留：偏差台账是审计面（何时偏、偏在哪 tick）——
        // 重同步清除的是「失步状态」，不是「失步历史」。
        _ownTimeline[_mirror.CurrentTick] = _mirror.ComputeStateHash();
    }

    private void ReconcileDueSamples()
    {
        while (_pendingHostSamples.Count > 0 && _pendingHostSamples.Peek().Tick <= _mirror.CurrentTick)
        {
            SimHashSample sample = _pendingHostSamples.Dequeue();
            if (!_ownTimeline.TryGetValue(sample.Tick, out long localHash))
            {
                SkippedDigests++;
                continue;
            }

            if (localHash == sample.StateHash)
            {
                MatchedDigests++;
            }
            else
            {
                _divergences.Add(new NetDivergence(sample.Tick, localHash, sample.StateHash));
            }
        }
    }
}
