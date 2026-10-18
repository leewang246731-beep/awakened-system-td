using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-21][T-141] viewport 缩放档（MobileViewportPolicy）：桌面=整数档
// 逐字节不变（ViewportScale.Fit 委托）；触屏=分数档仅在严格优于整数时启用
// （0.05 步进网格、≥1 钳制、居中偏移恒非负）；非法窗口尺寸与 ViewportScale
// 同守卫（非正数抛 ArgumentException）。
public class MobileViewportPolicyTests
{
    // ---- legal — 桌面口径零改动 ----------------------------------------------

    [Theory] // legal — 非触屏平台恒整数档（与 ViewportScale.Fit 同数）
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1600, 900)]
    [InlineData(1080, 2340)] // 竖屏窗口在桌面事实下仍整数档（开关关闭）
    public void Desktop_stays_integer_pixel_perfect(int windowWidth, int windowHeight)
    {
        ViewportScaleDecision decision = MobileViewportPolicy.Decide(windowWidth, windowHeight, touchPlatform: false);

        Assert.Equal(ViewportScaleTier.IntegerPixelPerfect, decision.Tier);
        ViewportFit fit = ViewportScale.Fit(windowWidth, windowHeight);
        Assert.Equal(fit.Scale, decision.Scale);
        Assert.Equal(fit.OffsetX, decision.OffsetX);
        Assert.Equal(fit.OffsetY, decision.OffsetY);
        Assert.Equal(fit.ScaledWidth, decision.ScaledWidth);
        Assert.Equal(fit.ScaledHeight, decision.ScaledHeight);
    }

    // ---- legal — 触屏分数档 ---------------------------------------------------

    [Fact] // legal：竖屏手机 1080×2340——整数档只能 1×（letterbox 占屏 ~70%），
    //        分数档 1.65×（0.05 网格下取整）画面宽度基本铺满
    public void Portrait_phone_gains_the_fractional_tier()
    {
        ViewportScaleDecision decision = MobileViewportPolicy.Decide(1080, 2340, touchPlatform: true);

        Assert.Equal(ViewportScaleTier.MobileFractional, decision.Tier);
        Assert.Equal(1.65, decision.Scale, 5); // min(1.6875, 6.5)=1.6875 → 1.65
        Assert.Equal(1056, decision.ScaledWidth);  // 640×1.65=1056
        Assert.Equal(594, decision.ScaledHeight); // 360×1.65=594
        Assert.Equal((1080 - 1056) / 2, decision.OffsetX); // 居中 12 px
        Assert.Equal((2340 - 594) / 2, decision.OffsetY); // 居中 873 px
        Assert.True(decision.OffsetX >= 0 && decision.OffsetY >= 0, "分数档不超窗——偏移恒非负");
    }

    [Fact] // legal：横屏手机 2340×1080——分数 3.0 不严格优于整数 3 → 保持整数档
    public void Landscape_phone_where_fractional_buys_nothing_keeps_integer()
    {
        ViewportScaleDecision decision = MobileViewportPolicy.Decide(2340, 1080, touchPlatform: true);

        Assert.Equal(ViewportScaleTier.IntegerPixelPerfect, decision.Tier);
        Assert.Equal(3, decision.Scale);
        Assert.Contains("integer kept", decision.Derivation, StringComparison.Ordinal);
    }

    [Fact] // legal：任意窗口的分数档 scale 必落在 0.05 步进网格（px-perfect 约束）
    public void Fractional_scale_always_lands_on_the_step_grid()
    {
        foreach ((int w, int h) in new[] { (1111, 555), (720, 1600), (1444, 900), (2048, 1536) })
        {
            ViewportScaleDecision decision = MobileViewportPolicy.Decide(w, h, touchPlatform: true);
            double remainder = decision.Scale / MobileViewportPolicy.FractionalStep;
            Assert.True(Math.Abs(remainder - Math.Round(remainder)) < 1e-6,
                $"scale {decision.Scale} off the 0.05 grid for window {w}x{h}");
        }
    }

    // ---- boundary — 钳制与守卫 -------------------------------------------------

    [Fact] // boundary：窗口小于基准（横屏 640×360 恰好、更小 500×300）——
    //        两档同回落 1×，不放大裁切（分数档不启用）
    public void Windows_at_or_below_base_clamp_to_one_and_stay_integer()
    {
        foreach ((int w, int h) in new[] { (640, 360), (500, 300) })
        {
            ViewportScaleDecision decision = MobileViewportPolicy.Decide(w, h, touchPlatform: true);
            Assert.Equal(ViewportScaleTier.IntegerPixelPerfect, decision.Tier);
            Assert.Equal(1, decision.Scale);
        }
    }

    [Theory] // illegal — 非正数窗口尺寸（与 ViewportScale.Fit 同守卫）
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-1, -1)]
    public void Non_positive_dimensions_throw(int windowWidth, int windowHeight)
    {
        Assert.Throws<ArgumentException>(
            () => MobileViewportPolicy.Decide(windowWidth, windowHeight, touchPlatform: true));
    }
}
