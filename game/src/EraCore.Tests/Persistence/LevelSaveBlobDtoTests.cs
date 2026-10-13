using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

// [C-04] K-7 save backend: level blob DTO (T-041)
public class LevelSaveBlobDtoTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Properties_pass_through_the_contract_view()
    {
        var sim = new byte[] { 1, 1, 2, 3 };
        var tail = new byte[] { 9 };
        ILevelSaveBlob asContract = new LevelSaveBlobDto(12, sim, tail);

        Assert.Equal(12, asContract.WaveCheckpoint);
        Assert.Same(sim, asContract.SimSnapshotPayload);
        Assert.Same(tail, asContract.CommandTailPayload);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Wave_checkpoint_minimum_from_rule_N7_is_representable()
    {
        // N7: checkpoint = floor(win waves x 0.75) and >= 10 — the DTO must
        // carry the boundary value without clamping of its own.
        var blob = new LevelSaveBlobDto(10, Array.Empty<byte>(), Array.Empty<byte>());

        Assert.Equal(10, blob.WaveCheckpoint);
        Assert.Empty(blob.SimSnapshotPayload);
        Assert.Empty(blob.CommandTailPayload);
    }

    [Fact] // boundary — both blobs empty at once: a checkpoint with no tail yet
    public void Empty_blobs_are_carried_verbatim()
    {
        ILevelSaveBlob asContract = new LevelSaveBlobDto(10, Array.Empty<byte>(), Array.Empty<byte>());

        Assert.Empty(asContract.SimSnapshotPayload);
        Assert.Empty(asContract.CommandTailPayload);
    }

    [Fact] // boundary — checkpoint upper edge (int.MaxValue, far above any win-wave count) plus max-length payloads the DTO must carry without copying or truncating
    public void Max_checkpoint_and_max_length_payloads_round_trip()
    {
        // 1 MiB doubles: two orders above a realistic wave-boundary snapshot,
        // still far below the SQLite BLOB ceiling — the DTO itself imposes no cap.
        var sim = new byte[1024 * 1024];
        var tail = new byte[1024 * 1024];
        sim[^1] = 0xAB;
        tail[^1] = 0xCD;
        ILevelSaveBlob asContract = new LevelSaveBlobDto(int.MaxValue, sim, tail);

        Assert.Equal(int.MaxValue, asContract.WaveCheckpoint);
        Assert.Same(sim, asContract.SimSnapshotPayload);
        Assert.Same(tail, asContract.CommandTailPayload);
        Assert.Equal(1024 * 1024, asContract.SimSnapshotPayload.Length);
        Assert.Equal(0xAB, asContract.SimSnapshotPayload[^1]);
        Assert.Equal(0xCD, asContract.CommandTailPayload[^1]);
    }
}
