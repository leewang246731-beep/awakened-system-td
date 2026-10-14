using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraCore.Presentation;

// T-076 (C-23 全量建造交互, F-118)：单机暂停/变速的纯逻辑时钟。设计 v3.2
// §10.5（条 6）：暂停/变速为单机全局——暂停时可建造、可下达军令（Sim tick
// 冻结但命令入队，恢复后按 (tick, seq) 语义原样应用）；1x/2x/3x 变速=宿主
// 每 render 帧步进 N tick。联机（房主控速）属远期 §9，本类不含任何网络
// 概念。
//
// 确定性口径（任务卡原文）：命令 (tick, seq) 语义不变——本类只决定
// 「render 帧 → sim tick」的映射节奏与「暂停期命令暂存」两件事，不改写
// 命令的IssuedAtTick、不重排 seq：恢复时按入队顺序 FlushInto 同一个
// SimCommandQueue（迟到命令本就合法，见 SimCommandQueue.Enqueue 契约），
// 因此「暂停入队→恢复应用」与「不暂停直接入队」在相同 tick 上产出逐位
// 相同的世界（PauseClockTests 的孪生模拟断言钉死这一点）。
//
// 纯逻辑（CS-1.4/CS-1.1）：无墙钟、无随机、无引擎类型。

/// <summary>
/// Single-player pause/speed clock (T-076, F-118): maps render frames to
/// simulation ticks (0 while paused, else the 1x/2x/3x multiplier) and
/// parks commands issued during a pause so they apply in enqueue order
/// right after resume — the frozen-tick build/order window of §10.5.
/// </summary>
public sealed class PauseClock
{
    /// <summary>Slowest legal speed multiplier (1x).</summary>
    public const int MinSpeedMultiplier = 1;

    /// <summary>Fastest legal speed multiplier — 设计 §10.5 冻结为 1x/2x/3x.</summary>
    public const int MaxSpeedMultiplier = 3;

    private readonly List<SimCommand> _pendingWhilePaused = new();

    /// <summary>True while the world is frozen (builds and orders still queue).</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Current speed multiplier (1..3); remembered across pauses.</summary>
    public int SpeedMultiplier { get; private set; } = MinSpeedMultiplier;

    /// <summary>Render frames paced so far (diagnostics/smoke evidence).</summary>
    public long FramesPaced { get; private set; }

    /// <summary>Sim ticks issued so far (= Σ TicksThisFrame; the smoke totals).</summary>
    public long TicksIssued { get; private set; }

    /// <summary>Commands parked during the current/last pause, not yet flushed.</summary>
    public int PendingCount => _pendingWhilePaused.Count;

    /// <summary>Freezes the world (commands keep queueing).</summary>
    public void Pause() => IsPaused = true;

    /// <summary>
    /// Unfreezes the world. Flushing the parked commands is a separate
    /// explicit step (<see cref="FlushInto"/>) so the caller controls the
    /// tick the flush lands on.
    /// </summary>
    public void Resume() => IsPaused = false;

    /// <summary>The pause key: freeze ⇄ unfreeze.</summary>
    public void TogglePause() => IsPaused = !IsPaused;

    /// <summary>
    /// Sets the speed multiplier (1x/2x/3x). Changing speed while paused is
    /// legal — the value simply takes effect on resume.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">multiplier outside [1,3].</exception>
    public void SetSpeed(int multiplier)
    {
        if (multiplier is < MinSpeedMultiplier or > MaxSpeedMultiplier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiplier), multiplier, "speed multiplier must be 1..3 (设计 §10.5: 1x/2x/3x).");
        }

        SpeedMultiplier = multiplier;
    }

    /// <summary>
    /// One render frame's tick budget: 0 while paused, else the speed
    /// multiplier — the host calls <c>Simulation.Step()</c> exactly this
    /// many times for this frame.
    /// </summary>
    public int TicksThisFrame()
    {
        FramesPaced++;
        int ticks = IsPaused ? 0 : SpeedMultiplier;
        TicksIssued += ticks;
        return ticks;
    }

    /// <summary>
    /// Parks one command issued during a pause (建造/军令入队, §10.5). The
    /// command keeps its frozen <c>IssuedAtTick</c>; nothing steps, so
    /// nothing applies until <see cref="FlushInto"/> after resume.
    /// </summary>
    /// <returns>1-based parked order (the eventual enqueue order).</returns>
    /// <exception cref="InvalidOperationException">not paused.</exception>
    public long EnqueueWhilePaused(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsPaused)
        {
            throw new InvalidOperationException(
                "not paused — enqueue through Simulation directly (this lot is the frozen-tick queue only).");
        }

        _pendingWhilePaused.Add(command);
        return _pendingWhilePaused.Count;
    }

    /// <summary>
    /// Resumes the parked commands into the simulation's ordered queue,
    /// strictly in parked order — so the next <c>Simulation.Step()</c>
    /// applies them by (tick, seq) with seq matching the pause-time issue
    /// order. Clears the lot.
    /// </summary>
    /// <returns>Number of commands flushed.</returns>
    /// <exception cref="InvalidOperationException">still paused.</exception>
    public int FlushInto(Simulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (IsPaused)
        {
            throw new InvalidOperationException("still paused — resume before flushing the parked commands.");
        }

        int flushed = 0;
        foreach (SimCommand command in _pendingWhilePaused)
        {
            simulation.Enqueue(command);
            flushed++;
        }

        _pendingWhilePaused.Clear();
        return flushed;
    }
}
