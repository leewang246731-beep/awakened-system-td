using EraCore.Presentation;
using Godot;

namespace EraTD;

// T-164（W10 D 批·皮肤与手感）皮肤与手感中心：时代化 StyleBox 三态、文字
// 描边/投影、按钮 hover/press 音效（复用既有叮通道——零新音频资产）、UI
// 控件图标装载（res:// 项目内轨，缺图=文字符号优雅回退）、banner 构建三连
// 抄聚合（T-162 遗留顺手项）。
// T-165（53 号文审计批）新增四族：①系统件像素皮肤（下拉/文本框/开关——
// 程序关/设置/无尽三面通用，边框/箭头/光标/焦点框像素语言+时代色，像素
// 件全部代码生成零二进制资产——BuildAvatarTexture 同律）；②hover 放大补间
// helper（主菜单六卡同律，选关卡片复用）；③banner/keyart 裁切色场垫底条带
// （BuildBannerStrip 无标题变体——满度原则：空位拿既有素材整片色场填）；
// ④锁遮罩时代色暗变体色阶（去「脏灰」）。纪律面：
//   - 色值经 visual-assets.json palettes 同源三级链（调用方传色——本类不
//     持有时代数值，RL7；HoloSkin.EdgeCyan=跨时代中性回退）；
//   - StyleBoxFlat 同拍复用不每拍新造：三态盒按时代色键静态缓存（跨面共享
//     一份实例——StyleBox 覆写持有引用，共享安全）；
//   - 音效=真通路才接：hover=ding_info（情报"叮"轻响，同类 2 s 节流防刷
//     屏）、press=ding_reward（奖励"叮叮"确认音）——DingAudioPlayer.
//     PlayCueSlot 既有闭集缝（T-089 音效位），无新合成件；
//   - headless 零副作用：音效/图标接线全部跳过（冒烟判据零改动纪律），
//     皮肤盒本身为纯样式覆写（headless 不实例化窗口面，天然不达）。

/// <summary>
/// The T-164 skin-and-feel center: cached era-tinted three-state button
/// boxes, the title outline/shadow recipe, hover/press ding cues on the
/// EXISTING ding lane, UI control-icon loading with graceful text fallback,
/// and the shared mode/chapter banner strip builder (T-162 triple-copy fix).
/// T-165 adds the pixel system-widget skins (OptionButton/LineEdit/CheckBox,
/// code-generated pixel arrow/checkbox art), the shared hover-scale tween,
/// and the banner-cut color-bed builder (53 号文 audit fixes).
/// </summary>
internal static class UiFeel
{
    // —— 三态 StyleBox 缓存（键=时代色 HTML 码；跨面跨拍共享一份实例） ——

    private static readonly Dictionary<string, (StyleBoxFlat Normal, StyleBoxFlat Hover, StyleBoxFlat Pressed)> _buttonTriples = new();

    /// <summary>
    /// 时代色按钮三态（normal/hover/pressed；disabled=normal——禁用语义由
    /// 调用方另叠加暗色/灰图标，见 BuildInteractionUi 两级灰度分层）。色阶
    /// 律（与主菜单六卡同族）：normal=暗底+足饱和时代色 2px 描边；hover=时代
    /// 色底+描边向白提亮 30%；pressed=时代色加重底+3px 描边。实例按色键缓存
    /// ——同拍复用不每拍新造（任务卡纪律）。
    /// </summary>
    internal static (StyleBoxFlat Normal, StyleBoxFlat Hover, StyleBoxFlat Pressed) ButtonTriple(Color era)
    {
        string key = era.ToHtml();
        if (!_buttonTriples.TryGetValue(key, out (StyleBoxFlat, StyleBoxFlat, StyleBoxFlat) triple))
        {
            Color hoverEdge = new( // 描边亮阶：向白提亮 30%（主菜单同律）
                era.R + (1f - era.R) * 0.30f, era.G + (1f - era.G) * 0.30f, era.B + (1f - era.B) * 0.30f);
            var normal = new StyleBoxFlat
            {
                BgColor = new Color(0.07f, 0.09f, 0.13f, 0.94f), // ci-allow: R7 T-164 暗底色阶（表现层三态底色，非平衡数值）
                BorderColor = era,
                ContentMarginLeft = 10f,
                ContentMarginRight = 10f,
                ContentMarginTop = 4f,
                ContentMarginBottom = 4f,
            };
            SetBorder(normal, 2);
            SetRadius(normal, 4);
            var hover = new StyleBoxFlat
            {
                BgColor = new Color(era.R * 0.22f + 0.08f, era.G * 0.22f + 0.09f, era.B * 0.22f + 0.12f, 0.96f),
                BorderColor = hoverEdge,
                ContentMarginLeft = 10f,
                ContentMarginRight = 10f,
                ContentMarginTop = 4f,
                ContentMarginBottom = 4f,
            };
            SetBorder(hover, 2);
            SetRadius(hover, 4);
            var pressed = new StyleBoxFlat
            {
                BgColor = new Color(era.R * 0.32f + 0.05f, era.G * 0.32f + 0.05f, era.B * 0.32f + 0.07f, 0.97f),
                BorderColor = hoverEdge,
                ContentMarginLeft = 10f,
                ContentMarginRight = 10f,
                ContentMarginTop = 4f,
                ContentMarginBottom = 4f,
            };
            SetBorder(pressed, 3);
            SetRadius(pressed, 4);
            triple = (normal, hover, pressed);
            _buttonTriples[key] = triple;
        }

        return triple;
    }

    /// <summary>把按钮接入三态皮肤（缓存盒覆写四态；disabled=normal——禁用叠加归调用方）.</summary>
    internal static void ApplyButtonSkin(Button button, Color era)
    {
        (StyleBoxFlat normal, StyleBoxFlat hover, StyleBoxFlat pressed) = ButtonTriple(era);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", normal);
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

    // —— 文字描边/投影（与主菜单标题同律——可读性处理单一配方式） ——

    /// <summary>
    /// 标题/数字描边+投影（任务卡「文字描边」项）：黑描边保对比+2px 投影
    /// 提层次（主菜单大标题同律缩小档）。<paramref name="outlineSize"/> 随
    /// 字号缩放（标题 6~8、正文 3、数字 3——调用方按位定档）。
    /// </summary>
    internal static void Outline(Label label, int outlineSize)
    {
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        label.AddThemeConstantOverride("outline_size", outlineSize);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
    }

    // —— 按钮三态音效（复用既有叮通道——零新音频资产） ——

    /// <summary>
    /// 按钮接入 hover/press 音效（hover=ding_info 情报"叮"轻响、press=
    /// ding_reward 奖励"叮叮"确认音——两档经 DingAudioPlayer.PlayCueSlot
    /// 既有闭集缝，同类 2 s 节流由 DingBus 承担防刷屏）。**解析时点=首个
    /// 事件**：调用方多在 AddChild 之前接线，入树前父链为空——事件时点
    /// 按钮已在树，FindDing 必达主场景（解析一次后缓存）。窗口车道专用：
    /// headless 直接跳过（冒烟判据零改动——纪律位）。
    /// </summary>
    internal static void AttachCues(Button button)
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            return; // headless 冒烟零副作用（窗口车道手感件）
        }

        Audio.DingAudioPlayer? ding = null;
        button.MouseEntered += () =>
        {
            ding ??= FindDing(button);
            if (ding is not null)
            {
                _ = ding.PlayCueSlot("ding_info", (long)Time.GetTicksMsec());
            }
        };
        button.Pressed += () =>
        {
            ding ??= FindDing(button);
            if (ding is not null)
            {
                _ = ding.PlayCueSlot("ding_reward", (long)Time.GetTicksMsec());
            }
        };
    }

    /// <summary>向上找主场景叮节点（Main 直子 DingAudio——MainMenuUi 同解析）.</summary>
    private static Audio.DingAudioPlayer? FindDing(Node from)
    {
        for (Node? node = from; node is not null; node = node.GetParent())
        {
            if (node.GetNodeOrNull<Audio.DingAudioPlayer>("DingAudio") is { } ding)
            {
                return ding;
            }
        }

        return null;
    }

    // —— UI 控件图标（10 件组：res:// 项目内轨；缺图=文字符号优雅回退） ——

    /// <summary>UI 控件图标装载（缺图=null——调用方保留文字符号回退，不阻塞）.</summary>
    internal static Texture2D? ControlIcon(string name)
    {
        string path = HudIconDecisions.ControlIconPath(name);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    /// <summary>
    /// 按钮图标化：icon 在=图文互换（icon 替文字符号，ExpandIcon 保持最小
    /// 尺寸档）；缺图=原文字符号照旧（T-163 遗留④的回退纪律）。
    /// </summary>
    internal static void Iconify(Button button, string iconName)
    {
        Texture2D? icon = ControlIcon(iconName);
        if (icon is null)
        {
            return; // 素材未落——文字符号回退（装载层存在性探针另报）
        }

        button.Icon = icon;
        button.ExpandIcon = true;
        button.Text = string.Empty; // 图标在位=文字符号退位（tooltip 保留语义）
    }

    // —— banner 构建三连抄聚合（T-162 遗留顺手项：战役章题图/无尽/程序关模式卡） ——

    /// <summary>
    /// 通栏题图/模式卡主视觉条带（三面共享 helper——T-162 三连抄收口）：贴图
    /// 整幅 KeepAspectCovered 横裁+底部压暗带+描边标题。缺图由调用方垫底后
    /// 传入（贴图非空断言不入——垫底链归调用方既有口径）。
    /// </summary>
    /// <param name="titleZh">条带标题（章名/模式名——已含锁态后缀等调用方语义）.</param>
    /// <param name="texture">主视觉贴图（banner 或垫底块）.</param>
    /// <param name="height">条带高（px，640×360 画布域）.</param>
    /// <param name="bandHeight">底部压暗带高（px——标题可读性衬底）.</param>
    internal static Control BuildBannerStrip(string titleZh, Texture2D texture, float height, float bandHeight)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var strip = new Control { CustomMinimumSize = new Vector2(0f, height) };
        var banner = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        banner.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        banner.MouseFilter = Control.MouseFilterEnum.Ignore;
        banner.Texture = texture;
        strip.AddChild(banner);

        var scrim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.45f) }; // ci-allow: R7 T-164 压暗带透明度（可读性衬底，非平衡数值）
        scrim.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        scrim.OffsetTop = -bandHeight;
        scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
        strip.AddChild(scrim);

        var title = new Label
        {
            Text = titleZh,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        title.OffsetTop = -bandHeight;
        title.OffsetBottom = -1;
        title.OffsetLeft = 6;
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", Colors.White);
        Outline(title, 6);
        title.MouseFilter = Control.MouseFilterEnum.Ignore;
        strip.AddChild(title);
        return strip;
    }

    // —— T-165 色场垫底条带（BuildBannerStrip 无标题变体：贴图+压暗带，无题字） ——

    /// <summary>
    /// banner/keyart 裁切色场垫底（53 号文 P2-3/P3-2 修法：空位拿既有素材
    /// 整片色场填——满度原则）。贴图整幅 KeepAspectCovered+全幅压暗带
    /// （标题等前景内容的可读性衬底）；条带自身不收鼠标。锚定/定位归调用方
    /// （容器内=CustomMinimumSize 高；锚定面=调用方设 offsets）。
    /// </summary>
    /// <param name="texture">垫底贴图（既有 banner/keyart 裁切）.</param>
    /// <param name="height">条带高（px，640×360 画布域）.</param>
    /// <param name="dimAlpha">压暗带透明度（0~1——0.62 主菜单按钮区档/0.78 表单衬底档）.</param>
    internal static Control BuildColorBed(Texture2D texture, float height, float dimAlpha)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var bed = new Control { CustomMinimumSize = new Vector2(0f, height) };
        var image = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        image.MouseFilter = Control.MouseFilterEnum.Ignore;
        image.Texture = texture;
        bed.AddChild(image);
        var scrim = new ColorRect { Color = new Color(0f, 0f, 0f, dimAlpha) }; // ci-allow: R7 T-165 色场压暗档（表现层可读性衬底）
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
        bed.AddChild(scrim);
        return bed;
    }

    // —— T-165 hover 放大补间（主菜单六卡同律——选关卡片/模式卡复用） ——

    /// <summary>
    /// 悬停放大补间（53 号文 P1-3 修法：卡片 hover 痕迹——主菜单六卡
    /// MouseEntered/MouseExited 同律收口为单源 helper）。<paramref name="hoverZ"/>
    /// &gt;0 时悬停期抬 Z（密集网格中放大卡压到邻卡上——绘制序修正）。
    /// PivotOffset 每次事件时点重取（构建时容器未排版 Size=0）。
    /// </summary>
    internal static void HoverScaleTween(Control control, float hoverScale, float seconds, int hoverZ = 0)
    {
        control.MouseEntered += () =>
        {
            if (hoverZ > 0)
            {
                control.ZIndex = hoverZ;
            }

            control.PivotOffset = control.Size / 2f;
            control.CreateTween().TweenProperty(control, "scale", new Vector2(hoverScale, hoverScale), seconds);
        };
        control.MouseExited += () =>
        {
            if (hoverZ > 0)
            {
                control.ZIndex = 0;
            }

            control.PivotOffset = control.Size / 2f;
            control.CreateTween().TweenProperty(control, "scale", Vector2.One, seconds);
        };
    }

    // —— T-165 系统件像素皮肤（下拉/文本框/开关：三面通用，像素语言+时代色） ——

    /// <summary>下拉像素箭头（9×5 白色三角——icon_normal_color 系列按时代着色）.</summary>
    private static Texture2D? _pixelArrow;

    /// <summary>开关像素方块 unchecked（12×12 暗底+时代色 1px 边）缓存（键=时代色）.</summary>
    private static readonly Dictionary<string, Texture2D> _pixelBoxes = new();

    /// <summary>
    /// 下拉 OptionButton 像素皮肤（53 号文 P1-1 修法）：暗底+足饱和时代色 2px
    /// 描边三态盒（ButtonTriple 同族色阶律）+9×5 像素箭头（icon_normal_color
    /// 着色——高分辨率系统箭头退位）+弹出清单时代色框（panel/hover 两态）。
    /// 高分辨率默认件与像素风横幅同屏的风格混血由此收口。
    /// </summary>
    internal static void ApplyOptionSkin(OptionButton box, Color era)
    {
        (StyleBoxFlat normal, StyleBoxFlat hover, StyleBoxFlat pressed) = ButtonTriple(era);
        StyleBoxFlat focus = CloneFlat(hover);
        SetBorder(focus, 3); // 焦点态=3px 提亮边（键盘/程序焦点可见——P1-3）
        StyleBoxFlat disabled = CloneFlat(normal);
        disabled.BorderColor = new Color(era.R * 0.45f + 0.06f, era.G * 0.45f + 0.07f, era.B * 0.45f + 0.09f, 0.9f);
        box.AddThemeStyleboxOverride("normal", normal);
        box.AddThemeStyleboxOverride("hover", hover);
        box.AddThemeStyleboxOverride("pressed", pressed);
        box.AddThemeStyleboxOverride("focus", focus);
        box.AddThemeStyleboxOverride("disabled", disabled);
        box.AddThemeIconOverride("arrow", PixelArrow());
        box.AddThemeColorOverride("icon_normal_color", new Color(era.R + (1f - era.R) * 0.2f, era.G + (1f - era.G) * 0.2f, era.B + (1f - era.B) * 0.2f));
        box.AddThemeColorOverride("icon_hover_color", new Color(era.R + (1f - era.R) * 0.45f, era.G + (1f - era.G) * 0.45f, era.B + (1f - era.B) * 0.45f));
        box.AddThemeColorOverride("icon_pressed_color", Colors.White);
        box.AddThemeColorOverride("icon_focus_color", Colors.White);
        box.AddThemeColorOverride("icon_disabled_color", new Color(0.55f, 0.58f, 0.64f));
        box.AddThemeColorOverride("font_color", new Color(0.90f, 0.93f, 0.97f));
        box.AddThemeColorOverride("font_hover_color", Colors.White);
        box.AddThemeColorOverride("font_pressed_color", Colors.White);
        box.AddThemeColorOverride("font_focus_color", Colors.White);
        box.AddThemeColorOverride("font_disabled_color", new Color(0.55f, 0.58f, 0.64f));

        // 弹出清单：暗底+时代色边（hover 行=时代色暗变体底——同律描边/台阶/色场）
        PopupMenu popup = box.GetPopup();
        var popupPanel = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.98f), // ci-allow: R7 T-165 弹出清单底色（表现层）
            BorderColor = era,
            ContentMarginLeft = 6f,
            ContentMarginRight = 6f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
        };
        SetBorder(popupPanel, 2);
        SetRadius(popupPanel, 2);
        popup.AddThemeStyleboxOverride("panel", popupPanel);
        var itemHover = new StyleBoxFlat { BgColor = new Color(era.R * 0.26f + 0.06f, era.G * 0.26f + 0.07f, era.B * 0.26f + 0.10f, 0.96f) }; // ci-allow: R7 T-165 弹出清单 hover 底（表现层）
        SetRadius(itemHover, 2);
        popup.AddThemeStyleboxOverride("hover", itemHover);
        popup.AddThemeStyleboxOverride("pressed", itemHover);
        popup.AddThemeStyleboxOverride("focused", itemHover);
        popup.AddThemeColorOverride("font_color", new Color(0.90f, 0.93f, 0.97f));
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
        popup.AddThemeColorOverride("font_selected_color", Colors.White);
    }

    /// <summary>
    /// 文本框 LineEdit 像素皮肤（P1-1/P1-3）：暗底+时代色暗变体 2px 边；
    /// 焦点态=足饱和时代色边（提亮阶——焦点痕迹可见）；光标/选区带时代色。
    /// </summary>
    internal static void ApplyLineEditSkin(LineEdit input, Color era)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.09f, 0.13f, 0.95f), // ci-allow: R7 T-165 文本框底色（表现层）
            BorderColor = new Color(era.R * 0.55f, era.G * 0.55f, era.B * 0.55f, 0.95f),
            ContentMarginLeft = 6f,
            ContentMarginRight = 6f,
            ContentMarginTop = 3f,
            ContentMarginBottom = 3f,
        };
        SetBorder(normal, 2);
        SetRadius(normal, 2);
        var focus = CloneFlat(normal);
        focus.BorderColor = era;
        focus.BgColor = new Color(0.09f, 0.11f, 0.16f, 0.97f); // ci-allow: R7 T-165 文本框焦点底色（表现层）
        input.AddThemeStyleboxOverride("normal", normal);
        input.AddThemeStyleboxOverride("focus", focus);
        input.AddThemeColorOverride("font_color", Colors.White);
        input.AddThemeColorOverride("font_placeholder_color", new Color(0.62f, 0.68f, 0.78f, 0.8f));
        input.AddThemeColorOverride("caret_color", era);
        input.AddThemeColorOverride("selection_color", new Color(era.R * 0.4f + 0.05f, era.G * 0.4f + 0.06f, era.B * 0.4f + 0.08f, 0.9f));
    }

    /// <summary>
    /// 开关 CheckBox 像素皮肤（P1-1）：checked/unchecked（radio 组同套覆盖——
    /// 同一控件族同律）=12×12 代码生成像素方块（unchecked=暗底+时代色 1px 边；
    /// checked=时代色填充+暗色勾两像素）；文字色三态齐。
    /// </summary>
    internal static void ApplyCheckBoxSkin(CheckBox box, Color era)
    {
        Texture2D uncheckedBox = PixelBox(era, isChecked: false);
        Texture2D checkedBox = PixelBox(era, isChecked: true);
        foreach (string icon in new[] { "checked", "radio_checked" })
        {
            box.AddThemeIconOverride(icon, checkedBox);
        }

        foreach (string icon in new[] { "unchecked", "radio_unchecked" })
        {
            box.AddThemeIconOverride(icon, uncheckedBox);
        }

        box.AddThemeColorOverride("font_color", new Color(0.90f, 0.93f, 0.97f));
        box.AddThemeColorOverride("font_hover_color", Colors.White);
        box.AddThemeColorOverride("font_pressed_color", Colors.White);
        box.AddThemeColorOverride("font_focus_color", Colors.White);
        box.AddThemeColorOverride("font_disabled_color", new Color(0.55f, 0.58f, 0.64f));
    }

    /// <summary>SpinBox 数字框=内嵌 LineEdit 同族皮肤（设置页数字行与文本行同律）.</summary>
    internal static void ApplySpinBoxSkin(SpinBox box, Color era) =>
        ApplyLineEditSkin(box.GetLineEdit(), era);

    /// <summary>StyleBoxFlat 浅拷贝（色值可变式过渡/派生态的安全基底——不动缓存实例）.</summary>
    private static StyleBoxFlat CloneFlat(StyleBoxFlat source) => new()
    {
        BgColor = source.BgColor,
        BorderColor = source.BorderColor,
        BorderWidthLeft = source.BorderWidthLeft,
        BorderWidthTop = source.BorderWidthTop,
        BorderWidthRight = source.BorderWidthRight,
        BorderWidthBottom = source.BorderWidthBottom,
        ContentMarginLeft = source.ContentMarginLeft,
        ContentMarginRight = source.ContentMarginRight,
        ContentMarginTop = source.ContentMarginTop,
        ContentMarginBottom = source.ContentMarginBottom,
        CornerRadiusTopLeft = source.CornerRadiusTopLeft,
        CornerRadiusTopRight = source.CornerRadiusTopRight,
        CornerRadiusBottomLeft = source.CornerRadiusBottomLeft,
        CornerRadiusBottomRight = source.CornerRadiusBottomRight,
        CornerDetail = source.CornerDetail,
    };

    /// <summary>9×5 白色像素下拉箭头（代码生成缓存——icon_*_color 按时代着色）.</summary>
    private static Texture2D PixelArrow()
    {
        if (_pixelArrow is null)
        {
            var image = Image.CreateEmpty(9, 5, false, Image.Format.Rgba8);
            image.Fill(Colors.Transparent);
            for (int row = 0; row < 5; row++)
            {
                int half = 4 - row; // 行宽 9/7/5/3/1 的居中像素三角
                for (int column = -half; column <= half; column++)
                {
                    image.SetPixel(4 + column, row, Colors.White);
                }
            }

            _pixelArrow = ImageTexture.CreateFromImage(image);
        }

        return _pixelArrow;
    }

    /// <summary>12×12 开关像素方块（键=时代色 HTML 码；on=时代色填充+暗勾像素）.</summary>
    private static Texture2D PixelBox(Color era, bool isChecked)
    {
        string key = era.ToHtml() + (isChecked ? ":on" : ":off");
        if (!_pixelBoxes.TryGetValue(key, out Texture2D? texture))
        {
            Color edge = isChecked
                ? new Color(era.R * 0.6f + 0.06f, era.G * 0.6f + 0.07f, era.B * 0.6f + 0.08f)
                : era;
            Color fill = isChecked
                ? new Color(era.R * 0.8f + 0.2f, era.G * 0.8f + 0.2f, era.B * 0.8f + 0.2f)
                : new Color(era.R * 0.22f + 0.05f, era.G * 0.22f + 0.06f, era.B * 0.22f + 0.09f); // T-165：未选中底=时代色暗变体（读灰=未决定——复评修正）
            var image = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
            image.Fill(fill);
            int ring = isChecked ? 1 : 2; // 未选中=2px 时代色环（复评二修：1px 在 24 物理位上读灰——加粗保像素框读相）
            for (int i = 0; i < 12; i++)
            {
                for (int t = 0; t < ring; t++)
                {
                    image.SetPixel(i, t, edge);
                    image.SetPixel(i, 11 - t, edge);
                    image.SetPixel(t, i, edge);
                    image.SetPixel(11 - t, i, edge);
                }
            }

            if (isChecked)
            {
                // 暗色勾两笔（4 像素斜线×2——12×12 档位内的像素语言勾形）
                Color ink = new(0.05f, 0.07f, 0.10f);
                foreach ((int x, int y) in new[] { (3, 6), (4, 7), (5, 8), (6, 7), (7, 6), (8, 5), (9, 4) })
                {
                    image.SetPixel(x, y, ink);
                }
            }

            texture = ImageTexture.CreateFromImage(image);
            _pixelBoxes[key] = texture;
        }

        return texture;
    }
}
