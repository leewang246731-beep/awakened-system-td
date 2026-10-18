using System.Text.Json;
using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;
using EraNet;
using Godot;

namespace EraTD.Net;

// T-139 (F-161) ENet 传输适配器（表现层，Godot 高层多人 API）：一个类实现
// 全部四个面（ICommandTransport/ISnapshotChannel=冻结预留端口的客户端视图、
// NetHostWire=宿主命令面、IAdvisorOrderChannel=军师订单面），与内存回环
// （EraNet.MemoryNetEndpoint）语义完全一致——会话编排（NetGameHost/
// NetGameClient）对两种承载零感知。宿主=ENetMultiplayerPeer.CreateServer
// （listen：宿主进程即房间）；客户端=CreateClient。
//
// 通道纪律：命令/摘要/军师/快照全部走 Reliable 有序通道（同一有序流保证
// 「命令先于其后的摘要」到达——客户端在对账前必已入队对应命令）；摘要尽力
// 而为（掉包由 ENet 重传兜底，宿主断连时静默丢弃）。
//
// 验证面诚实登记：本类的真实 socket 路径（建房/加入/断线信号）只在窗口
// 运行可验（G4 纪律=不真开网络）；headless 冒烟覆盖帧格式往返+内存回环
// 全链路（REV-20261018-T139 §验收矩阵）。

/// <summary>
/// One ENet link endpoint: host (listen server) or client. Implements every
/// T-139 net face; message routing follows the wire framing of
/// <see cref="NetWireFraming"/> over <see cref="SceneMultiplayer"/> reliable
/// ordered byte packets.
/// </summary>
internal sealed class EnetNetLink : IDisposable,
    ICommandTransport, ISnapshotChannel, IAdvisorOrderChannel, NetHostWire
{
    /// <summary>Default room port (fixture knob; the join UI can override).</summary>
    public const int DefaultPort = 23139;

    private static readonly EraNetJsonContext Json = EraNetJsonContext.Default;

    private readonly SceneMultiplayer _multiplayer;
    private readonly NetLinkStateMachine _link = new();
    private readonly bool _isHost;
    private readonly NetHandshakeInfo _localInfo;
    private readonly Func<NetSessionSnapshot>? _snapshotProvider;
    private readonly Action<NetSessionSnapshot>? _onSnapshot;
    private readonly List<long> _peers = new();
    private readonly List<SimCommand> _commands = new();
    private readonly List<SimHashSample> _digests = new();
    private readonly List<AdvisorOrderRequest> _requests = new();
    private readonly List<AdvisorOrderReply> _replies = new();
    private long _requestSeq;

    private EnetNetLink(
        SceneMultiplayer multiplayer,
        bool isHost,
        NetHandshakeInfo localInfo,
        Func<NetSessionSnapshot>? snapshotProvider,
        Action<NetSessionSnapshot>? onSnapshot)
    {
        _multiplayer = multiplayer;
        _isHost = isHost;
        _localInfo = localInfo;
        _snapshotProvider = snapshotProvider;
        _onSnapshot = onSnapshot;
        _multiplayer.PeerConnected += OnPeerConnected;
        _multiplayer.PeerDisconnected += OnPeerDisconnected;
        _multiplayer.PeerPacket += OnPeerPacket;
    }

    /// <summary>Host factory: opens the listen server (link immediately Online).</summary>
    internal static EnetNetLink CreateServer(
        SceneMultiplayer multiplayer,
        int port,
        NetHandshakeInfo localInfo,
        Func<NetSessionSnapshot> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(multiplayer);
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        var link = new EnetNetLink(multiplayer, isHost: true, localInfo, snapshotProvider, onSnapshot: null);
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateServer(port, maxClients: 4);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"ENet server create failed on port {port}: {error}.");
        }

        multiplayer.MultiplayerPeer = peer;
        link._link.BeginConnect();
        link._link.MarkOnline();
        return link;
    }

    /// <summary>Client factory: begins connecting (Connecting until the snapshot arrives).</summary>
    internal static EnetNetLink CreateClient(
        SceneMultiplayer multiplayer,
        string address,
        int port,
        NetHandshakeInfo localInfo,
        Action<NetSessionSnapshot> onSnapshot)
    {
        ArgumentNullException.ThrowIfNull(multiplayer);
        ArgumentNullException.ThrowIfNull(onSnapshot);
        var link = new EnetNetLink(multiplayer, isHost: false, localInfo, snapshotProvider: null, onSnapshot);
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateClient(address, port);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"ENet client create failed to {address}:{port}: {error}.");
        }

        multiplayer.MultiplayerPeer = peer;
        link._link.BeginConnect();
        multiplayer.ConnectedToServer += link.OnConnectedToServer;
        multiplayer.ServerDisconnected += link.OnServerDisconnected;
        multiplayer.ConnectionFailed += link.OnConnectionFailed;
        return link;
    }

    /// <summary>Current status (shared vocabulary; drives the room UI).</summary>
    public NetLinkStatus Status => _link.Status;

    // ------------------------------------------------------ ICommandTransport

    /// <summary>Client face — one command toward the host (peer 1).</summary>
    public bool Submit(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_isHost || !_link.CanSubmit)
        {
            return false;
        }

        return Send(NetWireMessageKind.CommandFromClient, JsonSerializer.Serialize(command, Json.SimCommand), 1);
    }

    /// <summary>Client face — broadcast commands in broadcast order.</summary>
    public IReadOnlyList<SimCommand> DrainIncoming()
    {
        return Drain(_commands);
    }

    // ----------------------------------------------------------- NetHostWire

    /// <summary>Host face — client submissions in arrival order.</summary>
    public IReadOnlyList<SimCommand> DrainSubmissions() => Drain(_commands);

    /// <summary>Host face — reliable ordered broadcast to every connected peer.</summary>
    public void Broadcast(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        byte[] packet = NetWireFraming.Frame(
            NetWireMessageKind.CommandBroadcast, JsonSerializer.Serialize(command, Json.SimCommand));
        SendPacket(packet, 0);
    }

    NetLinkStatus NetHostWire.Status => Status;

    // ------------------------------------------------------- ISnapshotChannel

    /// <summary>Host face — one digest toward every peer (best-effort lane).</summary>
    public void Publish(SimHashSample digest)
    {
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        byte[] packet = NetWireFraming.Frame(
            NetWireMessageKind.DigestPush, JsonSerializer.Serialize(digest, Json.SimHashSample));
        SendPacket(packet, 0);
    }

    /// <summary>Client face — digests in publish order.</summary>
    IReadOnlyList<SimHashSample> ISnapshotChannel.DrainIncoming() => Drain(_digests);

    // --------------------------------------------------- IAdvisorOrderChannel

    /// <summary>Client face — one request toward the host.</summary>
    public bool Submit(AdvisorOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isHost || !_link.CanSubmit)
        {
            return false;
        }

        request = request with { RequestId = ++_requestSeq };
        return Send(NetWireMessageKind.AdvisorRequest, JsonSerializer.Serialize(request, Json.AdvisorOrderRequest), 1);
    }

    /// <summary>Host face — requests in arrival order.</summary>
    public IReadOnlyList<AdvisorOrderRequest> DrainRequests() => Drain(_requests);

    /// <summary>Host face — one reply toward every connected peer (v1 room stream).</summary>
    public void Push(AdvisorOrderReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        byte[] packet = NetWireFraming.Frame(
            NetWireMessageKind.AdvisorReply, JsonSerializer.Serialize(reply, Json.AdvisorOrderReply));
        SendPacket(packet, 0);
    }

    /// <summary>Client face — replies in push order.</summary>
    public IReadOnlyList<AdvisorOrderReply> DrainReplies() => Drain(_replies);

    NetLinkStatus IAdvisorOrderChannel.Status => Status;

    // -------------------------------------------------------------- internal

    /// <summary>Headless smoke helper: injects one received packet (no socket).</summary>
    internal void InjectReceivedForSmoke(byte[] packet) => HandlePacket(-1, packet);

    /// <summary>Headless smoke helper: marks this end online without a socket.</summary>
    internal void SmokeMarkOnline() => _link.MarkOnline();

    /// <summary>
    /// Host-side LOCAL advisor request entry (listen semantics): the host
    /// player's own order enters the SAME request inbox remote client
    /// requests drain from — one approval throat, no privileged lane.
    /// </summary>
    internal void InjectHostLocalRequest(AdvisorOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);
    }

    private static List<T> Drain<T>(List<T> inbox)
    {
        if (inbox.Count == 0)
        {
            return new List<T>();
        }

        var drained = new List<T>(inbox);
        inbox.Clear();
        return drained;
    }

    private bool Send(NetWireMessageKind kind, string json, long peer)
    {
        byte[] packet = NetWireFraming.Frame(kind, json);
        return SendPacket(packet, peer);
    }

    private bool SendPacket(byte[] packet, long peer)
    {
        Error error = _multiplayer.SendBytes(
            packet, (int)peer, MultiplayerPeer.TransferModeEnum.Reliable, channel: 0);
        return error == Error.Ok;
    }

    private void OnPeerConnected(long id)
    {
        if (_isHost && !_peers.Contains(id))
        {
            _peers.Add(id);
        }
    }

    private void OnPeerDisconnected(long id)
    {
        _peers.Remove(id);
    }

    private void OnConnectedToServer()
    {
        // 客户端连上：先上行握手（宿主校验指纹后才回快照）。
        _ = Send(
            NetWireMessageKind.Handshake,
            JsonSerializer.Serialize(_localInfo, Json.NetHandshakeInfo),
            1);
    }

    private void OnServerDisconnected()
    {
        _link.MarkOffline();
    }

    private void OnConnectionFailed()
    {
        _link.MarkOffline();
    }

    private void OnPeerPacket(long id, byte[] packet) => HandlePacket(id, packet);

    private void HandlePacket(long id, byte[] packet)
    {
        if (!NetWireFraming.TryParse(packet, out NetWireMessageKind kind, out string json))
        {
            GD.PushWarning($"net: malformed packet from peer {id} ({packet?.Length ?? 0} bytes) dropped.");
            return;
        }

        switch (kind)
        {
            case NetWireMessageKind.Handshake when _isHost:
                var peerInfo = JsonSerializer.Deserialize<NetHandshakeInfo>(json, Json.NetHandshakeInfo);
                if (peerInfo is null || !peerInfo.Matches(_localInfo))
                {
                    // 房间门禁（48 §6.5）：指纹不一致=拒绝入房。
                    GD.PushWarning($"net: peer {id} handshake mismatch — refusing (protocol/core/content).");
                    _multiplayer.DisconnectPeer((int)id);
                    return;
                }

                if (_snapshotProvider is { } provider)
                {
                    _ = Send(
                        NetWireMessageKind.SessionSnapshot,
                        JsonSerializer.Serialize(provider(), Json.NetSessionSnapshot),
                        id);
                }

                return;

            case NetWireMessageKind.SessionSnapshot when !_isHost:
                var snapshot = JsonSerializer.Deserialize<NetSessionSnapshot>(json, Json.NetSessionSnapshot);
                if (snapshot is not null)
                {
                    _onSnapshot?.Invoke(snapshot);
                    _link.MarkOnline(); // 快照到达=加入完成（读档+追进度，48 §3.2）
                }

                return;

            case NetWireMessageKind.CommandFromClient when _isHost:
                _commands.Add(JsonSerializer.Deserialize<SimCommand>(json, Json.SimCommand)!);
                return;

            case NetWireMessageKind.CommandBroadcast when !_isHost:
                _commands.Add(JsonSerializer.Deserialize<SimCommand>(json, Json.SimCommand)!);
                return;

            case NetWireMessageKind.DigestPush when !_isHost:
                _digests.Add(JsonSerializer.Deserialize<SimHashSample>(json, Json.SimHashSample)!);
                return;

            case NetWireMessageKind.AdvisorRequest when _isHost:
                _requests.Add(JsonSerializer.Deserialize<AdvisorOrderRequest>(json, Json.AdvisorOrderRequest)!);
                return;

            case NetWireMessageKind.AdvisorReply when !_isHost:
                _replies.Add(JsonSerializer.Deserialize<AdvisorOrderReply>(json, Json.AdvisorOrderReply)!);
                return;

            default:
                GD.PushWarning($"net: unexpected message kind {kind} for this end (peer {id}) dropped.");
                return;
        }
    }

    public void Dispose()
    {
        _multiplayer.PeerConnected -= OnPeerConnected;
        _multiplayer.PeerDisconnected -= OnPeerDisconnected;
        _multiplayer.PeerPacket -= OnPeerPacket;
        if (!_isHost)
        {
            _multiplayer.ConnectedToServer -= OnConnectedToServer;
            _multiplayer.ServerDisconnected -= OnServerDisconnected;
            _multiplayer.ConnectionFailed -= OnConnectionFailed;
        }

        _link.MarkOffline();
        if (_multiplayer.MultiplayerPeer is { } peer)
        {
            peer.Close();
            _multiplayer.MultiplayerPeer = null;
        }
    }
}
