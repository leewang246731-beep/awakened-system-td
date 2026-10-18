using System.Text.Json.Serialization;
using EraCore.Net;

namespace EraNet;

/// <summary>
/// T-139 (F-161) 军师订单通道：军令文本/方案卡确认的上行与审批结论的下行
/// （48 号 §4.2「客户端军师=本地 L1 语法+建议上行，执行权在宿主」+ §4.3
/// 「确认也是一次上行」）。T-131 未为此预留第三接口（48 §8 明示留给 T-139
/// 裁决承载通道）——本接口即该裁决的落地：形状镜像预留两口（status+上行
/// +下行），成员集随本任务评审冻结（REV-20261018-T139）。
/// <para>
/// 载荷纪律：上行只有<strong>文本</strong>与 pendingId——动作 JSON 永不上行
/// （伪造的客户端动作 JSON 没有任何消费路径，48 §4.2 BYOK 分界）；下行只有
/// 审批结论摘要（OrderOutcome 的可显示投影），sim 变更一律走命令广播流。
/// </para>
/// </summary>
public interface IAdvisorOrderChannel
{
    /// <summary>Current link status (reported, never pushed).</summary>
    NetLinkStatus Status { get; }

    /// <summary>
    /// Upstream (client): queues one order/confirm/refuse request toward the
    /// host. Returns false when the link is not online or the transport
    /// refused (nothing queued, no exception).
    /// </summary>
    bool Submit(AdvisorOrderRequest request);

    /// <summary>
    /// Host side: drains every request received since the previous call, in
    /// arrival order; empty when nothing arrived.
    /// </summary>
    IReadOnlyList<AdvisorOrderRequest> DrainRequests();

    /// <summary>
    /// Downstream (host): pushes one approval reply toward the requesting
    /// client; a push while offline is dropped silently (best-effort lane —
    /// the reply digest is observability, the sim truth rides commands).
    /// </summary>
    void Push(AdvisorOrderReply reply);

    /// <summary>
    /// Client side: drains every reply received since the previous call, in
    /// push order; empty when nothing arrived.
    /// </summary>
    IReadOnlyList<AdvisorOrderReply> DrainReplies();
}

/// <summary>Kind of one advisor-order upstream request.</summary>
public enum AdvisorOrderRequestKind
{
    /// <summary>Order text toward the host K-5 pipeline (AgentPipeline.Handle).</summary>
    Order = 0,

    /// <summary>Player confirmed a pending plan card (host AgentPipeline.Confirm).</summary>
    Confirm = 1,

    /// <summary>Player refused a pending plan card (host AgentPipeline.Refuse).</summary>
    Refuse = 2,
}

/// <summary>
/// One upstream advisor request: order text, or a confirm/refuse of a pending
/// plan card. RequestId is assigned by the client (monotonic per client) and
/// echoed by the host reply so the UI can correlate.
/// </summary>
/// <param name="RequestId">Client-assigned correlation id.</param>
/// <param name="Kind">Order / Confirm / Refuse.</param>
/// <param name="OrderText">The order text (Order only; null otherwise).</param>
/// <param name="PendingId">The pending plan id (Confirm/Refuse only; -1 otherwise).</param>
public sealed record AdvisorOrderRequest(
    long RequestId,
    AdvisorOrderRequestKind Kind,
    string? OrderText,
    long PendingId);

/// <summary>
/// Host-side digest of one handled advisor request (48 §4.2：审批门/扣点/执行
/// 全在宿主；客户端收到的是<strong>结论</strong>——命令真相走广播流，本载荷
/// 只承载 UI 可显示投影，绝不承载动作 JSON)。
/// </summary>
/// <param name="RequestId">Echo of the request's correlation id (-1 host-initiated).</param>
/// <param name="Order">Host-side order sequence number (AgentPipeline).</param>
/// <param name="OrderText">The order text the pipeline handled.</param>
/// <param name="Kind">Pipeline outcome kind.</param>
/// <param name="ClarifyingKey">Clarifying key on clarification/refusal; null otherwise.</param>
/// <param name="PendingId">Pending id when <see cref="Kind"/> is AwaitingApproval; -1 otherwise.</param>
/// <param name="ReportDigest">One-line report digest (empty when no report).</param>
public sealed record AdvisorOrderReply(
    long RequestId,
    int Order,
    string OrderText,
    EraCore.Agent.Pipeline.PipelineOutcomeKind Kind,
    string? ClarifyingKey,
    long PendingId,
    string ReportDigest);
