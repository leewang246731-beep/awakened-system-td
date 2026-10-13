using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

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
}
