namespace EraCore.Net;

/// <summary>
/// Link lifecycle state machine of the reserved multiplayer face (T-131,
/// F-161) — the one behavioural component of the reservation: every T-139
/// transport implementation embeds one instance and reports its
/// <see cref="Status"/> through <see cref="ICommandTransport.Status"/> /
/// <see cref="ISnapshotChannel.Status"/>, so all implementations share one
/// transition vocabulary instead of each inventing its own link states.
/// <para>
/// Legal transitions:
/// <list type="bullet">
/// <item><see cref="BeginConnect"/>: Offline → Connecting (open a link).</item>
/// <item><see cref="MarkOnline"/>: Connecting → Online (handshake done —
/// the first transition that makes <see cref="CanSubmit"/> true).</item>
/// <item><see cref="MarkOffline"/>: Connecting → Offline (handshake failed
/// or aborted) and Online → Offline (drop / close). From Offline it is a
/// legal no-op: duplicate drop events (e.g. a read failure followed by a
/// close callback) must not crash the host loop.</item>
/// </list>
/// Every other transition (re-connecting while connecting or online, going
/// online straight from offline, re-confirming an online link) throws
/// <see cref="InvalidOperationException"/> and leaves the state untouched.
/// </para>
/// <para>
/// Single-player path: nothing in EraCore outside this namespace constructs
/// a <see cref="NetLinkStateMachine"/> (pinned by
/// EraCore.Tests/Architecture/NetCouplingGuardTests); a solo run never sees
/// this type.
/// </para>
/// </summary>
public sealed class NetLinkStateMachine
{
    /// <summary>Current status; starts <see cref="NetLinkStatus.Offline"/>.</summary>
    public NetLinkStatus Status { get; private set; } = NetLinkStatus.Offline;

    /// <summary>
    /// Whether command submission is legal right now — true only while
    /// <see cref="Status"/> is <see cref="NetLinkStatus.Online"/>. This is
    /// the gate <c>ICommandTransport.Submit</c> implementations consult.
    /// </summary>
    public bool CanSubmit => Status == NetLinkStatus.Online;

    /// <summary>
    /// Opens a link: Offline → Connecting.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Status is not Offline (a connect attempt while connecting or online
    /// is a caller bug, not a network event).
    /// </exception>
    public void BeginConnect()
    {
        Ensure(NetLinkStatus.Offline, "connect");
        Status = NetLinkStatus.Connecting;
    }

    /// <summary>
    /// Completes the handshake: Connecting → Online.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Status is not Connecting (online links never re-confirm; an offline
    /// link must connect first).
    /// </exception>
    public void MarkOnline()
    {
        Ensure(NetLinkStatus.Connecting, "go online");
        Status = NetLinkStatus.Online;
    }

    /// <summary>
    /// Drops or closes the link: Connecting/Online → Offline; from Offline a
    /// legal no-op (duplicate drop events are normal, see class remarks).
    /// </summary>
    public void MarkOffline()
    {
        Status = NetLinkStatus.Offline;
    }

    private void Ensure(NetLinkStatus required, string action)
    {
        if (Status != required)
        {
            throw new InvalidOperationException(
                $"Illegal link transition: cannot {action} from {Status} (required {required}).");
        }
    }
}
