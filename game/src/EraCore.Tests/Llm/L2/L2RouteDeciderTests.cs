using EraCore.Llm.L2;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] [F-163] T-137 三档路由状态机：降级矩阵全表（L1 恒通/L2 六相位/
// L3 Web 禁）——08 T-137 行验收词「失败降 L1」的回归判据（单元级）。
public class L2RouteDeciderTests
{
    private static readonly AgentTierAvailability Desktop = WebConstraints.Availability(isWeb: false);
    private static readonly AgentTierAvailability Web = WebConstraints.Availability(isWeb: true);

    private static L2RuntimeSnapshot Snapshot(L2RuntimePhase phase) =>
        new(phase, ModelPresent: phase != L2RuntimePhase.ModelMissing,
            RuntimePresent: phase != L2RuntimePhase.RuntimeMissing);

    // ------------------------------------------------ L1（地板档：永不降级）

    [Theory] // [F-078] L1 选中 → 任何运行时/平台状态都直通 L1（地板档无降级路径）
    [InlineData(L2RuntimePhase.ModelMissing)]
    [InlineData(L2RuntimePhase.RuntimeMissing)]
    [InlineData(L2RuntimePhase.Downloading)]
    [InlineData(L2RuntimePhase.Starting)]
    [InlineData(L2RuntimePhase.Idle)]
    [InlineData(L2RuntimePhase.Ready)]
    [InlineData(L2RuntimePhase.ProcessDead)]
    public void L1_selection_is_always_direct_regardless_of_runtime_or_platform(L2RuntimePhase phase)
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L1Grammar, Snapshot(phase), Desktop);

        Assert.Equal(LlmTierKind.L1Grammar, decision.EffectiveTier);
        Assert.False(decision.Downgraded);
        Assert.Equal(L2DegradationReason.None, decision.Reason);

        var onWeb = L2RouteDecider.Decide(LlmTierKind.L1Grammar, Snapshot(phase), Web);
        Assert.Equal(LlmTierKind.L1Grammar, onWeb.EffectiveTier);
        Assert.False(onWeb.Downgraded);
    }

    // ------------------------------------------------ L2（降级矩阵主面）

    [Fact] // [F-078] L2+Ready → 直通 L2（唯一不降级的 L2 相位）
    public void L2_ready_routes_direct()
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L2Local, Snapshot(L2RuntimePhase.Ready), Desktop);

        Assert.Equal(LlmTierKind.L2Local, decision.EffectiveTier);
        Assert.False(decision.Downgraded);
        Assert.Equal(L2DegradationReason.None, decision.Reason);
    }

    [Theory] // [F-078] L2 失败相位全表：模型缺/运行时缺/下载中/启动中/进程死 → 降 L1，理由逐相位可辨
    [InlineData(L2RuntimePhase.ModelMissing, L2DegradationReason.ModelMissing)]
    [InlineData(L2RuntimePhase.RuntimeMissing, L2DegradationReason.RuntimeMissing)]
    [InlineData(L2RuntimePhase.Downloading, L2DegradationReason.NotReady)]
    [InlineData(L2RuntimePhase.Starting, L2DegradationReason.NotReady)]
    [InlineData(L2RuntimePhase.Idle, L2DegradationReason.NotReady)]
    [InlineData(L2RuntimePhase.ProcessDead, L2DegradationReason.ProcessDead)]
    public void L2_failure_phases_all_downgrade_to_L1_with_distinct_reasons(
        L2RuntimePhase phase, L2DegradationReason expectedReason)
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L2Local, Snapshot(phase), Desktop);

        Assert.Equal(LlmTierKind.L1Grammar, decision.EffectiveTier);
        Assert.True(decision.Downgraded);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Fact] // [F-078] [F-164] L2+Web 平台行 → 降 L1/WebTier（浏览器沙箱——即使运行时虚报 Ready）
    public void L2_on_web_platform_downgrades_with_web_reason_even_if_snapshot_claims_ready()
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L2Local, Snapshot(L2RuntimePhase.Ready), Web);

        Assert.Equal(LlmTierKind.L1Grammar, decision.EffectiveTier);
        Assert.True(decision.Downgraded);
        Assert.Equal(L2DegradationReason.WebTier, decision.Reason);
    }

    // ------------------------------------------------ L3

    [Fact] // [F-078] L3+桌面 → 直通 L3（其自身失败留在网关错误路径，不经本矩阵）
    public void L3_on_desktop_routes_direct()
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L3Cloud, Snapshot(L2RuntimePhase.ModelMissing), Desktop);

        Assert.Equal(LlmTierKind.L3Cloud, decision.EffectiveTier);
        Assert.False(decision.Downgraded);
    }

    [Fact] // [F-078] [F-164] L3+Web → 降 L1/WebTier（Web 零网络约束同罩 L3，T-106 口径）
    public void L3_on_web_platform_downgrades_to_L1()
    {
        var decision = L2RouteDecider.Decide(LlmTierKind.L3Cloud, Snapshot(L2RuntimePhase.Ready), Web);

        Assert.Equal(LlmTierKind.L1Grammar, decision.EffectiveTier);
        Assert.True(decision.Downgraded);
        Assert.Equal(L2DegradationReason.WebTier, decision.Reason);
    }

    // ------------------------------------------------ 契约


    [Fact] // 快照 CanServe 与矩阵一致：仅 Ready 相位可服务
    public void Snapshot_can_serve_matches_the_ready_phase_only()
    {
        foreach (L2RuntimePhase phase in Enum.GetValues<L2RuntimePhase>())
        {
            bool canServe = L2RouteDecider.Decide(LlmTierKind.L2Local, Snapshot(phase), Desktop)
                .EffectiveTier == LlmTierKind.L2Local;
            Assert.Equal(phase == L2RuntimePhase.Ready, canServe);
        }
    }

    [Fact] // null 输入防御
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => L2RouteDecider.Decide(LlmTierKind.L2Local, null!, Desktop));
        Assert.Throws<ArgumentNullException>(
            () => L2RouteDecider.Decide(LlmTierKind.L2Local, Snapshot(L2RuntimePhase.Ready), null!));
    }
}
