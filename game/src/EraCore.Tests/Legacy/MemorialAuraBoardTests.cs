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
