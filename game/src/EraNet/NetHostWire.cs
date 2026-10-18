using EraCore.Contracts.Sim;
using EraCore.Net;

namespace EraNet;

/// <summary>
/// T-139 (F-161) 宿主端命令线面：预留端口 <see cref="ICommandTransport"/>
/// 冻结为「客户端视图」（status+上行+下行，NetReservationContractTests 钉死），
/// 而宿主端线需要的是「收客户端提交 + 广播盖章命令」两件——本接口是 T-139
/// 评审登记的自有缝（48 号 §7.1 冻结纪律：预留面不加宽，实现域自建面须随
/// 任务评审归档，见 REV-20261018-T139）。与 <see cref="ISnapshotChannel"/> 的
/// 分工：命令走本面（双向），摘要走快照口（宿主 Publish/客户端 Drain）。
/// <para>
/// 单实现纪律：内存回环（MemoryNetEndpoint）与表现层 ENet 适配器
/// （EraTD/scripts/net/EnetNetLink）各实现一份，语义完全一致——
/// <see cref="DrainSubmissions"/> 返回到达序（= 宿主入队盖章序），
/// <see cref="Broadcast"/> 按可靠有序通道下发（广播序 = 权威入队序）。
/// </para>
/// </summary>
public interface NetHostWire
{
    /// <summary>Current link status of the host end (reported, never pushed).</summary>
    NetLinkStatus Status { get; }

    /// <summary>
    /// Drains every command submitted by clients since the previous call, in
    /// arrival order; empty when nothing arrived.
    /// </summary>
    IReadOnlyList<SimCommand> DrainSubmissions();

    /// <summary>
    /// Broadcasts one (host-stamped) command to every connected client on the
    /// reliable ordered lane; a broadcast while offline is dropped silently
    /// (the reconnect path re-delivers the full journal via session snapshot).
    /// </summary>
    void Broadcast(SimCommand command);
}
