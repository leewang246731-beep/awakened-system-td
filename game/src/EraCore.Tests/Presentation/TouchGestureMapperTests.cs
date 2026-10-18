using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-23][T-141] 触控映射（TouchGestureMapper）：合成触控事件（哪根手指、
// 按下/抬起/移动、位置 px）→ 决策流 → **既有**建造命令流（BuildDrag
// Controller+PlayerBuildLane 同一咽喉）——只加输入映射、不改命令语义的
// 证据：①单指 press/drag/release 映射鼠标左键三段（BeginDrag/UpdateDrag/
// Commit）且产出与鼠标同格集同命令；②第二指按下=防误触取消+双指手势
// 分类（缩放/平移事实）；③手势期任何单指事件不出建造命令；④非法/乱序
// 事件（重复按下、未知抬起、未按下移动）全部惰性化——永不异常、永不
// 幻影提交。
public class TouchGestureMapperTests
{
    private const string WoodArrow = "era1:tower-wood-arrow";

    // 格心像素（Cell=Simulation.CellSizePx）——与 BuildInteractionUi 同换算。
    private static double Px(int cell) => cell * Simulation.CellSizePx + Simulation.CellSizePx / 2f;

    private static CellCoord CellAt(double worldX, double worldY) => new(
        (int)Math.Floor(worldX / Simulation.CellSizePx),
        (int)Math.Floor(worldY / Simulation.CellSizePx));

    private static (PipelineHarness Harness, PlayerBuildLane Lane, BuildDragController Controller) MakeLane()
    {
        var harness = new PipelineHarness();
        PlayerBuildLane lane = PlayerBuildLane.OverSimulation(
            harness.Simulation, PipelineHarness.Config(), () => PipelineHarness.AgentEra,
            // F5：SequencingJournalWriter 包装——与生产侧 BuildInteractionUi 接线同形。
            new SequencingJournalWriter(harness.Journal), harness.LogSink);
        return (harness, lane, new BuildDragController(
            new SimulationActionView(harness.Simulation, PipelineHarness.Config()),
            () => PipelineHarness.AgentEra));
    }

    // ---- legal — 单指三段=鼠标左键同一命令流 -------------------------------

    [Fact] // legal：单指按下→移动→抬起 = BuildPress→BuildDrag→BuildCommit，
           // 决策驱动既有 BuildDragController+PlayerBuildLane 发出真建造命令
    public void Single_finger_press_drag_release_maps_onto_the_existing_build_command_flow()
    {
        var mapper = new TouchGestureMapper();
        (PipelineHarness harness, PlayerBuildLane lane, BuildDragController controller) = MakeLane();

        // ① 单指按下（BuildInteractionUi 触控分支同款应用：BuildPress→BeginDrag）
        TouchGestureDecision press = mapper.Touch(0, Px(11), Px(11), pressed: true);
        Assert.Equal(TouchGestureKind.BuildPress, press.Kind);
        Assert.Equal(1, press.FingerCount);
        controller.BeginDrag(WoodArrow, CellAt(Px(11), Px(11)));

        // ② 单指移动（BuildDrag→UpdateDrag：1×2 矩形）
        TouchGestureDecision drag = mapper.Drag(0, Px(12), Px(11));
        Assert.Equal(TouchGestureKind.BuildDrag, drag.Kind);
        controller.UpdateDrag(CellAt(Px(12), Px(11)));
        Assert.Equal(BuildDragState.Dragging, controller.State);

        // ③ 单指抬起（BuildCommit→Commit→PlayerBuildLane.Build 同一咽喉）
        TouchGestureDecision release = mapper.Touch(0, Px(12), Px(11), pressed: false);
        Assert.Equal(TouchGestureKind.BuildCommit, release.Kind);
        Assert.Equal(0, release.FingerCount);
        BuildDragCommit? commit = controller.Commit();
        Assert.NotNull(commit);
        PlayerBuildOutcome outcome = lane.Build(commit!.TowerId, commit.Cells);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(WoodArrow, commit.TowerId);
        Assert.Contains(new CellCoord(11, 11), commit.Cells); // 锚格（夹具已证绿格）在内
        Assert.True(harness.Simulation.Towers.Count >= 1, "建造命令已真实落子");

        // K-8 玩家事件落流（生产侧同判据：PayloadJson 含 "Actor":"player"；
        // ReadAll 携带 ReadWrite 共享——活跃 writer 句柄下可读）
        Assert.Contains(
            JsonlJournalWriter.ReadAll(harness.JournalPath),
            evt => evt.PayloadJson.Contains("\"Actor\":\"player\"", StringComparison.Ordinal));

        // 语义保持证据：同事件流经鼠标三段（BeginDrag/UpdateDrag/Commit）在
        // 孪生夹具上产出同一格集——触控≠第二套命令语义。
        (_, PlayerBuildLane lane2, BuildDragController mouse) = MakeLane();
        mouse.BeginDrag(WoodArrow, CellAt(Px(11), Px(11)));
        mouse.UpdateDrag(CellAt(Px(12), Px(11)));
        BuildDragCommit? mouseCommit = mouse.Commit();
        Assert.NotNull(mouseCommit);
        Assert.Equal(commit.Cells, mouseCommit!.Cells); // 同格集=同命令载荷
        Assert.Equal(ActionResultStatus.Ok, lane2.Build(mouseCommit.TowerId, mouseCommit.Cells).Result.Status);
    }

    // ---- boundary — 双指手势与恢复 ------------------------------------------

    [Fact] // boundary：armed 拖放中第二指按下=BuildCancel（防误触）+双指事实；
    //        手势中移动=TwoFingerPanZoom（span/center 增量）；先抬主指不出提交；
    //        全抬后单指恢复建造映射
    public void Second_finger_cancels_build_then_owns_the_gesture_until_both_release()
    {
        var mapper = new TouchGestureMapper();

        TouchGestureDecision press = mapper.Touch(0, 100, 100, pressed: true);
        Assert.Equal(TouchGestureKind.BuildPress, press.Kind);

        // 第二指按下：BuildCancel+span 携带（纯手势事实，与建造格无关）
        TouchGestureDecision second = mapper.Touch(1, 300, 100, pressed: true);
        Assert.Equal(TouchGestureKind.BuildCancel, second.Kind);
        Assert.Equal(2, second.FingerCount);
        Assert.Equal(200, second.PinchSpanPx, 3); // |300-100|=200 px
        Assert.Equal("two-finger-start", second.Note);
        Assert.True(mapper.TwoFingerActive);

        // 张开双指：TwoFingerPanZoom——span 200→400（缩放比可换算），中心右移
        TouchGestureDecision spread = mapper.Drag(1, 500, 100);
        Assert.Equal(TouchGestureKind.TwoFingerPanZoom, spread.Kind);
        Assert.Equal(200, spread.PreviousPinchSpanPx, 3);
        Assert.Equal(400, spread.PinchSpanPx, 3);
        Assert.Equal(100, spread.CenterDeltaX, 3); // 中心 (200,100)→(300,100)

        // 平移单指：新 span=350（150↔500），中心 (325,100)——增量=25
        TouchGestureDecision pan = mapper.Drag(0, 150, 100);
        Assert.Equal(TouchGestureKind.TwoFingerPanZoom, pan.Kind);
        Assert.Equal(350, pan.PinchSpanPx, 3);
        Assert.Equal(25, pan.CenterDeltaX, 3);

        // 先抬主指：无提交（手势继续）；再抬副指：手势结束
        Assert.Equal(TouchGestureKind.None, mapper.Touch(0, 150, 100, pressed: false).Kind);
        Assert.True(mapper.TwoFingerActive, "一指仍在场=手势继续");
        TouchGestureDecision end = mapper.Touch(1, 500, 100, pressed: false);
        Assert.Equal(TouchGestureKind.None, end.Kind);
        Assert.Equal("two-finger-end", end.Note);
        Assert.False(mapper.TwoFingerActive);

        // 手势后单指恢复建造映射
        Assert.Equal(TouchGestureKind.BuildPress, mapper.Touch(0, 100, 100, pressed: true).Kind);
        Assert.Equal(TouchGestureKind.BuildCommit, mapper.Touch(0, 100, 100, pressed: false).Kind);
    }

    [Fact] // boundary：三指同压仍手势域（第三指不产生任何建造决策）
    public void Third_finger_stays_in_gesture_domain()
    {
        var mapper = new TouchGestureMapper();
        mapper.Touch(0, 100, 100, pressed: true);
        mapper.Touch(1, 200, 100, pressed: true);
        TouchGestureDecision third = mapper.Touch(5, 400, 400, pressed: true);
        Assert.Equal(TouchGestureKind.None, third.Kind);
        Assert.Equal(3, third.FingerCount);
        Assert.Equal(TouchGestureKind.TwoFingerPanZoom, mapper.Drag(5, 410, 400).Kind);
    }

    // ---- illegal — 乱序/重复事件惰性化 --------------------------------------

    [Fact] // illegal：重复按下/未知抬起/未按下移动——全部 None，无异常无幻影提交
    public void Malformed_event_streams_are_inert()
    {
        var mapper = new TouchGestureMapper();

        // 未按下就移动
        Assert.Equal(TouchGestureKind.None, mapper.Drag(0, 10, 10).Kind);
        // 同指重复按下
        mapper.Touch(0, 100, 100, pressed: true);
        Assert.Equal(TouchGestureKind.None, mapper.Touch(0, 120, 120, pressed: true).Kind);
        // 未知指抬起
        Assert.Equal(TouchGestureKind.None, mapper.Touch(9, 10, 10, pressed: false).Kind);

        // 手势残局：一指在场（_twoFinger 未清）——在场指移动=等第二指；离场指移动=未知
        mapper.Touch(1, 200, 200, pressed: true); // 第二指→BuildCancel（进手势）
        Assert.Equal(TouchGestureKind.None, mapper.Touch(1, 200, 200, pressed: false).Kind); // 回单指但手势未清
        Assert.Equal(TouchGestureKind.None, mapper.Drag(0, 210, 210).Kind); // gesture-waiting
        Assert.Equal(TouchGestureKind.None, mapper.Drag(1, 210, 210).Kind); // 未按下

        mapper.Reset();
        Assert.Equal(0, mapper.ActiveFingerCount);
        Assert.False(mapper.TwoFingerActive);

        // Reset 后新会话正常
        Assert.Equal(TouchGestureKind.BuildPress, mapper.Touch(0, 100, 100, pressed: true).Kind);
    }

    [Fact] // boundary：双指手势全程零建造决策（防误触核心安全性质）
    public void Two_finger_session_never_emits_any_build_decision()
    {
        var mapper = new TouchGestureMapper();
        var kinds = new List<TouchGestureKind>();
        kinds.Add(mapper.Touch(0, 100, 100, pressed: true).Kind); // 手势开始前的一次 press
        kinds.Add(mapper.Touch(1, 300, 100, pressed: true).Kind); // 取消+手势开始
        kinds.Add(mapper.Drag(0, 120, 100).Kind);
        kinds.Add(mapper.Drag(1, 320, 100).Kind);
        kinds.Add(mapper.Touch(0, 120, 100, pressed: false).Kind);
        kinds.Add(mapper.Drag(1, 330, 100).Kind); // 剩一指仍手势域
        kinds.Add(mapper.Touch(1, 330, 100, pressed: false).Kind);

        // 手势期内零 BuildPress/BuildDrag/BuildCommit（取消本身就是"撤建造"）。
        Assert.Contains(TouchGestureKind.BuildCancel, kinds);
        Assert.DoesNotContain(TouchGestureKind.BuildCommit, kinds);
        Assert.DoesNotContain(TouchGestureKind.BuildDrag, kinds);
        Assert.Equal(1, kinds.Count(k => k == TouchGestureKind.BuildPress)); // 只有开始前那一次
    }
}
