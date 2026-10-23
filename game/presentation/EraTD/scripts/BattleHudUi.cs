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
    /// 退出本关钮（T-163 进局收面板后的退出闭环）：真信号驱动
    /// CampaignMenuUi 的「退出本关」按钮链（存档续玩——与玩家在选关面板
    /// 点击同一条链，禁第二套退出逻辑）。T-164：ctrl-close 图标化（缺图=
    /// ✕ 文字符号回退——装载层存在性探针另报）。
    /// </summary>
    private void BuildExitButton()
    {
        var exit = new Button
        {
            Text = "✕",
            TooltipText = "退出本关（存档续玩）",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(24f, 24f), // ci-allow: R7 T-163 表现层控件档位 px
            MouseFilter = MouseFilterEnum.Stop,
        };
        exit.AddThemeFontSizeOverride("font_size", 12);
        UiFeel.Iconify(exit, "ctrl-close"); // T-164 控件图标组（缺图=文字符号优雅回退）
        UiFeel.AttachCues(exit);            // T-164 hover/press 音效（真叮通路）
        exit.Pressed += () => _main.CampaignUi?.PressExitLevel();
        AddChild(exit);
        exit.Position = new Vector2(610f, 70f); // ci-allow: R7 T-163 HUD 右上退出钮锚位（画布域）
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
        _waveTitle.AddThemeFontSizeOverride("font_size", 12); // T-165：预告标题 11→12（小字族）
        _waveTitle.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        UiFeel.Outline(_waveTitle, 3); // T-164：波次预告标题描边（可读性同律）
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
        AddPaceButton(Key.Space, "⏸", "ctrl-pause", "暂停/恢复（空格）");
        AddPaceButton(Key.Key1, "1×", "ctrl-speed-1x", "常速（1）");
        AddPaceButton(Key.Key2, "2×", "ctrl-speed-2x", "双倍速（2）");
        AddPaceButton(Key.Key3, "3×", "ctrl-speed-3x", "三倍速（3）");
        AddChild(_paceBarRoot);
        // 底部右侧（建造栏在底部左中——build_ui.tscn；StatusLabel 全宽其上）
        _paceBarRoot.Position = new Vector2(640f - 4 * 26f - 8f, 360f - 34f); // ci-allow: R7 T-163 表现层锚位算式（画布域 640×360）
    }

    private HBoxContainer _paceBarRoot = null!;

    /// <summary>
    /// 一个节奏按钮：按下=合成 InputEventKey（pressed+echo off）入真输入链
    /// ——BuildInteractionUi._UnhandledKeyInput 消费（与玩家敲键同一条链，
    /// 暂停/变速/暂存冲刷语义零回退；禁本地直调 PauseClock——无第二套逻辑）。
    /// T-164：节奏四键图标化（ctrl-pause/speed-1x~3x——缺图=文字符号回退）。
    /// </summary>
    private void AddPaceButton(Key key, string glyph, string iconName, string tooltip)
    {
        var button = new Button
        {
            Text = glyph,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
            ToggleMode = false,
            CustomMinimumSize = new Vector2(26f, 24f), // ci-allow: R7 T-163 表现层控件档位 px
            MouseFilter = MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", 12);
        UiFeel.Iconify(button, iconName); // T-164 控件图标组（缺图=⏸/1× 文字符号优雅回退）
        UiFeel.AttachCues(button);        // T-164 hover/press 音效（真叮通路）
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
    /// 军师面板时代皮肤/立绘按新 era 应用、HUD 面板底换时代色框（T-164：
    /// 资源条/预告横幅边框随时代——HoloSkin.BuildHoloBox(era) 缓存实例，
    /// 同拍复用不每拍新造；退出还原中性青）。双条件重绑律。
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
        _waveSignature = null; // T-164 脏守卫：换局=波构成必重渲染
        _waveBossState = false; // T-165：预告框 boss 态位复位（Bind 落非 boss 框）
        RebuildResourceRow(eraIndex);
        ApplyAdvisorEraSkin(eraIndex);
        // T-165（53 号文 P3-1）：时代皮肤切换 0.2 s 色彩过渡——live 盒插值到
        // 目标缓存盒（资源条全亮/预告框降亮变体——P3-3 让位资源条）。
        TransitionPanelSkin(_resourceBar, EraBox(eraIndex, boss: false, dim: false));
        TransitionPanelSkin(_waveBanner, EraBox(eraIndex, boss: false, dim: true));
        Show();
        GD.Print(
            $"BATTLE_HUD bind era={eraIndex} resources={_resourceCells.Count} " +
            $"icon_hits={ResourceIconCoverage.Hit}/{ResourceIconCoverage.Total} " +
            $"portrait={_main.AdvisorPanel.PortraitLoaded} skin={_main.ExpandDirector is not null}");
    }

    /// <summary>退出战役会话=Release（整组隐藏+军师面板还原菜单态+HUD 框还原中性青）.</summary>
    internal void Release()
    {
        _boundSim = null;
        _boundEra = 0;
        _waveSignature = null; // 再进局波预告按新构成重渲染（脏守卫复位）
        Hide();
        _resourceBar.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _waveBanner.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _main.AdvisorPanel.ResetEraTheme();
    }

    // —— T-164 时代框缓存（(era, boss, dim) 键——同拍复用不每拍新造） ——

    private readonly Dictionary<(int Era, bool Boss, bool Dim), StyleBoxFlat> _eraBoxes = new();

    /// <summary>
    /// 时代色 HUD 框（boss 波=金边变体；dim=预告框降亮变体——T-165 53 号文
    /// P3-3：预告框降一档亮度让位资源条；复评加强档=明度分离再拉开（实值见
    /// 内层注释，×0.70/α0.58 族）；boss 警示态不降亮）。缓存一份实例跨拍共享。
    /// </summary>
    private StyleBoxFlat EraBox(int eraIndex, bool boss, bool dim)
    {
        (int, bool, bool) key = (eraIndex, boss, dim);
        if (!_eraBoxes.TryGetValue(key, out StyleBoxFlat? box))
        {
            box = HoloSkin.BuildHoloBox(EraColor($"e{Math.Clamp(eraIndex, 1, 6)}"));
            if (boss)
            {
                box.BorderColor = new Color(1f, 0.82f, 0.36f, 0.95f); // ci-allow: R7 T-163 boss 金相色（表现层）
                box.BorderWidthLeft = box.BorderWidthRight = 2;
                box.BorderWidthTop = box.BorderWidthBottom = 2;
            }
            else if (dim)
            {
                // T-165 复评加强档：明度分离再拉开（首版 ×0.82/0.68 分析读
                // 「难分主次」——资源条全亮 vs 预告压暗的台阶要敢拉开）
                box.BgColor = new Color(box.BgColor.R * 0.70f, box.BgColor.G * 0.70f, box.BgColor.B * 0.70f, 0.58f);
                box.BorderColor = new Color(box.BorderColor.R * 0.72f, box.BorderColor.G * 0.72f, box.BorderColor.B * 0.72f, 0.50f);
            }

            _eraBoxes[key] = box;
        }

        return box;
    }

    // —— T-165 时代皮肤 0.2 s 色彩过渡（live 盒插值——缓存盒只在终点接管） ——

    /// <summary>在跑的皮肤过渡（面→tween；新过渡先杀旧——面板单一在跑律）.</summary>
    private readonly Dictionary<Control, Tween> _skinTweens = new();

    /// <summary>预告框 boss 态位（null=未定；Bind 复位 false——boss 变化才换框，过渡不被每拍 stomps）.</summary>
    private bool _waveBossState;

    /// <summary>
    /// 面板皮肤 0.2 s 色彩过渡（53 号文 P3-1）：当前框色→目标框色线性插值
    /// （live 盒独立实例每帧改色——缓存盒零突变零污染；终点/中途被状态变化
    /// （boss 波换框/新过渡）接管时如实让位）。像素风律内（色彩渐变非位移
    /// 模糊——transform/opacity 类动效）。
    /// </summary>
    private void TransitionPanelSkin(PanelContainer surface, StyleBoxFlat target)
    {
        if (_skinTweens.TryGetValue(surface, out Tween? previous))
        {
            previous.Kill();
            _skinTweens.Remove(surface);
        }

        if (surface.GetThemeStylebox("panel") is not StyleBoxFlat fromBox)
        {
            surface.AddThemeStyleboxOverride("panel", target);
            return;
        }

        Color fromBg = fromBox.BgColor;
        Color fromEdge = fromBox.BorderColor;
        var live = new StyleBoxFlat
        {
            BgColor = fromBg,
            BorderColor = fromEdge,
            BorderWidthLeft = fromBox.BorderWidthLeft,
            BorderWidthTop = fromBox.BorderWidthTop,
            BorderWidthRight = fromBox.BorderWidthRight,
            BorderWidthBottom = fromBox.BorderWidthBottom,
            CornerDetail = fromBox.CornerDetail,
        };
        live.SetCornerRadiusAll(HoloSkin.ChamferPx);
        surface.AddThemeStyleboxOverride("panel", live);

        Tween tween = CreateTween();
        _skinTweens[surface] = tween;
        tween.TweenMethod(Callable.From<float>(k =>
        {
            live.BgColor = fromBg.Lerp(target.BgColor, k);
            live.BorderColor = fromEdge.Lerp(target.BorderColor, k);
        }), 0f, 1f, 0.2); // ci-allow: R7 T-165 过渡时长秒（表现层动效）
        tween.TweenCallback(Callable.From(() =>
        {
            // 终点接管：仍在过渡位（未被 boss 框/新过渡覆盖）才换缓存目标盒
            if (_skinTweens.TryGetValue(surface, out Tween? running) && running == tween)
            {
                _skinTweens.Remove(surface);
                surface.AddThemeStyleboxOverride("panel", target);
            }
        }));
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

            // 数字右贴（icon 左、数量右——任务卡口径）；T-164：资源数字描边
            // +投影（与主菜单标题同律的可读性处理——亮 icon 上数字不糊）。
            var amount = new Label
            {
                Text = "0",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            amount.AddThemeFontSizeOverride("font_size", 15); // T-165：资源数字 14→15（HUD 小字族）
            amount.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
            UiFeel.Outline(amount, 3);
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

    /// <summary>
    /// 波次预告横幅渲染：标题（下波序号）+每类 icon×数量徽记行+boss 描边.
    /// T-164 审查判断项②收口：脏守卫（波构成签名未变不重建——原先每拍
    /// （15 帧）全拆全建 icon 行，波间 100+ 拍白造节点）+LoadIconOrSwatch
    /// 命中纹理静态缓存（缺图回退块不缓存——生成轨落地后随重绑自动换真图，
    /// 既有语义保真）。
    /// </summary>
    private void RenderWavePreview(WaveCompositionView view)
    {
        _waveTitle.Text = $"◆ 下波 W{view.NextWave:00} 预告";
        var entries = HudIconDecisions.WavePreviewEntries(
            view.NextWavePreview.Select(row => (row.EnemyId, row.Count))).ToList();
        bool boss = entries.Any(entry => entry.IsBoss);

        // 脏守卫：波序+敌构成（id×数量×boss 位）签名不变=零重建（标题文本
        // 幂等重赋，行与框不动）。
        string signature = $"{view.NextWave}|{string.Join(",", entries.Select(e => $"{e.EnemyId}x{e.Count}{(e.IsBoss ? "!" : "")}"))}";
        if (signature == _waveSignature)
        {
            return;
        }

        _waveSignature = signature;
        foreach (Node child in _waveRow.GetChildren())
        {
            _waveRow.RemoveChild(child);
            child.QueueFree();
        }

        foreach (HudIconDecisions.WavePreviewEntry entry in entries)
        {
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
            count.AddThemeFontSizeOverride("font_size", 12); // T-165：徽记数字 10→12（HUD 小字族）
            count.AddThemeColorOverride("font_color", entry.IsBoss
                ? new Color(1f, 0.82f, 0.36f)
                : HoloSkin.TextDimCyan);
            UiFeel.Outline(count, 2); // T-164：徽记数字描边（可读性）
            cell.AddChild(icon);
            cell.AddChild(count);
            _waveRow.AddChild(cell);
        }

        // boss 波横幅特殊描边（金边全亮——警示态不降亮度）；常规=时代色框降
        // 一档亮度的预告变体（T-165：53 号文 P3-3 预告框让位资源条）。boss 态
        // 位守卫：态不变不换框（Bind 后的 0.2 s 色彩过渡不被每拍 stomps）。
        if (boss != _waveBossState)
        {
            _waveBossState = boss;
            _waveBanner.AddThemeStyleboxOverride("panel", EraBox(_boundEra, boss, dim: !boss));
        }
    }

    /// <summary>波构成脏守卫签名（null=首拍/换局，必重建）.</summary>
    private string? _waveSignature;

    // ------------------------------------------------------------- 装载面 --

    /// <summary>res:// icon 装载（缺图=null——调用方优雅回退，不抛不阻）.</summary>
    private static Texture2D? LoadIcon(string resPath) =>
        ResourceLoader.Exists(resPath) ? GD.Load<Texture2D>(resPath) : null;

    // —— T-164 审查判断项②收口：装载命中纹理静态缓存（res:// 路径键；缺图
    //    回退块不入缓存——生成轨落地后下一次重建自动换真图，语义保真）。 ——

    private static readonly Dictionary<string, Texture2D> _iconCache = new(StringComparer.Ordinal);

    /// <summary>icon 或缺图回退（时代色底+白点纹——类色色块语义，LevelCardDecisions 回退同族）.</summary>
    private static Texture2D LoadIconOrSwatch(string resPath, Color swatch)
    {
        if (_iconCache.TryGetValue(resPath, out Texture2D? cached))
        {
            return cached;
        }

        Texture2D? texture = LoadIcon(resPath);
        if (texture is not null)
        {
            _iconCache[resPath] = texture; // 命中才缓存（缺图重探——落地即换真图）
            return texture;
        }

        int px = 24; // ci-allow: R7 T-163 回退块内部分辨率 px（展示位拉伸）
        var image = Image.CreateEmpty(px, px, false, Image.Format.Rgba8);
        image.Fill(new Color(swatch, 0.45f));
        image.SetPixel(4, 4, Colors.White);
        image.SetPixel(5, 4, Colors.White);
        return ImageTexture.CreateFromImage(image); // 生成轨缺图——落地后随重绑自动换真图
    }

    /// <summary>建造泳道节点解析（节奏态镜像的数据源；挂树后按名可达）.</summary>
    private BuildInteractionUi? BuildUi() =>
        _main.GetNodeOrNull<BuildInteractionUi>("BuildInteractionUi");

    /// <summary>
    /// era.json 资源行装载（内容侧单一数据源）。T-164 审查判断项④收口：
    /// 原先自带一份 AppContext walk-up 目录探测（与 Main.ReadRepoContent
    /// 双份 walk-up）——合一为 Main.ReadRepoContent 单源（既有 repo walk-up
    /// 约定同款，仓库不可达时抛——与宿主其余内容装载同一诚实语义）。
    /// </summary>
    private static IReadOnlyList<(string Id, string NameZh)> LoadEraResources(int eraIndex)
    {
        try
        {
            return HudIconDecisions.EraResources(
                Main.ReadRepoContent($"era{eraIndex}", "era.json"), $"era{eraIndex}/era.json");
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<(string, string)>(); // 仓库不可达（极端裸态）——空行不阻塞战斗（既有降级语义保真）
        }
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
