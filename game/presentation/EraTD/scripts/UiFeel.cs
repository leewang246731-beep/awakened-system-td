using EraCore.Presentation;
using Godot;

namespace EraTD;

// T-164（W10 D 批·皮肤与手感）皮肤与手感中心：时代化 StyleBox 三态、文字
// 描边/投影、按钮 hover/press 音效（复用既有叮通道——零新音频资产）、UI
// 控件图标装载（res:// 项目内轨，缺图=文字符号优雅回退）、banner 构建三连
// 抄聚合（T-162 遗留顺手项）。纪律面：
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
}
