using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [F-164] [C-26] T-110 Web UI 禁用适配表面矩阵：WebUiSurfaces 从
// WebConstraints 档位行派生 UI 面可见性（BYOK 表单/AI 高级语法入口随 L3
// 开关、对比条披露常显、desktop_only 标注只在 L3 平台禁用时激活）——三行
// legal/boundary/illegal + 文案锚点（措辞单源，面板只镜像）。
public class WebUiSurfacesTests
{
    // ---- legal -----------------------------------------------------------

    [Fact] // legal — Web 档位行：BYOK 表单与 AI 高级语法入口隐藏、披露保留、标注激活
    public void Web_row_hides_byok_surfaces_and_annotates_desktop_only_terms()
    {
        WebUiSurfacesRow row = WebUiSurfaces.Surfaces(WebConstraints.Availability(isWeb: true));

        Assert.False(row.ByokFormVisible);
        Assert.False(row.AiAdvancedSyntaxHintVisible);
        Assert.True(row.CompareBarVisible);
        Assert.True(row.DesktopOnlyAnnotationActive);
    }

    [Fact] // legal — 桌面档位行：全表面在位、无 desktop_only 标注
    public void Desktop_row_keeps_every_surface_and_never_annotates()
    {
        WebUiSurfacesRow row = WebUiSurfaces.Surfaces(WebConstraints.Availability(isWeb: false));

        Assert.True(row.ByokFormVisible);
        Assert.True(row.AiAdvancedSyntaxHintVisible);
        Assert.True(row.CompareBarVisible);
        Assert.False(row.DesktopOnlyAnnotationActive);
    }

    [Theory] // legal — 标注决策矩阵：只有 desktop_only 词条 × Web 档位行（L3 平台禁用）才标注
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Annotate_decision_matrix(bool desktopOnly, bool isWeb, bool expected)
    {
        bool annotated = WebUiSurfaces.AnnotateDesktopOnly(
            WebConstraints.Availability(isWeb), desktopOnly);

        Assert.Equal(expected, annotated);
    }

    [Fact] // legal — 文案锚点：标注后缀与 AI 高级语法提示措辞到位（中英双语、指向设置页）
    public void Copy_anchors_are_bilingual_and_name_the_source_facts()
    {
        Assert.Contains("桌面版功能", WebUiSurfaces.DesktopOnlySuffixZh);
        Assert.Contains("desktop-only", WebUiSurfaces.DesktopOnlySuffixEn);

        Assert.Contains("AI 高级语法", WebUiSurfaces.AiAdvancedSyntaxHintZh);
        Assert.Contains("BYOK", WebUiSurfaces.AiAdvancedSyntaxHintZh);      // §11.5 L3 行措辞锚
        Assert.Contains("默认关", WebUiSurfaces.AiAdvancedSyntaxHintZh);    // 可选档默认关闭
        Assert.Contains("设置", WebUiSurfaces.AiAdvancedSyntaxHintZh);      // 指向设置页
        Assert.Contains("advanced syntax", WebUiSurfaces.AiAdvancedSyntaxHintEn);
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 表与 WebConstraints 同源：L3 开关是唯一驱动维度（L1 恒真无关）
    public void Surfaces_follow_only_the_l3_switch()
    {
        AgentTierAvailability web = WebConstraints.Availability(isWeb: true);
        AgentTierAvailability desktop = WebConstraints.Availability(isWeb: false);

        Assert.Equal(web.L3CloudByok, WebUiSurfaces.Surfaces(web).ByokFormVisible);
        Assert.Equal(desktop.L3CloudByok, WebUiSurfaces.Surfaces(desktop).ByokFormVisible);
        Assert.Equal(web.L3CloudByok, WebUiSurfaces.Surfaces(web).AiAdvancedSyntaxHintVisible);
    }

    // ---- illegal ---------------------------------------------------------

    [Fact] // illegal — null 档位行拒绝（能力表驱动：无表即无表面决策）
    public void Null_tier_row_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => WebUiSurfaces.Surfaces(null!));
        Assert.Throws<ArgumentNullException>(
            () => WebUiSurfaces.AnnotateDesktopOnly(null!, entryDesktopOnly: true));
    }
}
