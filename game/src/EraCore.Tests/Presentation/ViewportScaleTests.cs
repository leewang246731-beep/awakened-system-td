using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-21] [F-125] 640×360 integer-scale math: exact table, centering
// remainder, clamp-at-1 floor and non-positive rejection.
public class ViewportScaleTests
{
    // ---- legal — canonical integer-multiple windows ------------------------

    [Theory] // legal — exact multiples scale cleanly with zero letterbox
    [InlineData(1280, 720, 2, 0, 0)]   // 2×  base
    [InlineData(1920, 1080, 3, 0, 0)]  // 3×  base (1080p)
    [InlineData(2560, 1440, 4, 0, 0)]  // 4×  base (1440p)
    public void Exact_multiples_scale_without_letterbox(
        int windowWidth, int windowHeight, int expectedScale, int expectedOffsetX, int expectedOffsetY)
    {
        var fit = ViewportScale.Fit(windowWidth, windowHeight);

        Assert.Equal(expectedScale, fit.Scale);
        Assert.Equal(expectedOffsetX, fit.OffsetX);
        Assert.Equal(expectedOffsetY, fit.OffsetY);
        Assert.Equal(ViewportScale.BaseWidth * expectedScale, fit.ScaledWidth);
        Assert.Equal(ViewportScale.BaseHeight * expectedScale, fit.ScaledHeight);
    }

    [Theory] // legal — non-multiple windows pick floor(min ratio) and center the remainder
    [InlineData(1600, 900, 2, 160, 90)]    // task-card example: 1600×900 → 2, margins 320×180 centered
    [InlineData(1920, 1200, 3, 0, 60)]     // X exact, Y has 120 px slack → 60 px top/bottom
    [InlineData(1366, 768, 2, 43, 24)]     // odd remainder floors left/top (documented bias)
    public void Non_multiples_floor_and_center(
        int windowWidth, int windowHeight, int expectedScale, int expectedOffsetX, int expectedOffsetY)
    {
        var fit = ViewportScale.Fit(windowWidth, windowHeight);

        Assert.Equal(expectedScale, fit.Scale);
        Assert.Equal(expectedOffsetX, fit.OffsetX);
        Assert.Equal(expectedOffsetY, fit.OffsetY);
    }

    [Theory] // legal — scaled rect plus letterbox reconstructs the window exactly
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2056, 1234)]
    public void Fit_reconstructs_window_from_scaled_rect_and_offsets(int windowWidth, int windowHeight)
    {
        var fit = ViewportScale.Fit(windowWidth, windowHeight);

        // For windows ≥ base resolution the scaled image fits: both margins are ≥ 0
        // and the doubled offsets plus the scaled size sum back to the window.
        Assert.True(fit.OffsetX >= 0 && fit.OffsetY >= 0,
            $"expected non-negative letterbox, got ({fit.OffsetX},{fit.OffsetY})");
        Assert.Equal(windowWidth - fit.ScaledWidth - 2 * fit.OffsetX, (windowWidth - fit.ScaledWidth) % 2 == 0 ? 0 : 1);
        Assert.Equal(windowHeight - fit.ScaledHeight - 2 * fit.OffsetY, (windowHeight - fit.ScaledHeight) % 2 == 0 ? 0 : 1);
        Assert.InRange(fit.Scale, 1, int.MaxValue);
    }

    // ---- boundary — clamp floor at 1 ---------------------------------------

    [Fact] // boundary — exactly the base resolution is 1× with no letterbox
    public void Base_resolution_is_scale_one()
    {
        var fit = ViewportScale.Fit(ViewportScale.BaseWidth, ViewportScale.BaseHeight);

        Assert.Equal(1, fit.Scale);
        Assert.Equal(0, fit.OffsetX);
        Assert.Equal(0, fit.OffsetY);
        Assert.Equal(640, fit.ScaledWidth);
        Assert.Equal(360, fit.ScaledHeight);
    }

    [Fact] // boundary — one pixel short of any multiple still floors to the lower factor
    public void One_pixel_short_of_double_floors_to_one()
    {
        var fit = ViewportScale.Fit(1279, 719);

        Assert.Equal(1, fit.Scale);
        Assert.Equal((1279 - 640) / 2, fit.OffsetX);
        Assert.Equal((719 - 360) / 2, fit.OffsetY);
    }

    [Fact] // boundary — windows smaller than base clamp to 1 (image exceeds window, offsets negative)
    public void Sub_base_window_clamps_to_scale_one()
    {
        var fit = ViewportScale.Fit(320, 180);

        Assert.Equal(1, fit.Scale);
        Assert.Equal((320 - 640) / 2, fit.OffsetX);
        Assert.Equal((180 - 360) / 2, fit.OffsetY);
        Assert.Equal(640, fit.ScaledWidth);
        Assert.Equal(360, fit.ScaledHeight);
    }

    // ---- illegal — non-positive dimensions rejected -------------------------

    [Theory] // illegal — zero or negative dimensions are refused, not clamped
    [InlineData(0, 360)]
    [InlineData(640, 0)]
    [InlineData(-1, 360)]
    [InlineData(640, -1)]
    public void Non_positive_window_is_rejected(int windowWidth, int windowHeight)
    {
        Assert.Throws<ArgumentException>(() => ViewportScale.Fit(windowWidth, windowHeight));
    }
}
