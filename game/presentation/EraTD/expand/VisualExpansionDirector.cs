using EraCore.Cinematics;
using EraCore.IO;
using EraCore.Narrative;
using EraTD.Cinematics;
using Godot;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraTD.Expand;

// T-152 ③ 视觉扩充总装（31 号 v2.0 §三 插入点映射表的表现层接线）。
// 职责边界（CS-1.6：表现层零业务规则）——本节点只做四件事：
//   1) 目录装载：game/content/cinematics/visual-assets.json → VisualAssetCatalog
//      （51 ogv+8 序列帧+《零的真相》+73 图+24 成就图标+六时代色板）；
//   2) 播放载体：主菜单循环 A-01（g 位）/VideoOverlay 全屏可跳过播片
//      （c/f/m 位：记忆段·仪式·开场·首通·彩蛋·合成篇）/Boss 序列帧 AnimatedSprite
//      式帧播放（e 位：战斗内零视频铁律的载体）/加载屏（i 位）/彩蛋展示位
//      （l 位：E-09 全屏图+E-05 手记封面）/时代皮肤变体（h 位：骨架恒定换主色）；
//   3) 收藏链：记忆档案页（D-01~08 逐段收藏）→ 集齐 8 段解锁《零的真相》
//      （MemoryArchiveStore，解锁只写 global 域——P5 同 seed 战报等价断言口径）；
//   4) 冒烟：headless 打印 CINE_V2 verbs/assets/ogv/strips/icons 计数+至少一条
//      真实 VideoStreamTheora 播放验证+记忆链全程+插画双轨解析计数（T-096-v02
//      起 49 图内置仓=硬判据 images_resolved==49）+皮肤九宫真应用（EXPAND_OK）。
// 缺素材自动降级：未登记键/聚合轨亦缺→纯文本/降级底色，永不阻断（D21-5）。
// 并行纪律：Main.cs 只追加一行 Instantiate（冲突由后提交者 rebase）。

/// <summary>
/// The T-152 visual-expansion director: hosts every catalog-driven performance
/// surface (menu loop, video overlay, boss strips, loading screen, easter
/// showcase, era skins) and the memory-archive collection chain.
/// </summary>
public sealed partial class VisualExpansionDirector : Control
{
    /// <summary>记忆段用途位（31 号 §二.2 D 层）。</summary>
    internal const string MemoryUsage = "memory";

    private VisualAssetCatalog _catalog = null!;
    private IFileStore _files = null!;
    private MemoryArchive _archive = MemoryArchive.Default;

    // ---- surfaces -----------------------------------------------------------
    private VideoStreamPlayer _menuLoop = null!;
    private CanvasLayer _menuLoopLayer = null!;
    private VideoOverlay _videoOverlay = null!;
    private Control _loadingScreen = null!;
    private TextureRect _loadingImage = null!;
    private Label _loadingLabel = null!;
    private PanelContainer _archivePanel = null!;
    private VBoxContainer _archiveRows = null!;
    private TextureRect _showcase = null!;
    private TextureRect _bossFrame = null!;
    private ColorRect _paletteTint = null!;

    // ---- boss strip player state -------------------------------------------
    private ImageTexture? _bossAtlas;
    private VisualStripAsset? _bossStrip;
    private int _bossFrameIndex;
    private int _bossFramesAdvanced;

    /// <summary>Smoke probes.</summary>
    internal int BossFramesAdvanced => _bossFramesAdvanced;
    internal VisualAssetCatalog Catalog => _catalog;
    internal VideoOverlay Overlay => _videoOverlay;
    internal bool MenuLoopReady => _menuLoop.Stream is VideoStreamTheora;

    /// <summary>Scene entry（Main.cs 单行接线）。</summary>
    internal static VisualExpansionDirector Instantiate(Main main)
    {
        // Name 固定（T-126）：代码挂载节点缺省名是 @Control@N——三联动立绘缝
        // 按名解析（InfoLayersUi/Main 的 GetNodeOrNull("VisualExpansionDirector")）。
        var director = new VisualExpansionDirector
        {
            _files = CreateFiles(),
            Name = "VisualExpansionDirector",
        };
        director.LoadContent();
        main.AddChild(director); // _Ready builds surfaces (and smokes headless)
        return director;
    }

    private static IFileStore CreateFiles()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            string root = System.IO.Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", "expand-smoke");
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            return new DevFileStore(root);
        }

        return new DevFileStore();
    }

    public override void _Ready()
    {
        ZIndex = 55; // below the cinematic director (50→60 overlay band): surfaces layer separately
        BuildSurfaces();
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessSmoke();
        }
    }

    private void LoadContent()
    {
        string dir = FindContentDir(
            System.IO.Path.Combine("game", "content", "cinematics"), "game/content/cinematics");
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "visual-assets.json"));
        _catalog = VisualAssetCatalog.Parse(json, "cinematics/visual-assets.json");
        _archive = MemoryArchiveStore.Load(_files);
    }

    // ---- g 位：主菜单循环 A-01 ----------------------------------------------

    /// <summary>
    /// 主菜单背景循环（31 号 §三 A-01 位）：VideoStreamTheora + loop。主菜单
    /// 场景属 Wave 4 打包任务——本方法即挂载点，Show/Hide 由菜单场景调用。
    /// </summary>
    internal bool TryStartMenuLoop()
    {
        string? path = ExpandAssetPaths.OgvPath(_catalog, MenuLoopAssetId);
        if (path is null)
        {
            return false;
        }

        var stream = new VideoStreamTheora { File = path };
        _menuLoop.Stream = stream;
        _menuLoop.Loop = true;
        _menuLoopLayer.Visible = true;
        _menuLoop.Play();
        return _menuLoop.IsPlaying();
    }

    internal void StopMenuLoop()
    {
        _menuLoop.Stop();
        _menuLoop.Stream = null; // 18 号 §2.3：过场结束立即释放（整文件驻内存）
        _menuLoopLayer.Visible = false;
    }

    /// <summary>菜单循环素材 id（目录 menu_loop 用途位唯一条目 A-01）。</summary>
    internal string MenuLoopAssetId =>
        _catalog.VideosByUsage("menu_loop").Single().Id;

    /// <summary>
    /// T-161：菜单循环 ogv 绝对路径（主菜单场景背景装载位）。不复用 g 位
    /// 播放器——那挂在 Layer=-10（夹具双层之下），主菜单的背景在 Layer=100
    /// 自持播放（MainMenuUi）；此处只答路径，播放/循环/释放归菜单侧。
    /// </summary>
    internal string? MenuLoopOgvPath() => ExpandAssetPaths.OgvPath(_catalog, MenuLoopAssetId);

    // ---- c/f/m 位：全屏可跳过播片 -------------------------------------------

    /// <summary>
    /// 目录驱动的全屏播片（记忆段 D/仪式 B/开场与首通 C/彩蛋 E/合成篇）。
    /// skippable=false 时隐藏跳过键（保护演出语义，31 号 §三）。
    /// </summary>
    internal bool PlayCatalogVideo(string assetId, bool skippable = true)
    {
        if (_catalog.VideoById(assetId) is null && assetId != _catalog.Composite.Id)
        {
            return false; // 未登记目录 id——调用方接线错误，静默拒绝
        }

        string? path = assetId == _catalog.Composite.Id
            ? ExpandAssetPaths.CompositePath(_catalog)
            : ExpandAssetPaths.OgvPath(_catalog, assetId);
        return _videoOverlay.Open(assetId, path, skippable);
    }

    /// <summary>记忆段播放完成回调：收藏 + 集齐解锁《零的真相》（31 号 ★行）。</summary>
    private void OnMemoryVideoFinished(string assetId)
    {
        _archive = MemoryArchiveStore.MarkViewed(_archive, assetId, _catalog);
        MemoryArchiveStore.Save(_files, _archive);
        RebuildArchiveRows();
    }

    /// <summary>播放一段记忆视频并走收藏链（c 位主通道）。</summary>
    internal bool PlayMemory(string assetId)
    {
        if (_catalog.VideoById(assetId) is not { Usage: MemoryUsage })
        {
            return false;
        }

        bool started = PlayCatalogVideo(assetId, skippable: true);
        if (started)
        {
            _videoOverlay.OnFinishedOnce = () => OnMemoryVideoFinished(assetId);
        }

        return started;
    }

    // ---- e 位：Boss 入场/败亡序列帧 -----------------------------------------

    /// <summary>
    /// Boss 序列帧演出（31 号 §三 C-01~08 位）：36 帧 Atlas @240x135 按帧步进
    /// （战斗内零视频铁律——序列帧不是视频）。帧推进=手动 pump（确定性）；
    /// live 模式由 _Process 按每秒 12 帧自动步进（3s 一场）。
    /// </summary>
    internal bool PlayBossStrip(string era, string kind)
    {
        string? path = ExpandAssetPaths.StripPath(_catalog, era, kind);
        _bossAtlas = ExpandAssetPaths.LoadPng(path);
        if (_bossAtlas is null)
        {
            return false;
        }

        _bossStrip = _catalog.StripFor(era, kind);
        _bossFrameIndex = 0;
        _bossFramesAdvanced = 0;
        ShowBossFrame();
        return true;
    }

    /// <summary>推进一帧（冒烟/演出宿主共用；越界自动收尾隐藏）。</summary>
    internal bool PumpBossFrame()
    {
        if (_bossStrip is null || _bossAtlas is null)
        {
            return false;
        }

        _bossFrameIndex++;
        if (_bossFrameIndex >= _bossStrip.Frames)
        {
            _bossFrame.Visible = false;
            _bossStrip = null;
            return false; // 演出结束
        }

        _bossFramesAdvanced++;
        ShowBossFrame();
        return true;
    }

    private void ShowBossFrame()
    {
        if (_bossStrip is null || _bossAtlas is null)
        {
            return;
        }

        int col = _bossFrameIndex % _bossStrip.Cols;
        int row = _bossFrameIndex / _bossStrip.Cols;
        var region = new Rect2(
            col * _bossStrip.FrameWidth, row * _bossStrip.FrameHeight,
            _bossStrip.FrameWidth, _bossStrip.FrameHeight);
        _bossFrame.Texture = AtlasTextureFromRegion(_bossAtlas, region);
        _bossFrame.Visible = true;
    }

    private static AtlasTexture AtlasTextureFromRegion(Texture2D atlas, Rect2 region) => new()
    {
        Atlas = atlas,
        Region = region,
    };

    // ---- i 位：加载屏 ---------------------------------------------------------

    /// <summary>关卡加载/章节选择背景（31 号 §三 加载图位）；缺图降级色域+时代名。</summary>
    internal bool ShowLoadingScreen(string eraKey)
    {
        _loadingScreen.Visible = true;
        EraPalette palette = _catalog.Palettes[eraKey];
        _loadingImage.Texture = ExpandAssetPaths.LoadPng(
            ExpandAssetPaths.ExternalImagePath(_catalog, $"loading-{eraKey}"));
        _loadingImage.Modulate = _loadingImage.Texture is null ? new Color(0f, 0f, 0f, 0f) : Colors.White;
        _loadingLabel.Text = _loadingImage.Texture is null
            ? $"『{palette.LabelZh}』正在集结防线……（图缺失·降级）"
            : $"『{palette.LabelZh}』{palette.MotifZh}正在集结防线……";
        return _loadingImage.Texture is not null;
    }

    internal void HideLoadingScreen() => _loadingScreen.Visible = false;

    // ---- h 位：时代皮肤变体 ---------------------------------------------------

    /// <summary>六时代主色（色板锚来自目录；皮肤层/UI/演出共用）。</summary>
    internal Color EraColor(string eraKey)
    {
        (int r, int g, int b) = _catalog.Palettes[eraKey].Primary;
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// 时代皮肤应用（骨架恒定纪律 §10.2）：优先 P4 皮肤底纹（T-096-v02 发布
    /// 聚合轨）作 StyleBoxTexture 九宫拉伸（单贴图+边距=纹理 1/8——边角不变形、
    /// 中心可伸缩；"切九宫格"的引擎原生形态，不切文件）；缺图回退色相 HoloBox
    /// （仅换边框主色）。信息结构零改动。palette_shift 演出（CinematicDirector）
    /// 与本 API 共用 EraColor 作为渐变端点。
    /// T-166 内容内距解耦：九宫 patch 边距只管绘制分区；内容边距（缺省=跟随
    /// patch 边距，1024 素材≈129px/侧）显式钉 6px——面板内距不再被装饰边厚
    /// 度绑架（军师面板 675px 高溢出画布的根因；绘制零改动）。
    /// </summary>
    internal bool ApplyEraSkin(PanelContainer panel, string eraKey)
    {
        Godot.ImageTexture? texture = ExpandAssetPaths.LoadPng(
            ExpandAssetPaths.ExternalImagePath(_catalog, $"skin-{eraKey}"));
        if (texture is not null)
        {
            int margin = Math.Max(1, Math.Min(texture.GetWidth(), texture.GetHeight()) / 8);
            var box = new StyleBoxTexture { Texture = texture };
            box.SetTextureMarginAll(margin);
            box.ContentMarginLeft = 6f;   // ci-allow: R7 T-166 皮肤盒内容内距 px（表现层排版，非平衡数值）
            box.ContentMarginRight = 6f;
            box.ContentMarginTop = 6f;
            box.ContentMarginBottom = 6f;
            panel.AddThemeStyleboxOverride("panel", box);
            return true;
        }

        var flat = HoloSkin.BuildHoloBox(EraColor(eraKey));
        panel.AddThemeStyleboxOverride("panel", flat);
        return false;
    }

    /// <summary>时代色域薄纱（加载屏/档案页氛围层：低透明主色全屏）。</summary>
    internal void ShowEraTint(string eraKey, float alpha)
    {
        _paletteTint.Color = new Color(EraColor(eraKey), alpha);
        _paletteTint.Visible = true;
    }

    internal void HideEraTint() => _paletteTint.Visible = false;

    // ---- j 位：军师人格立绘 ---------------------------------------------------

    /// <summary>
    /// 人格→立绘素材键（31 号 §三 立绘位：AI 直用插画，非 sprite）。
    /// T-126 起委托 <see cref="PersonaChoice.PortraitKeyOf"/>（三联动立绘臂
    /// 单一来源——本节点与 InfoLayersUi 切换条/冒烟探针共用一张映射表）。
    /// </summary>
    internal static string PersonaPortraitKey(NarrPersona persona) =>
        EraCore.Presentation.PersonaChoice.PortraitKeyOf(persona);

    /// <summary>人格立绘贴图（人格切换头像位；缺图 null→调用方降级既有头像）。</summary>
    internal Texture2D? PersonaPortrait(NarrPersona persona) =>
        ExpandAssetPaths.LoadPng(ExpandAssetPaths.ExternalImagePath(
            _catalog, PersonaPortraitKey(persona)));

    /// <summary>当前展示的人格（三联动冒烟探针；null=尚未调用过展示缝）。</summary>
    internal NarrPersona? ShownPersona { get; private set; }

    /// <summary>最近一次展示缝调用是否真的装载到立绘贴图（缺图=false，键仍生效）。</summary>
    internal bool PortraitTextureLoaded { get; private set; }

    /// <summary>
    /// T-126 三联动立绘臂：人格切换时更新立绘展示（1024×1536 竖幅右侧半身位；
    /// 缺图自动隐藏——D21-5 双轨降级纪律，缺图不阻断切换，仅留键级联动）。
    /// </summary>
    internal void ShowPersonaPortrait(NarrPersona persona)
    {
        _portraitRect ??= BuildPortraitRect();
        Texture2D? texture = PersonaPortrait(persona);
        _portraitRect.Texture = texture;
        _portraitRect.Visible = texture is not null;
        ShownPersona = persona;
        PortraitTextureLoaded = texture is not null;
    }

    private TextureRect? _portraitRect;

    private TextureRect BuildPortraitRect()
    {
        var rect = new TextureRect
        {
            Position = new Vector2(1010f, 88f),
            Size = new Vector2(144f, 216f), // 1024×1536 等比 2/3 缩放位（1/8 屏宽）
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(rect);
        return rect;
    }

    // ---- k 位：成就图标 -------------------------------------------------------

    /// <summary>成就图标贴图（repo 内置 64px 人工重绘件；Progression 成就 UI 消费）。</summary>
    internal Texture2D? AchievementIcon(string achievementId) =>
        ExpandAssetPaths.LoadPng(ExpandAssetPaths.AchievementPath(_catalog, achievementId));

    // ---- l 位：彩蛋展示 -------------------------------------------------------

    /// <summary>彩蛋全屏展示（E-09 拼贴长卷/E-05 手记封面等大图；点击关闭）。</summary>
    internal bool ShowEasterShowcase(string imageKey)
    {
        Texture2D? texture = ExpandAssetPaths.LoadPng(
            ExpandAssetPaths.ExternalImagePath(_catalog, imageKey));
        if (texture is null)
        {
            return false;
        }

        _showcase.Texture = texture;
        _showcase.Visible = true;
        return true;
    }

    internal void CloseEasterShowcase() => _showcase.Visible = false;

    // ---- c 位：记忆档案页 -----------------------------------------------------

    /// <summary>档案页打开（图鉴「记忆档案」；收藏 8 段+合成篇位）。</summary>
    internal void OpenArchive()
    {
        RebuildArchiveRows();
        ApplyEraSkin(_archivePanel, "e6");
        _archivePanel.Visible = true;
    }

    internal void CloseArchive() => _archivePanel.Visible = false;

    private void RebuildArchiveRows()
    {
        _archiveRows.RemoveAllTypeChildren();
        foreach (VisualVideoAsset memory in _catalog.VideosByUsage(MemoryUsage))
        {
            bool viewed = MemoryArchiveStore.IsViewed(_archive, memory.Id);
            var row = new Label
            {
                Text = $"{(viewed ? "✦" : "·")} {memory.Id}　考古记忆闪回（{memory.DurationSeconds:0.#}s）",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            row.AddThemeFontSizeOverride("font_size", 11);
            row.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
            _archiveRows.AddChild(row);
        }

        var composite = new Label
        {
            Text = _archive.ZeroTruthUnlocked
                ? "★《零的真相》已解锁——集齐 8 段记忆，合成篇可播放（60s 重剪）"
                : $"☆《零的真相》未解锁（收藏 {CollectedCount()}/8 段后解锁）",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        composite.AddThemeFontSizeOverride("font_size", 12);
        composite.AddThemeColorOverride(
            "font_color", _archive.ZeroTruthUnlocked ? new Color(0.98f, 0.84f, 0.32f) : HoloSkin.TextDimCyan);
        _archiveRows.AddChild(composite);
    }

    /// <summary>已收藏记忆段数（冒烟探针）。</summary>
    internal int CollectedCount() => _catalog.Composite.Requires.Count(
        id => MemoryArchiveStore.IsViewed(_archive, id));

    /// <summary>《零的真相》解锁位（冒烟探针；播放走 PlayCatalogVideo(composite)）。</summary>
    internal bool ZeroTruthUnlocked => _archive.ZeroTruthUnlocked;

    // ---- surfaces ------------------------------------------------------------

    private void BuildSurfaces()
    {
        // 主菜单循环层（默认隐藏——主菜单场景属 Wave 4 打包任务）
        _menuLoopLayer = new CanvasLayer { Layer = -10, Visible = false };
        AddChild(_menuLoopLayer);
        _menuLoop = new VideoStreamPlayer
        {
            Expand = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _menuLoop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _menuLoopLayer.AddChild(_menuLoop);

        // 全屏播片 overlay（可跳过位）
        _videoOverlay = new VideoOverlay { Visible = false };
        AddChild(_videoOverlay);

        // Boss 序列帧（世界层上方居中）
        _bossFrame = new TextureRect
        {
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(200f, 80f),
            Size = new Vector2(400f, 225f),
        };
        AddChild(_bossFrame);

        // 加载屏（全屏）
        _loadingScreen = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _loadingScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingImage = new TextureRect
        {
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _loadingImage.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _loadingScreen.AddChild(_loadingImage);
        _loadingLabel = new Label
        {
            Position = new Vector2(24f, 24f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _loadingLabel.AddThemeFontSizeOverride("font_size", 16);
        _loadingLabel.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _loadingScreen.AddChild(_loadingLabel);
        AddChild(_loadingScreen);

        // 时代色域薄纱
        _paletteTint = new ColorRect
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _paletteTint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_paletteTint);

        // 彩蛋全屏展示
        _showcase = new TextureRect
        {
            Visible = false,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        _showcase.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _showcase.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true })
            {
                CloseEasterShowcase();
            }
        };
        AddChild(_showcase);

        // 记忆档案页（e6 星门色板；OpenArchive 打开）
        _archivePanel = new PanelContainer { Visible = false };
        _archivePanel.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());
        _archivePanel.Position = new Vector2(140f, 60f);
        _archivePanel.CustomMinimumSize = new Vector2(360f, 0f);
        var archiveBody = new VBoxContainer();
        archiveBody.AddThemeConstantOverride("separation", 2);
        var title = new Label { Text = "◆ 记忆档案 · 考古收藏", MouseFilter = MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        _archiveRows = new VBoxContainer();
        _archiveRows.AddThemeConstantOverride("separation", 1);
        archiveBody.AddChild(title);
        archiveBody.AddChild(_archiveRows);
        _archivePanel.AddChild(archiveBody);
        AddChild(_archivePanel);
    }

    /// <summary>live 帧循环：Boss 序列帧每秒 12 帧自动步进（36 帧=3s）。</summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_bossStrip is not null)
        {
            _bossAutoAccumulator += delta;
            while (_bossAutoAccumulator >= BossStripFrameSeconds && _bossStrip is not null)
            {
                _bossAutoAccumulator -= BossStripFrameSeconds;
                PumpBossFrame();
            }
        }
    }

    private double _bossAutoAccumulator;
    private const double BossStripFrameSeconds = 1.0 / 12.0; // ci-allow: R7 T-152 Boss 序列帧播放帧率（演出表现常量，非平衡数值；36 帧@12fps=3s 一场）

    // ---- headless acceptance smoke（CINE_V2 / EXPAND_OK） ---------------------

    private void RunHeadlessSmoke()
    {
        // 1) 目录口径：16 动词（三新步入封闭表）/51 视频/8 序列帧/73 图/24 图标
        int verbs = CinematicScript.StepKinds.Count;
        int images = _catalog.Images.Values.Sum(batch => batch.Count) + _catalog.Achievements.Count;

        // 2) 入库验证：51 ogv+合成篇全部 OggS（真实文件系统）
        int ogvPresent = 0;
        int ogvMissing = 0;
        foreach (VisualVideoAsset video in _catalog.Videos)
        {
            if (ExpandAssetPaths.OgvPath(_catalog, video.Id) is null)
            {
                ogvMissing++;
                GD.PushError($"expand: ogv 缺失 {video.Id}");
            }
            else
            {
                ogvPresent++;
            }
        }

        bool compositePresent = ExpandAssetPaths.CompositePath(_catalog) is not null;

        // 3) 真实播放验证（18 号 C7）：A-01 经 VideoStreamTheora 加载并 Play
        bool menuLoopPlayed = TryStartMenuLoop();
        bool menuLoopReady = MenuLoopReady;
        StopMenuLoop();

        // 4) Boss 序列帧：e1 败亡整场步进（36 帧，零视频铁律载体）
        bool bossStarted = PlayBossStrip("e1", "boss_death");
        int bossPumped = 0;
        while (PumpBossFrame())
        {
            bossPumped++;
        }

        // 5) 记忆链：8 段收藏→集齐解锁→合成篇可播（档案页渲染+持久化）
        OpenArchive();
        int chainBefore = CollectedCount();
        bool unlockedBefore = ZeroTruthUnlocked;
        foreach (string required in _catalog.Composite.Requires.Take(7))
        {
            _archive = MemoryArchiveStore.MarkViewed(_archive, required, _catalog);
        }

        bool stillLockedAfter7 = !ZeroTruthUnlocked;
        _archive = MemoryArchiveStore.MarkViewed(_archive, _catalog.Composite.Requires[7], _catalog);
        MemoryArchiveStore.Save(_files, _archive);
        MemoryArchive reloaded = MemoryArchiveStore.Load(_files);
        bool unlockOn8 = reloaded.ZeroTruthUnlocked && MemoryArchiveStore.AllCollected(reloaded, _catalog);
        RebuildArchiveRows();
        bool compositePlayable = ExpandAssetPaths.CompositePath(_catalog) is not null
            && _catalog.VideoById("D-01") is { Usage: MemoryUsage };
        CloseArchive();

        // 6) 插画双轨解析计数（D21-5：开发轨=U 盘/发布轨=repo 聚合[T-096-v02]，
        //    任一命中即 resolved；49 图 T-096-v02 起全部内置仓，缺任一=聚合
        //    管线断裂——与 ogvMissing 同级硬判据）
        string[] imageBatches = { "banners", "story", "hooks", "easter", "skins", "loading", "personas" };
        int imagesResolved = 0;
        int imagesMissing = 0;
        foreach (string batch in imageBatches)
        {
            foreach (VisualImageAsset image in _catalog.Batch(batch))
            {
                if (ExpandAssetPaths.ExternalImagePath(_catalog, image.Id) is null)
                {
                    imagesMissing++;
                    GD.PushError($"expand: 插画缺失 {image.Id}（双轨均未命中）");
                }
                else
                {
                    imagesResolved++;
                }
            }
        }

        bool loadingShown = ShowLoadingScreen("e1");
        HideLoadingScreen();

        // 6b) 皮肤九宫：StyleBoxTexture 轨真应用（缺图回退色相轨=T-096-v02 前
        //     行为；面板骨架零改动——scratch 仅挂 stylebox）
        var scratchPanel = new PanelContainer();
        AddChild(scratchPanel);
        bool skinNineSlice = ApplyEraSkin(scratchPanel, "e1");
        RemoveChild(scratchPanel);
        scratchPanel.QueueFree();

        // 7) 内置轨成就图标（24 张全在 repo）
        int iconsResolved = _catalog.Achievements.Count(
            icon => ExpandAssetPaths.AchievementPath(_catalog, icon.Id) is not null);

        // 8) 皮肤变体/色板（六时代锚）
        bool palettesComplete = VisualAssetCatalog.EraKeys.All(
            era => _catalog.Palettes.ContainsKey(era));

        int totalAssets = _catalog.Videos.Count + _catalog.Strips.Count + 1 + images;
        GD.Print(
            $"CINE_V2 verbs={verbs} assets={totalAssets} ogv={ogvPresent}/{_catalog.Videos.Count} " +
            $"strips={_catalog.Strips.Count} images={images} icons={iconsResolved} composite={(compositePresent ? 1 : 0)} " +
            $"pixelate={_catalog.Videos.Count(v => v.Pixelate)} audio_tracks={_catalog.Videos.Count(v => v.Audio)}");
        GD.Print(
            $"CINE_V2 playback=a-01:{(menuLoopPlayed ? "played" : "FAILED")} stream:{(menuLoopReady ? "loaded" : "FAILED")} " +
            $"boss_strip=e1-death:{(bossStarted ? $"{bossPumped}frames" : "FAILED")} " +
            $"memory={chainBefore}->8 unlock_on_8={unlockOn8} held_locked_at_7={stillLockedAfter7} " +
            $"persisted_reload={reloaded.ZeroTruthUnlocked} zero_truth_playable={compositePlayable} " +
            $"image_track=resolved:{imagesResolved}/missing:{imagesMissing} skin_nineslice={(skinNineSlice ? "texture" : "fallback")} " +
            $"loading_e1={(loadingShown ? "image" : "degraded")} palettes={_catalog.Palettes.Count}/6");

        bool ok = verbs == 16
            && ogvMissing == 0 && ogvPresent == _catalog.Videos.Count
            && _catalog.Videos.Count == 51 && _catalog.Strips.Count == 8
            && images == 73 && iconsResolved == 24 && compositePresent
            && menuLoopPlayed && menuLoopReady
            && bossStarted && bossPumped == 35 // 首帧在 Play 内显示，pump 推进余下 35 帧
            && chainBefore == 0 && !unlockedBefore && stillLockedAfter7
            && unlockOn8 && compositePlayable && palettesComplete
            && imagesResolved == 49 && imagesMissing == 0 && skinNineSlice;
        if (!ok)
        {
            GD.PushError(
                "expand wiring broken: " +
                $"verbs={verbs} ogv={ogvPresent}/{_catalog.Videos.Count}(miss{ogvMissing}) " +
                $"images={images} icons={iconsResolved} composite={compositePresent} " +
                $"menu={menuLoopPlayed}/{menuLoopReady} boss={bossStarted}/{bossPumped} " +
                $"unlock={unlockOn8}/held7={stillLockedAfter7}/replayable={compositePlayable} " +
                $"imageTrack={imagesResolved}/49(miss{imagesMissing}) skinNineSlice={skinNineSlice}");
            GD.Print("CINE_FAIL expand");
            GetTree().Quit(1);
            return;
        }

        GD.Print("EXPAND_OK verbs=16 assets_inku=52ogv+8strips images_inku=73(49aggregated+24icons)");
    }

    // ---- helpers ----------------------------------------------------------------

    private static string FindContentDir(string relative, string label)
    {
        // T-162 双轴审查修（Spec c.1）：catalog 装载并入 ExpandAssetPaths 三级链
        // （env/editor/exe旁/walk-up 同族同序）——env 形态下 catalog 根与 banner/
        // 视频根不再分叉；链上无可达命中时保留既有 AppContext walk-up 兜底语义。
        foreach (string candidate in ExpandAssetPaths.ArtRootCandidates())
        {
            string chained = System.IO.Path.Combine(candidate, relative);
            if (Directory.Exists(chained))
            {
                return chained;
            }
        }

        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(AppContext.BaseDirectory)!);
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
}

/// <summary>
/// 全屏可跳过播片 overlay（31 号 §三 video_frame 的宿主载体之一——目录直发
/// 播片共用；跳过键在 skippable=false 时隐藏）。
/// </summary>
public sealed partial class VideoOverlay : Control
{
    private VideoStreamPlayer _player = null!;
    private Button _skipButton = null!;
    private Label _title = null!;

    /// <summary>播片完成回调（一次性；记忆收藏链挂接点）。</summary>
    internal Action? OnFinishedOnce;

    internal string CurrentAssetId { get; private set; } = string.Empty;
    internal bool IsPlaying => _player.IsPlaying();

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var black = new ColorRect { Color = new Color(0f, 0f, 0f, 1f), MouseFilter = MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(black);

        _player = new VideoStreamPlayer
        {
            Expand = true,
            VolumeDb = -6f, // A/D/E 原声轨压低——宿主 BGM 混音让位（31 号 §三）
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _player.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _player.Finished += OnFinished;
        AddChild(_player);

        _title = new Label { Position = new Vector2(16f, 8f), MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontSizeOverride("font_size", 12);
        _title.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        AddChild(_title);

        _skipButton = new Button { Text = "跳过 ▸▸", Position = new Vector2(560f, 380f) };
        _skipButton.Pressed += Skip;
        AddChild(_skipButton);
    }

    /// <summary>打开播片（path=null=静帧降级：黑屏+标题，仍走完成回调）。</summary>
    internal bool Open(string assetId, string? path, bool skippable)
    {
        CurrentAssetId = assetId;
        _title.Text = $"◈ {assetId}";
        _skipButton.Visible = skippable;
        Visible = true;
        if (path is null)
        {
            GD.PushWarning($"expand: 播片素材缺失，静帧降级（{assetId}）");
            return true; // 降级开场（D21-2 Web 静帧同语义）
        }

        _player.Stream = new VideoStreamTheora { File = path };
        _player.Play();
        return true;
    }

    internal void Skip() => OnFinished();

    private void OnFinished()
    {
        _player.Stop();
        _player.Stream = null; // 18 号 §2.3：立即释放（整文件驻内存）
        Visible = false;
        OnFinishedOnce?.Invoke();
        OnFinishedOnce = null;
    }
}
