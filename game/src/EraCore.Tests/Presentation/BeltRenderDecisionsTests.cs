using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-28] [F-127] T-119 传送带双层动画渲染决策表（BeltRenderDecisions）：
// 直线段=UV 滚动 shader（belt-scroll.gdshader）、转弯段=8 帧 tile 动画——
// 决策全在纯逻辑层（T-102 RendererDecisionMap/OffModeDowngrade 先例），
// headless 可全测；相位量 φ=(scroll−origin) mod 16 同时驱动 shader 偏移与
// 帧序号，转弯后 origin +21（=周期 16+5）使两层跨直/弯边界图案锁相。
public class BeltRenderDecisionsTests
{
    private static IReadOnlyList<CellCoord> Cells(params (int X, int Y)[] coords) =>
        coords.Select(c => new CellCoord(c.X, c.Y)).ToArray();

    // ------------------------------------------------ legal — 直线段决策

    [Fact] // legal — 纯直线带：全 Straight、origin 全 0（16 mod 16）、朝向=行进向
    public void Straight_belt_plans_all_straight_cells_with_locked_origin()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((0, 0), (0, -1), (0, -2), (0, -3)));

        Assert.Equal(4, plans.Count);
        Assert.All(plans, p => Assert.Equal(BeltTileKind.Straight, p.Kind));
        Assert.All(plans, p => Assert.Equal(0, p.OriginPx)); // 16k mod 16 = 0
        Assert.All(plans, p => Assert.False(p.FlipX));
        // 行进向北（0,-1）= quarter 2（南 0/西 1/北 2/东 3 的顺时针表）
        Assert.All(plans, p => Assert.Equal(2, p.SpriteRotationQuarters));
        // 首格无入向、末格无出向（链端点单侧格）
        Assert.Equal((0, 0), (plans[0].EntryDx, plans[0].EntryDy));
        Assert.Equal((0, -1), (plans[^1].EntryDx, plans[^1].EntryDy));
        Assert.Equal((0, 0), (plans[^1].ExitDx, plans[^1].ExitDy));
    }

    [Fact] // legal — 四向直线各得正确旋转：南 0 / 西 1 / 北 2 / 东 3
    public void Straight_rotation_follows_travel_quarter_table()
    {
        Assert.Equal(0, BeltRenderDecisions.SpriteRotationQuarters(
            BeltTileKind.Straight, 0, 0, 0, 1));  // 南（canonical）
        Assert.Equal(1, BeltRenderDecisions.SpriteRotationQuarters(
            BeltTileKind.Straight, 0, 0, -1, 0)); // 西
        Assert.Equal(2, BeltRenderDecisions.SpriteRotationQuarters(
            BeltTileKind.Straight, 0, 0, 0, -1)); // 北
        Assert.Equal(3, BeltRenderDecisions.SpriteRotationQuarters(
            BeltTileKind.Straight, 0, 0, 1, 0));  // 东
    }

    // ------------------------------------------------ legal — 转弯段决策

    [Fact] // legal — 右弯（北行转东出）：TurnRight、无镜像、旋转 0、下游 origin +21≡5
    public void Right_turn_classifies_chirality_and_shifts_downstream_origin_by_21()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((0, 0), (0, -1), (1, -1)));

        Assert.Equal(BeltTileKind.Straight, plans[0].Kind);
        Assert.Equal(BeltTileKind.TurnRight, plans[1].Kind);
        Assert.False(plans[1].FlipX);
        Assert.Equal(0, plans[1].SpriteRotationQuarters); // 规范右弯本体（南入北行东出）
        Assert.Equal(BeltTileKind.Straight, plans[2].Kind);
        // origin 累积：cell0 直段 16≡0 → cell1 弯 21≡5 → cell2 从 5 起
        Assert.Equal(0, plans[0].OriginPx);
        Assert.Equal(0, plans[1].OriginPx);
        Assert.Equal(5, plans[2].OriginPx);
    }

    [Fact] // legal — 左弯（北行转西出）：TurnLeft、镜像（FlipX）后同旋转表
    public void Left_turn_classifies_with_mirror_flag()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((0, 0), (0, -1), (-1, -1)));

        Assert.Equal(BeltTileKind.TurnLeft, plans[1].Kind);
        Assert.True(plans[1].FlipX); // 左弯=右弯规范 tile 水平镜像
        Assert.Equal(0, plans[1].SpriteRotationQuarters);
    }

    [Theory] // legal — 全部四个正交右弯（顺时针）与四个左弯（逆时针）
    [InlineData(0, -1, 1, 0, BeltTileKind.TurnRight)]   // 北→东
    [InlineData(1, 0, 0, 1, BeltTileKind.TurnRight)]    // 东→南
    [InlineData(0, 1, -1, 0, BeltTileKind.TurnRight)]   // 南→西
    [InlineData(-1, 0, 0, -1, BeltTileKind.TurnRight)]  // 西→北
    [InlineData(0, -1, -1, 0, BeltTileKind.TurnLeft)]   // 北→西
    [InlineData(-1, 0, 0, 1, BeltTileKind.TurnLeft)]    // 西→南
    [InlineData(0, 1, 1, 0, BeltTileKind.TurnLeft)]     // 南→东
    [InlineData(1, 0, 0, -1, BeltTileKind.TurnLeft)]    // 东→北
    public void All_eight_orthogonal_turns_classify_by_cross_product(
        int entryDx, int entryDy, int exitDx, int exitDy, BeltTileKind expected)
    {
        Assert.Equal(expected, BeltRenderDecisions.Classify(entryDx, entryDy, exitDx, exitDy));
    }

    [Theory] // legal — 转弯旋转表：entry quarter − 2（规范弯 entry=北=2）
    [InlineData(0, -1, 1, 0, 0)]  // 北入右弯 → 0
    [InlineData(1, 0, 0, 1, 1)]   // 东入右弯 → 1
    [InlineData(0, 1, -1, 0, 2)]  // 南入右弯 → 2
    [InlineData(-1, 0, 0, -1, 3)] // 西入右弯 → 3
    public void Turn_rotation_maps_entry_direction_onto_canonical_north(
        int entryDx, int entryDy, int exitDx, int exitDy, int expectedQuarters)
    {
        BeltTileKind kind = BeltRenderDecisions.Classify(entryDx, entryDy, exitDx, exitDy);

        Assert.Equal(
            expectedQuarters,
            BeltRenderDecisions.SpriteRotationQuarters(kind, entryDx, entryDy, exitDx, exitDy));
    }

    [Fact] // legal — 连续两弯：origin 累积 21+21=42≡10（mod 16）
    public void Two_turns_accumulate_origin_modulo_pattern_period()
    {
        // S 形带：北→东（右弯）→南（右弯）→东
        var plans = BeltRenderDecisions.PlanBelt(
            Cells((0, 0), (0, -1), (1, -1), (1, 0), (2, 0)));

        Assert.Equal(BeltTileKind.TurnRight, plans[1].Kind);
        Assert.Equal(BeltTileKind.TurnRight, plans[2].Kind);
        Assert.Equal(BeltTileKind.TurnLeft, plans[3].Kind); // 南行转东出=左弯（cross<0）
        Assert.Equal(0, plans[0].OriginPx);
        Assert.Equal(0, plans[1].OriginPx);
        Assert.Equal(5, plans[2].OriginPx);  // 直 16≡0 后：弯1 起 0
        Assert.Equal(10, plans[3].OriginPx); // 弯1 21 → 21≡5；弯2 起 5
        Assert.Equal(15, plans[4].OriginPx); // 弯2 21 → 5+21=26≡10；末格起 10+21=31≡15
    }

    // ------------------------------------------------ legal — 相位量/帧序号/shader 偏移

    [Theory] // legal — 帧序号 = floor(φ/2)，φ=(scroll−origin) mod 16：8 帧全覆盖
    [InlineData(0.0, 0, 0)]      // φ=0 → 帧 0
    [InlineData(1.99, 0, 0)]     // φ<2 → 帧 0
    [InlineData(2.0, 0, 1)]      // φ=2 → 帧 1
    [InlineData(7.9, 0, 3)]
    [InlineData(15.99, 0, 7)]    // φ<16 → 帧 7（末帧）
    [InlineData(16.0, 0, 0)]     // φ=16≡0 → 帧 0：8 帧循环闭合
    [InlineData(33.0, 5, 6)]     // φ=(33−5)%16=12 → 帧 6
    [InlineData(100.0, 5, 7)]    // φ=(100−5)%16=15 → 帧 7
    public void Frame_index_follows_phase_law_and_loops_every_16_px(
        double scrollPx, int originPx, int expectedFrame)
    {
        Assert.Equal(expectedFrame, BeltRenderDecisions.FrameIndexFor(scrollPx, originPx));
    }

    [Fact] // legal — shader 偏移=φ 原值（0..15 连续，供 UV 滚动直用）
    public void Shader_offset_equals_phase_and_wraps_continuously()
    {
        Assert.Equal(0.0, BeltRenderDecisions.ShaderOffsetPx(0, 0), 5);
        Assert.Equal(3.5, BeltRenderDecisions.ShaderOffsetPx(3.5, 0), 5);
        Assert.Equal(0.0, BeltRenderDecisions.ShaderOffsetPx(16, 0), 5);
        Assert.Equal(12.0, BeltRenderDecisions.ShaderOffsetPx(33, 5), 5); // (33−5)%16
    }

    [Fact] // legal — 负向滚动（倒带/暂停回退）同样包裹到 [0,16)
    public void Phase_wraps_negative_scroll_into_range()
    {
        Assert.Equal(15.0, BeltRenderDecisions.PhasePx(-1, 0), 5);
        Assert.Equal(14.0, BeltRenderDecisions.PhasePx(-1, 1), 5);
        Assert.Equal(12.0, BeltRenderDecisions.PhasePx(-20, 0), 5); // −20%16=−4 → +16=12
    }

    [Fact] // legal — 直/弯两层同 φ 锁相：帧序号变化点恰为 φ 跨偶数边界
    public void Frame_and_shader_share_one_phase_quantity()
    {
        for (double scroll = 0; scroll < 32; scroll += 0.25)
        {
            double phase = BeltRenderDecisions.PhasePx(scroll, 5);
            Assert.Equal(phase, BeltRenderDecisions.ShaderOffsetPx(scroll, 5), 5);
            Assert.Equal(
                (int)(phase / BeltRenderDecisions.TurnPxPerFrame),
                BeltRenderDecisions.FrameIndexFor(scroll, 5));
        }
    }

    [Fact] // legal — 帧几何冻结常量与合成器契约一致（16px tile/8 帧/2px 每帧/21px 弯）
    public void Frozen_geometry_constants_match_synthesizer_contract()
    {
        Assert.Equal(16, BeltRenderDecisions.BodyTilePx);
        Assert.Equal(16, BeltRenderDecisions.PatternPeriodPx);
        Assert.Equal(16, BeltRenderDecisions.StraightPathPx);
        Assert.Equal(21, BeltRenderDecisions.TurnPathPx);
        Assert.Equal(8, BeltRenderDecisions.TurnFrameCount);
        Assert.Equal(2, BeltRenderDecisions.TurnPxPerFrame);
        // 8 帧 × 2px = 16px = 图案周期 → 循环无缝闭合（合成器 u 律同式）
        Assert.Equal(
            BeltRenderDecisions.PatternPeriodPx,
            BeltRenderDecisions.TurnFrameCount * BeltRenderDecisions.TurnPxPerFrame);
        Assert.Equal(BeltRenderDecisions.StraightPathPx, BeltRenderDecisions.PathLengthPx(BeltTileKind.Straight));
        Assert.Equal(BeltRenderDecisions.TurnPathPx, BeltRenderDecisions.PathLengthPx(BeltTileKind.TurnLeft));
        Assert.Equal(BeltRenderDecisions.TurnPathPx, BeltRenderDecisions.PathLengthPx(BeltTileKind.TurnRight));
    }

    // ------------------------------------------------ boundary

    [Fact] // boundary — 最小两格带：两端点单侧格均判 Straight
    public void Minimal_two_cell_belt_plans_two_endpoint_cells()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((3, 4), (3, 5)));

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Equal(BeltTileKind.Straight, p.Kind));
        Assert.Equal((0, 0), (plans[0].EntryDx, plans[0].EntryDy));
        Assert.Equal((0, 0), (plans[^1].ExitDx, plans[^1].ExitDy));
        Assert.Equal(0, plans[0].SpriteRotationQuarters); // 行进南=canonical 0
        Assert.Equal(0, plans[1].SpriteRotationQuarters); // 末格回退用入向
    }

    [Fact] // boundary — 对角链合法（8 邻接）但正交 tile 集无 45° 画法：旋转=−1 显式上报
    public void Diagonal_chain_is_straight_but_has_no_quarter_rotation()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((0, 0), (1, -1), (2, -2)));

        Assert.All(plans, p => Assert.Equal(BeltTileKind.Straight, p.Kind));
        Assert.All(plans, p => Assert.Equal(-1, p.SpriteRotationQuarters));
    }

    [Fact] // boundary — 180° 折返显式上报 Reversal（不静默伪装成弯）
    public void Opposite_entry_exit_reports_reversal_kind()
    {
        var plans = BeltRenderDecisions.PlanBelt(Cells((0, 0), (0, -1), (0, 0)));

        Assert.Equal(BeltTileKind.Reversal, plans[1].Kind);
        Assert.Equal(16, BeltRenderDecisions.PathLengthPx(BeltTileKind.Reversal));
        Assert.Equal(0, plans[2].OriginPx); // 直 16 + 折返 16 = 32 ≡ 0 (mod 16)
    }

    [Fact] // boundary — φ 上/下边界：origin=15、φ=0 与 φ=15.99
    public void Phase_bounds_cover_zero_to_just_under_period()
    {
        Assert.Equal(0.0, BeltRenderDecisions.PhasePx(15, 15), 5);
        Assert.Equal(15.5, BeltRenderDecisions.PhasePx(15.5, 0), 5);
        Assert.Equal(7, BeltRenderDecisions.FrameIndexFor(15.99, 0));
    }

    // ------------------------------------------------ legal — 皮肤档位（W7 E4 视觉接线 F1）

    [Fact] // legal — [F-042] 档位→皮肤 stem：15/s（E4 传送带档）换装 era4、10/s（E3 矿轨档）保持 era3
    public void Skin_stem_switches_to_era4_at_the_15_per_s_conveyor_gear()
    {
        Assert.Equal("era4", BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era4GearItemsPerSecond));
        Assert.Equal("era3", BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era3GearItemsPerSecond));
        // 档位锚=冻结阶梯第 4 档位值（v3.2 §5 L78 10→15；era.json tier=3 行同值）
        Assert.Equal(10.0, BeltRenderDecisions.Era3GearItemsPerSecond);
        Assert.Equal(15.0, BeltRenderDecisions.Era4GearItemsPerSecond);
    }

    [Theory] // legal — [F-042] 未交付上档（20/∞=E5+）乘最新已交付皮肤；低于边界乘 E3 皮肤
    [InlineData(12.5, "era4")]  // 边界本身=切换点（含）
    [InlineData(13.0, "era4")]
    [InlineData(20.0, "era4")]  // 阶梯第 5 档（E5 皮肤 W8 T-142 前乘 era4）
    [InlineData(double.MaxValue, "era4")] // ∞ 档同乘最新
    [InlineData(12.49, "era3")]
    [InlineData(0.6, "era3")]   // 低位档乘最近已交付皮肤
    [InlineData(0.0, "era3")]
    public void Skin_stem_boundary_and_undelivered_upper_gears_ride_the_newest_skin(
        double throughputItemsPerSecond, string expectedStem)
    {
        Assert.Equal(expectedStem, BeltRenderDecisions.BeltSkinStem(throughputItemsPerSecond));
        Assert.Equal(12.5, BeltRenderDecisions.SkinSwitchItemsPerSecond); // 冻结边界=(10+15)/2
    }

    // ------------------------------------------------ illegal

    [Fact] // illegal — NaN 速率拒绝（垃圾速率无皮肤，不静默回落）
    public void NaN_skin_gear_rate_throws()
    {
        Assert.Throws<ArgumentException>(
            () => BeltRenderDecisions.BeltSkinStem(double.NaN));
    }

    [Fact] // illegal — null 链拒绝
    public void Null_cells_throw()
    {
        Assert.Throws<ArgumentNullException>(() => BeltRenderDecisions.PlanBelt(null!));
    }

    [Fact] // illegal — 单格链拒绝（ConveyorBelt 同款最小 2 格）
    public void Single_cell_chain_throws()
    {
        Assert.Throws<ArgumentException>(() => BeltRenderDecisions.PlanBelt(Cells((0, 0))));
    }

    [Theory] // illegal — 非邻接步（|dx|>1）与零步（重复格）拒绝
    [InlineData(0, 0, 2, 0)]  // 跳两格
    [InlineData(0, 0, 0, 0)]  // 原地重复
    [InlineData(0, 0, 2, 1)]  // 双轴越界
    public void Non_adjacent_steps_throw(int ax, int ay, int bx, int by)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => BeltRenderDecisions.PlanBelt(Cells((ax, ay), (bx, by))));
        Assert.Contains("8-neighbour-adjacent", ex.Message);
    }

    [Theory] // illegal — originPx 越界拒绝（相位必须已是 0..15）
    [InlineData(-1)]
    [InlineData(16)]
    public void Out_of_range_origin_throws(int originPx)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BeltRenderDecisions.PhasePx(0, originPx));
    }
}
