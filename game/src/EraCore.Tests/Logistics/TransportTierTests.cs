using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Logic tests of the six-tier ladder table (C-07 全量, T-064, [F-041]):
/// ladder shape (six rungs, design order, movement forms), the tier-1 row
/// frozen to the T-047 porter anchors, the round-trip formula hand-calc for
/// every trip tier at the frozen 160 px reference distance, and lookup
/// discipline (legal/boundary/illegal).
/// </summary>
// [F-041] 六档物流阶梯表（v3.2 §5：0.3→0.6→10→15→20→∞ 件/s，160px 冻结口径）
public class TransportTierTests
{
    // ------------------------------------------------------------ ladder shape

    [Fact]
    public void LadderHasSixRungsInDesignOrderWithTheirForms()
    {
        // [F-041] 阶梯形状：6 档、顺序、形态（民夫/马车=网格行程、矿轨=轨道
        // 行程、传送带=连续流、无人机=直线飞行、传送阵=瞬时）
        Assert.Equal(6, TransportTiers.Ladder.Count);
        Assert.Same(TransportTiers.Porter, TransportTiers.Ladder[0]);
        Assert.Same(TransportTiers.Cart, TransportTiers.Ladder[1]);
        Assert.Same(TransportTiers.MineRail, TransportTiers.Ladder[2]);
        Assert.Same(TransportTiers.Belt, TransportTiers.Ladder[3]);
        Assert.Same(TransportTiers.Drone, TransportTiers.Ladder[4]);
        Assert.Same(TransportTiers.Teleporter, TransportTiers.Ladder[5]);

        Assert.Equal(TransportForm.TripGrid, TransportTiers.Porter.Form);
        Assert.Equal(TransportForm.TripGrid, TransportTiers.Cart.Form);
        Assert.Equal(TransportForm.TripRail, TransportTiers.MineRail.Form);
        Assert.Equal(TransportForm.BeltFlow, TransportTiers.Belt.Form);
        Assert.Equal(TransportForm.TripFlight, TransportTiers.Drone.Form);
        Assert.Equal(TransportForm.Teleport, TransportTiers.Teleporter.Form);
    }

    [Fact]
    public void TierOneIsFrozenToTheT047PorterAnchors()
    {
        // [F-041] 第 1 档单源：阶梯表民夫行直读 Carrier 冻结锚点（容量/速度/
        // 服务窗/吞吐），两处永不漂移
        Assert.Equal(Carrier.CapacityItems, TransportTiers.Porter.CapacityItems);
        Assert.Equal(Carrier.SpeedPixelsPerTick, TransportTiers.Porter.SpeedPxPerTick);
        Assert.Equal(Carrier.LoadTicks, TransportTiers.Porter.LoadTicks);
        Assert.Equal(Carrier.UnloadTicks, TransportTiers.Porter.UnloadTicks);
        Assert.Equal(Carrier.PorterThroughputItemsPerSecond, TransportTiers.Porter.ThroughputAnchorItemsPerSecond);
    }

    // ------------------------------------------------------------ formula hand-calc at 160 px

    [Theory] // legal — 全部六档在冻结参考距 160px 的公式吞吐
    [InlineData("porter", 200.0, 0.3)]        // 2×160/2.0 + 20 + 20 = 200t → 2 件/6.67s
    [InlineData("cart", 199.42857142857142, 0.6017)]   // 2×160/3.5 + 54 + 54 → 4 件/RT
    [InlineData("mine-rail", 60.0, 10.0)]     // 2×160/16 + 20 + 20 = 60t → 20 件/2s
    [InlineData("drone", 30.0, 20.0)]         // 2×160/20 + 7 + 7 = 30t → 20 件/1s
    public void TripTierRoundTripsAndThroughputsMatchTheHandCalc(string tierId, double expectedRoundTrip, double expectedThroughput)
    {
        // [F-041] 手算对照：往返周期与吞吐=容量÷(周期/30)（六档同距 160px 冻结口径）
        TransportTier tier = TransportTiers.ById(tierId)!;
        Assert.NotNull(tier);

        double roundTrip = tier.RoundTripTicks(TransportTiers.ReferenceOneWayPx);
        Assert.Equal(expectedRoundTrip, roundTrip, 5); // 1e-5 abs tolerance: hand-calc reproduction

        double throughput = tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx);
        Assert.Equal(expectedThroughput, throughput, 3);
    }

    [Fact]
    public void EveryAnchorIsRealizedWithinToleranceAtTheReferenceDistance()
    {
        // [F-041] 阶梯锚点带内取值：0.3/0.6/10/20 四个行程档公式值 ∈ 锚点±5%，
        // 传送带 15 件/s 由带宽公式（速度÷格宽×每格件数）实现，传送阵 ∞
        foreach (TransportTier tier in TransportTiers.Ladder)
        {
            switch (tier.Form)
            {
                case TransportForm.TripGrid:
                case TransportForm.TripRail:
                case TransportForm.TripFlight:
                    double realized = tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx);
                    Assert.InRange(
                        realized,
                        tier.ThroughputAnchorItemsPerSecond * (1 - TransportTiers.AnchorTolerance),
                        tier.ThroughputAnchorItemsPerSecond * (1 + TransportTiers.AnchorTolerance));
                    break;
                case TransportForm.BeltFlow:
                    // 表面速度 16 px/tick ÷ 32 px/格 = 0.5 格/tick，1 件/格 ⇒ 15 件/s
                    double beltRate = tier.SpeedPxPerTick / Carrier.CellSizePx * tier.CapacityItems * Carrier.TicksPerSecond;
                    Assert.Equal(tier.ThroughputAnchorItemsPerSecond, beltRate, 6);
                    break;
                case TransportForm.Teleport:
                    Assert.Equal(double.PositiveInfinity, tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx));
                    break;
            }
        }
    }

    [Fact]
    public void ReferenceDistanceIsTheFrozenCaliper()
    {
        // [F-041] 冻结口径：参考距 = 5 格 × 32 px = 160 px（batch2 review F5）
        Assert.Equal(160.0, TransportTiers.ReferenceOneWayPx, 9);
        Assert.Equal(0.05, TransportTiers.AnchorTolerance, 9);
    }

    // ------------------------------------------------------------ lookup

    [Theory] // legal
    [InlineData("porter")]
    [InlineData("cart")]
    [InlineData("mine-rail")]
    [InlineData("conveyor-belt")]
    [InlineData("drone")]
    [InlineData("teleporter")]
    public void ByIdFindsEveryLadderRung(string id)
    {
        // [F-041] 查表：六档 id 全命中且返回同一实例（表冻结）
        TransportTier? tier = TransportTiers.ById(id);
        Assert.NotNull(tier);
        Assert.Equal(id, tier.Id);
        Assert.Contains(tier, TransportTiers.Ladder);
    }

    [Theory] // illegal — 未知/空/大小写不符 id
    [InlineData("steam-engine")]
    [InlineData("Porter")]
    [InlineData("")]
    public void ByIdRejectsUnknownIds(string id)
    {
        // [F-041] 负向：未知 id 确实返回 null（不是抛错即静默）
        Assert.Null(TransportTiers.ById(id));
    }
}
