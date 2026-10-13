using Godot;

namespace EraTD;

/// <summary>
/// T-055 (C-22 / F-131) system-panel skin per design v3.2 §13.4 pixel
/// recipe. Everything is built in code from Godot primitives — no new
/// binary assets enter the repo, so the T-045 asset gate is untouched.
/// <list type="bullet">
/// <item>base: deep blue, alpha 85%;</item>
/// <item>border: 2 px cyan (#00e5ff family) with a 1 px dark-cyan inner
/// bevel — StyleBoxFlat has no inner-shadow primitive, so the bevel is a
/// second nested box drawn one border-width inside (documented
/// approximation);</item>
/// <item>corners: 45° chamfer approximated with corner radii rendered at
/// <c>corner_detail = 1</c> — one straight diagonal segment per corner
/// instead of a curve (the closest StyleBoxFlat can express; a true
/// 9-slice chamfer ships with the T-075 full panel art);</item>
/// <item>scanlines: 1 px line every 2 px gap — a 1×3 code-generated tile
/// repeated by a TextureRect in tile stretch mode;</item>
/// <item>digits: monospace look via zero-padded fixed-width fields
/// (<see cref="EraCore.Presentation.HudMetrics"/>) — the 5×7 bitmap font
/// (m5x7 / Pixel Operator) is not in the asset pipeline yet, so the engine
/// default font renders those fields; gap noted here per the task card.</item>
/// </list>
/// </summary>
internal static class HoloSkin
{
    /// <summary>Panel base deep blue at 85% alpha (§13.4 底深蓝 alpha 85%).</summary>
    internal static readonly Color BaseBlue = new(0.031f, 0.086f, 0.180f, 0.85f);

    /// <summary>Border cyan of the #00e5ff family (§13.4 边框 2px 青).</summary>
    internal static readonly Color EdgeCyan = new(0.00f, 0.898f, 1.00f, 0.92f);

    /// <summary>Dark-cyan of the 1 px inner bevel (§13.4 暗青内投影 1px).</summary>
    internal static readonly Color InnerDarkCyan = new(0.00f, 0.353f, 0.416f, 0.95f);

    /// <summary>Dim cyan used for secondary text on the holo surfaces.</summary>
    internal static readonly Color TextDimCyan = new(0.62f, 0.93f, 1.00f, 0.95f);

    /// <summary>Risk red of plan-card highlights (F-066 风险红标).</summary>
    internal static readonly Color RiskRed = new(1.00f, 0.26f, 0.26f, 1.00f);

    /// <summary>Neutral-banner base of the off-mode HUD (§10.7 中性横幅).</summary>
    internal static readonly Color NeutralSlate = new(0.09f, 0.09f, 0.11f, 0.85f);

    /// <summary>Neutral-banner border — sober gray, no system cyan.</summary>
    internal static readonly Color NeutralEdge = new(0.45f, 0.45f, 0.50f, 0.90f);

    /// <summary>Scanline period: 1 px line + 2 px gap (§13.4).</summary>
    internal const int ScanPeriodPx = 3;

    /// <summary>Chamfer leg in px for the 45° corner cut approximation.</summary>
    internal const int ChamferPx = 5;

    /// <summary>
    /// The hologram panel box: deep blue 85% + 2 px cyan border + chamfered
    /// corners. Draw as the OUTER box; nest <see cref="BuildInnerBevel"/>
    /// inside it for the 1 px dark-cyan inner line.
    /// </summary>
    internal static StyleBoxFlat BuildHoloBox()
    {
        var box = new StyleBoxFlat
        {
            BgColor = BaseBlue,
            BorderColor = EdgeCyan,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerDetail = 1, // one straight segment per corner ⇒ 45° chamfer look
        };
        box.SetCornerRadiusAll(ChamferPx);
        return box;
    }

    /// <summary>
    /// The 1 px dark-cyan inner bevel box (§13.4 内投影). Draw INSIDE a
    /// <see cref="BuildHoloBox"/> panel with a 2 px margin — together the
    /// two boxes read as cyan edge + dark inner shadow.
    /// </summary>
    internal static StyleBoxFlat BuildInnerBevel()
    {
        var box = new StyleBoxFlat
        {
            BgColor = Colors.Transparent,
            BorderColor = InnerDarkCyan,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerDetail = 1,
        };
        box.SetCornerRadiusAll(ChamferPx - 1);
        return box;
    }

    /// <summary>
    /// The off-mode neutral banner box: dark slate, thin gray border, same
    /// chamfer silhouette so the layout does not jump when the system is
    /// switched off (§10.7 关闭系统=中性横幅).
    /// </summary>
    internal static StyleBoxFlat BuildNeutralBox()
    {
        var box = new StyleBoxFlat
        {
            BgColor = NeutralSlate,
            BorderColor = NeutralEdge,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerDetail = 1,
        };
        box.SetCornerRadiusAll(ChamferPx);
        return box;
    }

    /// <summary>
    /// The 1×<see cref="ScanPeriodPx"/> scanline tile: top pixel lit dim
    /// cyan, two transparent gap pixels. Tiled by a TextureRect over the
    /// panel body (§13.4 间隔 1px 扫描线 — 2 px spacing between 1 px lines).
    /// </summary>
    internal static ImageTexture BuildScanlineTexture()
    {
        var image = Image.CreateEmpty(1, ScanPeriodPx, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(EdgeCyan.R, EdgeCyan.G, EdgeCyan.B, 0.07f));
        image.SetPixel(0, 1, Colors.Transparent);
        image.SetPixel(0, 2, Colors.Transparent);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Additive-blend material for the entrance scan light band (§13.4 入场
    /// 水平扫描光带, Add 混合).
    /// </summary>
    internal static CanvasItemMaterial BuildAdditiveMaterial() => new()
    {
        BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
    };
}
