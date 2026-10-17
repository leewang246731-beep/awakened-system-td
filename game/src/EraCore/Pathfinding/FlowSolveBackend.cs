using EraCore.Contracts.Foundation;

namespace EraCore.Pathfinding;

/// <summary>
/// Strategy selector for slicing a flow-field solve across host time (T-101,
/// C-11; F-159/F-164 — R-E3 "分帧切片后端"). One enum member per named
/// strategy; <see cref="FlowSolveBackendFactory"/> turns a member into the
/// concrete <see cref="FlowField"/> advance entry and default budget.
/// <para>
/// The three strategies (07 D-31 / 03-decomposition R-E3):
/// <list type="bullet">
/// <item><see cref="FullBurst"/> — solve completely in one call (desktop
/// rebuilds, loading-time pre-warm).</item>
/// <item><see cref="PerTickCellQuota"/> — a fixed number of charged work
/// units per SIMULATION tick; the deterministic entry the game loop actually
/// drives (T-067 DEBT 清账, <see cref="FlowField.AdvanceDeterministic"/>).
/// </item>
/// <item><see cref="PerFrameCellBudget"/> — the Web strategy (F-164, single
/// threaded, unstable frame rates): a fixed number of charged work units per
/// HOST FRAME instead of a wall-clock budget, so a starved frame still makes
/// bounded, deterministic progress.</item>
/// </list>
/// </para>
/// <para>
/// Equivalence contract (the property T-101 pins as a regression asset):
/// for one immutable grid input ALL THREE strategies produce a bit-identical
/// field — same <see cref="FlowField.Mode"/>, same per-cell distances, same
/// <see cref="FlowField.Hash"/>. See the proof sketch on
/// <see cref="FlowSolveBackendFactory"/>.
/// </para>
/// </summary>
public enum FlowSolveBackend
{
    /// <summary>
    /// full-burst：一次算完。One generous wall-clock slice per call
    /// (<see cref="FlowField.BuildComplete"/>'s cadence); on any ship-scale
    /// grid a single call completes the build. Use when a whole tick's 8 ms
    /// share may be spent at once (desktop rebuilds, load-time pre-warm).
    /// </summary>
    FullBurst = 0,

    /// <summary>
    /// per-tick-quota：每 Simulation tick 固定格数（charged work units），
    /// driven by <see cref="FlowField.AdvanceDeterministic"/> — THE
    /// deterministic simulation entry (load-independent completion ticks;
    /// T-086 finding, T-067 DEBT 清账). Default budget =
    /// <see cref="FlowSolveBackendFactory.DefaultTickQuota"/>.
    /// </summary>
    PerTickCellQuota = 1,

    /// <summary>
    /// per-frame-budget：每宿主帧固定格数（Web 用，F-164）。
    /// <para>
    /// T-101 归并裁定（不造冗余抽象）：分析后确认 per-frame-budget 就是
    /// per-tick-quota 的宿主循环变体——同为"固定记账格数"的工作量预算，
    /// 同走 <see cref="FlowField.AdvanceDeterministic"/> 这一个引擎入口
    /// （Web 帧率不稳时墙钟预算会被饿死，工作量预算不会——这正是要用它的
    /// 原因，也正说明它与墙钟入口无关）。二者仅差两点，均为宿主侧事实
    /// 而非引擎差异：①驱动节奏（Simulation 30 Hz tick vs 表现层帧循环，
    /// 帧率不稳）；②默认预算标定（<see cref="FlowSolveBackendFactory.DefaultWebFrameCellBudget"/>
    /// 按 Web 单线程口径独立标定）。故引擎路径如实归并为同一条
    /// AdvanceDeterministic，不另造第三套切片机制；本枚举值保留是为把
    /// 任务卡点名的三后端等价钉成一等公民的回归目标。
    /// </para>
    /// <para>
    /// 仿真可见字段的确定性要求完成 tick 是图快照的纯函数——那只能由
    /// per-tick 节奏保证；per-frame 变体的定位是宿主驱动的预热/工具路径
    /// （等价性不受影响：见 <see cref="FlowSolveBackendFactory"/> 证明注记）。
    /// </para>
    /// </summary>
    PerFrameCellBudget = 2,
}

/// <summary>
/// Factory mapping a <see cref="FlowSolveBackend"/> strategy onto the
/// concrete <see cref="FlowField"/> advance entries (T-101). Two faces:
/// <list type="bullet">
/// <item><see cref="Advance"/> — ONE slice of the strategy (one simulation
/// tick / one host frame / one generous burst slice); the host loop calls it
/// per cadence and counts slices.</item>
/// <item><see cref="Solve"/> — blocking convenience driving the strategy to
/// completion; the equivalence regression's entry point.</item>
/// </list>
/// <para>
/// 等价性证明注记（数学依据，防未来改动破坏——任何破坏下列不变量的
/// 改动都会使 T-101 的 20 张噪声迷宫等价回归变红）：
/// <list type="number">
/// <item><description>输入冻结：快照阶段把 Cost/BloodCost/Passable 读入
/// 内部数组，泛洪阶段只读快照——求解结果是快照的纯函数，切片边界上的
/// 宿主活动（包括墙钟噪声）不进入结果。</description></item>
/// <item><description>Dijkstra 确定性：索引二叉最小堆按 (dist, cellIndex)
/// 全序比较（<c>HeapLess</c>——相等距离按格子索引破平），邻居松弛按固定
/// 8 邻扫描序。因此弹出序列与每次松弛结果由输入唯一决定，与调度无关。
/// </description></item>
/// <item><description>配额只切调度、不切算法：
/// <see cref="FlowField.AdvanceDeterministic"/> 的挂起点都在"记账工作单元
/// 之间"（快照逐格、行内可续位；泛洪逐 pop），恢复不改变后续单元的顺序
/// 与内容；相位结算簿记（不可达扫描/重泛洪/模式转移）不扣配额且至多每
/// 相位一次，同为纯函数。</description></item>
/// <item><description>墙钟切片同理：<see cref="FlowField.Advance(TimeSpan)"/>
/// 只决定"何时停"，停止点仍落在同一条确定性单元序列上（批检查只影响
/// 检查频率，不影响单元内容）。</description></item>
/// <item><description>故三后端对同一快照产出逐格相等的 dist 数组，从而
/// Hash/NextStep/BuildPath/ProcessedCells 全等。■</description></item>
/// </list>
/// </para>
/// </summary>
public static class FlowSolveBackendFactory
{
    /// <summary>
    /// Default per-tick quota of <see cref="FlowSolveBackend.PerTickCellQuota"/>
    /// — delegates to <see cref="FlowFieldManager.DefaultCellsPerTick"/>
    /// (6144 charged units since the T-153 recalibration; calibration record
    /// on that constant).
    /// </summary>
    public const int DefaultTickQuota = FlowFieldManager.DefaultCellsPerTick;

    /// <summary>
    /// Default per-frame cell budget of
    /// <see cref="FlowSolveBackend.PerFrameCellBudget"/> (Web 口径, F-164).
    /// Calibrated independently of the tick quota from the T-067 quiet-window
    /// per-unit record (≈0.32 µs/charged unit): WebAssembly single-thread is
    /// budgeted at a pessimistic 4× per-unit cost ⇒ 2048 units ≈ 2.6
    /// ms/frame — inside the 8 ms design slice (v3.2 §6) with ≈3× margin at
    /// that calibration (parallel-lane load shrinks the measured margin —
    /// the T-101 throughput anchor records per-run numbers and prints the
    /// live margin). Because the budget is work-based, a starved (long)
    /// frame still costs exactly 2048 units of bounded work instead of an
    /// unbounded wall-clock chase. Same discipline as the tick quota:
    /// enlarge only while the worst-case margin survives on target browsers.
    /// </summary>
    public const int DefaultWebFrameCellBudget = 2048;

    private static readonly TimeSpan BurstSlice = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Drives the strategy to completion and returns the finished field.
    /// Equivalence guarantee: for one immutable grid input the returned field
    /// is bit-identical across all three backends (see the proof sketch on
    /// this class; pinned by the T-101 20-maze regression).
    /// </summary>
    /// <param name="grid">Input grid; must not change while solving.</param>
    /// <param name="target">Flow target (the player base cell).</param>
    /// <param name="backend">Slicing strategy.</param>
    /// <param name="bloodCostMultiplier">
    /// Enemy-building HP multiplier (design default ×5).
    /// </param>
    /// <param name="cellsPerSlice">
    /// Charged work units per slice for the SLICED backends; null uses the
    /// backend's own default. Supplying it for
    /// <see cref="FlowSolveBackend.FullBurst"/> is rejected (a burst has no
    /// slice knob — silently ignoring it would hide caller bugs).
    /// </param>
    /// <exception cref="ArgumentNullException">grid or target is null.</exception>
    /// <exception cref="ArgumentException">
    /// grid dimensions are non-positive/too large, or
    /// <paramref name="cellsPerSlice"/> was supplied for
    /// <see cref="FlowSolveBackend.FullBurst"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// target outside the grid, negative multiplier, unknown backend value,
    /// or a non-positive <paramref name="cellsPerSlice"/> for a sliced
    /// backend (zero is the legal no-progress boundary at the
    /// <see cref="FlowFieldManager"/> level, but a blocking solve with a
    /// zero quota would never terminate — so here it is rejected outright).
    /// </exception>
    public static FlowField Solve(
        IFlowGrid grid,
        CellCoord target,
        FlowSolveBackend backend,
        int bloodCostMultiplier = FlowField.DefaultBloodCostMultiplier,
        int? cellsPerSlice = null)
    {
        var field = FlowField.StartBuild(grid, target, bloodCostMultiplier);
        while (!Advance(field, backend, cellsPerSlice))
        {
        }

        return field;
    }

    /// <summary>
    /// ONE slice of the strategy: one simulation tick
    /// (<see cref="FlowSolveBackend.PerTickCellQuota"/>), one host frame
    /// (<see cref="FlowSolveBackend.PerFrameCellBudget"/>) or one generous
    /// burst slice (<see cref="FlowSolveBackend.FullBurst"/> — completes any
    /// ship-scale grid). Returns true when the field is complete.
    /// </summary>
    /// <param name="field">Field under construction.</param>
    /// <param name="backend">Slicing strategy driving it.</param>
    /// <param name="cellsPerSlice">
    /// Charged work units this slice for the sliced backends; null uses the
    /// backend's default. Rejected for <see cref="FlowSolveBackend.FullBurst"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">field is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="cellsPerSlice"/> was supplied for
    /// <see cref="FlowSolveBackend.FullBurst"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// unknown backend value, or a non-positive slice quota.
    /// </exception>
    public static bool Advance(FlowField field, FlowSolveBackend backend, int? cellsPerSlice = null)
    {
        ArgumentNullException.ThrowIfNull(field);

        // 宿主循环变体归并（T-101 裁定，见枚举注）：per-frame-budget 与
        // per-tick-quota 共用同一条确定性引擎入口，仅默认预算不同。
        return backend switch
        {
            FlowSolveBackend.FullBurst => BurstAdvance(field, cellsPerSlice),
            FlowSolveBackend.PerTickCellQuota => QuotaAdvance(field, cellsPerSlice, DefaultTickQuota),
            FlowSolveBackend.PerFrameCellBudget => QuotaAdvance(field, cellsPerSlice, DefaultWebFrameCellBudget),
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend), backend, "Unknown FlowSolveBackend value."),
        };
    }

    private static bool BurstAdvance(FlowField field, int? cellsPerSlice)
    {
        if (cellsPerSlice is not null)
        {
            throw new ArgumentException(
                "cellsPerSlice is a slicing knob and is meaningless for the FullBurst backend; pass null.",
                nameof(cellsPerSlice));
        }

        // BuildComplete's cadence: one generous slice; any ship-scale grid
        // finishes here (the Solve loop merely guards astronomically large ones).
        return field.Advance(BurstSlice);
    }

    private static bool QuotaAdvance(FlowField field, int? cellsPerSlice, int defaultQuota)
    {
        int quota = cellsPerSlice ?? defaultQuota;
        if (quota <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellsPerSlice), quota,
                "Slice quota must be positive (a blocking solve with a zero quota would never terminate).");
        }

        return field.AdvanceDeterministic(quota);
    }
}
