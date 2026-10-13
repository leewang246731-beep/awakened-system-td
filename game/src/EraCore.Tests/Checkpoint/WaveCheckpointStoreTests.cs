using EraCore.Checkpoint;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-001][F-004] 波界检查点存储（T-057）：检查点波公式（⌊胜波×0.75⌋
// 且 ≥10）、普通波界快照滚动保留最近 2 代+检查点波永久、终局关清域墓碑、
// ObserveBoundary 波界探测——全部走真实 SqliteSavePort（K-7 扩展点）。
public class WaveCheckpointStoreTests : SqliteSavePortTestBase
{
    // --------------------------------------------------- 检查点波公式（表驱动）

    [Theory] // legal — SP-9 锚点威胁度：胜波 13/19/22 → 检查点 10/14/16
    [InlineData(0.3, 13, 10)] // ⌊13×0.75⌋=9 → 下限 10 生效
    [InlineData(0.9, 19, 14)] // ⌊14.25⌋=14
    [InlineData(1.2, 22, 16)] // ⌊16.5⌋=16
    public void Checkpoint_wave_follows_floor_of_win_waves_times_zero_seven_five(double threat, int winWaves, int expectedCheckpoint)
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", threat);

        Assert.Equal(expectedCheckpoint, store.CheckpointWave);
        Assert.Equal(winWaves, EraCore.Balance.Campaign.WinWaves(threat)); // 胜波锚点自检
    }

    [Fact] // illegal — 负威胁度：WinWaves 拒绝（经 Campaign 契约）
    public void Negative_threat_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => new WaveCheckpointStore(CreatePort(), "level.e1.m01", -0.1));
    }

    [Fact] // illegal — 空关卡槽 id / null 端口
    public void Blank_slot_id_and_null_port_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WaveCheckpointStore(CreatePort(), "  ", 0.3));
        Assert.Throws<ArgumentNullException>(() => new WaveCheckpointStore(null!, "level.e1.m01", 0.3));
    }

    // ------------------------------------------------------ 滚动保留+检查点永久

    [Fact] // legal — 波界快照：检查点波落 #ckpt 永久槽；普通波滚动保留最近 2 代
    public void Rolling_slots_keep_the_last_two_generations_and_checkpoint_is_permanent()
    {
        SqliteSavePort port = CreatePort();
        const double threat = 0.3; // 胜波 13 → 检查点 10
        var store = new WaveCheckpointStore(port, "level.e1.m01", threat);
        Assert.Equal(10, store.CheckpointWave);

        Simulation sim = CheckpointFixture.RunTo(700); // 全部 10 条里程碑命令已应用
        // 波 9（检查点前）→ roll-a；波 10 → #ckpt；波 11..14 交替覆盖 roll 槽
        foreach (int wave in new[] { 9, 10, 11, 12, 13, 14 })
        {
            store.SaveBoundary(sim, wave, SimE1Fixture.MilestoneCommands());
        }

        // 最近 2 代 = {13, 14}；更早代（9/11/12）被滚动覆盖
        Assert.True(store.TryRestoreLatestBoundary(out RestoredWaveSnapshot latest));
        Assert.Equal(14, latest.Wave);
        Assert.Equal(700, latest.Tick);
        Assert.Equal(sim.Seed, latest.Seed);
        Assert.Equal(10, latest.Commands.Count); // 10 条命令全部 ≤ 边界 tick

        Assert.Equal(13, port.ReadLevel(store.RollingSlotBId, SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(14, port.ReadLevel(store.RollingSlotAId, SaveVersion.Current).WaveCheckpoint);

        // 检查点波永久：后续滚动写不触碰 #ckpt
        Assert.True(store.TryRestoreCheckpoint(out RestoredWaveSnapshot checkpoint));
        Assert.Equal(10, checkpoint.Wave);
        Assert.Equal(10, port.ReadLevel(store.CheckpointSlotId, SaveVersion.Current).WaveCheckpoint);
    }

    [Fact] // boundary — 检查点未写时的滚动快照自足（basis 回退），恢复仍可用
    public void Rolling_save_before_any_checkpoint_is_self_sufficient()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 0.3); // 检查点 10
        Simulation sim = CheckpointFixture.RunTo(700);

        store.SaveBoundary(sim, 9, SimE1Fixture.MilestoneCommands()); // 检查点波之前

        Assert.True(store.TryRestoreLatestBoundary(out RestoredWaveSnapshot latest));
        Assert.Equal(9, latest.Wave);
        Assert.Equal(10, latest.Commands.Count); // basis=自身，无尾
        Assert.False(store.TryRestoreCheckpoint(out _)); // 检查点波未跨越
    }

    [Fact] // legal — 检查点之后的滚动快照：basis=检查点记录，尾=检查点后命令
    public void Rolling_save_after_the_checkpoint_carries_the_checkpoint_basis_and_tail()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 0.3); // 检查点 10
        Simulation sim = CheckpointFixture.RunTo(700);
        store.SaveBoundary(sim, 10, SimE1Fixture.MilestoneCommands());

        // 边界推进到 900：一条 tick 800 的迟到命令进尾（发行记账：应用与否都入流）
        var late = SimCommands.BuildTower(800, "era1:tower-wood-arrow", new(12, 21));
        sim.Enqueue(late);
        sim.Run(200);
        var withLate = SimE1Fixture.MilestoneCommands().Append(late).ToArray();
        store.SaveBoundary(sim, 11, withLate);

        Assert.True(store.TryRestoreLatestBoundary(out RestoredWaveSnapshot latest));
        Assert.Equal(11, latest.Wave);
        Assert.Equal(900, latest.Tick);
        Assert.Equal(11, latest.Commands.Count); // 10 条 basis + 1 条尾

        // 恢复=重放：新实例重放到 900 tick，与活体同哈希（T-040 不变量的消费）
        Simulation replayed = latest.Replay(SimE1Fixture.Config(), new SimRng(65535));
        Assert.Equal(900, replayed.CurrentTick);
        Assert.Equal(sim.ComputeStateHash(), replayed.ComputeStateHash());
    }

    [Fact] // illegal — 波号 1 基：0/负数拒绝
    public void Boundary_save_rejects_non_positive_wave_numbers()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 0.3);
        Simulation sim = CheckpointFixture.RunTo(100);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.SaveBoundary(sim, 0, SimE1Fixture.MilestoneCommands()));
        Assert.Throws<ArgumentNullException>(
            () => store.SaveBoundary(null!, 3, SimE1Fixture.MilestoneCommands()));
        Assert.Throws<ArgumentNullException>(() => store.SaveBoundary(sim, 3, null!));
    }

    // ------------------------------------------------------------- 清域（终局关）

    [Fact] // legal — Clear：本关三槽全部墓碑，恢复面=无快照；他关卡不受牵连
    public void Clear_tombstones_this_levels_slots_only()
    {
        SqliteSavePort port = CreatePort();
        var mine = new WaveCheckpointStore(port, "level.e1.m01", 0.3);
        var other = new WaveCheckpointStore(port, "level.e1.m02", 0.3);
        Simulation sim = CheckpointFixture.RunTo(700);
        mine.SaveBoundary(sim, 10, SimE1Fixture.MilestoneCommands());
        other.SaveBoundary(sim, 10, SimE1Fixture.MilestoneCommands());

        mine.Clear();

        Assert.False(mine.TryRestoreCheckpoint(out _));
        Assert.False(mine.TryRestoreLatestBoundary(out _));
        Assert.Equal(
            WaveCheckpointStore.ClearedWaveMarker,
            port.ReadLevel(mine.CheckpointSlotId, SaveVersion.Current).WaveCheckpoint);
        // 他关卡的检查点原样保留（只清"本关"存档域）
        Assert.True(other.TryRestoreCheckpoint(out RestoredWaveSnapshot foreign));
        Assert.Equal(10, foreign.Wave);
    }

    [Fact] // boundary — 空库（从未存档）：两个恢复面都报告无快照而非报错
    public void Fresh_store_restores_nothing()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 0.3);

        Assert.False(store.TryRestoreCheckpoint(out _));
        Assert.False(store.TryRestoreLatestBoundary(out _));
    }

    // ------------------------------------------------------------ ObserveBoundary

    [Fact] // legal — 波界探测：逐边界推进时完成的波被自动捕获；重复观察不重复存
    public void ObserveBoundary_saves_the_ended_wave_and_is_idempotent_between_boundaries()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 1.2);
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());

        Assert.Null(store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands())); // tick 0 基线
        sim.Run(CheckpointFixture.GraceTicks); // 波 1 装载（波 1 开始，非完成边界）
        Assert.Null(store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands()));

        sim.Run(CheckpointFixture.IntervalTicks); // 至 BoundaryTickOf(1)：波 2 装载=波 1 结束
        int? saved = store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands());
        Assert.Equal(1, saved);
        Assert.True(store.TryRestoreLatestBoundary(out RestoredWaveSnapshot latest));
        Assert.Equal(1, latest.Wave);
        Assert.Equal(CheckpointFixture.BoundaryTickOf(1), latest.Tick);

        Assert.Null(store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands())); // 无新边界
    }

    [Fact] // boundary — 波 0→1 是波 1 开始而非完成边界：不存档
    public void Wave_one_installing_is_not_a_completed_boundary()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 1.2);
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());

        Assert.Null(store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands())); // 基线 0
        sim.Run(CheckpointFixture.GraceTicks); // 波 1 刚装载

        Assert.Null(store.ObserveBoundary(sim, SimE1Fixture.MilestoneCommands()));
        Assert.False(store.TryRestoreLatestBoundary(out _));
    }
}
