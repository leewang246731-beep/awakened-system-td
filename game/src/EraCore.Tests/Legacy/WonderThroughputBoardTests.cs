using EraCore.Legacy;
using EraCore.Logistics;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-060] 世博会奇观吞吐通道（T-117）：世界博览会（E3，物流吞吐 +25%，
// 附录 F.3）经 WonderThroughputBoard 派生全局乘数 Π(1+0.25)=1.25，接入
// Logistics.TransportTier 吞吐公式的乘数通道——档位表对奇观保持不可知，
// 奇观表对档位保持不可知，本板是唯一交点（单一通道纪律）。
public class WonderThroughputBoardTests
{
    private static LegacyLedger Ledger() => new(Archaeology(), Shop());

    private static WonderSpec Expo() => Wonders().Single(w => w.BonusKind == WonderThroughputBoard.ThroughputBonusKind);

    // ---- legal --------------------------------------------------------------

    [Fact] // [F-060] legal — 内容驱动乘数：博览会未建=1.0；建成后=1+0.25（真 wonders.json 值）
    public void expo_wonder_yields_the_content_declared_multiplier()
    {
        WonderSpec expo = Expo();
        Assert.Equal("campaign:wonder-expo", expo.Id); // E3 奇观=世界博览会
        Assert.Equal(3, expo.EraIndex);

        var ledger = Ledger();
        var board = new WonderThroughputBoard(Wonders(), ledger);
        Assert.Equal(1.0, board.Multiplier, 12); // 未建：无加成

        ledger.MarkWonderBuilt(expo.Id);
        Assert.Equal(1.25, board.Multiplier, 12); // 建成：1+0.25（内容 bonus_value=0.25）
        Assert.Equal(1.0 + expo.BonusValue, board.Multiplier, 12); // 逐值源=内容行
    }

    [Fact] // [F-060] legal — 建成博览会：全档吞吐=建成前 ×1.25（通道级手算对照；
            // 端到端"E2 跃迁事务→建成→1.25×"见 E2ToE3TransitionRegressionTests）
    public void every_tier_throughput_scales_by_the_expo_multiplier()
    {
        var ledger = Ledger();
        var board = new WonderThroughputBoard(Wonders(), ledger);

        // 建成博览会（E3 奇观；跃迁链上它只能在 E2→E3 跃迁之后建成——硬前置
        // 排序由 EraTransition Precheck 执法）：乘数通道对全部六档逐一生效。
        ledger.MarkWonderBuilt(Expo().Id);
        double multiplier = board.Multiplier;
        Assert.Equal(1.25, multiplier, 12);

        foreach (TransportTier tier in TransportTiers.Ladder)
        {
            double before = tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx);
            double after = tier.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx, multiplier);
            if (tier.Form == TransportForm.Teleport)
            {
                Assert.True(double.IsInfinity(after)); // ∞×1.25=∞（耗能档不受数值加成影响）
                continue;
            }

            // 手算对照：乘数通道=线性放大（0.3→0.375、0.6→0.75、10→12.5、15→18.75、20→25）
            Assert.Equal(before * multiplier, after, 9);
            Assert.InRange( // 与设计锚点同带（±5%，T-064 口径——马车 0.6017 原型校准档）
                after,
                tier.ThroughputAnchorItemsPerSecond * multiplier * (1 - TransportTiers.AnchorTolerance),
                tier.ThroughputAnchorItemsPerSecond * multiplier * (1 + TransportTiers.AnchorTolerance));
        }
    }

    [Fact] // [F-060] legal — 其它奇观不进吞吐通道：金字塔/大图书馆建成乘数仍 1.0
    public void non_throughput_wonders_leave_the_multiplier_at_one()
    {
        var ledger = Ledger();
        foreach (WonderSpec wonder in Wonders().Where(w => w.BonusKind != WonderThroughputBoard.ThroughputBonusKind))
        {
            ledger.MarkWonderBuilt(wonder.Id);
        }

        var board = new WonderThroughputBoard(Wonders(), ledger);
        Assert.Equal(1.0, board.Multiplier, 12);
    }

    // ---- boundary --------------------------------------------------------------

    [Fact] // [F-060] boundary — 乘数通道下界=1：1.0 通道关闭（与未乘等价）
    public void unit_multiplier_is_the_disabled_channel()
    {
        TransportTier porter = TransportTiers.Porter;
        Assert.Equal(
            porter.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx),
            porter.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx, 1.0), 12);
    }

    // ---- illegal --------------------------------------------------------------

    [Theory] // [F-060] illegal — 乘数通道只放大：<1 或非有限值拒绝（F.3 增益无减免式折减）
    [InlineData(0.99)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void multiplier_below_one_is_rejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransportTiers.Porter.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx, value));
    }

    [Fact] // [F-060] illegal — 吞吐型奇观 bonus 非正有限值：板构造即拒（内容形状守卫）
    public void non_positive_throughput_bonus_is_rejected_at_construction()
    {
        WonderSpec broken = Expo() with { BonusValue = 0.0 };
        Assert.Throws<InvalidOperationException>(() => new WonderThroughputBoard(new[] { broken }, Ledger()));
    }
}
