using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

// [C-04] K-7 save backend: per-level layout facts DTO (T-041)
public class LevelLayoutSnapshotDtoTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Properties_pass_through_the_contract_view()
    {
        var payload = new byte[] { 5, 6, 7 };
        ILevelLayoutSnapshot asContract = new LevelLayoutSnapshotDto("level.e1.m03", payload);

        Assert.Equal("level.e1.m03", asContract.LevelId);
        Assert.Equal(payload, asContract.LayoutPayload);
    }

    // ------------------------------------------------------------ boundary

    [Fact] // boundary — empty payload + minimal id: the DTO carries them verbatim (K-7/A5: opaque bytes, no recompute, no clamping of its own)
    public void Empty_payload_and_minimal_level_id_round_trip_verbatim()
    {
        ILevelLayoutSnapshot asContract = new LevelLayoutSnapshotDto("a", Array.Empty<byte>());

        Assert.Equal("a", asContract.LevelId);
        Assert.Empty(asContract.LayoutPayload);
    }

    [Fact] // boundary — one-byte payload survives untouched (smallest non-empty blob)
    public void Single_byte_payload_round_trips_untouched()
    {
        ILevelLayoutSnapshot asContract = new LevelLayoutSnapshotDto("level.e1.m01", new byte[] { 0xFF });

        Assert.Equal(new byte[] { 0xFF }, asContract.LayoutPayload);
    }

    // ------------------------------------------------------------ illegal

    [Fact] // illegal — null id/payload stay observable as null, not silently coerced (validation is the port's contract: SqliteSavePort rejects blank ids on read/write)
    public void Null_references_stay_null_through_the_view()
    {
        ILevelLayoutSnapshot asContract = new LevelLayoutSnapshotDto(null!, null!);

        Assert.Null(asContract.LevelId);
        Assert.Null(asContract.LayoutPayload);
    }
}
