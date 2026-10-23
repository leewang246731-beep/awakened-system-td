using EraCore.Presentation;
using Godot;

namespace EraTD;

// T-161 (W10 A 批/C-21, F-125) 主菜单场景——开机首屏（窗口车道）。
//
// 构成（任务卡口径，纯决策半边=EraCore.Presentation.MainMenuDecisions）：
//   背景   = A-01 菜单循环视频（game/assets/cinematics 目录 menu_loop 用途位
//            唯一条目，VideoStreamTheora 循环；原声压到静音——菜单听觉面=
//            AudioDirector calm 态循环 BGM，三态机在无敌人时自动落在 Calm）。
//            视频加载失败回退 docs/assets/concept-keyart-eras.png 全屏静帧，
//            再失败纯深色底（双保险链，MainMenuDecisions.PickBackground 裁决）。
//   牌匾   = res://textures/ui/logo-banner.png 槽位（主管落图位，768×512——
//            300×200 等比缩放居中，不拉伸变形；构图=六塔群像在下半部、上半
//            部日落天空留白）。缺图=keyart 中央带横裁垫图
//            （MainMenuDecisions.LogoCropBand 几何，REV 登记）。
//   标题   = 中文大标题「我在游戏觉醒系统」叠在牌匾天空区（垂直 5%~50% 带
//            内——不盖塔群）+黑描边+投影；副题 Awakened System TD 小字同带。
//   入口   = 六主题按钮卡（战役/无尽/程序关卡/联机/设置/退出——全部既有
//            入口归拢；任务卡第 4 卡「军师(BYOK)」无独立既有入口面：BYOK
//            表单与军师三档下拉都在设置页内，如实归并不假接，REV 登记）——
//            StyleBoxFlat 时代色描边（色值查 visual-assets.json palettes，
//            RL7 键不色）+悬停放大+按下 ding 音效；点击走各泳道既有
//            OpenFromMenu 入口（悬浮入口按钮窗口态隐藏——入口归拢于此）。
//   角标   = 版本号（单一版本源：发布面=export_presets.cfg product_version
//            [开发/编辑器轨]，导出件=exe PE 版本资源，兜底=EraCore.
//            VersionInfo——本类不新造第二份版本号常量）。
//
// 画布口径：project.godot 640×360 canvas_items+integer stretch——本场景全部
// 坐标按 640×360 设计（与既有泳道 UI 同域），整数倍放大由引擎统一承担。
// 字体口径：项目现有中文渲染=引擎缺省字体（仓库无自定义字体资源，全部
// 既有中文 UI 同源）——本场景沿用同一缺省+描边/投影保可读，不另引字体。
//
// 承载面纪律（与各泳道 UI 同律）：
//   - headless 绝不实例化本类（Main 按 headless 分叉；G4 冒烟仍直进夹具
//     自检，十三段判据零改动）；
//   - 菜单可见性=每帧谓词（任一会话在局/任一面板打开/任一演出在放 → 隐藏，
//     CanvasLayer Layer=100 全屏不透明覆盖；隐藏即停视频释放整文件驻内存
//     ——18 号 §2.3 律）；
//   - 会话退出（战役存档退出/无尽弃局/程序关弃局/联机断开）→ 谓词翻真 →
//     回主菜单（「会话退出→回主菜单」链路的承载面）。

/// <summary>
/// The T-161 main-menu face: loop-video background with the double fallback,
/// logo plaque slot, big CJK title laid into the plaque's sky band, six
/// era-colored entry cards, version badge and the calm-loop menu BGM
/// (AudioDirector's three-state machine rests on Calm while no enemies).
/// </summary>
public sealed partial class MainMenuUi : Control
{
    // —— 版式常量（640×360 画布域；表现层排版常量，非平衡数值） ——
    private const float PlaqueWidth = 300f;   // 768×512 → 300×200 等比（同比 1.5）
    private const float PlaqueHeight = 200f;
    private const float PlaqueTop = 12f;
    private const int TitleFontPx = 21;       // 8 字 ≈168px，居中入 300px 牌匾
    private const float CardWidth = 140f;     // 三列两行网格
    private const float CardHeight = 34f;
    private const float HoverScale = 1.08f;
    private const float HoverTweenSeconds = 0.08f;

    private readonly Main _main;
    private readonly bool _headless;

    private CanvasLayer _layer = null!;
    private ColorRect _baseRect = null!;
    private TextureRect _keyartRect = null!;
    private VideoStreamPlayer _video = null!;
    private TextureRect _scrim = null!;
    private TextureRect _logoRect = null!;
    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Label _versionLabel = null!;
    private GridContainer _buttonGrid = null!;

    private readonly Dictionary<MainMenuDecisions.EntryId, Button> _buttons = new();

    private MainMenuDecisions.BackgroundKind _background = MainMenuDecisions.BackgroundKind.DarkFallback;
    private bool _backgroundResolved;
    private bool _menuShown; // false 起步——首帧谓词翻真即走 EnsureBackground 开视频

    private MainMenuUi(Main main)
    {
        _main = main;
        _headless = OS.HasFeature("headless") || DisplayServer.GetName() == "headless";
    }

    /// <summary>
    /// Code-built instantiation（窗口车道专用；headless 由 Main 分叉挡在外面，
    /// 本方法对 headless 调用直接拒绝——绝不把菜单树挂进冒烟局）。
    /// </summary>
    public static MainMenuUi Instantiate(Main main)
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            throw new InvalidOperationException("MainMenuUi 是窗口车道专表面（headless 冒烟不得挂载）");
        }

        var ui = new MainMenuUi(main)
        {
            Name = "MainMenuUi",
        };
        ui.BuildUi();
        main.AddChild(ui);
        return ui;
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        // Layer=100：盖过既有全部画面层（夹具双层 z0/10、视觉扩充 z55、
        // 播片 CanvasLayer 60）。演出期间的让位由每帧谓词负责。
        _layer = new CanvasLayer { Layer = 100, Name = "MainMenuLayer" };
        AddChild(_layer);

        var root = new Control { Name = "MenuRoot" };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Stop;
        _layer.AddChild(root);

        // —— 背景（三层：深色底 ← keyart 静帧 ← 循环视频）——
        _baseRect = new ColorRect { Color = new Color(0.035f, 0.045f, 0.075f, 1f) };
        _baseRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _baseRect.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_baseRect);

        _keyartRect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            Visible = false,
        };
        _keyartRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _keyartRect.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_keyartRect);

        _video = new VideoStreamPlayer
        {
            Expand = true,
            Loop = true,
            VolumeDb = -60f, // A-01 原声压到静音：菜单听觉面=calm 态 BGM（任务卡口径）
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _video.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_video);

        // 可读性渐变（上深下浅——牌匾天空区/按钮网格的压暗衬底；上端刻意压轻
        // 保住日落天空色彩，标题靠描边+投影保对比）
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(0f, 0f, 0f, 0.42f));
        gradient.SetColor(1, new Color(0f, 0f, 0f, 0.08f));
        _scrim = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = gradient,
                FillFrom = new Vector2(0f, 0f),
                FillTo = new Vector2(0f, 1f),
            },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Tile,
        };
        _scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_scrim);

        // —— 上半：logo 牌匾（等比居中）+ 叠天空区的中文标题 ——
        var plaque = new Control { Name = "LogoPlaque" };
        plaque.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        plaque.OffsetLeft = -PlaqueWidth / 2f;
        plaque.OffsetRight = PlaqueWidth / 2f;
        plaque.OffsetTop = PlaqueTop;
        plaque.OffsetBottom = PlaqueTop + PlaqueHeight;
        plaque.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(plaque);

        _logoRect = BuildLogoPlaque();
        _logoRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        plaque.AddChild(_logoRect);

        // 标题带=牌匾天空区（垂直 5%~50%，主管落图口径）：大标题中心 ≈21%，
        // 副题 ≈43.5%——都在带上沿之下，不盖下半部六塔群像。
        _titleLabel = new Label
        {
            Text = MainMenuDecisions.TitleZh,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _titleLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _titleLabel.OffsetTop = PlaqueHeight * 0.05f;
        _titleLabel.OffsetBottom = PlaqueHeight * 0.37f;
        _titleLabel.AddThemeFontSizeOverride("font_size", TitleFontPx);
        _titleLabel.AddThemeColorOverride("font_color", Colors.White);
        _titleLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        _titleLabel.AddThemeConstantOverride("outline_size", 8);
        _titleLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.65f));
        _titleLabel.AddThemeConstantOverride("shadow_offset_x", 2);
        _titleLabel.AddThemeConstantOverride("shadow_offset_y", 2);
        _titleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        plaque.AddChild(_titleLabel);

        _subtitleLabel = new Label
        {
            Text = MainMenuDecisions.TitleEn,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subtitleLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _subtitleLabel.OffsetTop = PlaqueHeight * 0.37f;
        _subtitleLabel.OffsetBottom = PlaqueHeight * 0.50f;
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 9);
        _subtitleLabel.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _subtitleLabel.AddThemeConstantOverride("outline_size", 4);
        _subtitleLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        _subtitleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        plaque.AddChild(_subtitleLabel);

        // —— 下半：六主题按钮卡（三列两行网格，居中） ——
        _buttonGrid = new GridContainer
        {
            Columns = 3,
            MouseFilter = Control.MouseFilterEnum.Ignore, // 网格本体不挡，卡各自收
        };
        _buttonGrid.AddThemeConstantOverride("h_separation", 10);
        _buttonGrid.AddThemeConstantOverride("v_separation", 8);
        root.AddChild(_buttonGrid);
        foreach (MainMenuDecisions.Entry entry in MainMenuDecisions.Entries())
        {
            _buttons[entry.Id] = BuildEntryButton(entry);
            _buttonGrid.AddChild(_buttons[entry.Id]);
        }

        // 锚点预设放在六卡成形后（Minsize 模式按当前最小尺寸定 offsets——
        // 空容器时预设会塌成零尺寸矩形，六卡全数不可见；实测截图抓出的回归）。
        _buttonGrid.SetAnchorsAndOffsetsPreset(
            Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 18);

        // —— 右下角：版本角标 ——
        _versionLabel = new Label
        {
            Text = MainMenuDecisions.VersionBadge(ResolveVersionFace()),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _versionLabel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _versionLabel.OffsetLeft = -160f;
        _versionLabel.OffsetTop = -18f;
        _versionLabel.AddThemeFontSizeOverride("font_size", 9);
        _versionLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.86f, 0.9f));
        _versionLabel.AddThemeConstantOverride("outline_size", 3);
        _versionLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        _versionLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_versionLabel);
    }

    /// <summary>
    /// logo 牌匾槽位（任务卡：res://textures/ui/logo-banner.png——主管落图
    /// 位）。缺图=keyart 中央带横裁垫图（<see cref="MainMenuDecisions.
    /// LogoCropBand"/> 几何，band=<see cref="MainMenuDecisions.
    /// LogoCropBandRatio"/>）；再缺=无图牌匾（空槽——槽位语义仍在）。
    /// </summary>
    private TextureRect BuildLogoPlaque()
    {
        var plaque = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };

        Texture2D? logo = null;
        if (ResourceLoader.Exists(MainMenuDecisions.LogoTexturePath))
        {
            logo = ResourceLoader.Load<Texture2D>(MainMenuDecisions.LogoTexturePath);
        }

        if (logo is null)
        {
            logo = BuildKeyartCropPlaceholder();
        }

        plaque.Texture = logo;
        return plaque;
    }

    /// <summary>keyart 中央带横裁垫图（牌匾缺图回退；读不到=返回 null 走空槽）.</summary>
    private static Texture2D? BuildKeyartCropPlaceholder()
    {
        string? root = Expand.ExpandAssetPaths.RepoRoot();
        if (root is null)
        {
            return null;
        }

        string path = System.IO.Path.Combine(root,
            MainMenuDecisions.KeyartRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        var image = new Image();
        if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.GetWidth() <= 0 || image.GetHeight() <= 0)
        {
            return null;
        }

        (int y, int height) = MainMenuDecisions.LogoCropBand(
            image.GetHeight(), MainMenuDecisions.LogoCropBandRatio);
        if (height <= 0)
        {
            return null;
        }

        return ImageTexture.CreateFromImage(image.GetRegion(new Rect2I(0, y, image.GetWidth(), height)));
    }

    private Button BuildEntryButton(MainMenuDecisions.Entry entry)
    {
        Color era = PaletteColor(entry.PaletteKey);
        // 对比度口径（W10 玩家视角截图实测迭代）：视频底部=暗塔剪影，常态
        // 描边须用足饱和时代色（55% 暗缩版在暗底上不可辨——首版截图按钮
        // 「看不见」的根因）；hover=时代色再向白提亮 30%（悬停醒目阶）。
        Color hoverEdge = new(
            era.R + (1f - era.R) * 0.30f, era.G + (1f - era.G) * 0.30f, era.B + (1f - era.B) * 0.30f);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.16f, 0.92f),
            BorderColor = era,
            ContentMarginLeft = 12f,
            ContentMarginRight = 12f,
            ContentMarginTop = 6f,
            ContentMarginBottom = 6f,
        };
        SetBorder(normal, 2);
        SetRadius(normal, 4);

        var hover = new StyleBoxFlat
        {
            BgColor = new Color(era.R * 0.22f + 0.08f, era.G * 0.22f + 0.09f, era.B * 0.22f + 0.12f, 0.96f),
            BorderColor = hoverEdge,
            ContentMarginLeft = 12f,
            ContentMarginRight = 12f,
            ContentMarginTop = 6f,
            ContentMarginBottom = 6f,
        };
        SetBorder(hover, 2);
        SetRadius(hover, 4);

        var pressed = new StyleBoxFlat
        {
            BgColor = new Color(era.R * 0.32f + 0.05f, era.G * 0.32f + 0.05f, era.B * 0.32f + 0.07f, 0.97f),
            BorderColor = hoverEdge,
            ContentMarginLeft = 12f,
            ContentMarginRight = 12f,
            ContentMarginTop = 6f,
            ContentMarginBottom = 6f,
        };
        SetBorder(pressed, 3);
        SetRadius(pressed, 4);

        var button = new Button
        {
            Text = entry.LabelZh,
            CustomMinimumSize = new Vector2(CardWidth, CardHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        button.PivotOffset = new Vector2(CardWidth / 2f, CardHeight / 2f); // 悬停放大的缩放中心
        button.AddThemeFontSizeOverride("font_size", 13);
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_focus_color", Colors.White);
        button.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        button.AddThemeConstantOverride("outline_size", 4);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", normal);

        button.Pressed += () => OnEntryPressed(entry.Id);
        UiFeel.AttachCues(button); // T-164：hover 轻ding/press 确认音（真叮通路——UiFeel 单源，替代 T-161 局部 PlayPressCue）
        button.MouseEntered += () =>
        {
            button.CreateTween()
                .TweenProperty(button, "scale", new Vector2(HoverScale, HoverScale), HoverTweenSeconds);
        };
        button.MouseExited += () =>
        {
            button.CreateTween()
                .TweenProperty(button, "scale", Vector2.One, HoverTweenSeconds);
        };
        return button;
    }

    private static void SetBorder(StyleBoxFlat box, int width)
    {
        box.BorderWidthTop = width;
        box.BorderWidthBottom = width;
        box.BorderWidthLeft = width;
        box.BorderWidthRight = width;
    }

    private static void SetRadius(StyleBoxFlat box, int radius)
    {
        box.CornerRadiusTopLeft = radius;
        box.CornerRadiusTopRight = radius;
        box.CornerRadiusBottomLeft = radius;
        box.CornerRadiusBottomRight = radius;
    }

    /// <summary>时代主色（RL7：色值查 visual-assets.json palettes，键来自决策表）.</summary>
    private Color PaletteColor(string paletteKey)
    {
        EraCore.Cinematics.EraPalette? palette = null;
        _main.ExpandDirector?.Catalog.Palettes.TryGetValue(paletteKey, out palette);
        if (palette is { } row)
        {
            return new Color(row.Primary.R / 255f, row.Primary.G / 255f, row.Primary.B / 255f);
        }

        return HoloSkin.EdgeCyan; // 目录不可达（发布裸包极端态）——中性青回退
    }

    /// <summary>
    /// 版本角标的单一版本源解析（不新造常量）：①开发/编辑器轨读
    /// res://export_presets.cfg 的 application/product_version（发布版本面，
    /// v1.1.x 发版链在此升版）；②导出件读 exe 的 PE 版本资源（同一
    /// product_version 由导出器打进 exe）；③兜底 EraCore.VersionInfo
    /// （引擎内部 wave 版本）。三源同一发布面，无第二份手工版本号。
    /// </summary>
    private static string ResolveVersionFace()
    {
        if (Godot.FileAccess.FileExists("res://export_presets.cfg"))
        {
            using Godot.FileAccess file = Godot.FileAccess.Open(
                "res://export_presets.cfg", Godot.FileAccess.ModeFlags.Read);
            while (file is { } reader && !reader.EofReached())
            {
                string line = file.GetLine();
                if (line.Contains("application/product_version", StringComparison.Ordinal)
                    && line.Split('"') is { Length: >= 2 } parts)
                {
                    return parts[^2];
                }
            }
        }

        try
        {
            string product = System.Diagnostics.FileVersionInfo.GetVersionInfo(
                OS.GetExecutablePath()).ProductVersion ?? string.Empty;
            if (product.Length > 0)
            {
                return product;
            }
        }
        catch (System.IO.FileNotFoundException)
        {
            // 探针落空（极端裸环境）——走 EraCore 兜底
        }

        return EraCore.VersionInfo.Current;
    }

    // ------------------------------------------------------------- 入口路由

    private void OnEntryPressed(MainMenuDecisions.EntryId id)
    {
        switch (id)
        {
            case MainMenuDecisions.EntryId.Campaign:
                _main.CampaignUi?.OpenFromMenu();
                break;
            case MainMenuDecisions.EntryId.Endless:
                _main.EndlessUi?.OpenFromMenu();
                break;
            case MainMenuDecisions.EntryId.Procedural:
                _main.ProceduralUi?.OpenFromMenu();
                break;
            case MainMenuDecisions.EntryId.Net:
                _main.NetUi?.OpenFromMenu();
                break;
            case MainMenuDecisions.EntryId.Settings:
                _main.SettingsUi.Open();
                break;
            case MainMenuDecisions.EntryId.Quit:
                GetTree().Quit(0);
                break;
        }
    }

    /// <summary>
    /// 按键名驱动真实入口（--shot-open 验收钩子）：发的是真按钮 Pressed
    /// 信号——与玩家点击同一条链（禁第二套开面板逻辑）。
    /// </summary>
    internal bool OpenEntry(string key)
    {
        if (!Enum.TryParse<MainMenuDecisions.EntryId>(key.Trim(), ignoreCase: true, out var id)
            || !_buttons.TryGetValue(id, out Button? button))
        {
            return false;
        }

        button.EmitSignal(Button.SignalName.Pressed);
        return true;
    }

    /// <summary>入口按钮表（--shot 悬停驱动位；键=EntryId）.</summary>
    internal IReadOnlyDictionary<MainMenuDecisions.EntryId, Button> EntryButtons => _buttons;

    /// <summary>背景视频当前播放位置（秒；--shot「≠首帧」证据位）.</summary>
    internal double VideoPositionSeconds => _video.StreamPosition;

    /// <summary>菜单在屏位（--shot/存活探针）.</summary>
    internal bool MenuShown => _menuShown;

    // PlayPressCue（T-161 局部按丁音效）已由 UiFeel.AttachCues 单源取代
    // （T-164 皮肤批：hover=ding_info 轻响、press=ding_reward 确认音——同一
    // 真叮通路，本类不再自持播放代码）。

    // ----------------------------------------------------- 可见性谓词（帧泵）

    /// <summary>
    /// 菜单在屏谓词：任一泳道会话在局（战役/无尽/程序关/联机）或任一入口
    /// 面板打开（含设置页）或任一演出在放（剧情导演/全屏播片）或战役演出
    /// 待演 → 菜单隐藏；否则菜单在屏。会话退出即翻真——「会话退出→回主
    /// 菜单」的承载面。
    /// </summary>
    private bool ShouldShowMenu()
    {
        bool sessionActive =
            (_main.CampaignUi?.SessionActive ?? false)
            || (_main.EndlessUi?.SessionActive ?? false)
            || (_main.ProceduralUi?.SessionActive ?? false)
            || (_main.NetUi?.SessionActive ?? false);
        bool panelOpen =
            (_main.CampaignUi?.PanelOpen ?? false)
            || (_main.EndlessUi?.PanelOpen ?? false)
            || (_main.ProceduralUi?.PanelOpen ?? false)
            || (_main.NetUi?.PanelOpen ?? false)
            || _main.SettingsUi.Visible;
        bool performing =
            (_main.CineDirector?.Playing ?? false)
            || (_main.ExpandDirector?.Overlay.IsPlaying ?? false)
            || (_main.CampaignUi?.PerformancesPending ?? false); // 胜利结算/跃迁待演不闪菜单
        return !sessionActive && !panelOpen && !performing;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_headless)
        {
            return; // 防御位：headless 不实例化本类（Main 分叉），到不了这里
        }

        SetMenuShown(ShouldShowMenu());
    }

    private void SetMenuShown(bool shown)
    {
        if (shown == _menuShown)
        {
            if (shown)
            {
                PumpVideoLoop(); // 已在屏：视频异常中断的自愈重播
            }

            return;
        }

        _menuShown = shown;
        _layer.Visible = shown;
        if (shown)
        {
            EnsureBackground();
        }
        else
        {
            // 隐藏即停：过场结束立即释放（18 号 §2.3——整文件驻内存律）
            _video.Stop();
            _video.Stream = null;
        }
    }

    /// <summary>背景双保险链的一次性裁决（文件级事实不随帧变化）.</summary>
    private void EnsureBackground()
    {
        if (_backgroundResolved)
        {
            PumpVideoLoop();
            return;
        }

        _backgroundResolved = true;
        string? ogv = _main.ExpandDirector?.MenuLoopOgvPath();
        bool keyartReadable = TryLoadKeyartStill();
        bool videoPlayable = false;
        if (ogv is not null)
        {
            _video.Stream = new VideoStreamTheora { File = ogv };
            _video.Play();
            videoPlayable = _video.IsPlaying();
        }

        _background = MainMenuDecisions.PickBackground(videoPlayable, keyartReadable);
        _keyartRect.Visible = _background == MainMenuDecisions.BackgroundKind.KeyartStill;
        if (_background != MainMenuDecisions.BackgroundKind.MenuLoopVideo)
        {
            _video.Stream = null; // 不可播即释放，不留悬空流
        }

        // 菜单 BGM 事实探针：AudioDirector 三态机在无敌人时落 Calm（StartBgm
        // 自 _Ready 即播）——此处只取真值做 MENU 行证据，不伪造状态。
        (string bgmState, bool bgmAudible) = _main.GetNodeOrNull<Audio.AudioDirector>("AudioDirector")
            ?.BgmFact() ?? ("absent", false);
        GD.Print(
            $"MENU background={_background} video_pos={_video.StreamPosition:0.00}s " +
            $"logo={DescribeLogo()} entries={_buttons.Count} bgm={bgmState}:{(bgmAudible ? "audible" : "silent")} " +
            $"version={_versionLabel.Text} title={MainMenuDecisions.TitleZh}");
    }

    /// <summary>视频态自愈：在屏且视频停了（异常中断）→ 重播（循环位由 Loop 承担）.</summary>
    private void PumpVideoLoop()
    {
        if (_background == MainMenuDecisions.BackgroundKind.MenuLoopVideo
            && _menuShown
            && !_video.IsPlaying())
        {
            _video.Play();
        }
    }

    private bool TryLoadKeyartStill()
    {
        string? root = Expand.ExpandAssetPaths.RepoRoot();
        if (root is null)
        {
            return false;
        }

        string path = System.IO.Path.Combine(root,
            MainMenuDecisions.KeyartRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Texture2D? texture = Expand.ExpandAssetPaths.LoadPng(path);
        if (texture is null)
        {
            return false;
        }

        _keyartRect.Texture = texture;
        return true;
    }

    private string DescribeLogo() =>
        ResourceLoader.Exists(MainMenuDecisions.LogoTexturePath) ? "banner" : "keyart-crop-placeholder";
}
