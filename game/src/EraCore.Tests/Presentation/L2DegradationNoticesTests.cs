using EraCore.Llm.L2;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [F-078] T-137 降级横幅文案：全理由双语非空+中性口径（不含「错误/失败」
// 危机词——降级是能力事实非危机，OffModeDowngrade 中性横幅先例）+None 空。
public class L2DegradationNoticesTests
{
    [Fact] // 全理由覆盖：每种降级都有可渲染的横幅（双语文案缺一即红）
    public void Every_reason_carries_non_empty_bilingual_copy()
    {
        Assert.NotEmpty(L2DegradationNotices.AllReasons);
        foreach (L2DegradationReason reason in L2DegradationNotices.AllReasons)
        {
            Assert.NotEmpty(L2DegradationNotices.BannerZh(reason));
            Assert.NotEmpty(L2DegradationNotices.BannerEn(reason));
        }
    }

    [Fact] // 无降级=无横幅（None 出空串——正常路径不打扰）
    public void None_reason_renders_no_banner()
    {
        Assert.Equal(string.Empty, L2DegradationNotices.BannerZh(L2DegradationReason.None));
        Assert.Equal(string.Empty, L2DegradationNotices.BannerEn(L2DegradationReason.None));
    }

    [Theory] // 中性口径：横幅明说「已按 L1 处理」（回退事实），不用风险红系危机词
    [InlineData(L2DegradationReason.WebTier)]
    [InlineData(L2DegradationReason.ModelMissing)]
    [InlineData(L2DegradationReason.RuntimeMissing)]
    [InlineData(L2DegradationReason.NotReady)]
    [InlineData(L2DegradationReason.ProcessDead)]
    public void Banner_copy_states_the_L1_fallback_factually(L2DegradationReason reason)
    {
        string zh = L2DegradationNotices.BannerZh(reason);

        Assert.Contains("L1", zh);
        Assert.DoesNotContain("警报", zh);
        Assert.DoesNotContain("严重", zh);
    }

    [Fact] // 模型缺横幅指向设置页下载入口（玩家行动指引在文案里）
    public void Model_missing_banner_points_to_the_settings_entry()
    {
        Assert.Contains("设置页", L2DegradationNotices.BannerZh(L2DegradationReason.ModelMissing));
    }
}
