using EraCore.Combat;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-120] T-093 塔头状态条（三层信息②：弹药/冷却进度，一级存档=1 件
// 弹药=10 发）：三行——合法（比率/档位）、边界（档位阈值边界/满格/无弹耗塔
// 隐藏）、非法（负值/shotsPerItem<1）。
public class TowerHeadBarTests
{
    private static TowerSpec ArrowSpec() => new(
        Id: "era1:tower-wood-arrow",
        Class: TowerClass.Pierce,
        Tier: 1,
        MaxTier: 2,
        CostTotal: 30,
        Dps: 10.0,
        DamagePerShot: 10,
        ReloadTicks: 30,
        RangeCells: 5.0,
        Hp: 120,
        AmmoItem: "era1:ammo-arrow",
        AmmoPerShot: 1,
        TierGrowth: null,
        Traits: null);

    private static TowerSpec CampfireSpec() => ArrowSpec() with
    {
        Id = "era1:tower-campfire",
        Class = TowerClass.Support,
        AmmoItem = null,
        AmmoPerShot = 0,
        ReloadTicks = null,
    };

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 满一件弹药（10 发）=满格 Normal；冷却 30/30=1.0
    public void Full_item_reference_fills_the_bar_normal()
    {
        TowerHeadBarView view = TowerHeadBar.Build(
            ArrowSpec(), ammoShotsRemaining: 10, shotsPerItem: 10, cooldownTicksRemaining: 30);

        Assert.Equal(1.0, view.AmmoRatio, precision: 6);
        Assert.Equal(HeadBarTier.Normal, view.AmmoTier);
        Assert.Equal(1.0, view.ReloadRatio, precision: 6);
    }

    [Theory] // legal — 表驱动：余量→比率+档位（30%/10% 两级灰度竖置）
    [InlineData(10, 1.0, HeadBarTier.Normal)]
    [InlineData(5, 0.5, HeadBarTier.Normal)]
    [InlineData(3, 0.3, HeadBarTier.Amber)]   // 0.30 ≤ 0.30 → amber（含边界）
    [InlineData(1, 0.1, HeadBarTier.Red)]     // 0.10 ≤ 0.10 → red（含边界）
    [InlineData(0, 0.0, HeadBarTier.Red)]
    public void Ammo_tier_follows_the_two_band_thresholds(
        long shots, double expectedRatio, HeadBarTier expectedTier)
    {
        TowerHeadBarView view = TowerHeadBar.Build(
            ArrowSpec(), shots, shotsPerItem: 10, cooldownTicksRemaining: 0);

        Assert.Equal(expectedRatio, view.AmmoRatio, precision: 6);
        Assert.Equal(expectedTier, view.AmmoTier);
        Assert.Equal(0.0, view.ReloadRatio, precision: 6); // 冷却 0=ready
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 超过一件参考（25 发）钳到满格；无弹耗塔（篝火）整条隐藏
    public void Overflow_clamps_and_no_ammo_tower_hides_the_bar()
    {
        TowerHeadBarView overflow = TowerHeadBar.Build(
            ArrowSpec(), ammoShotsRemaining: 25, shotsPerItem: 10, cooldownTicksRemaining: 15);

        Assert.Equal(1.0, overflow.AmmoRatio, precision: 6);
        Assert.Equal(0.5, overflow.ReloadRatio, precision: 6); // 15/30

        TowerHeadBarView campfire = TowerHeadBar.Build(
            CampfireSpec(), ammoShotsRemaining: 7, shotsPerItem: 10, cooldownTicksRemaining: 0);

        Assert.Equal(HeadBarTier.Hidden, campfire.AmmoTier);
        Assert.Equal(0.0, campfire.ReloadRatio);
    }

    // ---- illegal ---------------------------------------------------------

    [Fact] // illegal — 负余量/负冷却/shotsPerItem<1/null spec 全部拒绝
    public void Negative_and_degenerate_arguments_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TowerHeadBar.Build(ArrowSpec(), ammoShotsRemaining: -1, shotsPerItem: 10, cooldownTicksRemaining: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TowerHeadBar.Build(ArrowSpec(), ammoShotsRemaining: 1, shotsPerItem: 10, cooldownTicksRemaining: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TowerHeadBar.Build(ArrowSpec(), ammoShotsRemaining: 1, shotsPerItem: 0, cooldownTicksRemaining: 0));
        Assert.Throws<ArgumentNullException>(
            () => TowerHeadBar.Build(null!, 1, 10, 0));
    }
}
