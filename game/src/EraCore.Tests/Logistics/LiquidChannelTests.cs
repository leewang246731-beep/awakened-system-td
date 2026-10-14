using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the liquid channel family (C-07 T-064, [F-042]; 任务卡
/// "水渠（E2 液体薄版：结构化通道类型+流速常量）"): channel-kind flow-rate
/// constants and their tick-domain scaling, contiguity validation
/// (legal/boundary/illegal), and the structural content hash (same
/// construction ⇒ same hash; any cell change ⇒ different hash).
/// </summary>
// [F-042] 水渠（E2 液体薄版：结构化通道类型+流速常量；液体内容随 E2 内容任务）
public class LiquidChannelTests
{
    private static CellCoord[] StraightRun(int cells = 5) =>
        Enumerable.Range(0, cells).Select(x => new CellCoord(x, 0)).ToArray();

    // ------------------------------------------------------------ constants

    [Fact]
    public void AqueductFlowConstantsScaleToTheTickDomain()
    {
        // [F-042] 流速常量：水渠 2.0 单位/s；tick 域折算 = 2.0/30 = 1/15 每tick
        var channel = new LiquidChannel(LiquidChannelKind.Aqueduct, StraightRun());

        Assert.Equal(LiquidChannelKind.Aqueduct, channel.Kind);
        Assert.Equal(2.0, LiquidChannel.AqueductFlowUnitsPerSecond, 9);
        Assert.Equal(2.0, channel.FlowUnitsPerSecond, 9);
        Assert.Equal(2.0 / 30.0, channel.FlowUnitsPerTick, 9);
        Assert.Equal(5, channel.Cells.Count);
    }

    // ------------------------------------------------------------ validation

    [Theory] // legal
    [InlineData(2)]
    [InlineData(12)]
    public void ContiguousRunsOfAnyLengthAreAccepted(int cells)
    {
        // [F-042] 合法：≥2 格的连续渠道（直/折皆可）
        var channel = new LiquidChannel(LiquidChannelKind.Aqueduct, StraightRun(cells));
        Assert.Equal(cells, channel.Cells.Count);
    }

    [Fact] // legal — 折线渠道（8 邻接含对角）
    public void ADiagonalTurnIsAContiguousRun()
    {
        var cells = new[] { new CellCoord(0, 0), new CellCoord(1, 1), new CellCoord(2, 1) };
        var channel = new LiquidChannel(LiquidChannelKind.Aqueduct, cells);
        Assert.Equal(3, channel.Cells.Count);
    }

    [Theory] // illegal
    [InlineData("single-cell")]
    [InlineData("gap")]
    [InlineData("repeat")]
    public void MalformedRunsAreRejected(string site)
    {
        // [F-042] 非法：单格/断档/重复格 → 构造即拒
        CellCoord[] cells = site switch
        {
            "single-cell" => new[] { new CellCoord(0, 0) },
            "gap" => new[] { new CellCoord(0, 0), new CellCoord(2, 0) },
            _ => new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(0, 0) },
        };
        Assert.Throws<ArgumentException>(() => new LiquidChannel(LiquidChannelKind.Aqueduct, cells));
    }

    [Fact] // illegal
    public void NullCellsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new LiquidChannel(LiquidChannelKind.Aqueduct, null!));
    }

    // ------------------------------------------------------------ structural hash

    [Fact]
    public void StructuralHashIsStableAndSensitiveToEveryCell()
    {
        // [F-042] 结构哈希：同构同哈希（确定性探针）；任一格变化或种类不同
        // ⇒ 哈希不同
        var a = new LiquidChannel(LiquidChannelKind.Aqueduct, StraightRun());
        var b = new LiquidChannel(LiquidChannelKind.Aqueduct, StraightRun());
        var shifted = new LiquidChannel(
            LiquidChannelKind.Aqueduct, Enumerable.Range(1, 5).Select(x => new CellCoord(x, 0)).ToArray());
        var longer = new LiquidChannel(LiquidChannelKind.Aqueduct, StraightRun(6));

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.NotEqual(a.ComputeStateHash(), shifted.ComputeStateHash());
        Assert.NotEqual(a.ComputeStateHash(), longer.ComputeStateHash());
        Assert.Equal(a.ComputeStateHash(), a.ComputeStateHash()); // 幂等
    }
}
