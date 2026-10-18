namespace EraCore.Presentation;

// T-141（移动端触控原型，08 W8 批 6；49 号文 §4 UI/分辨率面）：viewport
// 缩放档决策。桌面口径（T-042/F-125）=640×360 像素完美整数缩放——
// ViewportScale.Fit 原口径**逐字节不变**；触屏平台增补分数缩放档：
// 竖屏手机（例 1080×2340）整数档只能取 1×（letterbox 占屏 ~70%），分数档
// 取 min(w/640,h/360) 的 0.05 步进下取整（1.65×），画面宽度铺满。仅当
// 分数档严格优于整数档时启用，否则回落整数档（零收益不换档）。
//
// 纯逻辑（D-01：无引擎类型）；Godot 侧应用点=Window.ContentScaleStretch
// （Integer↔Fractional——4.7.2 GodotSharp 反射实证运行时可写，见 49 号文
// §4；整数档=project.godot 既有 canvas_items+keep+integer 原样不动）。

/// <summary>Which viewport scaling tier applies (T-141: desktop stays integer, touch platforms may go fractional).</summary>
public enum ViewportScaleTier
{
    /// <summary>Desktop default (T-042/F-125): pixel-perfect integer scale, project.godot settings untouched.</summary>
    IntegerPixelPerfect = 0,

    /// <summary>Touch-platform tier: fractional scale on a 0.05 grid (portrait phones gain screen area).</summary>
    MobileFractional = 1,
}

/// <summary>
/// One viewport scaling decision: the tier, the effective scale and the
/// centered letterbox rectangle, plus the derivation note (数值完整性：来源可溯）。
/// </summary>
/// <param name="Tier">The tier chosen for this window/platform fact.</param>
/// <param name="Scale">Effective scale (integer on <see cref="ViewportScaleTier.IntegerPixelPerfect"/>; multiple of 0.05 on fractional).</param>
/// <param name="OffsetX">Letterbox X offset in px (centered; never negative on the fractional tier).</param>
/// <param name="OffsetY">Letterbox Y offset in px (centered).</param>
/// <param name="ScaledWidth">640 × <paramref name="Scale"/> rounded to px.</param>
/// <param name="ScaledHeight">360 × <paramref name="Scale"/> rounded to px.</param>
/// <param name="Derivation">Why this tier (rule trace for the smoke output and the report).</param>
public sealed record ViewportScaleDecision(
    ViewportScaleTier Tier,
    double Scale,
    int OffsetX,
    int OffsetY,
    int ScaledWidth,
    int ScaledHeight,
    string Derivation);

/// <summary>
/// The T-141 viewport scaling switch (pure decision math). Desktop input
/// delegates to <see cref="ViewportScale"/> unchanged; touch platforms pick
/// the fractional tier only when it strictly beats the integer scale.
/// </summary>
public static class MobileViewportPolicy
{
    /// <summary>Fractional scale step (px-perfect grid: 1.00, 1.05, … — avoids arbitrary per-pixel rescaling).</summary>
    public const double FractionalStep = 0.05;

    /// <summary>
    /// Decides the scaling tier for one window. Non-positive dimensions throw
    /// (same guard as <see cref="ViewportScale.Fit"/>).
    /// </summary>
    public static ViewportScaleDecision Decide(int windowWidth, int windowHeight, bool touchPlatform)
    {
        ViewportFit integer = ViewportScale.Fit(windowWidth, windowHeight);
        if (!touchPlatform)
        {
            return new ViewportScaleDecision(
                ViewportScaleTier.IntegerPixelPerfect,
                integer.Scale, integer.OffsetX, integer.OffsetY,
                integer.ScaledWidth, integer.ScaledHeight,
                "desktop: ViewportScale.Fit (T-042/F-125 原口径)");
        }

        double raw = Math.Min(
            windowWidth / (double)ViewportScale.BaseWidth,
            windowHeight / (double)ViewportScale.BaseHeight);
        // 0.05 步进下取整（浮点容差内取不超 raw 的最大格点）+ ≥1 钳制
        // （窗口小于基准时与整数档同回落，不放大裁切）。
        double fractional = Math.Max(
            1.0,
            Math.Floor((raw + 1e-9) / FractionalStep) * FractionalStep);

        if (fractional <= integer.Scale)
        {
            return new ViewportScaleDecision(
                ViewportScaleTier.IntegerPixelPerfect,
                integer.Scale, integer.OffsetX, integer.OffsetY,
                integer.ScaledWidth, integer.ScaledHeight,
                $"touch: fractional {fractional:0.00}<=integer {integer.Scale} — integer kept");
        }

        // fractional ≤ min(w/640,h/360) ⇒ 缩放矩形不超窗口 ⇒ 偏移恒 ≥0。
        int scaledWidth = (int)Math.Round(ViewportScale.BaseWidth * fractional);
        int scaledHeight = (int)Math.Round(ViewportScale.BaseHeight * fractional);
        return new ViewportScaleDecision(
            ViewportScaleTier.MobileFractional,
            fractional,
            (windowWidth - scaledWidth) / 2,
            (windowHeight - scaledHeight) / 2,
            scaledWidth, scaledHeight,
            $"touch: fractional {fractional:0.00}x beats integer {integer.Scale}x (raw {raw:0.00})");
    }
}
