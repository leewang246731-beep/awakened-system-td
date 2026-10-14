using Xunit;
using EraCore.Legacy;

namespace EraCore.Tests.Legacy;

// [C-20][F-011] 文明基金账本与传承商店（T-074）：3:1 折算入账、装饰蓝图
// 兑付规则（07 #23：仅装饰/蓝图，不碰核心数值）。
public class CivilizationFundLedgerTests
{
    private static CivilizationFundLedger Ledger(long opening = 0) =>
        new(LegacyFixture.Shop(), opening);

    [Fact] // legal — 跃迁 3:1 折算入账（30% 带走中无用途部分 → 基金）
    public void fund_deposit_accumulates()
    {
        CivilizationFundLedger ledger = Ledger();
        Assert.Equal(0, ledger.Balance);
        ledger.DepositFund(40); // e.g. food 带走 120 × 1/3
        ledger.DepositFund(2);
        Assert.Equal(42, ledger.Balance);
    }

    [Fact] // legal — 兑换装饰蓝图：余额扣减+目录可兑
    public void redeem_spends_fund_units()
    {
        CivilizationFundLedger ledger = Ledger(opening: 100);
        HeritageRedemption redemption = ledger.Redeem("legacy:shop-bp-gilded-granary"); // 40
        Assert.Equal(40, redemption.PricePaid);
        Assert.Equal(60, redemption.RemainingBalance);
        Assert.Equal(60, ledger.Balance);
        Assert.Equal(new[] { "legacy:shop-bp-gilded-granary" }, ledger.RedeemedIds);
    }

    [Fact] // legal — 三张全兑（30% 带走清单的完整目录演练：40+60+90=190）
    public void whole_shelf_is_redeemable()
    {
        CivilizationFundLedger ledger = Ledger(opening: 190);
        ledger.Redeem("legacy:shop-bp-gilded-granary");
        ledger.Redeem("legacy:shop-bp-bastion-of-echoes");
        ledger.Redeem("legacy:shop-bp-archivist-pavilion");
        Assert.Equal(0, ledger.Balance);
        Assert.Equal(3, ledger.RedeemedIds.Count);
    }

    [Fact] // illegal — 余额不足拒绝（传承商店不赊账）
    public void insufficient_balance_is_refused()
    {
        CivilizationFundLedger ledger = Ledger(opening: 39);
        Assert.Throws<InvalidOperationException>(() => ledger.Redeem("legacy:shop-bp-gilded-granary"));
        Assert.Equal(39, ledger.Balance);
        Assert.Empty(ledger.RedeemedIds);
    }

    [Fact] // illegal — 装饰唯一：同张蓝图不重复兑付
    public void duplicate_redemption_is_refused()
    {
        CivilizationFundLedger ledger = Ledger(opening: 500);
        _ = ledger.Redeem("legacy:shop-bp-gilded-granary");
        Assert.Throws<InvalidOperationException>(() => ledger.Redeem("legacy:shop-bp-gilded-granary"));
        Assert.Equal(460, ledger.Balance); // 只扣一次
    }

    [Theory] // illegal — 未知条目/空白 id 拒绝
    [InlineData("legacy:shop-bp-nope")]
    [InlineData(" ")]
    public void unknown_items_are_refused(string itemId)
    {
        Assert.Throws<ArgumentException>(() => Ledger(500).Redeem(itemId));
    }

    [Fact] // illegal — 负入账拒绝
    public void negative_deposit_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger().DepositFund(-1));
    }

    [Fact] // boundary — WithdrawFund 只许回滚到非负（事务日志补偿口径）
    public void withdrawal_cannot_drive_negative()
    {
        CivilizationFundLedger ledger = Ledger(opening: 10);
        ledger.WithdrawFund(10);
        Assert.Equal(0, ledger.Balance);
        Assert.Throws<InvalidOperationException>(() => ledger.WithdrawFund(1));
        ledger.DepositFund(5); // 回滚路径可再补
        Assert.Equal(5, ledger.Balance);
    }
}
