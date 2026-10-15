using Xunit;
using EraCore.Legacy;

namespace EraCore.Tests.Legacy;

// [C-20][F-059] 纪念馆光环板（T-074）：同类光环取最大不叠加 + 全图至多 3 座
// 生效（F.3）。
public class MemorialAuraBoardTests
{
    private static MemorialAuraBoard Board() => new(LegacyFixture.Archaeology());

    [Fact] // legal — 单馆：内容光环生效（research 4% / repair 5%）
    public void one_memorial_applies_content_auras()
    {
        MemorialAuraBoard board = Board();
        board.RegisterMemorial();
        Assert.Equal(0.04, board.EffectiveResearchRelief(), 9);
        Assert.Equal(0.05, board.EffectiveRepairDiscount(), 9);
        Assert.Equal(1, board.EffectiveCount);
    }

    [Fact] // legal — 同类光环取最大不叠加：两馆 4%/9% → 9%（不是 13%）
    public void same_kind_auras_take_the_maximum()
    {
        MemorialAuraBoard board = Board();
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.04 });
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.09 });
        Assert.Equal(0.09, board.EffectiveResearchRelief(), 9);
    }

    [Fact] // legal — 至多 3 座生效：第 4 座起不贡献（T-117 第 4 座纪念馆不生效同则）
    public void only_the_first_three_memorials_count()
    {
        MemorialAuraBoard board = Board();
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.04 });
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.05 });
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.06 });
        Assert.Equal(3, board.EffectiveCount);
        Assert.Equal(0.06, board.EffectiveResearchRelief(), 9);
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.90 }); // 第 4 座
        Assert.Equal(4, board.Memorials.Count); // 馆还立着
        Assert.Equal(3, board.EffectiveCount); // 生效数封顶
        Assert.Equal(0.06, board.EffectiveResearchRelief(), 9); // 第 4 座不生效
    }

    [Fact] // legal — 不同类光环互不挤占（repair 与 research 各自取最大）
    public void different_kinds_track_independently()
    {
        MemorialAuraBoard board = Board();
        board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = 0.07 });
        board.RegisterMemorial(new Dictionary<string, double> { ["repair_discount"] = 0.05 });
        Assert.Equal(0.07, board.EffectiveResearchRelief(), 9);
        Assert.Equal(0.05, board.EffectiveRepairDiscount(), 9);
    }

    [Fact] // [F-059] legal（T-117 显式化）——第 4 座"建"不生效：经考古修复通道真实建出 4 座
    // 纪念馆（真 ArchaeologyService.Restore 抽中 AsMemorial→注册），第 4 座落地后
    // 前 3 座的生效光环值逐项不变，且下游 StackingRule 修复报价分文不动。
    public void fourth_memorial_built_through_the_restoration_channel_changes_no_effective_aura()
    {
        var table = LegacyFixture.Archaeology();
        var service = new ArchaeologyService(table);
        MemorialAuraBoard board = Board();

        // 建造路径=修复通道抽中纪念馆（权重 40/100，确定性 seed 驱动；每座带递增
        // research 光环+内容 repair 光环以使"取最大"两通道皆可观测——auras 覆写
        // 是 RegisterMemorial 的测试缝）
        var rng = new EraCore.Tests.World.LcgRng(20261174);
        double[] escalating = { 0.04, 0.05, 0.06, 0.90 };
        int built = 0;
        while (built < 4)
        {
            RestorationOutcome outcome = service.Restore(wreckValue: 1000, rng, board);
            if (outcome.AsMemorial)
            {
                board.RegisterMemorial(new Dictionary<string, double>
                {
                    ["research_relief"] = escalating[built],
                    ["repair_discount"] = 0.05, // 内容值（archaeology.json memorial_auras）
                });
                built++;
            }
        }

        Assert.Equal(4, board.Memorials.Count); // 四馆全立（拆除是危险位，永不自动）
        Assert.Equal(3, board.EffectiveCount); // 生效数封顶 3（F.3 全图至多 3 座生效）
        Assert.Equal(0.06, board.EffectiveResearchRelief(), 9); // 第 4 座 0.90 不生效——前 3 最大值不变
        Assert.Equal(0.05, board.EffectiveRepairDiscount(), 9); // 内容 repair 光环不变

        // 下游单一咽喉：第 4 座前后修复报价逐分一致（Restore 折算基数=残值×0.3×0.8=240，
        // 光环经 StackingRule：240×(1−0.05)=228）
        const double foldedBase = 1000 * 0.3 * 0.8; // Restore 的 07 #28 折算层（×0.3×0.8）
        double quoteWithFourth = EraCore.Legacy.StackingRule.Apply(foldedBase, new[] { board.EffectiveRepairDiscount() });
        MemorialAuraBoard threeOnly = Board();
        for (int i = 0; i < 3; i++)
        {
            threeOnly.RegisterMemorial(new Dictionary<string, double>
            {
                ["research_relief"] = escalating[i],
                ["repair_discount"] = 0.05,
            });
        }

        double quoteWithThree = EraCore.Legacy.StackingRule.Apply(foldedBase, new[] { threeOnly.EffectiveRepairDiscount() });
        Assert.Equal(quoteWithThree, quoteWithFourth, 12);
        Assert.Equal(foldedBase * (1 - 0.05), quoteWithFourth, 9); // 手算对照=228
    }

    [Fact] // boundary — 无馆=零光环
    public void no_memorials_means_no_auras()
    {
        MemorialAuraBoard board = Board();
        Assert.Equal(0, board.EffectiveCount);
        Assert.Equal(0.0, board.EffectiveResearchRelief(), 12);
        Assert.Equal(0.0, board.EffectiveRepairDiscount(), 12);
    }

    [Theory] // illegal — 光环值域 [0,1) 外拒绝
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void out_of_domain_auras_rejected(double value)
    {
        MemorialAuraBoard board = Board();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            board.RegisterMemorial(new Dictionary<string, double> { ["research_relief"] = value }));
    }
}
