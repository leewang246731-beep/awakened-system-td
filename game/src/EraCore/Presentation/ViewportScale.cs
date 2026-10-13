namespace EraCore.Presentation;

/// <summary>
/// Result of fitting the fixed 640×360 design resolution into a window:
/// integer scale factor plus the letterbox offsets that center the scaled
/// image (C-21 render baseline, F-125). Immutable value type.
/// </summary>
/// <param name="Scale">Integer scale factor, always ≥ 1.</param>
/// <param name="OffsetX">X offset of the scaled image inside the window (px).</param>
/// <param name="OffsetY">Y offset of the scaled image inside the window (px).</param>
/// <param name="ScaledWidth">640 × <paramref name="Scale"/>.</param>
/// <param name="ScaledHeight">360 × <paramref name="Scale"/>.</param>
public readonly record struct ViewportFit(
    int Scale,
    int OffsetX,
    int OffsetY,
    int ScaledWidth,
    int ScaledHeight);

/// <summary>
/// Integer-scale math for the fixed 640×360 design resolution (C-21, F-125).
/// Pure functions only — no engine types (D-01) — so the project.godot
/// stretch settings (canvas_items + keep + integer), the DebugLabel readout
/// and any future UI code share ONE testable source of truth for the scale
/// decision: floor(min(w/640, h/360)) clamped to ≥ 1, remainder split evenly
/// to center the scaled image. When the window is smaller than the base
/// resolution the scale clamps to 1 and offsets go negative (image larger
/// than the window — the engine clips); that behavior is intentional and
/// asserted by tests.
/// </summary>
public static class ViewportScale
{
    /// <summary>Design-time render resolution width (px).</summary>
    public const int BaseWidth = 640;

    /// <summary>Design-time render resolution height (px).</summary>
    public const int BaseHeight = 360;

    /// <summary>
    /// Computes the integer scale factor and centering offsets for a window
    /// of <paramref name="windowWidth"/> × <paramref name="windowHeight"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Non-positive window dimension.</exception>
    public static ViewportFit Fit(int windowWidth, int windowHeight)
    {
        if (windowWidth <= 0)
        {
            throw new ArgumentException($"window width must be positive, got {windowWidth}.", nameof(windowWidth));
        }

        if (windowHeight <= 0)
        {
            throw new ArgumentException($"window height must be positive, got {windowHeight}.", nameof(windowHeight));
        }

        // Integer division is floor for positive operands: the largest k with
        // 640k ≤ w and 360k ≤ h, clamped to ≥ 1 so tiny windows still render.
        int scale = Math.Max(1, Math.Min(windowWidth / BaseWidth, windowHeight / BaseHeight));

        int scaledWidth = BaseWidth * scale;
        int scaledHeight = BaseHeight * scale;

        // Remainder split; odd remainders floor left/top (documented bias).
        return new ViewportFit(
            Scale: scale,
            OffsetX: (windowWidth - scaledWidth) / 2,
            OffsetY: (windowHeight - scaledHeight) / 2,
            ScaledWidth: scaledWidth,
            ScaledHeight: scaledHeight);
    }
}
