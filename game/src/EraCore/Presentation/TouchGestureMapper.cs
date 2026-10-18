namespace EraCore.Presentation;

// T-141（移动端触控原型，08 W8 批 6；49 号文 §3 输入面）：把触控事实
// （哪根手指、按下/抬起/移动、位置 px）翻译成对既有建造交互的调用决策。
// 纯逻辑（CS-1.4/CS-1.1：无引擎类型、无时钟、无随机）——Godot 侧
// BuildInteractionUi 只做事件翻译（InputEventScreenTouch/Drag → 本类决策
// → 既有鼠标面同一控制器/泳道）。**只加输入映射，不改命令语义**：
// BuildPress/BuildDrag/BuildCommit 分别对应鼠标左键按下/拖动/松开的
// BeginDrag/UpdateDrag/CommitDrag 逐字同参（提交仍走 PlayerBuildLane→
// ActionRegistry→SimCommand 同一咽喉）。
//
// 映射方案（49 号文 §3，先例=快捷轮盘 F-116）：
//   单指按下→BuildPress（=格选锚定，单格=1×1 矩形与鼠标同一交互）；
//   单指移动→BuildDrag（=矩形扩选）；
//   单指抬起→BuildCommit（=松开提交）；
//   第二指按下→BuildCancel（**防误触**：双指手势期绝不建造——先撤预览）；
//   双指在场中移动→TwoFingerPanZoom（缩放/平移增量；非建造事实）；
//   双指全部抬起→手势结束（None+Note），下次单指按下恢复建造映射。
//   长按→快捷轮盘（F-116 先例）为报告建议项，原型不实现（无时钟依赖）。

/// <summary>One translated touch decision: which existing UI action the event maps to.</summary>
public enum TouchGestureKind
{
    /// <summary>Nothing to do (inert event, or mid-gesture finger bookkeeping).</summary>
    None = 0,

    /// <summary>Single finger pressed → UI calls <c>BuildDragController.BeginDrag</c> (same as mouse left press).</summary>
    BuildPress = 1,

    /// <summary>Single finger moved → UI calls <c>BuildDragController.UpdateDrag</c> (same as mouse motion).</summary>
    BuildDrag = 2,

    /// <summary>Single finger released → UI calls <c>BuildDragController.Commit</c> lane submit (same as mouse release).</summary>
    BuildCommit = 3,

    /// <summary>A second finger went down → UI cancels the build preview (mis-touch guard; the gesture takes over).</summary>
    BuildCancel = 4,

    /// <summary>A finger moved while the two-finger gesture is active → pan/zoom facts only, never a build command.</summary>
    TwoFingerPanZoom = 5,
}

/// <summary>
/// The decision for one touch event. <paramref name="PinchSpanPx"/> /
/// <paramref name="CenterX"/> / <paramref name="CenterY"/> are the raw
/// two-finger facts (current span and midpoint); the deltas compare against
/// the immediately preceding facts so the viewport layer can derive
/// zoom ratio and pan offset without its own state.
/// </summary>
/// <param name="Kind">Which existing UI action (or gesture fact) this event maps to.</param>
/// <param name="FingerCount">Fingers currently down after the event.</param>
/// <param name="PinchSpanPx">Current two-finger distance in px (0 when fewer than two fingers).</param>
/// <param name="PreviousPinchSpanPx">Two-finger distance before this event (0 on gesture start).</param>
/// <param name="CenterX">Midpoint X of the current two fingers in px.</param>
/// <param name="CenterY">Midpoint Y of the current two fingers in px.</param>
/// <param name="CenterDeltaX">Midpoint X movement since the previous event in px.</param>
/// <param name="CenterDeltaY">Midpoint Y movement since the previous event in px.</param>
/// <param name="Note">Why the decision is <see cref="TouchGestureKind.None"/> when it is (diagnostics).</param>
public sealed record TouchGestureDecision(
    TouchGestureKind Kind,
    int FingerCount = 0,
    double PinchSpanPx = 0,
    double PreviousPinchSpanPx = 0,
    double CenterX = 0,
    double CenterY = 0,
    double CenterDeltaX = 0,
    double CenterDeltaY = 0,
    string? Note = null);

/// <summary>
/// Touch-event to build-action translator (T-141 prototype). Stateful per
/// input surface (finger table + gesture phase); every method is pure of
/// engine types so xUnit drives it with synthetic events and asserts the
/// decision stream, while the Godot side feeds the same decisions into the
/// SAME controller/lane the mouse branch uses — there is no second command
/// semantics anywhere.
/// </summary>
public sealed class TouchGestureMapper
{
    private readonly Dictionary<int, (double X, double Y)> _fingers = new();
    private bool _twoFinger;
    private int _primary = -1;

    /// <summary>Fingers currently down.</summary>
    public int ActiveFingerCount => _fingers.Count;

    /// <summary>True while the two-finger pan/zoom gesture owns the input (build mapping suspended).</summary>
    public bool TwoFingerActive => _twoFinger;

    /// <summary>
    /// Translates one press/release. A repeat press of a known index and a
    /// release of an unknown index are inert (robustness against duplicated
    /// OS events) — never an exception, never a phantom commit.
    /// </summary>
    public TouchGestureDecision Touch(int index, double x, double y, bool pressed)
    {
        if (pressed)
        {
            if (_fingers.ContainsKey(index))
            {
                return None("repeat-press-ignored");
            }

            _fingers[index] = (x, y);
            if (_twoFinger)
            {
                return None("extra-finger-in-gesture"); // 手势已接管——第三指不起建造作用
            }

            if (_fingers.Count >= 2)
            {
                // 第二指按下（1→2 转移）：防误触——先撤建造预览（UI 门：Cancel
                // 幂等），双指事实（span/center）随决策携带供 viewport 播种手势。
                _twoFinger = true;
                _primary = -1;
                (double span, double cx, double cy) = PinchFacts();
                return new TouchGestureDecision(
                    TouchGestureKind.BuildCancel, _fingers.Count,
                    PinchSpanPx: span, CenterX: cx, CenterY: cy,
                    Note: "two-finger-start");
            }

            _primary = index;
            return new TouchGestureDecision(TouchGestureKind.BuildPress, 1);
        }

        if (!_fingers.ContainsKey(index))
        {
            return None("unknown-release-ignored");
        }

        _fingers.Remove(index);
        if (_twoFinger)
        {
            if (_fingers.Count == 0)
            {
                _twoFinger = false;
                return None("two-finger-end");
            }

            // 手势期先抬一指：剩余手指继续手势——绝不触发 BuildCommit
            // （单指抬起≠提交，防半途误触出命令）。
            return None("gesture-continues");
        }

        if (index == _primary)
        {
            _primary = -1;
            return new TouchGestureDecision(TouchGestureKind.BuildCommit, 0);
        }

        return None("non-primary-release-ignored");
    }

    /// <summary>
    /// Translates one finger move. While the two-finger gesture is active the
    /// decision carries the fresh pinch facts (span + midpoint deltas);
    /// otherwise only the primary finger's move maps to <see cref="TouchGestureKind.BuildDrag"/>.
    /// </summary>
    public TouchGestureDecision Drag(int index, double x, double y)
    {
        if (!_fingers.ContainsKey(index))
        {
            return None("drag-without-press-ignored");
        }

        if (_twoFinger)
        {
            if (_fingers.Count < 2)
            {
                return None("gesture-waiting-second-finger");
            }

            (double prevSpan, double prevCx, double prevCy) = PinchFacts();
            _fingers[index] = (x, y);
            (double span, double cx, double cy) = PinchFacts();
            return new TouchGestureDecision(
                TouchGestureKind.TwoFingerPanZoom, _fingers.Count,
                PinchSpanPx: span, PreviousPinchSpanPx: prevSpan,
                CenterX: cx, CenterY: cy,
                CenterDeltaX: cx - prevCx, CenterDeltaY: cy - prevCy);
        }

        _fingers[index] = (x, y);
        if (index == _primary)
        {
            return new TouchGestureDecision(TouchGestureKind.BuildDrag, 1);
        }

        return None("non-primary-drag-ignored");
    }

    /// <summary>Clears the finger table and gesture phase (surface teardown / smoke fixture reset).</summary>
    public void Reset()
    {
        _fingers.Clear();
        _twoFinger = false;
        _primary = -1;
    }

    private TouchGestureDecision None(string note) => new(TouchGestureKind.None, _fingers.Count, Note: note);

    /// <summary>Current two-finger span and midpoint (0,0,0 when fewer than two fingers).</summary>
    private (double Span, double CenterX, double CenterY) PinchFacts()
    {
        if (_fingers.Count < 2)
        {
            return (0, 0, 0);
        }

        // 任取两指（枚举序稳定：插入序——重复事件流下决策可复现）。
        var keys = new List<int>(_fingers.Keys);
        (double x1, double y1) = _fingers[keys[0]];
        (double x2, double y2) = _fingers[keys[1]];
        return (
            Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)),
            (x1 + x2) / 2,
            (y1 + y2) / 2);
    }
}
