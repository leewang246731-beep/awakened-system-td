using EraCore.Contracts.Foundation;

namespace EraCore.Presentation;

/// <summary>
/// Belt tile kind for one cell of a <see cref="Logistics.ConveyorBelt"/> chain
/// (T-119, F-127 传送带双层动画). Straight cells render through the UV-scroll
/// shader (belt body flows + static rails); turn cells render the 8-frame
/// turn strip + static corner-edge overlay.
/// </summary>
public enum BeltTileKind
{
    /// <summary>直线段：belt-scroll.gdshader UV 滚动（带体+带边双层）.</summary>
    Straight,

    /// <summary>左弯（逆时针，屏幕坐标 y 向下）：转弯 8 帧 tile + 弯口静态边框（镜像）.</summary>
    TurnLeft,

    /// <summary>右弯（顺时针）：转弯 8 帧 tile + 弯口静态边框.</summary>
    TurnRight,

    /// <summary>
    /// 180° 折返（出向 = 入向反向）：合法链（8 邻接允许）但无单 tile 画法——
    /// 单格内带体原地折返，直线 shader 与转弯帧都不适用。决策层显式上报
    /// （不静默伪装成弯），渲染侧降级=按 TurnLeft 画（降级处置见 41 号 §5）；
    /// 在关卡通行的正交带链（levels 数据）不产生该形态。
    /// </summary>
    Reversal,
}

/// <summary>
/// Render plan for ONE belt cell (T-119): everything the presentation layer
/// needs to draw it — which of the two F-127 layers points where, and the
/// pattern phase origin that keeps the scroll shader and the 8-frame turn
/// animation phase-locked along the whole chain. Pure data, derived only
/// from the cell chain (head→tail order, <see cref="Logistics.ConveyorBelt"/>
/// .Cells) — the Godot side mirrors it, never re-derives it
/// (OffModeDowngrade / RendererDecisionMap 先例：决策全在纯逻辑层,
/// headless 可全测).
/// </summary>
/// <param name="Index">Cell index in the chain (0 = head/source end).</param>
/// <param name="Kind">Tile kind (straight / left / right / reversal).</param>
/// <param name="EntryDx">Travel direction INTO the cell x (0,0 on the head cell, which has no upstream).</param>
/// <param name="EntryDy">Travel direction INTO the cell y.</param>
/// <param name="ExitDx">Travel direction OUT of the cell x (0,0 on the tail cell, which has no downstream).</param>
/// <param name="ExitDy">Travel direction OUT of the cell y.</param>
/// <param name="OriginPx">Pattern phase origin (0..15): accumulated path length from the chain head mod the 16 px pattern period. Straight shader scroll = (scroll − origin) mod 16; turn frame = floor(((scroll − origin) mod 16) / 2).</param>
/// <param name="SpriteRotationQuarters">Clockwise quarter turns for the sprite (canonical: straight travels south, turn enters from the south edge heading north). −1 when the cell involves diagonal steps — the orthogonal tile set does not cover diagonals (diagonal chains stay legal in the sim; pack art covers the canonical orthogonal case, 41 号 §5).</param>
/// <param name="FlipX">Mirror the sprite horizontally before rotating (left turns mirror the right-turn canonical tile).</param>
public sealed record BeltCellPlan(
    int Index,
    BeltTileKind Kind,
    int EntryDx,
    int EntryDy,
    int ExitDx,
    int ExitDy,
    int OriginPx,
    int SpriteRotationQuarters,
    bool FlipX);

/// <summary>
/// Pure belt-render decision table (T-119 acceptance: headless 渲染决策层
/// 正确指向——T-102 决策表先例 + 传送带类型输入). Turns a conveyor chain
/// (8-neighbour-adjacent cells, head→tail) into per-cell render plans and
/// converts the live surface scroll into the two F-127 layer inputs:
/// the straight-segment shader offset (UV 滚动量) and the turn-segment frame
/// index (8 帧循环). The frame geometry constants are the frozen contract the
/// procedural synthesizer <c>tools/art/era3_placeholders.py</c> generates
/// against and <c>Era3ArtMappingTests</c> re-asserts pixel-level.
/// </summary>
public static class BeltRenderDecisions
{
    /// <summary>Belt body source tile edge in px (32 px cell = exact 2x nearest scale, terrain-atlas baseline).</summary>
    public const int BodyTilePx = 16;

    /// <summary>Belt surface pattern period in px (= body tile edge; PAT16 in the synthesizer).</summary>
    public const int PatternPeriodPx = 16;

    /// <summary>Path length a STRAIGHT cell advances the pattern origin: one full period ⇒ downstream straight cells keep the origin.</summary>
    public const int StraightPathPx = 16;

    /// <summary>
    /// Path length a TURN cell advances the pattern origin (u ∈ [0,20] along
    /// the L band: 竖臂 5 + 弯心 11 + 横臂 5 = 21). 21 = period 16 + 5, so a
    /// turn shifts downstream phase by 5 px — the synthesizer draws the same
    /// u-law (frame k body pixel = PAT16[(u − 2k) mod 16]).
    /// </summary>
    public const int TurnPathPx = 21;

    /// <summary>Turn animation frame count (F-127/14 §3: 转弯 8 帧双层 tile 动画).</summary>
    public const int TurnFrameCount = 8;

    /// <summary>Pattern px advanced per turn frame: 8 frames × 2 px = 16 px = one period ⇒ the loop closes seamlessly.</summary>
    public const int TurnPxPerFrame = 2;

    /// <summary>
    /// Plans every cell of a belt chain (head→tail, the exact order
    /// <see cref="Logistics.ConveyorBelt.Cells"/> exposes). Entry direction
    /// of cell 0 and exit direction of the last cell are (0,0).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is null.</exception>
    /// <exception cref="ArgumentException">Fewer than two cells (ConveyorBelt minimum), a non-adjacent or zero step (same validation as the belt constructor — a chain the sim rejects has no render plan either).</exception>
    public static IReadOnlyList<BeltCellPlan> PlanBelt(IReadOnlyList<CellCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count < 2)
        {
            throw new ArgumentException("A belt render plan needs at least two cells.", nameof(cells));
        }

        // Steps: step[i] = travel direction cells[i] → cells[i+1].
        (int Dx, int Dy)[] steps = new (int, int)[cells.Count - 1];
        for (int i = 0; i < steps.Length; i++)
        {
            int dx = cells[i + 1].X - cells[i].X;
            int dy = cells[i + 1].Y - cells[i].Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0))
            {
                throw new ArgumentException(
                    $"cells[{i}]→cells[{i + 1}] step ({dx},{dy}) is not 8-neighbour-adjacent.",
                    nameof(cells));
            }

            steps[i] = (dx, dy);
        }

        var plans = new BeltCellPlan[cells.Count];
        int origin = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            (int inDx, int inDy) = i == 0 ? (0, 0) : steps[i - 1];
            (int outDx, int outDy) = i == steps.Length ? (0, 0) : steps[i];
            BeltTileKind kind = Classify(inDx, inDy, outDx, outDy);
            plans[i] = new BeltCellPlan(
                i,
                kind,
                inDx, inDy,
                outDx, outDy,
                origin,
                SpriteRotationQuarters(kind, inDx, inDy, outDx, outDy),
                FlipX: kind == BeltTileKind.TurnLeft);
            origin = (origin + PathLengthPx(kind)) % PatternPeriodPx;
        }

        return plans;
    }

    /// <summary>
    /// Classifies one cell from its entry/exit travel directions (pure
    /// geometry, any of the 8 neighbour directions): same direction (or a
    /// one-sided endpoint direction) ⇒ Straight; cross product &gt; 0 ⇒
    /// TurnRight (clockwise, screen y-down); &lt; 0 ⇒ TurnLeft; opposite
    /// vectors ⇒ <see cref="BeltTileKind.Reversal"/>.
    /// </summary>
    public static BeltTileKind Classify(int entryDx, int entryDy, int exitDx, int exitDy)
    {
        if ((entryDx == 0 && entryDy == 0) || (exitDx == 0 && exitDy == 0))
        {
            return BeltTileKind.Straight; // chain endpoint: single-sided cell
        }

        if (entryDx == exitDx && entryDy == exitDy)
        {
            return BeltTileKind.Straight;
        }

        int cross = entryDx * exitDy - entryDy * exitDx;
        if (cross == 0)
        {
            // Non-equal, collinear ⇒ opposite vectors: 180° fold.
            return BeltTileKind.Reversal;
        }

        return cross > 0 ? BeltTileKind.TurnRight : BeltTileKind.TurnLeft;
    }

    /// <summary>Pattern-origin advance per cell kind (straight 16 / turns 21; reversal has no tile art — contributes the straight length).</summary>
    public static int PathLengthPx(BeltTileKind kind) => kind switch
    {
        BeltTileKind.Straight => StraightPathPx,
        BeltTileKind.TurnLeft => TurnPathPx,
        BeltTileKind.TurnRight => TurnPathPx,
        BeltTileKind.Reversal => StraightPathPx,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown belt tile kind."),
    };

    /// <summary>
    /// The shared phase quantity (px, 0..15) both F-127 layers consume:
    /// φ = (scroll − origin) mod 16. Straight shader offset = φ (feed the
    /// belt-scroll.gdshader scroll_px uniform); turn frame = floor(φ / 2).
    /// Same φ ⇒ the shader surface and the 8-frame tiles stay pattern-locked
    /// across straight/turn boundaries.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originPx"/> outside 0..15.</exception>
    public static double PhasePx(double scrollPx, int originPx)
    {
        if (originPx is < 0 or >= PatternPeriodPx)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originPx), originPx, $"origin must be a phase in 0..{PatternPeriodPx - 1}.");
        }

        double phase = scrollPx - originPx;
        phase %= PatternPeriodPx;
        return phase < 0 ? phase + PatternPeriodPx : phase;
    }

    /// <summary>
    /// Straight-segment shader scroll (px, 0..15): feed straight to the
    /// belt-scroll.gdshader <c>scroll_px</c> uniform. Equal to
    /// <see cref="PhasePx"/> (documented alias — the shader consumes the
    /// phase directly).
    /// </summary>
    public static double ShaderOffsetPx(double scrollPx, int originPx) => PhasePx(scrollPx, originPx);

    /// <summary>
    /// Turn-segment frame index (0..7): floor(φ / 2) with φ from
    /// <see cref="PhasePx"/> — frame k's pre-rendered tile shows pattern
    /// (u − 2k) mod 16 at path coordinate u (synthesizer u-law), so frame
    /// advance and shader scroll share one clock and one phase origin.
    /// </summary>
    public static int FrameIndexFor(double scrollPx, int originPx) =>
        (int)(PhasePx(scrollPx, originPx) / TurnPxPerFrame);

    /// <summary>
    /// Clockwise quarter turns for the sprite. Canonical tiles: straight
    /// travels SOUTH; turn enters from the south edge heading NORTH and
    /// exits EAST (right turn; left turns mirror via <see cref="BeltCellPlan.FlipX"/>
    /// then rotate). Returns −1 when entry or exit is diagonal — the
    /// orthogonal quarter grid has no mapping for 45° steps.
    /// </summary>
    public static int SpriteRotationQuarters(
        BeltTileKind kind, int entryDx, int entryDy, int exitDx, int exitDy)
    {
        // Quarter index of a unit travel vector under CLOCKWISE screen
        // rotation: south 0, west 1, north 2, east 3 (rotating the image CW
        // by one quarter moves the canonical south travel to west).
        static int Quarter(int dx, int dy) => (dx, dy) switch
        {
            (0, 1) => 0,
            (-1, 0) => 1,
            (0, -1) => 2,
            (1, 0) => 3,
            _ => -1, // diagonal or empty
        };

        switch (kind)
        {
            case BeltTileKind.Straight or BeltTileKind.Reversal:
            {
                int travelQuarter = Quarter(exitDx, exitDy);
                if (travelQuarter < 0)
                {
                    travelQuarter = Quarter(entryDx, entryDy); // tail cell: travel from entry
                }

                return travelQuarter; // canonical straight travels south (quarter 0)
            }

            case BeltTileKind.TurnLeft or BeltTileKind.TurnRight:
            {
                // Canonical turn entry = north (quarter 2); rotating the
                // (mirrored for left) tile CW by q moves entry to the actual.
                int entryQuarter = Quarter(entryDx, entryDy);
                return entryQuarter < 0 ? -1 : (entryQuarter - 2 + 4) % 4;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown belt tile kind.");
        }
    }
}
