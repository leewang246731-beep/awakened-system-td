using EraCore.Agent.Pipeline;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.IO;
using EraCore.Net;
using EraCore.Sim;
using EraCore.Units;
using EraNet;
using Godot;

namespace EraTD.Net;

// T-139 (F-161) 联机 UI 最小面（08 验收词「主菜单联机入口+房内状态」）：
// 右上「联机」入口 → 房间面板（建房/加入 IP+房内状态，NetLinkStateMachine
// 状态驱动）+ 军令输入（宿主=本地管线直送权威；客户端=文本上行宿主审批）。
//
// 承载面纪律：
//  - headless（G4 冒烟）：绝不真开端口——跑「帧格式往返+内容指纹+两实例
//    内存回环对局（含军师审批全链）」并打印 NET_OK（任一自检不过 Quit(1)）；
//  - 窗口运行：ENet 真实路径（EnetNetLink 建房=listen server/加入=client），
//    本文件只做装配与状态显示，会话编排全部在 EraNet（与测试同一份代码）。
//
// 单机零耦合：本 UI 自持游戏实例（NetDemoLevel——E1 内容+唯一日志名），不
// 触碰 Main 的 SimHost/桥/渲染泳道；Main.cs 仅追加一行实例化（并行纪律：
// 冲突面由后提交者 rebase）。

/// <summary>
/// The T-139 minimal multiplayer face: entry button, room panel (host/join +
/// link-state-driven status), advisor order box, and the headless loopback
/// acceptance lane.
/// </summary>
public sealed partial class NetMenuUi : Control
{
    private const long DemoSeed = 20261014; // 与 Sim/Agent 夹具同种子族（E1 关卡形态）
    private const int SmokeFrames = 120;
    private const int StatusRefreshFrames = 15;

    private readonly bool _headless;

    private Button _entryButton = null!;
    private PanelContainer _panel = null!;
    private LineEdit _portField = null!;
    private LineEdit _addressField = null!;
    private LineEdit _orderField = null!;
    private Button _hostButton = null!;
    private Button _joinButton = null!;
    private Button _sendOrderButton = null!;
    private Button _disconnectButton = null!;
    private Label _statusLabel = null!;

    // —— 活动联机会话（窗口车道；headless 冒烟跑内存回环后即拆）——
    private NetGameHost? _host;
    private NetGameClient? _client;
    private EnetNetLink? _enetLink;
    private MemoryNetMesh? _loopbackMesh;
    private EraCore.Presentation.PauseClock? _hostClock;
    private NetClientViewport? _viewport; // T-157 ④：客户端车道视口（加入后渲染镜像战场）
    private int _frameCounter;

    private NetMenuUi()
    {
        _headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
    }

    /// <summary>Code-built instantiation (no .tscn — the menu scene itself is the Wave-4 packaging task).</summary>
    public static NetMenuUi Instantiate(Control host)
    {
        var ui = new NetMenuUi
        {
            Name = "NetMenuUi",
        };
        ui.BuildUi();
        host.AddChild(ui);
        if (ui._headless)
        {
            ui.RunHeadlessSmoke();
        }

        return ui;
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        _entryButton = new Button
        {
            Text = "联机",
            Position = new Vector2(940, 8),
            Size = new Vector2(72, 30),
        };
        _entryButton.Pressed += () => _panel.Visible = !_panel.Visible;
        AddChild(_entryButton);
        if (!_headless)
        {
            _entryButton.Hide(); // T-161 入口归拢：窗口态入口在主菜单（headless 冒烟照旧）
        }

        _panel = new PanelContainer
        {
            Position = new Vector2(640, 48),
            Size = new Vector2(360, 260),
            Visible = false,
        };
        var rows = new VBoxContainer();
        _panel.AddChild(rows);

        var hostRow = new HBoxContainer();
        _hostButton = new Button { Text = "建房（宿主）" };
        _hostButton.Pressed += OnHostPressed;
        _portField = new LineEdit { Text = EnetNetLink.DefaultPort.ToString(), CustomMinimumSize = new Vector2(90, 0) };
        hostRow.AddChild(_hostButton);
        hostRow.AddChild(_portField);
        rows.AddChild(hostRow);

        var joinRow = new HBoxContainer();
        _joinButton = new Button { Text = "加入 IP" };
        _joinButton.Pressed += OnJoinPressed;
        _addressField = new LineEdit { Text = "127.0.0.1", CustomMinimumSize = new Vector2(110, 0) };
        joinRow.AddChild(_joinButton);
        joinRow.AddChild(_addressField);
        rows.AddChild(joinRow);

        var orderRow = new HBoxContainer();
        _sendOrderButton = new Button { Text = "下军令" };
        _sendOrderButton.Pressed += OnSendOrderPressed;
        _orderField = new LineEdit { Text = "守住东门", CustomMinimumSize = new Vector2(200, 0) };
        orderRow.AddChild(_sendOrderButton);
        orderRow.AddChild(_orderField);
        rows.AddChild(orderRow);

        _disconnectButton = new Button { Text = "断开" };
        _disconnectButton.Pressed += OnDisconnectPressed;
        rows.AddChild(_disconnectButton);

        var menuButton = new Button { Text = "返回主菜单" };
        menuButton.Pressed += CloseFromMenu; // T-161：会话退出→回主菜单（承载钮）
        rows.AddChild(menuButton);

        _statusLabel = new Label
        {
            Text = "链路=Offline",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340, 90),
        };
        rows.AddChild(_statusLabel);

        AddChild(_panel);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_headless)
        {
            return; // headless 冒烟在 Instantiate 内同步跑完；窗口车道才有帧循环
        }

        _frameCounter++;
        PumpActiveSession(delta);
        if (_frameCounter % StatusRefreshFrames == 0)
        {
            RefreshStatus();
        }
    }

    // ------------------------------------------------------ windowed wiring

    private void OnHostPressed()
    {
        if (_host is not null || _client is not null)
        {
            return;
        }

        var level = new NetDemoLevel(withPipeline: true);
        var info = LocalHandshake();
        _enetLink = EnetNetLink.CreateServer(
            GetTree().GetMultiplayer() as SceneMultiplayer ?? new SceneMultiplayer(),
            ParsePort(_portField.Text),
            info,
            snapshotProvider: () => _host!.BuildSessionSnapshot());
        _hostClock = new EraCore.Presentation.PauseClock();
        _host = new NetGameHost(level.Simulation, _enetLink, _enetLink, _enetLink, level.Pipeline);
        _entryButton.Text = "联机·房主";
    }

    private void OnJoinPressed()
    {
        if (_host is not null || _client is not null)
        {
            return;
        }

        var level = new NetDemoLevel(withPipeline: false);
        var mirror = level.Simulation;
        var info = LocalHandshake();
        EnetNetLink link = EnetNetLink.CreateClient(
            GetTree().GetMultiplayer() as SceneMultiplayer ?? new SceneMultiplayer(),
            string.IsNullOrWhiteSpace(_addressField.Text) ? "127.0.0.1" : _addressField.Text.Trim(),
            ParsePort(_portField.Text),
            info,
            onSnapshot: snapshot =>
            {
                _client?.RestoreFrom(snapshot, seed => new NetDemoLevel(withPipeline: false, seedOverride: seed).Simulation);
            });
        _enetLink = link;
        _client = new NetGameClient(mirror, link, link, link);
        _viewport = NetClientViewport.Instantiate(this); // T-157 ④：客户端视口（镜像快照渲染+回显行）
        _entryButton.Text = "联机·已加入";
    }

    private void OnSendOrderPressed()
    {
        string text = _orderField.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (_host is not null && _enetLink is not null)
        {
            // 宿主也是玩家（listen 语义）：军令进入与远端请求同一收件箱——
            // 同一条 K-5 审批+系统点结算路径，无特权道。
            _enetLink.InjectHostLocalRequest(new AdvisorOrderRequest(0, AdvisorOrderRequestKind.Order, text, -1));
        }
        else if (_client is { } client)
        {
            client.SubmitAdvisorOrder(text); // 客户端=文本上行（执行权在宿主）
        }
    }

    private void OnDisconnectPressed()
    {
        Teardown();
        _entryButton.Text = "联机";
    }

    // ------------------------------------------- T-161 主菜单入口归拢（窗口车道）

    /// <summary>主菜单「联机」入口打开面板（headless 冒烟不走此路）.</summary>
    internal void OpenFromMenu() => _panel.Visible = true;

    /// <summary>
    /// 返回主菜单：链路在=诚实断开（既有 OnDisconnectPressed 同一条路）后收
    /// 面板——「会话退出→回主菜单」链路的联机半边。
    /// </summary>
    internal void CloseFromMenu()
    {
        if (_host is not null || _client is not null)
        {
            OnDisconnectPressed();
        }

        _panel.Visible = false;
    }

    /// <summary>联机会话在局位（建房/加入任一在——主菜单可见性谓词探针）.</summary>
    internal bool SessionActive => _host is not null || _client is not null;

    /// <summary>联机面板打开位（主菜单可见性谓词探针）.</summary>
    internal bool PanelOpen => _panel.Visible;

    private void Teardown()
    {
        _host = null;
        _client = null;
        _loopbackMesh = null;
        _hostClock = null;
        if (_viewport is not null)
        {
            _viewport.ReleaseCamera();
            _viewport.QueueFree();
            _viewport = null;
        }

        if (_enetLink is not null)
        {
            _enetLink.Dispose();
            _enetLink = null;
        }
    }

    private void PumpActiveSession(double delta)
    {
        _ = delta;
        if (_host is { } host)
        {
            host.PumpIncoming();
            int ticks = _hostClock?.TicksThisFrame() ?? 1; // 与单机 harness 同帧律（1x=1 tick/帧）
            for (int i = 0; i < ticks; i++)
            {
                host.AdvanceTick();
            }
        }
        else if (_client is { } client && _enetLink is { Status: NetLinkStatus.Online })
        {
            client.PumpIncoming();
            client.StepMirror(); // 帧步进 1 tick；对账为延迟比较（摘要到即核，落后不误报）
            _viewport?.PumpFrame(client); // T-157 ④：镜像快照→脏块→双层重建+本地输入回显行
        }
    }

    private void RefreshStatus()
    {
        NetLinkStatus status = NetLinkStatus.Offline;
        if (_enetLink is { } link)
        {
            status = link.Status;
        }
        else if (_loopbackMesh is not null)
        {
            status = NetLinkStatus.Online;
        }

        string room = _host is not null
            ? $"tick={_host.Sim.CurrentTick} 广播={_host.BroadcastCount}"
            : _client is not null
                ? $"tick={_client.Mirror.CurrentTick} 对账={_client.MatchedDigests}/{_client.MatchedDigests + _client.SkippedDigests}"
                : "未建房";
        string diverged = _client is { Divergences.Count: > 0 } divergent
            ? $" 偏差={divergent.Divergences.Count}@t{divergent.Divergences[^1].Tick}"
            : string.Empty;
        string lastReply = _client is { AdvisorReplies: { Count: > 0 } replies }
            ? $" 军师[{replies[^1].Kind}]"
            : string.Empty;
        _statusLabel.Text = $"链路={status}\n{room}{diverged}{lastReply}";
    }

    private static int ParsePort(string text) =>
        int.TryParse(text.Trim(), out int port) && port is > 0 and <= 65535 ? port : EnetNetLink.DefaultPort;

    private static NetHandshakeInfo LocalHandshake() => new(
        NetWireProtocol.Version,
        EraCore.VersionInfo.Current,
        NetContentFingerprint.Compute(NetDemoLevel.ReadFingerprintFiles()));

    // ------------------------------------------------- headless smoke lane

    /// <summary>
    /// G4 冒烟（零端口）：①帧格式全类往返+畸形拒收；②内容指纹稳定/敏感；
    /// ③两实例内存回环对局 120 tick（含军师 ask→confirm 审批全链）→终局
    /// 哈希全等+零偏差。打印 NET_OK；任一自检不过 Quit(1)。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        bool framingOk = SmokeFramingRoundTrip() && SmokeFingerprint();
        if (!framingOk)
        {
            GD.PushError("NET smoke: framing/fingerprint self-check FAILED");
            GetTree().Quit(1);
            return;
        }

        // —— 两实例：宿主（带管线）+镜像，内存回环联机。 ——
        var hostLevel = new NetDemoLevel(withPipeline: true);
        var clientLevel = new NetDemoLevel(withPipeline: false);
        var mesh = new MemoryNetMesh();
        MemoryNetEndpoint endpoint = mesh.AddClient();
        endpoint.Connect();
        endpoint.CompleteHandshake();

        var host = new NetGameHost(hostLevel.Simulation, mesh.Host, mesh.Host, mesh.Host, hostLevel.Pipeline);
        var client = new NetGameClient(clientLevel.Simulation, endpoint, endpoint, endpoint);
        bool orderSent = client.SubmitAdvisorOrder("守住东门"); // 客户端先行一单（ask）

        for (int frame = 0; frame < SmokeFrames; frame++)
        {
            if (frame == SmokeFrames / 2 && client.AdvisorReplies.Count > 0)
            {
                AdvisorOrderReply awaiting = client.AdvisorReplies[^1];
                if (awaiting.Kind == PipelineOutcomeKind.AwaitingApproval)
                {
                    client.ConfirmPending(awaiting.PendingId); // 确认上行（审批全链过线）
                }
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

            if (client.Divergences.Count > 0)
            {
                GD.PushError($"NET smoke: divergence at tick {client.Divergences[^1].Tick}");
                GetTree().Quit(1);
                return;
            }
        }

        long hostHash = host.Sim.ComputeStateHash();
        long clientHash = client.Mirror.ComputeStateHash();
        int hostTowers = hostLevel.Simulation.Towers.Count;
        int clientTowers = clientLevel.Simulation.Towers.Count;

        bool pass = orderSent
            && client.MatchedDigests >= (SmokeFrames / NetTiming.SnapshotCadenceTicks) - 1
            && client.Divergences.Count == 0
            && hostHash == clientHash
            && hostTowers == clientTowers
            && hostTowers >= 2; // 军师审批落塔（守住东门=2 塔）

        if (!pass)
        {
            GD.PushError(
                $"NET smoke FAILED order={orderSent} matched={client.MatchedDigests} " +
                $"divergences={client.Divergences.Count} hashEq={hostHash == clientHash} " +
                $"towers={hostTowers}/{clientTowers}");
            GetTree().Quit(1);
            return;
        }

        GD.Print(
            $"NET_OK link=loopback frames={SmokeFrames} hash={hostHash} towers={hostTowers} " +
            $"digests={client.MatchedDigests} divergences=0 advisor=ask-confirm");
        hostLevel.DeleteJournal(); // 冒烟日志即用即清（防 GUID 命名在 D-03 根无限堆积）

        // T-157 ④：客户端视口渲染集成冒烟（同回环介质+真渲染调用——NETVIEW_OK
        // 判据=渲染面活/镜像追平/本地输入回显闭环；失败 Quit(1)）。
        NetClientViewport.RunSmoke(this);
    }

    private static bool SmokeFramingRoundTrip()
    {
        foreach (NetWireMessageKind kind in Enum.GetValues<NetWireMessageKind>())
        {
            byte[] packet = NetWireFraming.Frame(kind, "{\"probe\":true}");
            if (!NetWireFraming.TryParse(packet, out NetWireMessageKind parsed, out string json)
                || parsed != kind
                || json != "{\"probe\":true}")
            {
                return false;
            }
        }

        if (NetWireFraming.TryParse(Array.Empty<byte>(), out _, out _))
        {
            return false; // 空包=拒收
        }

        var malformed = new byte[] { 200, 1, 2 }; // 未知消息类
        return !NetWireFraming.TryParse(malformed, out _, out _);
    }

    private static bool SmokeFingerprint()
    {
        string[] files = NetDemoLevel.ReadFingerprintFiles();
        string first = NetContentFingerprint.Compute(files);
        if (NetContentFingerprint.Compute(files) != first)
        {
            return false; // 同内容必须稳定
        }

        var mutated = new List<string>(files) { "{\"drift\":1}" };
        return NetContentFingerprint.Compute(mutated) != first; // 内容漂移必须可见
    }
}

/// <summary>
/// Headless-safe E1 demo level for the net lanes (T-139): era1 content walk-up
/// (same loader discipline as SimHost), seeded simulation, optional host-side
/// agent pipeline with a UNIQUE journal file (two SimHosts would collide on
/// the one t055 war-report path — the net lanes never touch the single-player
/// host instance, keeping the zero-coupling acceptance intact).
/// </summary>
internal sealed class NetDemoLevel
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly CellCoord EastGateAnchor = new(12, 12);
    private static readonly CellCoord SouthGateAnchor = new(4, 8);
    private static readonly CellCoord WarehouseCell = new(2, 14);

    internal NetDemoLevel(bool withPipeline, long seedOverride = DemoSeed)
    {
        string era1Dir = FindContentDir(System.IO.Path.Combine("game", "content", "era1"), "game/content/era1");
        string grammarDir = FindContentDir(System.IO.Path.Combine("game", "content", "grammar"), "game/content/grammar");

        var enemies = EnemySpecCatalog.Parse(ReadFile(era1Dir, "enemies.json"));
        var towers = TowerSpec.LoadCollection(ReadFile(era1Dir, "towers.json"));
        Simulation = new Simulation(
            BuildConfig(era1Dir, enemies, towers),
            new SplitMix64Rng(seedOverride),
            seedOverride);

        if (withPipeline)
        {
            var parser = EraCore.Agent.Grammar.L1IntentParser.FromJson(
                ReadFile(grammarDir, "patterns-zh.json"),
                ReadFile(grammarDir, "patterns-en.json"),
                ReadFile(grammarDir, "lexicon.json"),
                ReadFile(era1Dir, "towers.json"));
            var store = new DevFileStore();
            JournalPath = System.IO.Path.Combine(
                store.Root, "reports", $"t139-net-{System.Guid.NewGuid():N}.jsonl"); // GUID=并行冒烟不互撞（先例：T-140 G4 文件争用）
            _journalWriter = new JsonlJournalWriter(JournalPath);
            Pipeline = AgentPipeline.OverSimulation(
                Simulation,
                BuildConfig(era1Dir, enemies, towers),
                parser,
                2,
                _journalWriter,
                new GodotLogSink(),
                new Dictionary<string, CellCoord>
                {
                    ["east_gate"] = EastGateAnchor,
                    ["south_gate"] = SouthGateAnchor,
                });
            Pipeline.Ledger.BeginWave(1, isBossWave: false);
        }
        else
        {
            Pipeline = null;
        }

        Simulation.Enqueue(new SimCommand(
            1, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\",\"cell\":{\"x\":6,\"y\":21}}"));
    }

    private const long DemoSeed = 20261014;

    internal Simulation Simulation { get; }

    internal AgentPipeline? Pipeline { get; }

    /// <summary>War-report journal of this instance (null without a pipeline).</summary>
    internal string? JournalPath { get; }

    private readonly JsonlJournalWriter? _journalWriter;

    /// <summary>
    /// Closes and deletes the journal file (headless smoke cleans up after
    /// itself — the writer must be disposed FIRST, its handle keeps the file
    /// locked against File.Delete).
    /// </summary>
    internal void DeleteJournal()
    {
        _journalWriter?.Dispose();
        if (JournalPath is not null && System.IO.File.Exists(JournalPath))
        {
            System.IO.File.Delete(JournalPath);
        }
    }

    /// <summary>The fingerprint file set (level assembly inputs — same files both ends).</summary>
    internal static string[] ReadFingerprintFiles()
    {
        string era1Dir = FindContentDir(System.IO.Path.Combine("game", "content", "era1"), "game/content/era1");
        return new[]
        {
            ReadFile(era1Dir, "towers.json"),
            ReadFile(era1Dir, "enemies.json"),
        };
    }

    private static SimulationConfig BuildConfig(string era1Dir, EnemySpecCatalog enemies, IReadOnlyList<TowerSpec> towers) => new(
        Width: 16,
        Height: 24,
        ForestRatio: 0.10,
        RockRatio: 0.05,
        WaterRatio: 0.0,
        BaseCell: new CellCoord(8, 23),
        EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1) },
        TowerCatalog: towers,
        Enemies: enemies,
        Waves: new WaveSpawnConfig(
            BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
            RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
            BatchesPerWave: 4,
            EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1) }),
        InitialStock: new List<KeyValuePair<ResourceKey, double>> { new(Wood, 200.0) },
        WarehouseCell: WarehouseCell,
        Costs: new BuildCostPolicy(Wood));

    private static string ReadFile(string dir, string fileName) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, fileName));

    private static string FindContentDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"content directory {label} not found above the executable (dev runs need the repo checkout)");
    }

    private sealed class GodotLogSink : ILogSink
    {
        public void Write(LogLevel level, long tick, string message) =>
            GD.Print($"[agent:{level.ToString().ToLowerInvariant()}] tick={tick} {message}");
    }

    private sealed class SplitMix64Rng(long seed) : IRng
    {
        private ulong _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxValue));
            }

            long span = (long)maxValue - minValue;
            return minValue + (int)(NextDouble() * span);
        }

        public double NextDouble()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / 9007199254740992.0);
        }

        public void Reseed(long newSeed) => _state = (ulong)newSeed;
    }
}
