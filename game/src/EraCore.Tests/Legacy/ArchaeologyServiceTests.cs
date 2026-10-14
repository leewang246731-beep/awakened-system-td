using Xunit;
using EraCore.Legacy;
using EraCore.Tests.World;

namespace EraCore.Tests.Legacy;

// [C-20][F-058] 考古三通道（T-074）：挖掘计价/时长、通道权重律、修复折算
// （残骸→折算建筑或纪念馆，07 #28 ×30%×0.8）。
public class ArchaeologyServiceTests
{
    private static ArchaeologyService Service() => new(LegacyFixture.Archaeology());

    // ---- dig economics ---------------------------------------------- legal

    [Fact] // legal — 各档位时长与钻探 tick（30 Hz 对齐）
    public void dig_quote_scales_with_site_tier()
    {
        ArchaeologyService service = Service();
        Assert.Equal(30, service.Quote(1).DurationSeconds);
        Assert.Equal(50, service.Quote(2).DurationSeconds);
        Assert.Equal(70, service.Quote(3).DurationSeconds);
        Assert.Equal(90, service.Quote(4).DurationSeconds);
        Assert.Equal(30 * 50, service.Quote(2).DrillTicks); // 秒×30Hz
        Assert.Equal(20, service.Quote(4).ManpowerCost);
        Assert.Equal(35, service.Quote(4).EnergyCost);
    }

    [Theory] // illegal — 档位域 1..4 外拒绝
    [InlineData(0)]
    [InlineData(5)]
    public void dig_rejects_out_of_domain_tier(int tier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Service().Quote(tier));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Dig(tier, 100.0, new LcgRng(1)));
    }

    // ---- channel draws ---------------------------------------------- legal

    [Fact] // legal — 同 seed 同序两次挖掘通道序列全等（确定性）
    public void dig_sequence_is_deterministic()
    {
        ArchaeologyService service = Service();
        var first = new LcgRng(77);
        var second = new LcgRng(77);
        for (int i = 0; i < 30; i++)
        {
            ArchaeologyDigResult a = service.Dig(1 + (i % 4), 100.0, first);
            ArchaeologyDigResult b = service.Dig(1 + (i % 4), 100.0, second);
            Assert.Equal(a.Channel, b.Channel);
            Assert.Equal(a.Scroll?.Id, b.Scroll?.Id);
            Assert.Equal(a.Blueprint?.Id, b.Blueprint?.Id);
            Assert.Equal(a.Restoration?.AsMemorial, b.Restoration?.AsMemorial);
        }
    }

    [Fact] // legal — 权重律：大批量抽样通道频率落在内容权重 ±10% 绝对带内
    public void channel_draws_follow_content_weights()
    {
        ArchaeologyService service = Service();
        var rng = new LcgRng(20261074);
        const int samples = 2000; // 抽样大数律（容差=统计带，源=二项方差近似）
        int scrolls = 0, blueprints = 0, restorations = 0;
        for (int i = 0; i < samples; i++)
        {
            switch (service.Dig(1, 0.0, rng).Channel)
            {
                case ArchaeologyChannelKind.KnowledgeScroll: scrolls++; break;
                case ArchaeologyChannelKind.AncientBlueprint: blueprints++; break;
                case ArchaeologyChannelKind.Restoration: restorations++; break;
            }
        }

        Assert.InRange(scrolls / (double)samples, 0.40, 0.60); // 内容 50%
        Assert.InRange(blueprints / (double)samples, 0.20, 0.40); // 内容 30%
        Assert.InRange(restorations / (double)samples, 0.10, 0.30); // 内容 20%
    }

    [Fact] // legal — 通道载荷：残卷带记忆文案、图纸带跨代 mix
    public void channel_payloads_carry_real_rows()
    {
        ArchaeologyService service = Service();
        var rng = new LcgRng(5);
        ArchaeologyDigResult? scrollHit = null, blueprintHit = null, restorationHit = null;
        for (int i = 0; i < 500 && (scrollHit is null || blueprintHit is null || restorationHit is null); i++)
        {
            ArchaeologyDigResult result = service.Dig(1, 100.0, rng);
            if (result.Channel == ArchaeologyChannelKind.KnowledgeScroll && scrollHit is null)
            {
                scrollHit = result;
            }

            if (result.Channel == ArchaeologyChannelKind.AncientBlueprint && blueprintHit is null)
            {
                blueprintHit = result;
            }

            if (result.Channel == ArchaeologyChannelKind.Restoration && restorationHit is null)
            {
                restorationHit = result;
            }
        }

        Assert.NotNull(scrollHit);
        Assert.False(string.IsNullOrWhiteSpace(scrollHit!.Scroll!.MemoryZh)); // F-099 记忆恢复
        Assert.NotNull(blueprintHit);
        Assert.Equal(2, blueprintHit!.Blueprint!.Mix.Count); // E1 构件+E2 结构
        Assert.NotNull(restorationHit);
        Assert.True(restorationHit!.Restoration!.RepairCost > 0);
    }

    // ---- restoration ------------------------------------------------- legal

    [Fact] // legal — 修复折算：残值 ×0.3×0.8（07 #28 叠加）
    public void restoration_quote_folds_wreck_value()
    {
        ArchaeologyService service = Service();
        RestorationOutcome outcome = service.Restore(1000.0, new LcgRng(1));
        Assert.Equal(1000.0 * 0.3 * 0.8, outcome.RepairCost, 9); // 240
    }

    [Fact] // legal — 纪念馆修复折扣经单一咽喉叠加：5% 光环 → 228
    public void restoration_merges_memorial_discount_through_the_choke()
    {
        ArchaeologyService service = Service();
        var board = new MemorialAuraBoard(LegacyFixture.Archaeology());
        board.RegisterMemorial(); // repair_discount 5%
        RestorationOutcome outcome = service.Restore(1000.0, new LcgRng(1), board);
        Assert.Equal(240.0 * 0.95, outcome.RepairCost, 9);
    }

    [Fact] // legal — 折算建筑/纪念馆权重律（60/40）
    public void restoration_outcome_follows_functional_and_memorial_weights()
    {
        ArchaeologyService service = Service();
        var rng = new LcgRng(991);
        const int samples = 2000; // 统计带同上
        int memorials = 0;
        for (int i = 0; i < samples; i++)
        {
            if (service.Restore(10.0, rng).AsMemorial)
            {
                memorials++;
            }
        }

        Assert.InRange(memorials / (double)samples, 0.30, 0.50); // 内容 40%
    }

    [Theory] // illegal — 残值负数拒绝
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void restoration_rejects_bad_wreck_value(double wreckValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Service().Restore(wreckValue, new LcgRng(1)));
    }

    // ---- boundary ------------------------------------------------------

    [Fact] // boundary — 残值 0：修复通道报价 0（免费清除废墟口径）
    public void zero_wreck_restores_for_free()
    {
        RestorationOutcome outcome = Service().Restore(0.0, new LcgRng(3));
        Assert.Equal(0.0, outcome.RepairCost, 12);
    }

    [Fact] // illegal — 空目录表拒绝（构造校验）
    public void empty_catalogs_rejected_at_construction()
    {
        ArchaeologyTable table = LegacyFixture.Archaeology();
        Assert.Throws<ArgumentException>(() => new ArchaeologyService(table with { Scrolls = Array.Empty<KnowledgeScrollSpec>() }));
        Assert.Throws<ArgumentException>(() => new ArchaeologyService(table with { Blueprints = Array.Empty<AncientBlueprintSpec>() }));
    }
}
