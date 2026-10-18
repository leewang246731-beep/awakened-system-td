using EraCore.Contracts.Sim;
using EraCore.Net;
using EraCore.Sim;

namespace EraNet;

// T-139 (F-161) 内存回环网：ICommandTransport / ISnapshotChannel /
// IAdvisorOrderChannel / NetHostWire 的零端口实现（一个 mesh 对象=一根不出
// 进程的网线）。确定性铁律（48 §1.3）：无线程、无墙钟、无随机——「网络」被
// 建模为显式的两段队列（wire=在途，inbox=已送达），Deliver() 一次=一跳；
// 测试与 G4 冒烟据此获得完全可复现的投递节奏（G4 不真开网络的纪律承载面）。
// 故障注入面（DropPendingDigests/DropPendingCommands）支撑「摘要尽力而为」
// 「命令可靠有序」两条线语义的负向验证。

/// <summary>
/// One in-memory link fabric: exactly one host endpoint plus any number of
/// client endpoints, wired through deterministic FIFO wire buffers.
/// </summary>
public sealed class MemoryNetMesh
{
    private readonly MemoryNetEndpoint _host;
    private readonly List<MemoryNetEndpoint> _clients = new();

    /// <summary>The host-side endpoint (DrainSubmissions/Broadcast/Publish faces).</summary>
    public MemoryNetEndpoint Host => _host;

    /// <summary>All client endpoints ever created, in creation order.</summary>
    public IReadOnlyList<MemoryNetEndpoint> Clients => _clients;

    /// <summary>Creates the mesh with its host endpoint already online (server listening).</summary>
    public MemoryNetMesh()
    {
        _host = new MemoryNetEndpoint(this, isHost: true);
        _host.OpenServer();
    }

    /// <summary>
    /// Adds one client endpoint (starts Offline — call
    /// <see cref="MemoryNetEndpoint.Connect"/> then
    /// <see cref="MemoryNetEndpoint.CompleteHandshake"/> to bring it online).
    /// </summary>
    public MemoryNetEndpoint AddClient()
    {
        var endpoint = new MemoryNetEndpoint(this, isHost: false);
        _clients.Add(endpoint);
        return endpoint;
    }

    /// <summary>
    /// One network hop: moves every in-flight wire message into its
    /// destination inbox, preserving per-direction FIFO order. Call this to
    /// simulate delivery latency ticks (messages sent since the previous
    /// Deliver become readable on the next one).
    /// </summary>
    public void Deliver()
    {
        foreach (MemoryNetEndpoint client in _clients)
        {
            while (client.Link.HostToClientWire.Count > 0)
            {
                client.Inbox.Enqueue(client.Link.HostToClientWire.Dequeue());
            }

            while (client.Link.ClientToHostWire.Count > 0)
            {
                _host.Inbox.Enqueue(client.Link.ClientToHostWire.Dequeue());
            }
        }
    }

    /// <summary>
    /// Fault injection: drops up to <paramref name="count"/> pending digests
    /// still in flight toward client <paramref name="clientIndex"/> (digests
    /// are best-effort — 48 §3.1 losing one edge sample must be harmless).
    /// Returns the number actually dropped.
    /// </summary>
    public int DropPendingDigests(int clientIndex, int count)
    {
        MemoryNetEndpoint client = _clients[clientIndex];
        return DropMatching(client.Link.HostToClientWire, static message => message is SimHashSample, count);
    }

    /// <summary>
    /// Fault injection: drops up to <paramref name="count"/> pending command
    /// broadcasts still in flight toward client <paramref name="clientIndex"/>.
    /// Returns the number actually dropped (tests of reconnect semantics use
    /// this to starve a client, forcing the snapshot-resync path).
    /// </summary>
    public int DropPendingCommands(int clientIndex, int count)
    {
        MemoryNetEndpoint client = _clients[clientIndex];
        return DropMatching(client.Link.HostToClientWire, static message => message is SimCommand, count);
    }

    private static int DropMatching(Queue<object> wire, Func<object, bool> matches, int count)
    {
        int dropped = 0;
        var kept = new Queue<object>();
        while (wire.Count > 0)
        {
            object message = wire.Dequeue();
            if (dropped < count && matches(message))
            {
                dropped++;
            }
            else
            {
                kept.Enqueue(message);
            }
        }

        while (kept.Count > 0)
        {
            wire.Enqueue(kept.Dequeue());
        }

        return dropped;
    }
}

/// <summary>Wire state of one host-client link (in-flight queues both directions).</summary>
internal sealed class MemoryLink
{
    public required Queue<object> HostToClientWire { get; init; }

    public required Queue<object> ClientToHostWire { get; init; }
}

/// <summary>
/// One endpoint of a <see cref="MemoryNetMesh"/>: implements every T-139 net
/// face at once — the frozen reservation ports (client faces:
/// <see cref="ICommandTransport"/>, <see cref="ISnapshotChannel"/>), the host
/// command face (<see cref="NetHostWire"/>) and the advisor channel. The host
/// endpoint uses the host faces (DrainSubmissions/Broadcast/Publish/
/// DrainRequests/Push); client endpoints use the client faces
/// (Submit/DrainIncoming). One physical wire, documented faces; the single
/// inbox mixes message kinds FIFO, so every drainer filters by type while
/// preserving relative order of BOTH the drained and the kept messages.
/// </summary>
public sealed class MemoryNetEndpoint :
    ICommandTransport, ISnapshotChannel, IAdvisorOrderChannel, NetHostWire
{
    private readonly MemoryNetMesh _mesh;
    private readonly NetLinkStateMachine _link = new();
    private long _requestSeq;

    internal MemoryNetEndpoint(MemoryNetMesh mesh, bool isHost)
    {
        _mesh = mesh;
        Link = new MemoryLink
        {
            HostToClientWire = new Queue<object>(),
            ClientToHostWire = new Queue<object>(),
        };
        IsHost = isHost;
    }

    internal MemoryLink Link { get; }

    internal Queue<object> Inbox { get; } = new();

    private bool IsHost { get; }

    // ------------------------------------------------------------ lifecycle

    /// <summary>Host only: opens the listening server (link immediately Online).</summary>
    internal void OpenServer()
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("Only the host endpoint can open the server.");
        }

        _link.BeginConnect();
        _link.MarkOnline();
    }

    /// <summary>Client only: Offline → Connecting.</summary>
    public void Connect()
    {
        if (IsHost)
        {
            throw new InvalidOperationException("The host endpoint does not connect to itself.");
        }

        _link.BeginConnect();
    }

    /// <summary>Client only: completes the (simulated) handshake, Connecting → Online.</summary>
    public void CompleteHandshake() => _link.MarkOnline();

    /// <summary>Drops the link from either end (duplicate drops are legal no-ops).</summary>
    public void MarkOffline() => _link.MarkOffline();

    /// <summary>Current status (shared vocabulary of every face).</summary>
    public NetLinkStatus Status => _link.Status;

    // ------------------------------------------------------ ICommandTransport

    /// <summary>
    /// Client face — upstream toward the host. The host endpoint has no
    /// upstream (it IS the authority) and always refuses.
    /// </summary>
    public bool Submit(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsHost || !_link.CanSubmit)
        {
            return false;
        }

        Link.ClientToHostWire.Enqueue(command);
        return true;
    }

    /// <summary>
    /// Client face — every broadcast command delivered since the previous
    /// call, in broadcast order.
    /// </summary>
    public IReadOnlyList<SimCommand> DrainIncoming() => DrainWhere<SimCommand>();

    // ----------------------------------------------------------- NetHostWire

    /// <summary>Host face — client submissions in arrival order.</summary>
    public IReadOnlyList<SimCommand> DrainSubmissions() => DrainWhere<SimCommand>();

    /// <summary>Host face — broadcast to every online client (dropped while offline).</summary>
    public void Broadcast(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        foreach (MemoryNetEndpoint client in _mesh.Clients)
        {
            if (client.Status == NetLinkStatus.Online)
            {
                client.Link.HostToClientWire.Enqueue(command);
            }
        }
    }

    /// <summary>Current link status (NetHostWire face).</summary>
    NetLinkStatus NetHostWire.Status => Status;

    // ------------------------------------------------------- ISnapshotChannel

    /// <summary>Host face — publishes one digest toward every online client.</summary>
    public void Publish(SimHashSample digest)
    {
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        foreach (MemoryNetEndpoint client in _mesh.Clients)
        {
            if (client.Status == NetLinkStatus.Online)
            {
                client.Link.HostToClientWire.Enqueue(digest);
            }
        }
    }

    /// <summary>Client face — delivered digests in publish order.</summary>
    public IReadOnlyList<SimHashSample> DrainIncomingDigests() => DrainWhere<SimHashSample>();

    /// <summary>Client face of the frozen snapshot port (routes to the typed drainer).</summary>
    IReadOnlyList<SimHashSample> ISnapshotChannel.DrainIncoming() => DrainIncomingDigests();

    // --------------------------------------------------- IAdvisorOrderChannel

    /// <summary>Client face — one request toward the host (id re-stamped monotonic).</summary>
    public bool Submit(AdvisorOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsHost || !_link.CanSubmit)
        {
            return false;
        }

        Link.ClientToHostWire.Enqueue(request with { RequestId = ++_requestSeq });
        return true;
    }

    /// <summary>Host face — requests in arrival order.</summary>
    public IReadOnlyList<AdvisorOrderRequest> DrainRequests() => DrainWhere<AdvisorOrderRequest>();

    /// <summary>Host face — one reply toward every online client (v1: single room stream).</summary>
    public void Push(AdvisorOrderReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (_link.Status != NetLinkStatus.Online)
        {
            return;
        }

        foreach (MemoryNetEndpoint client in _mesh.Clients)
        {
            if (client.Status == NetLinkStatus.Online)
            {
                client.Link.HostToClientWire.Enqueue(reply);
            }
        }
    }

    /// <summary>Client face — replies in push order.</summary>
    public IReadOnlyList<AdvisorOrderReply> DrainReplies() => DrainWhere<AdvisorOrderReply>();

    /// <summary>Current link status (IAdvisorOrderChannel face).</summary>
    NetLinkStatus IAdvisorOrderChannel.Status => Status;

    // ------------------------------------------------------------------ core

    /// <summary>
    /// Drains every inbox message of type <typeparamref name="T"/> preserving
    /// relative order; non-matching messages stay in the inbox in their
    /// original relative order (the FIFO is never reordered across kinds).
    /// </summary>
    private List<T> DrainWhere<T>()
    {
        var drained = new List<T>();
        var kept = new Queue<object>();
        while (Inbox.Count > 0)
        {
            object message = Inbox.Dequeue();
            if (message is T matched)
            {
                drained.Add(matched);
            }
            else
            {
                kept.Enqueue(message);
            }
        }

        while (kept.Count > 0)
        {
            Inbox.Enqueue(kept.Dequeue());
        }

        return drained;
    }
}
