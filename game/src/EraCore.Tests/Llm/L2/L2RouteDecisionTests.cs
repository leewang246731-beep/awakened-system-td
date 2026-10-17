using EraCore.Llm.L2;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] T-137 路由决策值语义（L2RouteDecision/枚举族）：降级工厂唯一落
// 地=L1、Direct 工厂不带降级标记、快照 CanServe 仅 Ready——决策值的结构
// 保证面（矩阵行为在 L2RouteDeciderTests）。
public class L2RouteDecisionTests
{
    [Fact] // [F-078] 降级落地恒为 L1：DegradeToL1 工厂唯一出口（验收词「失败降 L1」的结构保证）
    public void Degrade_factory_always_lands_on_L1()
    {
        foreach (L2DegradationReason reason in Enum.GetValues<L2DegradationReason>()
            .Where(r => r != L2DegradationReason.None))
        {
            L2RouteDecision verdict = L2RouteDecision.DegradeToL1(reason);
            Assert.Equal(LlmTierKind.L1Grammar, verdict.EffectiveTier);
            Assert.True(verdict.Downgraded);
            Assert.Equal(reason, verdict.Reason);
        }
    }

    [Fact] // Direct 工厂：三档直通值均不带降级标记、理由 None
    public void Direct_factory_carries_no_downgrade_for_every_tier()
    {
        foreach (LlmTierKind tier in Enum.GetValues<LlmTierKind>())
        {
            L2RouteDecision verdict = L2RouteDecision.Direct(tier);
            Assert.Equal(tier, verdict.EffectiveTier);
            Assert.False(verdict.Downgraded);
            Assert.Equal(L2DegradationReason.None, verdict.Reason);
        }
    }

    [Fact] // 快照 CanServe：仅 Ready 相位可服务（其余相位全部 false）
    public void Snapshot_can_serve_is_true_only_in_ready()
    {
        foreach (L2RuntimePhase phase in Enum.GetValues<L2RuntimePhase>())
        {
            var snapshot = new L2RuntimeSnapshot(
                phase, ModelPresent: true, RuntimePresent: true);
            Assert.Equal(phase == L2RuntimePhase.Ready, snapshot.CanServe);
        }
    }

    [Fact] // 工厂快照：NothingInstalled=ModelMissing+双缺（首装态）
    public void Nothing_installed_snapshot_defaults_to_model_missing()
    {
        L2RuntimeSnapshot snapshot = L2RuntimeSnapshot.NothingInstalled();

        Assert.Equal(L2RuntimePhase.ModelMissing, snapshot.Phase);
        Assert.False(snapshot.ModelPresent);
        Assert.False(snapshot.RuntimePresent);
        Assert.Equal(L2Facts.DefaultPort, snapshot.Port);
    }
}
