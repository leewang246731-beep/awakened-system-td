using EraCore.Agent.Autopilot;
using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Agent.Pipeline;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [C-13][F-093][F-060] E5 全托管可选档战役通道闸门（T-134, v3.2 §4 L62 系统形态
// 列「E5 未来=全托管可选（设计既定）」）：主线战役域 E1~E4 拒绝 Engage、E5 起
// 可选开启；无尽三榜（SP-7 手动/系统辅助/全托管，任选时代）不受闸门约束（原始
// EngageFullAutopilot() 保持 T-081 泳道入口）。三面同源：本闸门+era5/era.json
// system_stage_features full_autopilot_optional+era5/levels.json slot 3 unlocks
// full_autopilot（Era5ContentTests/Era5HandcraftedLevelSetTests 侧断言）。
// 夹具=真实管线夹具（PipelineHarness：E1 关卡+E2 智能体栈，CS-3.4 禁 mock 被测系统）。
public class AutopilotEraGateTests
{
    private static (PipelineHarness Harness, AutopilotDirector Director) NewDirector()
    {
        var harness = new PipelineHarness();
        var director = AutopilotDirector.OverSimulation(
            harness.Pipeline,
            harness.Simulation,
            PipelineHarness.Config(),
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = PipelineHarness.EastGateAnchor,
                ["south_gate"] = PipelineHarness.SouthGateAnchor,
            });
        return (harness, director);
    }

    [Fact] // legal — 系统形态阶梯：E1~E4 战役不可 Engage、E5/终章可选（谓词面）
    public void The_gate_opens_exactly_at_E5_per_the_system_stage_ladder()
    {
        // v3.2 §4 系统形态列：E1 绑定期/E2 执行期/E3 自主期/E4 多核期/E5 全托管可选。
        Assert.Equal(EraOrdinals.E5, AutopilotEraGate.FullAutopilotUnlockEra);
        Assert.False(AutopilotEraGate.CampaignMayEngageFullAutopilot(1));
        Assert.False(AutopilotEraGate.CampaignMayEngageFullAutopilot(4)); // E4 多核期=巡逻子代理，非全权
        Assert.True(AutopilotEraGate.CampaignMayEngageFullAutopilot(5));  // E5=解锁位
        Assert.True(AutopilotEraGate.CampaignMayEngageFullAutopilot(6));  // 终章保持解锁态（T-135 域承接）
    }

    [Theory] // legal — 战役泳道 Engage：E1~E4 拒绝且模式不变，E5 起进入全托管
    //（T-135 补 E6 行：全托管可选档按『E5 开、终章同开』延续——系统形态阶梯
    //为授权递进列（E1 绑定→E5 全托管→终章真相揭示），真相揭示=叙事形态升级
    //不收回全托管授权；闸门谓词 >=E5 自然覆盖 E6，Engage 泳道面同钉）
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void The_campaign_lane_engage_respects_the_era_gate(int era, bool shouldEngage)
    {
        (PipelineHarness harness, AutopilotDirector director) = NewDirector();
        using (harness)
        {
            Assert.Equal(AutopilotMode.Off, director.Mode);

            if (shouldEngage)
            {
                director.EngageFullAutopilot(era);
                Assert.Equal(AutopilotMode.FullAutopilot, director.Mode); // E5：可选开启
            }
            else
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => director.EngageFullAutopilot(era));
                Assert.Contains($"E{era}", ex.Message, StringComparison.Ordinal); // 消息点名当前战役时代
                Assert.Contains("全托管", ex.Message, StringComparison.Ordinal);
                Assert.Equal(AutopilotMode.Off, director.Mode); // 拒绝后模式不变
            }
        }
    }

    [Fact] // illegal — 时代序号域守卫：0/越界档拒收
    public void Out_of_range_era_ordinals_reject()
    {
        (PipelineHarness harness, AutopilotDirector director) = NewDirector();
        using (harness)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => director.EngageFullAutopilot(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => director.EngageFullAutopilot(7));
        }
    }

    [Fact] // legal — 无尽泳道不受闸门：原始 EngageFullAutopilot() 任意时代可用（SP-7 三榜）
    public void The_endless_lane_keeps_the_raw_engage_open()
    {
        (PipelineHarness harness, AutopilotDirector director) = NewDirector();
        using (harness)
        {
            director.EngageFullAutopilot(); // T-081 泳道：无时代参数（无尽三榜/宿主自裁）
            Assert.Equal(AutopilotMode.FullAutopilot, director.Mode);
        }
    }

    [Fact] // legal — 拒绝后无残留授权（E4 战役在 E5 前不可托管）
    public void A_refused_engage_leaves_no_residue()
    {
        (PipelineHarness harness, AutopilotDirector director) = NewDirector();
        using (harness)
        {
            Assert.Throws<InvalidOperationException>(() => director.EngageFullAutopilot(4));
            director.Disengage();
            Assert.Equal(AutopilotMode.Off, director.Mode);
        }
    }
}
