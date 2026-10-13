using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

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
}
