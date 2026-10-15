using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

/// <summary>
/// [F-164] Web 版约束表（T-106，设计 v3.2 §17 条 15）——平台事实 → 三档
/// 可用性的唯一决策点：Web=L1 恒开 + L2/L3 禁；桌面=三档全开。设置页
/// 入口可见性、LLM 网关平台门、冒烟断言全部从这张表取值（能力表驱动，
/// 各消费点不得自行分支）。
/// </summary>
// [F-164][F-144] C-26/C-31 T-106：Web 版约束（L2/L3 自动禁用仅 L1）
public class WebConstraintsTests
{
    [Fact]
    public void Web_platform_keeps_only_the_L1_grammar()
    {
        // legal: Web 行——L1 军令语法（纯离线解析）恒可用，L2（GDExtension
        // 原生推理）与 L3（BYOK 云直连）双禁
        var web = WebConstraints.Availability(isWeb: true);

        Assert.True(web.L1Grammar);
        Assert.False(web.L2LocalModel);
        Assert.False(web.L3CloudByok);
    }

    [Fact]
    public void Desktop_platform_keeps_all_three_tiers()
    {
        // legal: 桌面行——L2 待其后续任务落地，L3 即 BYOK 网关，全部开放
        var desktop = WebConstraints.Availability(isWeb: false);

        Assert.True(desktop.L1Grammar);
        Assert.True(desktop.L2LocalModel);
        Assert.True(desktop.L3CloudByok);
    }

    [Fact]
    public void Availability_is_stable_under_repeated_resolution()
    {
        // boundary: 纯函数——同一事实两次解析结果相等（无隐藏状态）
        Assert.Equal(
            WebConstraints.Availability(isWeb: true),
            WebConstraints.Availability(isWeb: true));
        Assert.Equal(
            WebConstraints.Availability(isWeb: false),
            WebConstraints.Availability(isWeb: false));
    }

    [Fact]
    public void L1_is_the_only_tier_available_on_every_platform()
    {
        // boundary: L1 是全平台交集（回落的语法底线——Web 下军令仍可用）
        foreach (bool isWeb in new[] { false, true })
        {
            Assert.True(WebConstraints.Availability(isWeb).L1Grammar);
        }
    }
}
