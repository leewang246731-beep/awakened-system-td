using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Interaction;

// [C-23][F-118] T-076 暂停/变速纯逻辑（PauseClock，设计 v3.2 §10.5）：
// 1) 节奏表——暂停=0 tick/帧、1x/2x/3x=N tick/帧（表驱动）；
// 2) 暂停中命令入队→Sim tick 冻结→恢复后按 (tick,seq) 原序应用（真实
//    Simulation + SimCommandQueue，非 mock——CS-3.4）；
// 3) 确定性孪生——「暂停入队→恢复应用」与「直接入队」在相同 tick 上产出
//    逐位相同的世界哈希（命令 (tick,seq) 语义不变，任务卡原文）；
// 4) 非法态——未暂停入队/仍暂停即冲刷 → InvalidOperationException；
//    变速越界 [1,3] → ArgumentOutOfRangeException（边界行）。
public class PauseClockTests
{
    private const string WoodArrow = "era1:tower-wood-arrow";

    private static Simulation NewSim(double initialWood = 200.0)
    {
        var config = PipelineHarness.Config(initialWood);
        return new Simulation(config, new SimRng(PipelineHarness.Seed), PipelineHarness.Seed);
    }

    [Theory] // legal：节奏表——(speed, pause) → 每 render 帧步进 tick 数
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    [InlineData(1, true, 0)]
    [InlineData(2, true, 0)]
    [InlineData(3, true, 0)]
    public void TicksThisFrame_maps_pause_and_speed(int speed, bool paused, int expectedTicks) // [F-118]
    {
        var clock = new PauseClock();
        clock.SetSpeed(speed);
        if (paused)
        {
            clock.Pause();
        }

        Assert.Equal(expectedTicks, clock.TicksThisFrame());
        Assert.Equal(expectedTicks, clock.TicksThisFrame());
        Assert.Equal(2, clock.FramesPaced);
        Assert.Equal(expectedTicks * 2, clock.TicksIssued);
    }

    [Theory] // boundary：变速钳制域外拒绝（设计 §10.5 冻结 1x/2x/3x，无第 4 档）
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void SetSpeed_outside_the_frozen_1x_2x_3x_scale_throws(int speed) // [F-118]
    {
        var clock = new PauseClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetSpeed(speed));
        Assert.Equal(PauseClock.MinSpeedMultiplier, clock.SpeedMultiplier); // 拒绝后保持原值
    }

    [Theory] // legal：宿主帧循环——每 render 帧按 TicksThisFrame() 步进，N 帧后
              // sim 恰好前进 N×speed 个 tick（SimHost.StepFrame 的纯逻辑形态；
              // 1x 与旧「每帧 1 tick」逐位同轨）
    [InlineData(1, 1, 1)]
    [InlineData(1, 660, 660)] // Main 冒烟 660 帧 × 1x = 660 tick（里程碑流末令 tick 600）
    [InlineData(2, 4, 8)]
    [InlineData(3, 4, 12)]
    [InlineData(3, 1, 3)]
    public void Host_frame_loop_steps_the_simulation_N_ticks_per_frame(int speed, int frames, long expectedTicks) // [F-118]
    {
        Simulation sim = NewSim();
        var clock = new PauseClock();
        clock.SetSpeed(speed);
        long tickBefore = sim.CurrentTick;

        for (int frame = 0; frame < frames; frame++)
        {
            int ticks = clock.TicksThisFrame(); // 每帧取一次预算（TicksThisFrame 有计数副作用——不得置于循环条件）
            for (int t = 0; t < ticks; t++)
            {
                sim.Step();
            }
        }

        Assert.Equal(tickBefore + expectedTicks, sim.CurrentTick);
        Assert.Equal(frames, clock.FramesPaced);
        Assert.Equal(expectedTicks, clock.TicksIssued);
    }

    [Fact] // legal：变速中途改档——1x 两帧 + 3x 两帧与「先 3x 两帧 + 1x 两帧」
           // 步进同一 tick 总数（节奏可换，tick 推进总量只由 ΣTicksThisFrame 决定）
    public void Speed_changes_mid_run_only_change_pacing_not_the_tick_total() // [F-118]
    {
        Simulation first = NewSim();
        Simulation second = NewSim();
        var clockA = new PauseClock();
        var clockB = new PauseClock();

        clockA.SetSpeed(1);
        clockA.SetSpeed(3);
        for (int frame = 0; frame < 2; frame++)
        {
            int ticks = clockA.TicksThisFrame();
            for (int t = 0; t < ticks; t++)
            {
                first.Step();
            }
        }

        clockA.SetSpeed(1);
        for (int frame = 0; frame < 2; frame++)
        {
            int ticks = clockA.TicksThisFrame();
            for (int t = 0; t < ticks; t++)
            {
                first.Step();
            }
        }

        clockB.SetSpeed(3);
        for (int frame = 0; frame < 2; frame++)
        {
            int ticks = clockB.TicksThisFrame();
            for (int t = 0; t < ticks; t++)
            {
                second.Step();
            }
        }

        clockB.SetSpeed(1);
        for (int frame = 0; frame < 2; frame++)
        {
            int ticks = clockB.TicksThisFrame();
            for (int t = 0; t < ticks; t++)
            {
                second.Step();
            }
        }

        Assert.Equal(8, clockA.TicksIssued); // 2×1 + 2×3 = 8，顺序无关
        Assert.Equal(first.CurrentTick, second.CurrentTick);
        Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash()); // 同 tick 数 ⇒ 同世界
    }

    [Fact] // legal：核心验收——暂停中命令入队→冻结→恢复后按入队序应用
    public void Commands_enqueued_while_paused_apply_in_order_after_resume() // [F-118]
    {
        Simulation sim = NewSim();
        var clock = new PauseClock();
        clock.Pause();
        long frozenTick = sim.CurrentTick;

        // 暂停中下达两道建造军令（K-6 泳道原语，冻结 tick 入队）
        long order1 = clock.EnqueueWhilePaused(SimCommands.BuildTower(frozenTick + 1, WoodArrow, new CellCoord(11, 11)));
        long order2 = clock.EnqueueWhilePaused(SimCommands.BuildTower(frozenTick + 1, WoodArrow, new CellCoord(12, 12)));
        Assert.Equal(1, order1);
        Assert.Equal(2, order2);
        Assert.Equal(2, clock.PendingCount);

        // 冻结证据：整个暂停窗口每帧 0 tick——Sim tick 不动、世界无塔
        for (int frame = 0; frame < 5; frame++)
        {
            int ticks = clock.TicksThisFrame();
            for (int i = 0; i < ticks; i++)
            {
                sim.Step();
            }
        }

        Assert.Equal(0, clock.TicksIssued);
        Assert.Equal(frozenTick, sim.CurrentTick);
        Assert.Empty(sim.Towers);

        // 恢复：冲刷→步进→两令按序落子（(tick,seq) 语义原样）
        clock.Resume();
        Assert.Equal(2, clock.FlushInto(sim));
        Assert.Equal(0, clock.PendingCount);
        sim.Step();

        Assert.Equal(2, sim.Towers.Count);
        SimCommandResult[] applied = sim.CommandResults
            .Where(result => result.Accepted)
            .TakeLast(2)
            .ToArray();
        Assert.All(applied, result => Assert.Equal(SimCommandKind.PlayerBuild, result.Kind));
        Assert.Equal(frozenTick + 1, applied[0].Tick);
        Assert.True(applied[0].Seq < applied[1].Seq, "恢复后按入队序（seq 单调）应用");
    }

    [Fact] // legal：确定性孪生——暂停路径与直入路径同 tick 同序 ⇒ 世界哈希逐位一致
    public void Paused_enqueues_are_bit_identical_to_direct_enqueues_at_the_same_tick() // [F-118]
    {
        Simulation direct = NewSim();
        Simulation viaPause = NewSim();

        // 直入路径：冻结 tick 上下两令，随后步进 3 tick
        direct.Enqueue(SimCommands.BuildTower(1, WoodArrow, new CellCoord(11, 11)));
        direct.Enqueue(SimCommands.BuildTower(1, WoodArrow, new CellCoord(12, 12)));
        for (int i = 0; i < 3; i++)
        {
            direct.Step();
        }

        // 暂停路径：暂停（tick 0 冻结）→入队→3 个暂停帧→恢复→冲刷→3 tick
        var clock = new PauseClock();
        clock.Pause();
        clock.EnqueueWhilePaused(SimCommands.BuildTower(1, WoodArrow, new CellCoord(11, 11)));
        clock.EnqueueWhilePaused(SimCommands.BuildTower(1, WoodArrow, new CellCoord(12, 12)));
        for (int frame = 0; frame < 3; frame++)
        {
            clock.TicksThisFrame();
        }

        clock.Resume();
        clock.FlushInto(viaPause);
        for (int i = 0; i < 3; i++)
        {
            viaPause.Step();
        }

        Assert.Equal(direct.CurrentTick, viaPause.CurrentTick);
        Assert.Equal(direct.ComputeStateHash(), viaPause.ComputeStateHash());
    }

    [Fact] // legal：变速在暂停中修改合法——恢复后按新速率步进
    public void Speed_set_while_paused_takes_effect_on_resume() // [F-118]
    {
        var clock = new PauseClock();
        clock.Pause();
        clock.SetSpeed(3);
        Assert.Equal(0, clock.TicksThisFrame());

        clock.Resume();
        Assert.Equal(3, clock.TicksThisFrame());
    }

    [Fact] // legal：TogglePause 往复 + 空冲刷零副作用（无驻留命令时冲刷=0、世界不动）
    public void Toggle_round_trips_and_empty_flush_is_a_noop() // [F-118]
    {
        Simulation sim = NewSim();
        long hashBefore = sim.ComputeStateHash();
        var clock = new PauseClock();
        Assert.False(clock.IsPaused);

        clock.TogglePause();
        Assert.True(clock.IsPaused);
        clock.TogglePause();
        Assert.False(clock.IsPaused);

        Assert.Equal(0, clock.FlushInto(sim));
        Assert.Equal(hashBefore, sim.ComputeStateHash());
    }

    [Fact] // illegal：未暂停时入暂停队列 → 拒绝（该队列只承载冻结窗口的命令）
    public void Enqueue_while_running_is_refused() // [F-118]
    {
        var clock = new PauseClock();
        Assert.Throws<InvalidOperationException>(
            () => clock.EnqueueWhilePaused(SimCommands.BuildTower(1, WoodArrow, new CellCoord(11, 11))));
        Assert.Equal(0, clock.PendingCount);
    }

    [Fact] // illegal：仍处于暂停就冲刷 → 拒绝（冲刷必须发生在恢复之后）
    public void Flush_while_still_paused_is_refused() // [F-118]
    {
        Simulation sim = NewSim();
        var clock = new PauseClock();
        clock.Pause();
        clock.EnqueueWhilePaused(SimCommands.BuildTower(1, WoodArrow, new CellCoord(11, 11)));
        Assert.Throws<InvalidOperationException>(() => clock.FlushInto(sim));
        Assert.Equal(1, clock.PendingCount); // 冲刷被拒后驻留命令不丢
    }
}
