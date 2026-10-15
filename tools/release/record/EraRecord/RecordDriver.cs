using System.Reflection;
using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Supply;
using EraTD;
using Godot;

namespace EraRecord;

/// <summary>
/// T-108 商店物料录制驱动（真实录屏引导脚本）。
/// <para>
/// 本类不是游戏代码：它把 scenes/main.tscn 的节点树在运行期按同名同型
/// 搭建一遍（root=真 <see cref="Main"/> 脚本实例），<c>AddChild</c> 触发
/// 真 <c>Main._Ready</c> 冒烟原样执行；随后在 <c>_Process</c> 里按真主循环
/// 的同序驱动渲染回路（<c>StepFrame → SyncFrame → 脏块重建 → HUD 刷新</c>），
/// 并按时间轴走三个演示节拍，供 Godot 内建 <c>--write-movie</c> 逐帧收录：
/// </para>
/// <list type="number">
/// <item>演出段：真 VisualExpansionDirector 主菜单循环（A-01 ogv）实机播放；</item>
/// <item>断链减速段：波次接敌后，军令代建的新塔无供给线——真 BottleneckDetector
/// 事件红圈+因果提示气泡+邻塔有弹药在射的对照（断链=减速可视化）；</item>
/// <item>军令代建段：真 SystemPanel——Expand→军令句逐字输入→方案卡→确认→
/// 新塔落地；随后 assign-carrier 接通供给（恢复弧线，MP4 用）。</item>
/// </list>
/// <para>
/// 反射仅用于编排（拿 internal 成员的引用/调内部方法），不改任何被调逻辑；
/// 演示里的一切状态变化全部走游戏自身命令通道（PlayerBuild 军令/补线）。
/// 参数经 <c>-- key=value</c> 用户参数注入（OS.GetCmdlineUserArgs）。
/// </para>
/// </summary>
public sealed partial class RecordDriver : Control
{
    // ---- 反射目标（Main 私有字段；名称与 Main.cs 源同步） -------------------
    private Main _main = null!;
    private object _host = null!;                 // SimHost（internal 类型，反射驱动）
    private Type _hostType = null!;
    private Simulation _sim = null!;
    private SimSnapshotBridge _bridge = null!;
    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private SystemPanel _panel = null!;
    private IFramebufferProvider _framebuffer = null!;

    private readonly Dictionary<long, int> _prevUnitHp = new();
    private int _frame;

    // 反射缓存（热路径每帧调用，缓存强类型委托避免逐帧查找）。
    private Action _stepHostOnce = null!;
    private Func<HudSnapshot> _buildHud = null!;
    private Func<WaveCompositionView> _buildComp = null!;
    private Func<string, string> _nameOfEnemy = null!;
    private Action<HudSnapshot> _panelRefreshHud = null!;
    private Action<WaveCompositionView, Func<string, string>> _panelRefreshComp = null!;
    private Func<string> _panelHudText = null!;

    // ---- 时间轴参数（-- key=value；默认值=已实测的 60s 母片节拍） -----------
    private int _totalFrames = 3600;     // 60 s @60fps 电影时基（MP4 另拼题图/收尾）
    private int _menuFrames = 150;       // 2.5 s 主菜单循环演出段
    private long _ffUntilTick = 3210;    // 快进到波 1 前 1 s（spawn≈3240）
    private int _ffRate = 12;            // 快进期每渲染帧步进 tick 数
    private int _speed = 1;              // 实拍期每渲染帧步进 tick 数（=真 1x）
    private int _cruiseSpeed = 2;        // 波间巡航提速（波 1 展示后到波 2 前）
    private int _cruiseAfterFrames = 840;// 波 1 接敌后 1x 展示时长（14 s）再提速
    private int _orderWave = 2;          // 军令节拍所在波（波起点系统点重充=真规则）
    private int _orderAtFrame = -1;      // 军令节拍起点（-1=目标波开启后 0.5 s）
    private int _typeFrames = 42;        // 军令句逐字输入时长（0.7 s）
    private int _cardHold = 96;          // 方案卡停留（1.6 s）
    private int _carrierAtFrame = -1;    // 补线节拍（-1=确认后 4 s）
    private int _hudRefreshFrames = 15;  // 与 Main.HudRefreshFrames 同值

    // ---- 节拍状态机 ---------------------------------------------------------
    private enum Phase { MenuLoop, FastForward, Live, Done }

    private Phase _phase = Phase.MenuLoop;
    private bool _menuStopped;
    private bool _waveLogged;
    private long _lastSeenWave = 1;
    private int _waveContactFrame = -1;  // 波 1 接敌帧（cruise 提速计时起点）
    private int _orderState;             // 0=未开始 1=expand 2=typing 3=card 4=confirmed
    private int _orderSubFrame;
    private int _orderStartFrame = -1;
    private int _carrierStartFrame = -1;
    private int _maxTowerIdBeforeOrder;
    private string _orderText = "守住南门"; // 东门环位已被 Main 冒烟的首次军令占满（日志实证），南门锚点 (4,8) 空闲
    private Node? _expandDirector;
    private SystemPointLedger _pipelineLedger = null!;
    private Camera2D _camera = null!;

    // 节拍①证据遥测：挂在同一真 SupplyMonitor 上的第二个只读
    // BottleneckDetector（各自维护 episode 状态，互不影响 InfoLayersUi
    // 的那个）——断链事件帧号/塔位留档进日志（REV 用），并动态排程回拉镜头。
    private BottleneckDetector? _telemetryDetector;
    private long _telemetryLastTick = -1;
    private int _bottleneckSeen;

    // 镜头关键帧：A=塔线+东门全入镜（世界 16×24 格 ×32px，视口 11.25 格高——
    // y 8.6..23 连底座一行在内）；波 2 前推到南门取军令代建框，补线后回拉。
    private Vector2 _camAnchorA = new(8.0f * SimSnapshotBridge.CellSizePx, 17.25f * SimSnapshotBridge.CellSizePx);
    private Vector2 _camAnchorB = new(5.5f * SimSnapshotBridge.CellSizePx, 10.5f * SimSnapshotBridge.CellSizePx);
    private (int From, int To, Vector2 Target)? _camPan;

    public override void _Ready()
    {
        ParseUserArgs();
        BuildRealSceneTree();
        CacheReflection();
        AimCamera();
        if (_menuFrames <= 0)
        {
            _phase = Phase.FastForward;
        }

        GD.Print($"RECORD_START total={_totalFrames} menu={_menuFrames} ff_until={_ffUntilTick} " +
                 $"ff_rate={_ffRate} speed={_speed} tick0={_sim.CurrentTick} towers0={_sim.Towers.Count}");
    }

    /// <summary>按 scenes/main.tscn 同构搭建真节点树并触发 Main._Ready。</summary>
    private void BuildRealSceneTree()
    {
        _main = new Main
        {
            Name = "Main",
        };
        _main.SetAnchorsPreset(LayoutPreset.FullRect);
        _main.MouseFilter = MouseFilterEnum.Ignore;

        var camera = new Camera2D { Name = "WorldCamera" };
        _main.AddChild(camera);

        var debug = new Label { Name = "DebugLabel" };
        debug.Position = new Vector2(0f, 0f);
        debug.Size = new Vector2(420f, 40f);
        debug.MouseFilter = MouseFilterEnum.Ignore;
        _main.AddChild(debug);

        var ground = new MultiMeshRenderer { Name = "GroundLayer", ZIndex = 0 };
        _main.AddChild(ground);

        var units = new MultiMeshRenderer { Name = "UnitLayer", ZIndex = 10 };
        _main.AddChild(units);

        _main.AddChild(new EraTD.Audio.DingAudioPlayer { Name = "DingAudio" });

        var panelScene = GD.Load<PackedScene>("res://scenes/system_panel.tscn");
        var panel = panelScene.Instantiate<SystemPanel>();
        panel.Name = "SystemPanel";
        _main.AddChild(panel);

        AddChild(_main); // → Main._Ready（真冒烟：真 SimHost+全部导演挂载）
    }

    /// <summary>取 Main 私有字段与 SimHost internal 成员的反射缓存。</summary>
    private void CacheReflection()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        _host = typeof(Main).GetField("_host", Any)!.GetValue(_main)!;
        _hostType = _host.GetType();
        _bridge = (SimSnapshotBridge)typeof(Main).GetField("_bridge", Any)!.GetValue(_main)!;
        _groundLayer = (MultiMeshRenderer)typeof(Main).GetField("_groundLayer", Any)!.GetValue(_main)!;
        _unitLayer = (MultiMeshRenderer)typeof(Main).GetField("_unitLayer", Any)!.GetValue(_main)!;
        _panel = (SystemPanel)typeof(Main).GetField("_panel", Any)!.GetValue(_main)!;
        _sim = (Simulation)_hostType.GetProperty("Simulation", Any)!.GetValue(_host)!;
        _framebuffer = (IFramebufferProvider)_hostType.GetProperty("Framebuffer", Any)!.GetValue(_host)!;

        // 热路径委托缓存（internal 成员→强类型委托）。
        _stepHostOnce = _hostType.GetMethod("StepFrame", Any)!.CreateDelegate<Action>(_host);
        _buildHud = _hostType.GetMethod("BuildHudSnapshot", Any)!.CreateDelegate<Func<HudSnapshot>>(_host);
        _buildComp = _hostType.GetMethod("BuildWaveCompositionView", Any)!.CreateDelegate<Func<WaveCompositionView>>(_host);
        _nameOfEnemy = _hostType.GetMethod("NameOfEnemy", Any)!.CreateDelegate<Func<string, string>>(_host);
        _panelRefreshHud = typeof(SystemPanel).GetMethod("RefreshHud", Any)!.CreateDelegate<Action<HudSnapshot>>(_panel);
        _panelRefreshComp = typeof(SystemPanel).GetMethod("RefreshComposition", Any)!
            .CreateDelegate<Action<WaveCompositionView, Func<string, string>>>(_panel);
        _panelHudText = typeof(SystemPanel).GetMethod("HudText", Any)!.CreateDelegate<Func<string>>(_panel);

        // 主菜单循环导演：Main._Ready 挂的 VisualExpansionDirector（internal 类，
        // 按类型全名在子树里找实例）。
        _expandDirector = FindDescendant(_main, n => n.GetType().FullName?.Contains("VisualExpansionDirector") == true);

        // 波起点系统点重充（§11.4：每波开始补满到上限+按波花费上限重置）。
        // 表现层宿主尚未接 C-12 的逐波钩子（SimHost 只在构造时 BeginWave(1)），
        // 本驱动在模拟波号跨变时按同一规则调用公开 API——录制编排补接线，
        // 非新规则（AutopilotDirector.BeginWave 同式）。
        var pipeline = (AgentPipeline)_hostType.GetProperty("Pipeline", Any)!.GetValue(_host)!;
        _pipelineLedger = pipeline.Ledger;

        // 节拍①遥测（只读同一 monitor 的高效率曲线）。
        _telemetryDetector = new BottleneckDetector(
            new SimTickClockAdapter(_sim), _sim.SupplyMonitor);
        _telemetryDetector.BottleneckDetected += evt =>
        {
            _bottleneckSeen++;
            GD.Print($"RECORD_BOTTLENECK frame={_frame} tick={evt.Tick} tower={evt.TowerId.Value} " +
                     $"cell=({evt.TowerPosition.X},{evt.TowerPosition.Y}) efficiency={evt.Efficiency:0.00} " +
                     $"demand={evt.DemandShotsPerSecond:0.00}/s delivery={evt.DeliveryShotsPerSecond:0.00}/s");
            if (_bottleneckSeen == 1)
            {
                // 首个断链事件在镜 4 s 后再回拉塔线（红圈 TTL 20 s，收尾仍可见回程）。
                PanCamera(_frame + 240, _frame + 360, _camAnchorA);
            }
        };
    }

    /// <summary>遥测探测器的 tick 源（每帧补齐与 InfoLayersUi 同式的 catch-up）。</summary>
    private sealed class SimTickClockAdapter(Simulation sim) : IClock
    {
        public long Tick => sim.CurrentTick;
        public long NowMs => 0;
    }

    /// <summary>镜头对准演示动线：塔线（y≈21）到东门（12,12）一带（世界 16×24 格 ×32px）。</summary>
    private void AimCamera()
    {
        _camera = _main.GetNode<Camera2D>("WorldCamera");
        _camera.Position = _camAnchorA;
        _camera.Zoom = Vector2.One;
    }

    /// <summary>镜头推拉（帧区间线性缓动；beats 触发时排程）。</summary>
    private void PanCamera(int fromFrame, int toFrame, Vector2 target) => _camPan = (fromFrame, toFrame, target);

    private void ApplyCameraPan()
    {
        if (_camPan is not { } pan)
        {
            return;
        }

        if (_frame >= pan.To)
        {
            _camera.Position = pan.Target;
            _camPan = null;
            return;
        }

        if (_frame >= pan.From)
        {
            float t = (_frame - pan.From) / (float)Math.Max(1, pan.To - pan.From);
            _camera.Position = _camera.Position.Lerp(pan.Target, MathF.Min(1f, t * 0.12f + 0.02f));
        }
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        _frame++;

        switch (_phase)
        {
            case Phase.MenuLoop:
                RunMenuLoopPhase();
                return; // 演出段不步模拟（与真主菜单一致）
            case Phase.FastForward:
                RunFastForwardPhase();
                break;
            case Phase.Live:
                StepLive();
                break;
        }

        RunOrderBeat();
        RunCarrierBeat();
        RunHintJanitor();
        PumpTelemetryDetector();
        ApplyCameraPan();

        if (_frame % 60 == 0)
        {
            LogState();
        }

        if (_frame >= _totalFrames)
        {
            LogState();
            GD.Print("RECORD_DONE");
            GetTree().Quit(0);
        }
    }

    // ---- 演出段：真主菜单循环（A-01 ogv）实机播放 ---------------------------

    private void RunMenuLoopPhase()
    {
        if (_frame == 1 && _expandDirector is not null)
        {
            bool started = (bool)InvokeInternal(_expandDirector, "TryStartMenuLoop")!;
            GD.Print($"RECORD_MENU loop_started={started}");
        }

        if (_frame >= _menuFrames)
        {
            if (_expandDirector is not null)
            {
                InvokeInternal(_expandDirector, "StopMenuLoop");
            }

            _menuStopped = true;
            _phase = Phase.FastForward;
            GD.Print($"RECORD_MENU_STOPPED frame={_frame} tick={_sim.CurrentTick}");
        }
    }

    // ---- 快进段：把模拟推到波 1 接敌前（真时间压缩，非伪造状态） -------------

    private void RunFastForwardPhase()
    {
        for (int i = 0; i < _ffRate; i++)
        {
            StepHost();
        }

        RenderOneFrame();

        if (_sim.CurrentTick >= _ffUntilTick)
        {
            _phase = Phase.Live;
            GD.Print($"RECORD_FF_DONE frame={_frame} tick={_sim.CurrentTick} wave={_sim.CurrentWave}");
            LogState();
        }
    }

    // ---- 实拍段：1x/巡航逐帧步进 + 渲染回路（同 Main 主循环同序） -------------

    private void StepLive()
    {
        ObserveWaveCrossing();

        int speed = _speed;
        if (_waveContactFrame >= 0
            && _frame - _waveContactFrame > _cruiseAfterFrames
            && _sim.CurrentWave < _orderWave)
        {
            speed = _cruiseSpeed; // 波间巡航提速（真 2x 时钟档的每帧步进数）
        }

        for (int i = 0; i < speed; i++)
        {
            StepHost();
            ObserveWaveCrossing();
        }

        RenderOneFrame();

        if (!_waveLogged && _bridge.Current is { Units.Count: > 0 })
        {
            _waveLogged = true;
            _waveContactFrame = _frame;
            GD.Print($"RECORD_WAVE_CONTACT frame={_frame} tick={_sim.CurrentTick} units={_bridge.Current.Units.Count}");
        }
    }

    /// <summary>
    /// 模拟波号跨变观测：按 §11.4 规则在波起点重充系统点（表现宿主未接的
    /// C-12 逐波钩子，录制编排补接线），并在目标波开启时安排军令节拍。
    /// </summary>
    private void ObserveWaveCrossing()
    {
        long wave = _sim.CurrentWave;
        if (wave == _lastSeenWave)
        {
            return;
        }

        _lastSeenWave = wave;
        bool refilled = _pipelineLedger.BeginWave(wave, isBossWave: false);
        GD.Print($"RECORD_WAVE_BEGIN wave={wave} frame={_frame} tick={_sim.CurrentTick} " +
                 $"ledger_refilled={refilled} points={_pipelineLedger.Points}");
        if (wave >= _orderWave && _orderStartFrame < 0)
        {
            _orderStartFrame = _orderAtFrame >= 0 ? _orderAtFrame : _frame + 150;
            PanCamera(_frame, _frame + 120, _camAnchorB); // 推到南门取军令代建框
        }
    }

    /// <summary>一帧渲染回路：StepFrame 已在外层完成，此处为快照→脏块→重建。</summary>
    private void RenderOneFrame()
    {
        _bridge.SyncFrame();

        HashSet<long> flashes = CollectImpactFlashes(_bridge.Current);

        var groundChunks = _bridge.TakeDirtyGroundChunks();
        if (groundChunks.Count > 0)
        {
            _groundLayer.RebuildGroundChunks(groundChunks, _framebuffer);
        }

        var unitChunks = _bridge.TakeDirtyUnitChunks();
        if (unitChunks.Count > 0)
        {
            _unitLayer.RebuildUnitChunks(
                unitChunks, _bridge.Current!, flashes.Count > 0 ? flashes : null);
        }

        if (_frame % _hudRefreshFrames == 0)
        {
            RefreshPanel();
        }
    }

    /// <summary>HP 掉落=受击闪白（与 Main.CollectImpactFlashes 同判据；M 档密度门在请求数低位时全放行）。</summary>
    private HashSet<long> CollectImpactFlashes(SimSnapshot? snapshot)
    {
        var flashes = new HashSet<long>();
        if (snapshot is not null)
        {
            foreach (IUnit unit in snapshot.Units)
            {
                if (_prevUnitHp.TryGetValue(unit.Id.Value, out int before) && unit.Hp < before)
                {
                    flashes.Add(unit.Id.Value);
                }
            }

            _prevUnitHp.Clear();
            foreach (IUnit unit in snapshot.Units)
            {
                _prevUnitHp[unit.Id.Value] = unit.Hp;
            }
        }

        return flashes;
    }

    /// <summary>HUD 三指标+波构成刷新（真数据源：SimHost.BuildHudSnapshot/BuildWaveCompositionView）。</summary>
    private void RefreshPanel()
    {
        _panelRefreshHud(_buildHud());
        _panelRefreshComp(_buildComp(), _nameOfEnemy);
    }
    // ---- 军令代建节拍：真 SystemPanel 全链路 ---------------------------------

    private void RunOrderBeat()
    {
        if (_orderState is 4 or 0)
        {
            if (_orderState == 0 && _orderStartFrame >= 0 && _frame >= _orderStartFrame)
            {
                _maxTowerIdBeforeOrder = _sim.Towers.Count > 0
                    ? (int)_sim.Towers.Max(t => t.Id.Value)
                    : 0;
                _orderState = 1;
                _orderSubFrame = 0;
                GD.Print($"RECORD_ORDER_BEGIN frame={_frame} tick={_sim.CurrentTick} towers={_sim.Towers.Count}");
            }
            else
            {
                return;
            }
        }

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        switch (_orderState)
        {
            case 1: // 展开+军令句逐字输入（真 LineEdit）
                if (_orderSubFrame == 0)
                {
                    typeof(SystemPanel).GetMethod("Expand", Any)!.Invoke(_panel, null);
                    RefreshPanel();
                }

                _orderSubFrame++;
                if (_orderSubFrame >= 20 && _orderSubFrame <= 20 + _typeFrames)
                {
                    int typed = Math.Min(_orderText.Length, (_orderSubFrame - 20) * _orderText.Length / Math.Max(1, _typeFrames));
                    var input = (LineEdit)typeof(SystemPanel).GetProperty("OrderInput", Any)!.GetValue(_panel)!;
                    input.Text = _orderText[..typed];
                }

                if (_orderSubFrame > 20 + _typeFrames)
                {
                    var input = (LineEdit)typeof(SystemPanel).GetProperty("OrderInput", Any)!.GetValue(_panel)!;
                    input.Text = _orderText;
                    input.EmitSignal(LineEdit.SignalName.TextSubmitted, _orderText);
                    bool card = (bool)typeof(SystemPanel).GetProperty("PlanCardVisible", Any)!.GetValue(_panel)!;
                    GD.Print($"RECORD_ORDER_SUBMITTED frame={_frame} card={card}");
                    _orderState = 2;
                    _orderSubFrame = 0;
                    RefreshPanel();
                }

                break;

            case 2: // 方案卡停留（真人审阅时长）
                if (++_orderSubFrame >= _cardHold)
                {
                    var outcome = (PipelineOutcomeKind?)typeof(SystemPanel)
                        .GetMethod("ConfirmPending", Any)!.Invoke(_panel, null);
                    int towersAfter = _sim.Towers.Count;
                    string report = (string)typeof(SystemPanel).GetProperty("ReportText", Any)!.GetValue(_panel)!;
                    GD.Print($"RECORD_ORDER_CONFIRMED frame={_frame} outcome={outcome} " +
                             $"towers={_maxTowerIdBeforeOrder}->{towersAfter} report=\"{report}\"");
                    if (_carrierAtFrame < 0)
                    {
                        _carrierStartFrame = _frame + 240; // 确认后 4 s 接供给线
                    }

                    _orderState = 3;
                    _orderSubFrame = 0;
                    RefreshPanel();
                }

                break;

            case 3:
                if (++_orderSubFrame >= 150)
                {
                    // 汇报气泡在镜 2.5 s 后收起面板——把画面让给新塔+断链红圈。
                    typeof(SystemPanel).GetMethod("Collapse",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_panel, null);
                    RefreshPanel();
                    _orderState = 4;
                    GD.Print($"RECORD_PANEL_COLLAPSED frame={_frame}");
                }

                break;
        }
    }

    // ---- 补线节拍：assign-carrier 接通新塔供给（恢复弧线，真 PlayerBuild 通道） ----

    private void RunCarrierBeat()
    {
        if (_carrierStartFrame < 0 || _frame != _carrierStartFrame)
        {
            return;
        }

        // 供给源=里程碑链的 converter（SimHost 执行序：仓库1/营地2/采集3/加工4）。
        // 新塔=确认后 Id 超过节拍前最大值的塔；喂最近的一座（converter→南门往返
        // 长，恢复弧线要收得快），另一座留断链红圈在镜（节拍①对照）。
        const long converterId = 4;
        var newTowers = _sim.Towers
            .Where(t => t.Id.Value > _maxTowerIdBeforeOrder)
            .OrderBy(t => t.Id.Value)
            .Take(1) // 只喂最近的一座（converter→南门往返长，恢复弧线要收得快；另一座留红圈对照）
            .ToList();
        if (newTowers.Count == 0)
        {
            GD.Print("RECORD_CARRIER skipped=no-new-towers");
            return;
        }

        var commands = new List<SimCommand>();
        long tick = _sim.CurrentTick + 1;
        foreach (var tower in newTowers)
        {
            commands.Add(new SimCommand(tick++, SimCommandKind.PlayerBuild,
                "{\"verb\":\"assign-carrier\",\"from\":" + converterId + ",\"to\":" + tower.Id.Value + "}"));
        }

        _sim.EnqueueRange(commands);
        if (_bottleneckSeen == 0)
        {
            // 开发宿主 monitor 宽限恒 1.0（SimHost 构造 warmup=int.MaxValue，
            // InfoLayersUi 注释同证）——断链红圈在本宿主语义下不触发；回拉
            // 塔线走固定节拍（补线 10 s 后），节拍①画面=空弹头条对照+补线复射。
            PanCamera(_frame + 600, _frame + 720, _camAnchorA);
        }

        GD.Print($"RECORD_CARRIER frame={_frame} tick={_sim.CurrentTick} assigned={newTowers.Count} " +
                 $"tower_ids=[{string.Join(",", newTowers.Select(t => t.Id.Value))}]");
    }

    /// <summary>遥测探测器 catch-up（每帧补齐自上帧以来的 sim tick 数）。</summary>
    private void PumpTelemetryDetector()
    {
        if (_telemetryDetector is null)
        {
            return;
        }

        long tick = _sim.CurrentTick;
        long advanced = _telemetryLastTick < 0 ? 1 : tick - _telemetryLastTick;
        _telemetryLastTick = tick;
        for (long i = 0; i < advanced; i++)
        {
            _telemetryDetector.Tick();
        }
    }

    // ---- 提示气泡看护：替玩家点「知道了」收掉新手提示 -------------------------
    // 真实交互路径（Button.Pressed 信号）；断链因果提示（效率/供给字样）与
    // 饥饿观察窗内的任何提示不收——那是节拍①的画面证据。

    private void RunHintJanitor()
    {
        if (_frame % 45 != 0)
        {
            return;
        }

        bool starvationWindow = _orderState >= 3
            && _carrierStartFrame > 0
            && _frame < _carrierStartFrame + 180;
        if (starvationWindow)
        {
            return;
        }

        foreach (Node node in EnumerateDescendants(_main))
        {
            if (node is not Button { Text: "知道了" } button || !button.IsVisibleInTree())
            {
                continue;
            }

            string hintText = BubbleText(button);
            if (hintText.Contains("效率", StringComparison.Ordinal)
                || hintText.Contains("供给", StringComparison.Ordinal))
            {
                continue; // 断链因果提示=节拍①证据，保留在镜
            }

            GD.Print($"RECORD_HINT_DISMISSED frame={_frame} text=\"{hintText[..Math.Min(40, hintText.Length)]}\"");
            button.EmitSignal(Button.SignalName.Pressed);
            return; // 一帧最多收一个
        }
    }

    private static string BubbleText(Node button)
    {
        // 气泡结构：PanelContainer > VBox > [Label(提示文本), HBox(术语/知道了)]
        for (Node? parent = button.GetParent(); parent is not null; parent = parent.GetParent())
        {
            foreach (Node child in parent.GetChildren())
            {
                if (child is Label label && label.Text.Length > 0)
                {
                    return label.Text;
                }
            }

            if (parent is PanelContainer)
            {
                break;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node nested in EnumerateDescendants(child))
            {
                yield return nested;
            }
        }
    }

    // ---- 杂项 ----------------------------------------------------------------

    private void StepHost() => _stepHostOnce();

    private static object? InvokeInternal(Node target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, null);

    private static Node? FindDescendant(Node root, Func<Node, bool> match)
    {
        foreach (Node child in root.GetChildren())
        {
            if (match(child))
            {
                return child;
            }

            if (FindDescendant(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void LogState()
    {
        string supply = _orderState >= 3
            ? " supply=[" + string.Join(",", _sim.Towers
                .Where(t => t.Id.Value > _maxTowerIdBeforeOrder)
                .Select(t => $"#{t.Id.Value}@({t.Cell.X},{t.Cell.Y})={_sim.SupplyMonitor.EfficiencyOf(t.Id):0.00}")) + "]"
            : string.Empty;
        GD.Print($"RECORD_STATE frame={_frame} tick={_sim.CurrentTick} wave={_sim.CurrentWave} " +
                 $"towers={_sim.Towers.Count} buildings={_bridge.Current?.Buildings.Count.ToString() ?? "-"} " +
                 $"units={_bridge.Current?.Units.Count.ToString() ?? "-"} hud=\"{_panelHudText()}\"{supply}");
    }

    private void ParseUserArgs()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = arg[..eq];
            string value = arg[(eq + 1)..];
            switch (key)
            {
                case "total": _totalFrames = int.Parse(value); break;
                case "menu": _menuFrames = int.Parse(value); break;
                case "ff_until": _ffUntilTick = long.Parse(value); break;
                case "ff_rate": _ffRate = int.Parse(value); break;
                case "speed": _speed = int.Parse(value); break;
                case "order_at": _orderAtFrame = int.Parse(value); break;
                case "type_frames": _typeFrames = int.Parse(value); break;
                case "card_hold": _cardHold = int.Parse(value); break;
                case "carrier_at": _carrierAtFrame = int.Parse(value); break;
                case "cruise_speed": _cruiseSpeed = int.Parse(value); break;
                case "cruise_after": _cruiseAfterFrames = int.Parse(value); break;
                case "order_wave": _orderWave = int.Parse(value); break;
                case "order": _orderText = value; break;
            }
        }
    }
}
