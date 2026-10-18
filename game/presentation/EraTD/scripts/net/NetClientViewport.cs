using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraNet;
using Godot;

namespace EraTD.Net;

// T-157 (F-161/C-21) 联机客户端视口渲染集成（W8 挂账 ④——48 号 §3.2 口径）：
// 客户端从宿主快照（广播命令流驱动的镜像 Simulation）渲染战场——「读档+
// 追进度+本地输入回显」三件套：
//   读档+追进度 = NetGameClient.RestoreFrom（加入/重连=检查点重放，K-7
//     体系网络化——镜像 sim 被整体替换，本视口检测到镜像引用变化即重建
//     桥/渲染层，恢复后继续从广播流逐 tick 对账追进度）；
//   战场渲染   = 镜像 sim 的 SimSnapshotBridge + 双 MultiMesh 层（与单机
//     Main 同一套脏块渲染路径——镜像的 Snapshots.Latest 双缓冲就是 K-6
//     快照面，客户端零新增快照概念）；
//   本地输入回显 = NetGameClient.SubmittedEcho（v1 无预测——建议上行后
//     回显列表供 UI 展示，宿主盖章广播回来才生效）。
//
// 承载面纪律（NetMenuUi 同律）：headless 冒烟跑「两实例内存回环+真渲染
// 调用」（客户端渲染循环消费镜像快照，渲染计数/镜像哈希/回显三面断言）
// 并打印 NETVIEW_OK（任一自检不过 Quit(1)）；窗口运行=加入房间的车道挂
// 本视口逐帧泵。单机零耦合不变（本视口只读 NetGameClient，不触碰 Main
// 的 SimHost 泳道）。

/// <summary>
/// The client-side battlefield viewport (T-157 ④): renders the mirror
/// simulation the host broadcast stream drives, re-binds wholesale on
/// restore (late join / reconnect / resync), and surfaces the local input
/// echo lane.
/// </summary>
public sealed partial class NetClientViewport : Control
{
    private const int UnitLayerCapacity = 256; // E1 波预算余量（Main.UnitLayerCapacity 同带加宽）
    private const int EchoRefreshFrames = 15;

    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private Camera2D _camera = null!;
    private Label _echoLabel = null!;
    private SimSnapshotBridge _bridge = null!;
    private Simulation? _boundMirror;

    private int _framesDrawn;
    private int _groundChunkRebuilds;
    private int _unitChunkRebuilds;

    private NetClientViewport()
    {
    }

    /// <summary>
    /// Code-built instantiation: the viewport node tree (camera + two render
    /// layers + echo label). Call <see cref="Bind"/> with the live client.
    /// </summary>
    public static NetClientViewport Instantiate(Control host)
    {
        var viewport = new NetClientViewport
        {
            Name = "NetClientViewport",
        };
        viewport.BuildTree();
        host.AddChild(viewport);
        return viewport;
    }

    private void BuildTree()
    {
        _camera = new Camera2D { Name = "ClientCamera", Enabled = false };
        AddChild(_camera);

        _groundLayer = new MultiMeshRenderer { Name = "ClientGroundLayer" };
        AddChild(_groundLayer);
        _unitLayer = new MultiMeshRenderer { Name = "ClientUnitLayer" };
        AddChild(_unitLayer);

        _echoLabel = new Label
        {
            Name = "ClientEchoLabel",
            Position = new Vector2(8, 690),
            Size = new Vector2(700, 24),
            Text = "本地输入回显：—",
        };
        AddChild(_echoLabel);
    }

    /// <summary>
    /// Binds (or re-binds) the viewport onto the client's CURRENT mirror:
    /// <see cref="NetGameClient.RestoreFrom"/> replaces the mirror wholesale,
    /// so a reference change rebuilds the bridge and both layers from the new
    /// world's dimensions (读档+追进度后的渲染重建).
    /// </summary>
    public void Bind(NetGameClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (ReferenceEquals(_boundMirror, client.Mirror))
        {
            return;
        }

        _boundMirror = client.Mirror;
        var source = new MirrorSnapshotSource(client.Mirror);
        var framebuffer = new MirrorFramebuffer(client.Mirror);
        _bridge = new SimSnapshotBridge(
            source, framebuffer,
            framebuffer.WidthCells, framebuffer.HeightCells,
            chunkCells: EraCore.Presentation.DirtyRegionSet.DefaultChunkCells);

        int width = framebuffer.WidthCells;
        int height = framebuffer.HeightCells;
        float cell = SimSnapshotBridge.CellSizePx;
        _camera.Position = new Vector2(width * cell / 2f, height * cell / 2f);
        _camera.Enabled = true; // 客户端车道激活时接管镜头（断开由 NetMenuUi 置回）
        _groundLayer.SetupGround(width, height, EraCore.Presentation.DirtyRegionSet.DefaultChunkCells,
            TerrainAtlas.Build(), TerrainAtlas.TileIndexForColor);
        _unitLayer.SetupUnits(width, height, EraCore.Presentation.DirtyRegionSet.DefaultChunkCells,
            capacity: UnitLayerCapacity);
    }

    /// <summary>Releases the camera (host lane teardown).</summary>
    public void ReleaseCamera()
    {
        _camera.Enabled = false;
        _boundMirror = null;
    }

    /// <summary>
    /// One client render frame: diff the mirror's latest snapshot into dirty
    /// chunks and rebuild exactly those (单机 Main 同一脏块路径——客户端渲染
    /// 不发明第二条渲染法)。Also refreshes the local-input echo line.
    /// </summary>
    public void PumpFrame(NetGameClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Bind(client); // restore 换镜像 ⇒ 重建（幂等：引用相等零动作）

        _bridge.SyncFrame();
        var groundChunks = _bridge.TakeDirtyGroundChunks();
        if (groundChunks.Count > 0)
        {
            _groundLayer.RebuildGroundChunks(groundChunks, MirrorFramebufferOf(client));
            _groundChunkRebuilds += groundChunks.Count;
        }

        var unitChunks = _bridge.TakeDirtyUnitChunks();
        if (unitChunks.Count > 0 && _bridge.Current is not null)
        {
            _unitLayer.RebuildUnitChunks(unitChunks, _bridge.Current);
            _unitChunkRebuilds += unitChunks.Count;
        }

        _framesDrawn++;
        if (_framesDrawn % EchoRefreshFrames == 0)
        {
            RefreshEcho(client);
        }
    }

    /// <summary>本地输入回显行：上行建议数+最近一单（v1 无预测——回显即证据）。</summary>
    private void RefreshEcho(NetGameClient client)
    {
        if (client.SubmittedEcho.Count == 0)
        {
            _echoLabel.Text = "本地输入回显：—";
            return;
        }

        SimCommand last = client.SubmittedEcho[^1];
        _echoLabel.Text = $"本地输入回显：{client.SubmittedEcho.Count} 单（最近 {last.Kind}，待宿主盖章生效）";
    }

    /// <summary>Render-loop counters (headless smoke probes; window lane observability).</summary>
    public int FramesDrawn => _framesDrawn;

    /// <summary>Ground chunk rebuilds so far.</summary>
    public int GroundChunkRebuilds => _groundChunkRebuilds;

    /// <summary>Unit chunk rebuilds so far.</summary>
    public int UnitChunkRebuilds => _unitChunkRebuilds;

    private MirrorFramebuffer MirrorFramebufferOf(NetGameClient client) => new(client.Mirror);

    // ------------------------------------------------- headless smoke lane

    /// <summary>
    /// G4 冒烟（零端口）：两实例内存回环 120 帧——客户端视口每帧真渲染
    /// （镜像快照→脏块→双层重建），断言：①渲染面活（帧数+地面脏块>0）；
    /// ②镜像追平宿主（终局哈希全等+对账 digest 命中）③本地输入回显
    /// （客户端 SubmitCommand 一单→回显列表+1→宿主盖章广播回投→镜像塔数
    /// 增长）。打印 NETVIEW_OK；任一自检不过 Quit(1)。
    /// </summary>
    public static void RunSmoke(Node tree)
    {
        var problems = new List<string>();
        const int frames = 120;

        var hostLevel = new NetDemoLevel(withPipeline: true);
        var clientLevel = new NetDemoLevel(withPipeline: false);
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(hostLevel.Simulation, mesh.Host, mesh.Host, mesh.Host, hostLevel.Pipeline);
        var client = new NetGameClient(clientLevel.Simulation, endpoint, endpoint, endpoint);

        // 视口挂在临时节点下（headless 同样需要节点树让 MultiMesh 生效）
        var carrier = new Control { Name = "NetViewSmokeCarrier" };
        tree.AddChild(carrier);
        NetClientViewport viewport = Instantiate(carrier);

        int towersBeforeEcho = clientLevel.Simulation.Towers.Count;
        bool echoSubmitted = false;
        for (int frame = 0; frame < frames; frame++)
        {
            if (frame == frames / 2)
            {
                // 本地输入回显道：客户端上行一单建造建议（v1 无预测——镜像
                // 只等宿主广播）；宿主盖章广播回投后镜像塔数应增长。
                echoSubmitted = client.SubmitCommand(new SimCommand(
                    client.Mirror.CurrentTick, SimCommandKind.PlayerBuild,
                    "{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\",\"cell\":{\"x\":9,\"y\":19}}"));
            }

            mesh.Deliver();
            host.PumpIncoming();
            host.AdvanceTick();
            mesh.Deliver();
            client.PumpIncoming();
            while (client.Mirror.CurrentTick < host.Sim.CurrentTick)
            {
                client.StepMirror();
            }

            viewport.PumpFrame(client);
            if (client.Divergences.Count > 0)
            {
                problems.Add($"偏差 @tick {client.Divergences[^1].Tick}（视口渲染前提=镜像在同步）");
                break;
            }
        }

        long hostHash = host.Sim.ComputeStateHash();
        long clientHash = client.Mirror.ComputeStateHash();
        int mirrorTowers = client.Mirror.Towers.Count;
        bool renderAlive = viewport.FramesDrawn == frames && viewport.GroundChunkRebuilds > 0;
        bool caughtUp = clientHash == hostHash
            && client.MatchedDigests >= (frames / EraNet.NetTiming.SnapshotCadenceTicks) - 1;
        bool echoLanded = echoSubmitted
            && client.SubmittedEcho.Count == 1
            && mirrorTowers > towersBeforeEcho;

        if (!renderAlive)
        {
            problems.Add($"渲染面未活：frames={viewport.FramesDrawn} ground_chunks={viewport.GroundChunkRebuilds}");
        }

        if (!caughtUp)
        {
            problems.Add($"镜像未追平宿主：hash_eq={hostHash == clientHash} matched={client.MatchedDigests}");
        }

        if (!echoLanded)
        {
            problems.Add($"回显道未闭环：submitted={echoSubmitted} echo={client.SubmittedEcho.Count} towers {towersBeforeEcho}->{mirrorTowers}");
        }

        carrier.QueueFree();
        hostLevel.DeleteJournal(); // 冒烟日志即用即清（NetMenuUi 同纪律）

        if (problems.Count == 0)
        {
            GD.Print(
                $"NETVIEW_OK frames={frames} ground_chunks={viewport.GroundChunkRebuilds} " +
                $"unit_chunks={viewport.UnitChunkRebuilds} hash_eq={hostHash == clientHash} " +
                $"digests={client.MatchedDigests} divergences={client.Divergences.Count} " +
                $"echo=1 towers={towersBeforeEcho}->{mirrorTowers} source=mirror-snapshot-render");
        }
        else
        {
            GD.PushError("NETVIEW_FAIL " + string.Join("; ", problems));
            GD.Print("NETVIEW_FAIL " + string.Join("; ", problems));
            tree.GetTree().Quit(1);
        }
    }

    // ---- adapters（镜像 sim → 桥的两端口；SimHost 同款映射，读面独立成对） -

    private sealed class MirrorSnapshotSource(Simulation mirror) : ISimSnapshotSource
    {
        public SimSnapshot? Latest => mirror.Snapshots.Latest;
    }

    private sealed class MirrorFramebuffer(Simulation mirror) : IFramebufferProvider
    {
        public int WidthCells => mirror.World.Width;

        public int HeightCells => mirror.World.Height;

        public long FramebufferVersion => 1L; // 静态地形（SimHost.TerrainFramebuffer 同律）：绑定时一次性全量重建；Restore 换桥=新实例再触发一次

        public int ColorAt(int x, int y)
        {
            if (x < 0 || x >= WidthCells)
            {
                throw new ArgumentException($"cell X {x} outside grid [0,{WidthCells}).", nameof(x));
            }

            if (y < 0 || y >= HeightCells)
            {
                throw new ArgumentException($"cell Y {y} outside grid [0,{HeightCells}).", nameof(y));
            }

            return mirror.World.TerrainAt(new CellCoord(x, y)) switch
            {
                EraCore.World.TerrainKind.Forest => 0x36523C, // atlas slot 1 (floor-dirt-pebbled)
                EraCore.World.TerrainKind.Rock => 0x2A4430,   // atlas slot 3 (floor-rubble)
                EraCore.World.TerrainKind.Water => 0x4A6648,  // atlas slot 7 (water-pool)
                EraCore.World.TerrainKind.Wall => 0x405C42,   // atlas slot 2 (floor-dirt-mottled)
                _ => 0x2E4A34,                                // atlas slot 0 (floor-dirt) — Plain/Road
            };
        }
    }
}
