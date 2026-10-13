using System.Text.Json;
using EraCore.Checkpoint;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-001] 波界快照载荷（T-057）：K-7 level 域不透明字节的编解码与
// "载荷=重放键"语义——恢复走确定性重放，而不是反序列化活对象。
public class WaveSnapshotRecordTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Snapshot_record_round_trips_through_the_payload_codec()
    {
        var record = new WaveSnapshotRecord(
            SimE1Fixture.Seed, 16, 37_800,
            SimE1Fixture.MilestoneCommands()
                .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson)).ToArray());

        WaveSnapshotRecord decoded = CheckpointPayload.DecodeSnapshot(CheckpointPayload.Encode(record));

        Assert.Equal(record.Seed, decoded.Seed);
        Assert.Equal(record.Wave, decoded.Wave);
        Assert.Equal(record.Tick, decoded.Tick);
        Assert.Equal(record.Commands, decoded.Commands);
    }

    [Fact]
    public void Tail_record_round_trips_through_the_payload_codec()
    {
        var record = new WaveTailRecord(18, 42_120, new[]
        {
            new SimCommandLog(40_000, "PlayerBuild", "{\"verb\":\"build-tower\"}"),
        });

        WaveTailRecord decoded = CheckpointPayload.DecodeTail(CheckpointPayload.Encode(record));

        Assert.Equal(record.BoundaryWave, decoded.BoundaryWave);
        Assert.Equal(record.BoundaryTick, decoded.BoundaryTick);
        Assert.Equal(record.Commands, decoded.Commands);
    }

    [Fact]
    public void Restored_snapshot_replays_to_the_boundary_tick_with_identical_hash()
    {
        Simulation reference = CheckpointFixture.RunTo(CheckpointFixture.BoundaryTickOf(1));
        var record = new WaveSnapshotRecord(
            reference.Seed, 1, reference.CurrentTick,
            SimE1Fixture.MilestoneCommands()
                .Where(c => c.IssuedAtTick <= reference.CurrentTick)
                .Select(c => new SimCommandLog(c.IssuedAtTick, c.Kind.ToString(), c.PayloadJson)).ToArray());
        var restored = new RestoredWaveSnapshot(1, reference.CurrentTick, record.Seed, record.Commands);

        Simulation replayed = restored.Replay(SimE1Fixture.Config(), new SimRng(4242));

        Assert.Equal(reference.CurrentTick, replayed.CurrentTick);
        Assert.Equal(reference.ComputeStateHash(), replayed.ComputeStateHash());
    }

    // ----------------------------------------------------------- illegal

    [Fact]
    public void Malformed_payload_bytes_fail_as_JsonException()
    {
        byte[] garbage = { 0xDE, 0xAD, 0xBE, 0xEF };

        Assert.Throws<JsonException>(() => CheckpointPayload.DecodeSnapshot(garbage));
        Assert.Throws<JsonException>(() => CheckpointPayload.DecodeTail(garbage));
        Assert.Throws<JsonException>(() => CheckpointPayload.DecodeSnapshot(Array.Empty<byte>()));
    }

    [Fact]
    public void Null_payload_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CheckpointPayload.DecodeSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => CheckpointPayload.DecodeTail(null!));
    }
}
