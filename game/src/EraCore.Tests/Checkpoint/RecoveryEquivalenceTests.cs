using EraCore.Checkpoint;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-001][F-004] 恢复等价（T-057 灵魂断言）：第 30 波 kill→检查点恢复重放
// 14 波→与"从未中断连续跑"逐 tick StateHash 相等；崩溃注入（波界写存档中途
// kill，复用 T-041 注入手法）→恢复走上一完整检查点；篡改检查点=哈希分叉。
// [C-04][F-001] 重负载串行集合（见 CheckpointTestSupport）
[Collection(CheckpointHeavyCollection.Name)]
public class RecoveryEquivalenceTests : SqliteSavePortTestBase
{
    // ------------------------------------------------------- 恢复等价主断言

    [Fact] // legal — 第 30 波 kill（t=1.2：检查点 16，重放 14 波）恢复后逐 tick 等价。
    // 口径说明：波间隔公式冻结（120s×0.6=72s/波）下第 30 波边界≈68040 tick≈38
    // 分钟模拟时间，任务卡"60s 局"按测试墙钟预算执行（三世界×70k tick 实测
    // 远低于 60s），等价判定覆盖全部比较 tick（含检查点重放前缀与 kill 后续跑）。
    // 长地平线用薄敌量夹具（同 seed/同地图/同公式结构，见 CheckpointFixture.
    // LongHorizonConfig）：检查点语义与敌密度无关，金样回归保留全密度夹具。
    public void Restore_from_checkpoint_replays_fourteen_waves_bit_for_bit()
    {
        // 迟到命令（检查点后）：恢复世界必须重发行同一条命令流
        var late = SimCommands.BuildTower(40_000, "era1:tower-wood-arrow", new(12, 21));
        IReadOnlyList<Contracts.Sim.SimCommand> commands = CheckpointFixture.MilestonePlusLate(late);
        long horizon = CheckpointFixture.BoundaryTickOf(30) + CheckpointFixture.IntervalTicks;

        RecoveryEquivalenceResult result = RecoveryEquivalence.VerifyAcrossRestore(
            CheckpointFixture.LongHorizonConfig(),
            rngFactory: () => new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed,
            commands,
            CheckpointFixture.Threat,
            killAtWave: 30,
            horizonTick: horizon,
            savePort: CreatePort(),
            levelSlotId: "level.e1.m01");

        Assert.True(result.Verified, $"首分叉 tick={result.FirstDivergenceTick}");
        Assert.Null(result.FirstDivergenceTick);
        Assert.Equal(16, result.CheckpointWave);
        Assert.Equal(CheckpointFixture.BoundaryTickOf(16), result.CheckpointTick);
        Assert.Equal(30, result.KillAtWave);
        Assert.Equal(CheckpointFixture.BoundaryTickOf(30), result.KillTick);
        Assert.Equal(horizon + 1, result.ComparedTicks);
    }

    [Fact] // boundary — 最小等价：检查点后 1 波即 kill，恢复仍逐 tick 等价
    // （全密度夹具的同场景等价由检查点金样每测复证——CheckpointGoldenTests）
    public void Restore_equivalence_holds_for_a_single_replayed_wave()
    {
        long horizon = CheckpointFixture.BoundaryTickOf(17) + CheckpointFixture.IntervalTicks;

        RecoveryEquivalenceResult result = RecoveryEquivalence.VerifyAcrossRestore(
            CheckpointFixture.LongHorizonConfig(),
            rngFactory: () => new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed,
            SimE1Fixture.MilestoneCommands(),
            CheckpointFixture.Threat,
            killAtWave: 17,
            horizonTick: horizon,
            savePort: CreatePort(),
            levelSlotId: "level.e1.m01");

        Assert.True(result.Verified, $"首分叉 tick={result.FirstDivergenceTick}");
        Assert.Equal(16, result.CheckpointWave);
        Assert.Equal(17, result.KillAtWave);
    }

    [Fact] // illegal — kill 波未过检查点 / 非正 horizon：参数即拒绝
    public void Harness_rejects_kill_at_or_before_the_checkpoint_wave()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryEquivalence.VerifyAcrossRestore(
            SimE1Fixture.Config(), () => new SimRng(1), SimE1Fixture.Seed,
            SimE1Fixture.MilestoneCommands(), CheckpointFixture.Threat,
            killAtWave: 16, horizonTick: 45_000, savePort: CreatePort(), levelSlotId: "L"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryEquivalence.VerifyAcrossRestore(
            SimE1Fixture.Config(), () => new SimRng(1), SimE1Fixture.Seed,
            SimE1Fixture.MilestoneCommands(), CheckpointFixture.Threat,
            killAtWave: 30, horizonTick: 0, savePort: CreatePort(), levelSlotId: "L"));
    }

    // ------------------------------------------------------- 篡改检查点（负向）

    [Fact] // negative — 检查点载荷被篡改（丢一条命令）：恢复重放哈希必须分叉
    public void Tampered_checkpoint_record_is_detected_by_hash_divergence()
    {
        SqliteSavePort port = CreatePort();
        var store = new WaveCheckpointStore(port, "level.e1.m01", 1.2);
        Simulation live = CheckpointFixture.RunToLongHorizon(CheckpointFixture.BoundaryTickOf(16));
        store.SaveBoundary(live, store.CheckpointWave, SimE1Fixture.MilestoneCommands());
        long referenceHash = live.ComputeStateHash();

        // 篡改：少记最后一条命令的检查点记录覆盖 #ckpt 槽
        var tamperedCommands = SimE1Fixture.MilestoneCommands()
            .Take(SimE1Fixture.MilestoneCommands().Count - 1)
            .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson))
            .ToArray();
        var tampered = new WaveSnapshotRecord(live.Seed, store.CheckpointWave, live.CurrentTick, tamperedCommands);
        port.WriteLevel(
            store.CheckpointSlotId,
            new LevelSaveBlobDto(
                store.CheckpointWave,
                CheckpointPayload.Encode(tampered),
                CheckpointPayload.Encode(new WaveTailRecord(
                    store.CheckpointWave, live.CurrentTick, Array.Empty<SimCommandLog>()))),
            SaveVersion.Current);

        Assert.True(store.TryRestoreCheckpoint(out RestoredWaveSnapshot snapshot));
        Simulation replayed = snapshot.Replay(CheckpointFixture.LongHorizonConfig(), new SimRng(3312));
        Assert.Equal(live.CurrentTick, replayed.CurrentTick);
        Assert.NotEqual(referenceHash, replayed.ComputeStateHash()); // 损坏被哈希暴露
    }

    // ---------------------------------------------------------- 崩溃注入恢复

    [Fact] // crash — 波界写存档中途 kill：写失败（锁楔住），恢复走上一完整检查点
    // （薄敌量长地平线夹具：崩溃注入语义与敌密度无关，见 CheckpointFixture）
    public void Crash_mid_boundary_save_recovers_from_the_last_complete_checkpoint()
    {
        SqliteSavePort port = CreatePort();
        var store = new WaveCheckpointStore(port, "level.e1.m01", 1.2);
        var sim = new Simulation(CheckpointFixture.LongHorizonConfig(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());

        // 完整跑过检查点波 16 与滚动波 17/18，记录每个边界的活体哈希
        var boundaryHashes = new Dictionary<int, long>();
        foreach (int wave in new[] { 16, 17, 18 })
        {
            sim.Run(CheckpointFixture.BoundaryTickOf(wave) - sim.CurrentTick);
            store.SaveBoundary(sim, wave, SimE1Fixture.MilestoneCommands());
            boundaryHashes[wave] = sim.ComputeStateHash();
        }

        // 波 19 边界写存档中途 kill：外部连接楔住写锁（T-041 注入手法），写必须失败
        sim.Run(CheckpointFixture.BoundaryTickOf(19) - sim.CurrentTick);
        using (var outside = OpenRawConnection())
        using (var txn = outside.BeginTransaction())
        {
            using var wedge = outside.CreateCommand();
            wedge.CommandText = "INSERT INTO level_blob (level_slot_id, wave_checkpoint, "
                + "sim_snapshot, command_tail) VALUES ('wedge', 99, x'01', x'02');";
            wedge.ExecuteNonQuery();

            Assert.Throws<SqliteException>(
                () => store.SaveBoundary(sim, 19, SimE1Fixture.MilestoneCommands()));
        } // 未提交事务回滚——"崩溃"现场：楔住的写从未落盘

        // 恢复一：走上一完整检查点（波 16，永久槽），重放哈希与活体逐位相等
        Assert.True(store.TryRestoreCheckpoint(out RestoredWaveSnapshot checkpoint));
        Assert.Equal(16, checkpoint.Wave);
        Assert.Equal(CheckpointFixture.BoundaryTickOf(16), checkpoint.Tick);
        Simulation fromCheckpoint = checkpoint.Replay(CheckpointFixture.LongHorizonConfig(), new SimRng(8888));
        Assert.Equal(boundaryHashes[16], fromCheckpoint.ComputeStateHash());

        // 恢复二：崩溃前的最后完整滚动代（波 18）仍在，撕裂写没有毁掉上一代
        Assert.True(store.TryRestoreLatestBoundary(out RestoredWaveSnapshot latest));
        Assert.Equal(18, latest.Wave);
        Simulation fromRolling = latest.Replay(CheckpointFixture.LongHorizonConfig(), new SimRng(9999));
        Assert.Equal(boundaryHashes[18], fromRolling.ComputeStateHash());

        // 半途写入的楔行随事务回滚从未落盘（无 torn 行存活）
        Assert.Throws<InvalidOperationException>(
            () => port.ReadLevel("wedge", SaveVersion.Current));
    }
}
