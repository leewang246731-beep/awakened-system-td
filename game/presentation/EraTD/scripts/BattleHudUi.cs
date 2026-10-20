using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using EraCore.Sim;
using Godot;

namespace EraTD;

// T-163（W10 C 批/F-125 战斗 HUD 图标化）战斗 HUD 图标化总装（窗口车道；
// headless 不实例化——判据零改动纪律，图标装载存在性由
// <see cref="ProbeIconCoverage"/> 静态探针承担）。四面：
//   ①顶部资源条：当前时代资源 icon+数字（era 装载后按 era.json resources
//     渲染行；icon 48px 档位，数字右贴；数量从战役会话 sim 的 ResourceStock
//     活体读——CS-1.5 UI 永不写模拟）；
//   ②波次预告横幅：下波敌人构成 → 敌 icon 行（每类 icon×数量徽记），与
//     SystemPanel 既有文字预告并存增强（零回退）；boss 波横幅特殊描边；
//   ③暂停/倍速图标化按钮（⏸/1×/2×/3×）：按下=合成 InputEventKey 走
//     BuildInteractionUi._UnhandledKeyInput 既有键链（与玩家敲空格/1-2-3
//     同一条链——零第二套语义，ShotHook.EmitWheel 真事件先例）；
//   ④军师面板时代皮肤+立绘绑定驱动：进局按关时代调 SystemPanel.
//     ApplyEraTheme（立绘双条件重绑）+VisualExpansionDirector.ApplyEraSkin
//     （palettes 同源），退出还原菜单态。
// 绑定律（SessionBattlefieldViewport.Bind 同律）：sim 引用+eraIndex 双条件
// 重绑；每拍幂等（引用相等零动作）；退出=Release（隐藏+面板还原）。

/// <summary>
/// The T-163 battle-HUD iconification control: top resource strip (era
/// resource icons + live amounts), next-wave preview banner (enemy icon ×
/// count badges, boss-wave outline), iconized pause/speed buttons (synthetic
/// key events into the EXISTING key lane), and the advisor-panel era
/// skin/portrait binding drive.
/// </summary>
public sealed partial class BattleHudUi : Control
{
    private const int RefreshFrames = 15; // HUD 刷新节拍（0.5 s 仿真时——SystemPanel/CampaignMenuUi 同拍）
    private const int ResourceIconPx = HudIconDecisions.ResourceIconPx;
    private const int EnemyIconPx = HudIconDecisions.EnemyIconPx;

    private readonly Main _main;

    // ---- 绑定面（双条件重绑） ------------------------------------------------
    private Simulation? _boundSim;
    private int _boundEra;

    // ---- ①顶部资源条 ---------------------------------------------------------
    private PanelContainer _resourceBar = null!;
    private HBoxContainer _resourceRow = null!;
    private readonly Dictionary<string, (TextureRect Icon, Label Amount)> _resourceCells = new();

    // ---- ②波次预告横幅 --------------------------------------------------------
    private PanelContainer _waveBanner = null!;
    private Label _waveTitle = null!;
    private HBoxContainer _waveRow = null!;

    // ---- ③暂停/倍速按钮 -------------------------------------------------------
    private readonly Dictionary<Key, Button> _paceButtons = new();

    private BattleHudUi(Main main)
    {
        _main = main;
    }

    /// <summary>
    /// 窗口车道挂载（Main 窗口分支；headless 分支不挂——冒烟判据零改动）。
    /// 建面后整组隐藏，战役会话 Bind 时才显示。
    /// </summary>
    internal static BattleHudUi Instantiate(Main main)
    {
        var ui = new BattleHudUi(main) { Name = "BattleHudUi" };
        main.AddChild(ui); // _Ready builds every surface
        return ui;
    }

    public override void _Ready()
    {
        ZIndex = 21; // SystemPanel(20) 之上、塔详情卡(25)之下——HUD 不藏入世界层
        MouseFilter = MouseFilterEnum.Ignore; // 整组只读式呈现；交互件各自 Stop
        BuildResourceBar();
        BuildWaveBanner();
        BuildPaceBar();
        BuildExitButton();
        Hide();
    }

    /// <summary>
    /// 退出本关 ✕ 钮（T-163 进局收面板后的退出闭环）：真信号驱动
    /// CampaignMenuUi 的「退出本关」按钮链（存档续玩——与玩家在选关面板
    /// 点击同一条链，禁第二套退出逻辑）。
    /// </summary>
    private void BuildExitButton()
    {
        var exit = new Button
        {
            Text = "✕",
            TooltipText = "退出本关（存档续玩）",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(24f, 24f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        exit.AddThemeFontSizeOverride("font_size", 12);
        exit.Pressed += () => _main.CampaignUi?.PressExitLevel();
        AddChild(exit);
        exit.Position = new Vector2(610f, 70f);
    }

    // ------------------------------------------------------------- ①资源条 --

    private void BuildResourceBar()
    {
        _resourceBar = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _resourceBar.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _resourceBar.AddThemeConstantOverride("margin_left", 6);
        _resourceBar.AddThemeConstantOverride("margin_right", 6);
        _resourceBar.AddThemeConstantOverride("margin_top", 3);
        _resourceBar.AddThemeConstantOverride("margin_bottom", 3);
        _resourceRow = new HBoxContainer();
        _resourceRow.AddThemeConstantOverride("separation", 10);
        _resourceBar.AddChild(_resourceRow);
        AddChild(_resourceBar);
        _resourceBar.Position = new Vector2(8f, 68f); // HUD 指标条(46) 之下一行
    }

    // ------------------------------------------------------- ②波次预告横幅 --

    private void BuildWaveBanner()
    {
        _waveBanner = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _waveBanner.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _waveBanner.AddThemeConstantOverride("margin_left", 6);
        _waveBanner.AddThemeConstantOverride("margin_right", 6);
        _waveBanner.AddThemeConstantOverride("margin_top", 3);
        _waveBanner.AddThemeConstantOverride("margin_bottom", 3);

        var box = new VBoxContainer();
        _waveTitle = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _waveTitle.AddThemeFontSizeOverride("font_size", 11);
        _waveTitle.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _waveRow = new HBoxContainer();
        _waveRow.AddThemeConstantOverride("separation", 6);
        box.AddChild(_waveTitle);
        box.AddChild(_waveRow);
        _waveBanner.AddChild(box);
        AddChild(_waveBanner);
        _waveBanner.Position = new Vector2(430f, 100f); // 分区/人格 chip 列(438) 之下
    }

    // ------------------------------------------------------- ③暂停/倍速按钮 --

    private void BuildPaceBar()
    {
        _paceBarRoot = new HBoxContainer();
        _paceBarRoot.AddThemeConstantOverride("separation", 4);
        AddPaceButton(Key.Space, "⏸", "暂停/恢复（空格）");
        AddPaceButton(Key.Key1, "1×", "常速（1）");
        AddPaceButton(Key.Key2, "2×", "双倍速（2）");
        AddPaceButton(Key.Key3, "3×", "三倍速（3）");
        AddChild(_paceBarRoot);
        // 底部右侧（建造栏在底部左中——build_ui.tscn；StatusLabel 全宽其上）
        _paceBarRoot.Position = new Vector2(640f - 4 * 26f - 8f, 360f - 34f);
    }

    private HBoxContainer _paceBarRoot = null!;

    /// <summary>
    /// 一个节奏按钮：按下=合成 InputEventKey（pressed+echo off）入真输入链
    /// ——BuildInteractionUi._UnhandledKeyInput 消费（与玩家敲键同一条链，
    /// 暂停/变速/暂存冲刷语义零回退；禁本地直调 PauseClock——无第二套逻辑）。
    /// </summary>
    private void AddPaceButton(Key key, string glyph, string tooltip)
    {
        var button = new Button
        {
            Text = glyph,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
            ToggleMode = false,
            CustomMinimumSize = new Vector2(26f, 24f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += () => Input.ParseInputEvent(new InputEventKey
        {
            Keycode = key,
            Pressed = true,
            Echo = false,
        });
        _paceBarRoot.AddChild(button);
        _paceButtons[key] = button;
    }

    // ------------------------------------------------------------- 绑定律 --

    /// <summary>
    /// 战役会话绑定（CampaignMenuUi.PumpBattlefield 每帧喂）：sim 引用或
    /// eraIndex 变化=整面重绑——资源行按新时代 era.json resources 重建、
    /// 军师面板时代皮肤/立绘按新 era 应用（双条件重绑律）。
    /// </summary>
    internal void Bind(CampaignRun run, int eraIndex)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (ReferenceEquals(_boundSim, run.Session.Sim) && _boundEra == eraIndex)
        {
            return;
        }

        _boundSim = run.Session.Sim;
        _boundEra = eraIndex;
        RebuildResourceRow(eraIndex);
        ApplyAdvisorEraSkin(eraIndex);
        Show();
        GD.Print(
            $"BATTLE_HUD bind era={eraIndex} resources={_resourceCells.Count} " +
            $"icon_hits={ResourceIconCoverage.Hit}/{ResourceIconCoverage.Total} " +
            $"portrait={_main.AdvisorPanel.PortraitLoaded} skin={_main.ExpandDirector is not null}");
    }

    /// <summary>退出战役会话=Release（整组隐藏+军师面板还原菜单态）.</summary>
    internal void Release()
    {
        _boundSim = null;
        _boundEra = 0;
        Hide();
        _main.AdvisorPanel.ResetEraTheme();
    }

    /// <summary>当前绑定时代（探针；0=未绑）.</summary>
    internal int BoundEra => _boundSim is null ? 0 : _boundEra;

    /// <summary>
    /// 布局探针（--shot battle-hud 取景诊断）：各面全局矩形+相机事实——
    /// 窗口车道布局排障的客观数据（GlobalRect 带画布→窗口缩放）。
    /// </summary>
    internal void PrintLayoutProbe()
    {
        Camera2D? camera = GetViewport().GetCamera2D();
        GD.Print(
            $"HUD_LAYOUT resource={_resourceBar.GetGlobalRect()} banner={_waveBanner.GetGlobalRect()} " +
            $"pace={( _paceBarRoot is null ? new Rect2() : _paceBarRoot.GetGlobalRect())} " +
            $"visible={Visible} camera={(camera is null ? "null" : camera.Position.ToString())} " +
            $"camera_name={(camera is null ? "-" : camera.Name)}");
    }

    /// <summary>资源条当前 icon 命中数/总行数（装载覆盖观测探针）.</summary>
    internal (int Hit, int Total) ResourceIconCoverage
    {
        get
        {
            int hit = 0;
            foreach ((TextureRect icon, _) in _resourceCells.Values)
            {
                if (icon.Texture is not null)
                {
                    hit++;
                }
            }

            return (hit, _resourceCells.Count);
        }
    }

    /// <summary>资源行重建：era.json resources（数据序）→ icon 48px 档+数字右贴.</summary>
    private void RebuildResourceRow(int eraIndex)
    {
        foreach (Node child in _resourceRow.GetChildren())
        {
            _resourceRow.RemoveChild(child);
            child.QueueFree();
        }

        _resourceCells.Clear();
        foreach ((string id, string nameZh) in LoadEraResources(eraIndex))
        {
            var cell = new HBoxContainer();
            cell.AddThemeConstantOverride("separation", 4);

            // icon 48px 档位（缺图=时代色底+名首字回退块——优雅降级不阻塞）
            var icon = new TextureRect
            {
                CustomMinimumSize = new Vector2(ResourceIconPx, ResourceIconPx),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Texture = LoadIconOrSwatch(HudIconDecisions.ResourceIconPath(id), EraColor($"e{eraIndex}"));

            // 数字右贴（icon 左、数量右——任务卡口径）
            var amount = new Label
            {
                Text = "0",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            amount.AddThemeFontSizeOverride("font_size", 14);
            amount.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
            var stack = new VBoxContainer();
            stack.AddThemeConstantOverride("separation", 0);
            stack.Alignment = BoxContainer.AlignmentMode.Center;
            stack.AddChild(amount);
            cell.AddChild(icon);
            cell.AddChild(stack);
            cell.TooltipText = nameZh;
            _resourceRow.AddChild(cell);
            _resourceCells[id] = (icon, amount);
        }
    }

    /// <summary>军师面板时代皮肤+立绘（era 装载面：palettes 同源色+九宫皮肤+立绘位）.</summary>
    private void ApplyAdvisorEraSkin(int eraIndex)
    {
        string eraKey = $"e{Math.Clamp(eraIndex, 1, 6)}";
        _main.AdvisorPanel.ApplyEraTheme(EraColor(eraKey), eraIndex);
        _ = _main.ExpandDirector?.ApplyEraSkin(_main.AdvisorPanel.ExpandSkinTarget, eraKey);
    }

    /// <summary>时代主色（visual-assets.json palettes 同源，经 ExpandDirector——别新造根解析）.</summary>
    private Color EraColor(string eraKey) =>
        _main.ExpandDirector?.EraColor(eraKey) ?? HoloSkin.EdgeCyan;

    // ------------------------------------------------------------- 每拍刷新 --

    /// <summary>
    /// 每拍刷新（CampaignMenuUi.PumpBattlefield %15 喂）：资源数字=会话 sim
    /// ResourceStock 活体读；预告横幅=第三指标全口径视图的 icon 行+boss 描边；
    /// 节奏按钮高亮=BuildInteractionUi.PaceFact 镜像（只读镜像，不持有钟）。
    /// </summary>
    internal void RefreshPulse(CampaignAdvisorBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (_boundSim is null)
        {
            return;
        }

        // ①资源数字
        foreach ((string id, (TextureRect _, Label amount)) in _resourceCells)
        {
            double value = _boundSim.Stock.AmountOf(new ResourceKey(id));
            amount.Text = value >= 1000 ? $"{value / 1000d:0.#}k" : $"{Math.Floor(value):0}";
        }

        // ②波次预告横幅（敌 icon×数量徽记；boss 波描边）
        WaveCompositionView view = board.BuildWaveCompositionView();
        RenderWavePreview(view);
        _main.AdvisorPanel.RefreshComposition(view, board.NameOfEnemy); // 既有文字预告同拍增强（零回退）

        // ③节奏按钮态镜像
        BuildInteractionUi? build = BuildUi();
        if (build is null)
        {
            return;
        }

        (bool paused, int speed) = build.PaceFact();
        foreach (KeyValuePair<Key, Button> pair in _paceButtons)
        {
            bool active = pair.Key == Key.Space ? paused : !paused && (int)pair.Key - (int)Key.Key1 + 1 == speed;
            pair.Value.SelfModulate = active
                ? new Color(HoloSkin.EdgeCyan, 0.95f)
                : Colors.White with { A = 0.62f };
        }
    }

    /// <summary>波次预告横幅渲染：标题（下波序号）+每类 icon×数量徽记行+boss 描边.</summary>
    private void RenderWavePreview(WaveCompositionView view)
    {
        _waveTitle.Text = $"◆ 下波 W{view.NextWave:00} 预告";
        foreach (Node child in _waveRow.GetChildren())
        {
            _waveRow.RemoveChild(child);
            child.QueueFree();
        }

        bool boss = false;
        foreach (HudIconDecisions.WavePreviewEntry entry in HudIconDecisions.WavePreviewEntries(
                     view.NextWavePreview.Select(row => (row.EnemyId, row.Count))))
        {
            boss |= entry.IsBoss;
            var cell = new VBoxContainer();
            cell.AddThemeConstantOverride("separation", 0);
            var icon = new TextureRect
            {
                CustomMinimumSize = new Vector2(EnemyIconPx, EnemyIconPx),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Texture = LoadIconOrSwatch(HudIconDecisions.EnemyIconPath(entry.EnemyId), HoloSkin.EdgeCyan);
            if (entry.IsBoss)
            {
                icon.Modulate = new Color(1f, 0.82f, 0.36f); // boss 类金相
            }

            var count = new Label
            {
                Text = $"×{entry.Count}",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            count.AddThemeFontSizeOverride("font_size", 10);
            count.AddThemeColorOverride("font_color", entry.IsBoss
                ? new Color(1f, 0.82f, 0.36f)
                : HoloSkin.TextDimCyan);
            cell.AddChild(icon);
            cell.AddChild(count);
            _waveRow.AddChild(cell);
        }

        // boss 波横幅特殊描边（金边；常规=全息青默认框）
        var box = HoloSkin.BuildHoloBox();
        if (boss)
        {
            box.BorderColor = new Color(1f, 0.82f, 0.36f, 0.95f);
            box.BorderWidthLeft = box.BorderWidthRight = 2;
            box.BorderWidthTop = box.BorderWidthBottom = 2;
        }

        _waveBanner.AddThemeStyleboxOverride("panel", box);
    }

    // ------------------------------------------------------------- 装载面 --

    /// <summary>res:// icon 装载（缺图=null——调用方优雅回退，不抛不阻）.</summary>
    private static Texture2D? LoadIcon(string resPath) =>
        ResourceLoader.Exists(resPath) ? GD.Load<Texture2D>(resPath) : null;

    /// <summary>icon 或缺图回退（时代色底+白点纹——类色色块语义，LevelCardDecisions 回退同族）.</summary>
    private static Texture2D LoadIconOrSwatch(string resPath, Color swatch)
    {
        Texture2D? texture = LoadIcon(resPath);
        if (texture is not null)
        {
            return texture;
        }

        int px = 24; // 回退块内部分辨率（展示位拉伸）
        var image = Image.CreateEmpty(px, px, false, Image.Format.Rgba8);
        image.Fill(new Color(swatch, 0.45f));
        image.SetPixel(4, 4, Colors.White);
        image.SetPixel(5, 4, Colors.White);
        return ImageTexture.CreateFromImage(image); // 生成轨缺图——落地后随重绑自动换真图
    }

    /// <summary>建造泳道节点解析（节奏态镜像的数据源；挂树后按名可达）.</summary>
    private BuildInteractionUi? BuildUi() =>
        _main.GetNodeOrNull<BuildInteractionUi>("BuildInteractionUi");

    /// <summary>era.json 资源行装载（内容侧单一数据源；repo walk-up 先例）.</summary>
    private static IReadOnlyList<(string Id, string NameZh)> LoadEraResources(int eraIndex)
    {
        string relative = System.IO.Path.Combine("game", "content", $"era{eraIndex}");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return HudIconDecisions.EraResources(
                    System.IO.File.ReadAllText(System.IO.Path.Combine(probe, "era.json")),
                    $"era{eraIndex}/era.json");
            }

            dir = dir.Parent!;
        }

        return Array.Empty<(string, string)>(); // 仓库不可达（极端裸态）——空行不阻塞战斗
    }

    // --------------------------------------------- headless 图标装载探针 --

    /// <summary>
    /// 图标装载覆盖探针（headless 判据扩展——只加不改旧判据）：92 图标三族
    /// +六立绘的 res:// 存在性（ResourceLoader.Exists——.import 补齐后为真；
    /// 探针在主场景装载后调用，此时图标已 import）。立绘缺图属并行生成中
    /// 的主管素材轨——计数如实上报，不作门禁（缺图=优雅回退纪律）。
    /// </summary>
    internal static (int Towers, int Enemies, int Resources, int Portraits) ProbeIconCoverage(
        IReadOnlyList<string> towerIds, IReadOnlyList<string> enemyIds, IReadOnlyList<string> resourceIds)
    {
        int towers = towerIds.Count(id => ResourceLoader.Exists(HudIconDecisions.TowerIconPath(id)));
        int enemies = enemyIds.Count(id => ResourceLoader.Exists(HudIconDecisions.EnemyIconPath(id)));
        int resources = resourceIds.Count(id => ResourceLoader.Exists(HudIconDecisions.ResourceIconPath(id)));
        int portraits = 0;
        for (int era = 1; era <= 6; era++)
        {
            if (ResourceLoader.Exists(HudIconDecisions.AdvisorPortraitPath(era)))
            {
                portraits++;
            }
        }

        return (towers, enemies, resources, portraits);
    }
}
