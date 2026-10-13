using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Units;
using Godot;

namespace EraTD;

// T-056（C-23 建造交互薄版，F-036/F-119）：拖放建造 UI——场景=scenes/
// build_ui.tscn（本脚本唯一挂载点；Main.cs 仅追加实例化行——并行纪律：
// 与 T-055 的冲突面在 Main.cs，后提交者 rebase）。
//
// 交互链：建造栏 4 塔 icon（E1 目录驱动+类色色块）→按下拖动=矩形选格
// （单格=1×1 矩形；cells≤4+2×E 同限截断）→绿/红格预览（合法性=BuildPlan
// 草案输出，红格带共享拒绝码）→松开→BuildPlan 动作 JSON→PlayerBuildLane
// （与军师同一咽喉：PipelineExecutor.InvokeStep→ActionRegistry.Invoke→
// SimCommand 落子；不扣系统点——口径裁定见 PlayerBuildLane.cs 注释）。
// ESC=取消清预览；右键点塔=拆除模式（confirm 弹层，confirm_sell=true 走
// SellTower 同路径草案——E1/薄版注册表诚实拒绝并记 actor=player 战报）。
//
// headless（smoke）下自动跑一遍脚本化拖放/取消/红格/拆除序列并打印
// BUILD_OK（含注册表调用计数）；有头模式走真实鼠标输入，同一控制器与
// 泳道，无第二套逻辑。

/// <summary>
/// The C-23 thin build-interaction control (T-056): build bar, drag
/// rectangle preview, commit through the shared BuildPlan throat, ESC
/// cancel and right-click demolish with the mandatory confirm card.
/// </summary>
public sealed partial class BuildInteractionUi : Control
{
    private const int MapWidthCells = 16;
    private const int MapHeightCells = 24;
    private const long DemoSeed = 20261014;      // 与 Sim/Agent 测试夹具同种子族（E1 关卡形态）
    private const int AgentEra = 2;              // BuildPlan/SellTower 为 E2 动词（K-3 解锁列）——与军师测试夹具同口径
    private const float Cell = Simulation.CellSizePx;

    private static readonly ResourceKey Wood = new("era1:res-wood");

    private readonly List<ColorRect> _highlights = new();
    private readonly Dictionary<string, Button> _towerButtons = new();
    private readonly Dictionary<string, Color> _classSwatch = new()
    {
        ["pierce"] = Colors.LimeGreen,
        ["physical"] = Colors.SaddleBrown,
        ["slow"] = Colors.CornflowerBlue,
        ["support"] = Colors.Orange,
    };

    private Simulation _sim = null!;
    private PlayerBuildLane _lane = null!;
    private BuildDragController _controller = null!;
    private CountingLogSink _registrySink = null!;
    private InMemoryJournalWriter _journal = null!;
    private HBoxContainer _buildBar = null!;
    private Control _previewLayer = null!;
    private PanelContainer _demolishCard = null!;
    private Label _status = null!;
    private Label _demolishLabel = null!;
    private string? _armedTower;
    private bool _leftDrag;
    private long _demolishTargetId;

    /// <summary>Instance factory for the host (Main.cs appends one init line).</summary>
    public static BuildInteractionUi Instantiate(Control host)
    {
        var scene = GD.Load<PackedScene>("res://scenes/build_ui.tscn");
        var ui = scene.Instantiate<BuildInteractionUi>();
        host.AddChild(ui);
        return ui;
    }

    public override void _Ready()
    {
        WireScene();
        SetupCore();
        BuildBar();
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessDemo();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            switch (mouse.ButtonIndex)
            {
                case MouseButton.Left when mouse.Pressed && _armedTower is not null:
                    _leftDrag = true;
                    _controller.BeginDrag(_armedTower, CellAtMouse());
                    RepaintPreview();
                    AcceptEvent();
                    break;
                case MouseButton.Left when !_leftDrag:
                    break;
                case MouseButton.Left:
                    _leftDrag = false;
                    CommitDrag();
                    AcceptEvent();
                    break;
                case MouseButton.Right when mouse.Pressed:
                    EnterDemolishMode(CellAtMouse());
                    AcceptEvent();
                    break;
            }
        }
        else if (@event is InputEventMouseMotion && _leftDrag)
        {
            _controller.UpdateDrag(CellAtMouse());
            RepaintPreview();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            _controller.Cancel(); // ESC 清预览/取消拖放（含拆除弹层）
            HideDemolishCard();
            RepaintPreview();
            SetStatus("已取消（ESC）");
            GetViewport().SetInputAsHandled();
        }
    }

    private void WireScene()
    {
        _buildBar = GetNode<HBoxContainer>("BuildBar");
        _previewLayer = GetNode<Control>("PreviewLayer");
        _demolishCard = GetNode<PanelContainer>("DemolishCard");
        _status = GetNode<Label>("StatusLabel");
        _demolishLabel = GetNode<Label>("DemolishCard/VBox/DemolishLabel");
        GetNode<Button>("DemolishCard/VBox/Buttons/ConfirmButton").Pressed += OnDemolishConfirmed;
        GetNode<Button>("DemolishCard/VBox/Buttons/CancelButton").Pressed += HideDemolishCard;
        MouseFilter = MouseFilterEnum.Stop;
    }

    private void SetupCore()
    {
        IReadOnlyList<TowerSpec> catalog = LoadTowerCatalog();
        var enemies = EnemySpecCatalog.Parse(System.IO.File.ReadAllText(FindContentFile("enemies.json")));
        _sim = new Simulation(DemoConfig(catalog, enemies), new DemoRng(DemoSeed), DemoSeed);
        _registrySink = new CountingLogSink();
        _journal = new InMemoryJournalWriter();
        // F5：PlayerBuildLane 的 journal 契约=sequence-ordered per run（其
        // ctor 参数注释明示 SequencingJournalWriter 负责 Seq 重编号）——裸
        // 传入内存写入器会使玩家事件 Seq=0 恒不递增，违 K-8 单流有序。
        _lane = PlayerBuildLane.OverSimulation(
            _sim, DemoConfig(catalog, enemies), () => AgentEra, new SequencingJournalWriter(_journal), _registrySink);
        _controller = new BuildDragController(_lane.View, () => AgentEra);
        SetStatus("建栏选塔 → 左键拖矩形 → 松开建造（ESC 取消 / 右键拆除）");
    }

    private void BuildBar()
    {
        foreach (TowerSpec spec in LoadTowerCatalog())
        {
            var button = new Button
            {
                Text = $"{spec.Id.Split(':')[^1]}\n{spec.CostTotal:0}",
                CustomMinimumSize = new Vector2(72, 48),
                FocusMode = FocusModeEnum.None,
            };
            button.Pressed += () => ArmTower(spec.Id, button);
            _buildBar.AddChild(button);
            _towerButtons[spec.Id] = button;
        }
    }

    private void ArmTower(string towerId, Button button)
    {
        _armedTower = towerId;
        foreach (KeyValuePair<string, Button> pair in _towerButtons)
        {
            pair.Value.Modulate = pair.Key == towerId
                ? _classSwatch.TryGetValue(TowerClassOf(towerId), out Color swatch) ? swatch : Colors.White
                : Colors.White with { A = 0.6f };
        }

        SetStatus($"已选 {towerId}——地图上左键拖矩形（≤{PlayerBuildLane.MaxBuildCells(AgentEra)} 格）");
    }

    private void CommitDrag()
    {
        BuildDragCommit? commit = _controller.Commit();
        RepaintPreview();
        if (commit is null)
        {
            SetStatus("无可建格（红格）——未发送任何动作");
            return;
        }

        PlayerBuildOutcome outcome = _lane.Build(commit.TowerId, commit.Cells);
        SetStatus(outcome.Result.Status == ActionResultStatus.Ok
            ? $"已建 {outcome.CellsPlaced} 座（走 ActionRegistry→SimCommand 同一咽喉）"
            : $"建造被拒：{outcome.Result.ErrorMessage}");
    }

    private void EnterDemolishMode(CellCoord cell)
    {
        foreach (SimTower tower in _sim.Towers)
        {
            if (!tower.Cell.Equals(cell))
            {
                continue;
            }

            _demolishTargetId = tower.Id.Value;
            _demolishLabel.Text = $"拆除目标塔 #{tower.Id.Value}？\nSellTower 需显式 confirm_sell=true（危险位，永不自动允许）";
            _demolishCard.Visible = true;
            return;
        }

        SetStatus($"({cell.X},{cell.Y}) 无可拆塔");
    }

    private void OnDemolishConfirmed()
    {
        PlayerBuildOutcome outcome = _lane.Sell(_demolishTargetId, confirmSell: true);
        SetStatus(outcome.Result.Status == ActionResultStatus.Ok
            ? $"已拆除 #{_demolishTargetId}"
            : $"SellTower 走同一注册表（诚实拒绝）：{outcome.Result.ErrorMessage}");
        HideDemolishCard();
    }

    private void HideDemolishCard() => _demolishCard.Visible = false;

    private void RepaintPreview()
    {
        foreach (ColorRect rect in _highlights)
        {
            rect.QueueFree();
        }

        _highlights.Clear();
        foreach (BuildDragPreviewCell cell in _controller.Preview().Cells)
        {
            var rect = new ColorRect
            {
                Position = new Vector2(cell.Cell.X * Cell, cell.Cell.Y * Cell),
                Size = new Vector2(Cell, Cell),
                Color = cell.Buildable
                    ? new Color(0.2f, 1.0f, 0.3f, 0.45f)
                    : new Color(1.0f, 0.2f, 0.2f, 0.45f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _previewLayer.AddChild(rect);
            _highlights.Add(rect);
        }
    }

    private CellCoord CellAtMouse()
    {
        Vector2 world = GetGlobalMousePosition(); // camera-applied world coordinates
        return new CellCoord(
            (int)Math.Floor(world.X / Cell),
            (int)Math.Floor(world.Y / Cell));
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string TowerClassOf(string towerId) => towerId switch
    {
        "era1:tower-wood-arrow" => "pierce",
        "era1:tower-rockfall" => "physical",
        "era1:tower-spike-trap" => "slow",
        "era1:tower-campfire" => "support",
        _ => "pierce",
    };

    // ---- headless smoke（脚本化同一控制器/泳道——无第二套逻辑） ----

    private void RunHeadlessDemo()
    {
        int okBuilds = 0;
        int rejected = 0;

        // 1) 单格拖放：锚定首个可建格 → 松开 → 同一咽喉落子
        CellCoord green = FirstBuildableCell();
        _controller.BeginDrag("era1:tower-wood-arrow", green);
        _controller.UpdateDrag(green); // 单格=1×1 矩形
        okBuilds += CommitDemo("era1:tower-wood-arrow", green);

        // 2) 批量矩形：3×3 超 era2 上限（4+2×2=8）→ 截断后批量落子
        _controller.BeginDrag("era1:tower-rockfall", green with { X = green.X + 1, Y = green.Y + 1 });
        _controller.UpdateDrag(green with { X = green.X + 3, Y = green.Y + 3 });
        okBuilds += CommitDemo("era1:tower-rockfall", green with { X = green.X + 1, Y = green.Y + 1 });

        // 3) ESC 取消：拖放中取消，零动作零调用
        _controller.BeginDrag("era1:tower-wood-arrow", green with { X = green.X + 2 });
        _controller.UpdateDrag(green with { X = green.X + 2, Y = green.Y + 2 });
        _controller.Cancel();
        int escIdle = _controller.State == BuildDragState.Idle ? 1 : 0;

        // 4) pit#6 红格：步进到首敌在图，对敌格建造→同一咽喉诚实拒绝
        StepUntilEnemies(maxTicks: 6_000);
        Unit? enemy = FirstLivingEnemy();
        if (enemy is not null)
        {
            var enemyCell = new CellCoord(
                (int)Math.Floor(enemy.X / Simulation.CellSizePx),
                (int)Math.Floor(enemy.Y / Simulation.CellSizePx));
            PlayerBuildOutcome refused = _lane.Build("era1:tower-wood-arrow", new[] { enemyCell });
            rejected += refused.Result.Status == ActionResultStatus.Ok ? 0 : 1;
        }

        // 5) 右键拆除草案：confirm_sell=true → SellTower 同一注册表（薄版诚实拒绝）
        PlayerBuildOutcome sell = _sim.Towers.Count > 0
            ? _lane.Sell(_sim.Towers[0].Id.Value, confirmSell: true)
            : _lane.Sell(1, confirmSell: true);
        int sellRefused = sell.Result.Status == ActionResultStatus.Ok ? 0 : 1;

        GD.Print(
            $"BUILD_OK registry_invocations={_registrySink.Lines} ok_builds={okBuilds} " +
            $"towers_placed={_sim.Towers.Count} throat_rejections={rejected} esc_idle={escIdle} " +
            $"sell_refusals={sellRefused} player_journal_events={_journal.PlayerEvents} " +
            $"player_journal_last_seq={_journal.LastPlayerSeq} " +
            $"tick={_sim.CurrentTick} state_hash={_sim.ComputeStateHash()}");
    }

    private int CommitDemo(string towerId, CellCoord anchor)
    {
        BuildDragCommit? commit = _controller.Commit();
        if (commit is null)
        {
            return 0;
        }

        PlayerBuildOutcome outcome = _lane.Build(towerId, commit.Cells);
        return outcome.Result.Status == ActionResultStatus.Ok ? 1 : 0;
    }

    private CellCoord FirstBuildableCell()
    {
        for (int y = 0; y < MapHeightCells; y++)
        {
            for (int x = 0; x < MapWidthCells; x++)
            {
                var cell = new CellCoord(x, y);
                if (_lane.View.IsBuildable(cell, out _))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("demo 地图无可建格（内容/种子漂移）");
    }

    private void StepUntilEnemies(int maxTicks)
    {
        for (int i = 0; i < maxTicks && _sim.Units.Count == 0; i++)
        {
            _sim.Step();
        }
    }

    private Unit? FirstLivingEnemy()
    {
        foreach (Unit unit in _sim.Units)
        {
            if (unit.Alive)
            {
                return unit;
            }
        }

        return null;
    }

    // ---- 内容装载（仓库相对向上探测，与 EraCore.Tests 同模式；RL9 无盘符路径） ----

    private static string FindContentFile(string fileName)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = System.IO.Path.Combine(dir.FullName, "game", "content", "era1", fileName);
            if (System.IO.File.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new System.IO.FileNotFoundException(
            $"未找到 game/content/era1/{fileName}（headless 冒烟须在仓库内运行）");
    }

    private static IReadOnlyList<TowerSpec> LoadTowerCatalog() =>
        TowerSpec.LoadCollection(System.IO.File.ReadAllText(FindContentFile("towers.json")));

    private static SimulationConfig DemoConfig(IReadOnlyList<TowerSpec> catalog, EnemySpecCatalog enemies)
    {
        var entries = new[]
        {
            new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1),
        };
        return new SimulationConfig(
            MapWidthCells,
            MapHeightCells,
            ForestRatio: 0.10,
            RockRatio: 0.05,
            WaterRatio: 0.0,
            BaseCell: new CellCoord(8, 23),
            EntryCells: entries,
            TowerCatalog: catalog,
            Enemies: enemies,
            Waves: new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
                BatchesPerWave: 4,
                EntryCells: entries),
            InitialStock: new List<KeyValuePair<ResourceKey, double>> { new(Wood, 1000.0) },
            WarehouseCell: new CellCoord(2, 14),
            Costs: new BuildCostPolicy(Wood));
    }

    /// <summary>SplitMix64 演示种子流（K-1 端口适配；headless 冒烟确定性）。</summary>
    private sealed class DemoRng(long seed) : IRng
    {
        private ulong _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            long span = maxValue - (long)minValue;
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

    /// <summary>注册表诊断计数 sink——每条 verdict 行=一次 ActionRegistry.Invoke。</summary>
    private sealed class CountingLogSink : ILogSink
    {
        public int Lines { get; private set; }

        public void Write(LogLevel level, long tick, string message) => Lines++;
    }

    /// <summary>内存 K-8 sink（headless 冒烟计数；正式宿主换 JSONL 落盘写入器）。
    /// 只见经 SequencingJournalWriter 重编号后的事件（F5）——lastSeq 供冒烟
    /// 观测玩家流 Seq 递增，不再出现裸 Seq=0。</summary>
    private sealed class InMemoryJournalWriter : IJournalWriter
    {
        public int PlayerEvents { get; private set; }

        public long LastPlayerSeq { get; private set; }

        public void Append(JournalEvent evt)
        {
            if (evt.PayloadJson.Contains("\"Actor\":\"player\"", StringComparison.Ordinal))
            {
                PlayerEvents++;
                LastPlayerSeq = evt.Seq;
            }
        }
    }
}
