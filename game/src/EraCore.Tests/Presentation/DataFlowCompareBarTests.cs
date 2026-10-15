using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [F-153] [C-26] T-109 「你的话发给谁」三档对比条（DataFlowCompareBar）：
// 桌面三态（L1 恒零发送/L2 本地推理/L3 随启用与 base_url）、Web 平台态
// （L2/L3 平台禁用文案）、空白 base_url 空位、行序稳定、能力表同源
// （WebConstraints 唯一来源）。纯文案+状态——无 I/O。
public class DataFlowCompareBarTests
{
    private static readonly AgentTierAvailability Desktop =
        WebConstraints.Availability(isWeb: false);

    private static readonly AgentTierAvailability Web =
        WebConstraints.Availability(isWeb: true);

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 桌面+L3 启用：三行齐备，L3 含当前 base_url（唯一去向）
    public void Desktop_enabled_shows_three_rows_with_the_configured_destination()
    {
        var rows = DataFlowCompareBar.Resolve(
            Desktop, l3Enabled: true, "https://open.bigmodel.cn/api/paas/v4");

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            [DataFlowTier.L1Grammar, DataFlowTier.L2LocalModel, DataFlowTier.L3CloudByok],
            rows.Select(r => r.Tier));

        // 三档数据流各表其态：L1 不发送/L2 本地推理/L3 发送到你配置的服务
        Assert.Contains("不发送任何数据", rows[0].Zh);
        Assert.Contains("本地推理", rows[1].Zh);
        Assert.Contains("你配置的服务", rows[2].Zh);
        Assert.Contains("https://open.bigmodel.cn/api/paas/v4", rows[2].Zh);

        // 英文行同态（中英不漂移）
        Assert.Contains("no data is sent anywhere", rows[0].En);
        Assert.Contains("never uploaded", rows[1].En);
        Assert.Contains("the service you configured", rows[2].En);
    }

    [Fact] // legal — 桌面+L3 关闭（默认态）：L3 行切「不发送任何数据」，
    // 与启用行成清晰三态对比（开=发送到配置服务 / 关=零网络）
    public void Desktop_disabled_l3_row_states_zero_network()
    {
        var rows = DataFlowCompareBar.Resolve(Desktop, l3Enabled: false, "https://relay.example/v1");

        Assert.Contains("不发送任何数据", rows[2].Zh);
        Assert.DoesNotContain("https://relay.example/v1", rows[2].Zh); // 关闭态不显示去向
        Assert.Contains("no data is sent", rows[2].En);
    }

    [Fact] // legal — L1 行恒定：与平台/启用状态无关（离线语法全平台恒开），
    // 两态解析下逐字相等（对比条的稳定锚）
    public void L1_row_is_constant_across_every_state()
    {
        var desktopOn = DataFlowCompareBar.Resolve(Desktop, l3Enabled: true, "https://a.test/v1");
        var desktopOff = DataFlowCompareBar.Resolve(Desktop, l3Enabled: false, string.Empty);
        var web = DataFlowCompareBar.Resolve(Web, l3Enabled: true, "https://a.test/v1");

        Assert.Equal(desktopOn[0], desktopOff[0]);
        Assert.Equal(desktopOn[0], web[0]);
        Assert.Contains("离线", desktopOn[0].Zh);
    }

    // ------------------------------------------------------------ boundary

    [Fact] // boundary — Web 平台态：L2/L3 走平台禁用文案（与 WebConstraints
    // 能力表同源——浏览器沙箱/零网络承诺），L3 不显示任何去向
    public void Web_platform_disables_l2_and_l3_rows_with_platform_copy()
    {
        var rows = DataFlowCompareBar.Resolve(Web, l3Enabled: true, "https://a.test/v1");

        Assert.Contains("Web 版不可用", rows[1].Zh);
        Assert.Contains("Web 版禁用", rows[2].Zh);
        Assert.Contains("不发送任何数据", rows[2].Zh);
        Assert.DoesNotContain("https://a.test/v1", rows[2].Zh); // 平台禁用 ⇒ 无去向可显
        Assert.Contains("browser sandbox", rows[1].En);
        Assert.Contains("zero-network", rows[2].En);
    }

    [Theory] // boundary — 启用态下 base_url 空白三态 → 共享空位插入（无假去向）
    [InlineData("")]
    [InlineData("   ")]
    public void Enabled_with_blank_base_url_uses_the_blank_insert(string baseUrl)
    {
        var rows = DataFlowCompareBar.Resolve(Desktop, l3Enabled: true, baseUrl);

        Assert.Contains(DataFlowCompareBar.BlankBaseUrlZh, rows[2].Zh);
    }

    // ------------------------------------------------------------ illegal

    [Fact] // illegal — null 能力表拒绝（能力表必经 WebConstraints，禁手工拼行）
    public void Null_tier_row_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => DataFlowCompareBar.Resolve(null!, l3Enabled: true, "https://a.test/v1"));
    }
}
