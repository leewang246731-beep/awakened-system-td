// T2: pure contract — T-131 联机命令传输端口预留（F-161 草案；实现域=T-139）。

using EraCore.Contracts.Sim;

namespace EraCore.Net;

/// <summary>
/// Link status of one network end (T-131, F-161). The three states are the
/// shared vocabulary of every future transport: the state machine that
/// governs the legal transitions is <see cref="NetLinkStateMachine"/>; this
/// enum is the observable face a transport reports to its host.
/// </summary>
public enum NetLinkStatus
{
    /// <summary>No link, or the link dropped: command submission is refused.</summary>
    Offline = 0,

    /// <summary>Handshake in progress: the link is not yet usable.</summary>
    Connecting = 1,

    /// <summary>Link live: the command stream may flow in both directions.</summary>
    Online = 2,
}

/// <summary>
/// Command transport port of the reserved multiplayer face (T-131, F-161;
/// review doc docs/engineering/48-t131-multiplayer-architecture.md §四).
/// A T-139 listen-server implementation plugs its wire protocol (ENet,
/// WebSocket, …) behind this port; EraCore itself ships ZERO implementation
/// — the simulation already exposes its natural intake, the
/// <see cref="EraCore.Sim.SimCommandQueue"/> (same-seed replay invariant),
/// so the transport only has to move <see cref="SimCommand"/> records
/// between peers.
/// <para>
/// Direction contract (listen-server topology):
/// <list type="bullet">
/// <item><see cref="Submit"/> (upstream): one local command toward the
/// authoritative host. Returns false when the link is not
/// <see cref="NetLinkStatus.Online"/> or the transport refuses (send
/// buffer backpressure); a refused submission never throws for link-state
/// reasons and mutates nothing.</item>
/// <item><see cref="DrainIncoming"/> (downstream): every command received
/// from the host since the previous drain, in host broadcast order. The
/// caller enqueues them into its local
/// <see cref="EraCore.Sim.SimCommandQueue"/> verbatim — arrival order is
/// the enqueue order, so every client assigns the same per-queue sequence
/// numbers and the deterministic replay invariant holds end to end.</item>
/// </list>
/// </para>
/// <para>
/// Reservation discipline (Speculative Generality red line): the member set
/// is frozen to「status + upstream + downstream」— widening it (acks,
/// retries, channels, framing) belongs to T-139 under its own review, not
/// to the reservation.
/// </para>
/// </summary>
public interface ICommandTransport
{
    /// <summary>Current link status of this end (reported, never pushed).</summary>
    NetLinkStatus Status { get; }

    /// <summary>
    /// Upstream: queues one command for delivery to the host.
    /// </summary>
    /// <param name="command">Command to deliver; carried verbatim.</param>
    /// <returns>True when accepted for delivery; false when the link is not
    /// online or the transport refused (nothing queued).</returns>
    bool Submit(SimCommand command);

    /// <summary>
    /// Downstream: drains every command received from the host since the
    /// previous call, in broadcast order; empty when nothing arrived.
    /// </summary>
    /// <returns>Received commands in order (never null).</returns>
    IReadOnlyList<SimCommand> DrainIncoming();
}
