using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent;

// [F-062][F-063][F-071~F-075] T-070 验收矩阵：19 动作 × 权限档/计价/解锁/
// 边界 全表——单一真相表驱动（单源化后的 ActionCatalog 是唯一行源，本矩阵
// 把冻结期望逐行钉在源上：改错任何一格立即红）。权限侧经 ApprovalGate
// （F.4 allow/ask/deny 三级+危险位+能力锁），计价侧经派生关税视图。

/// <summary>[F-063][F-071~F-075] The 19-action metadata/permission/boundary matrix, driven from the single source.</summary>
public class ActionGovernanceMatrixTests
{
    // ------------------------------------------------ 19×6 元数据矩阵（唯一行源逐格钉死）

    [Theory] // legal — 每行=冻结值（组/权限档/计价/解锁时代/Boss 加倍/危险位）
    [InlineData(SystemActionKind.LookAround, ActionGroup.Recon, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.InspectTile, ActionGroup.Recon, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.QueryWorld, ActionGroup.Recon, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.BuildPlan, ActionGroup.Construction, ActionPermissionTier.Ask, 2, 2, false, false)]
    [InlineData(SystemActionKind.UpgradeTower, ActionGroup.Construction, ActionPermissionTier.Ask, 2, 2, false, false)]
    [InlineData(SystemActionKind.SellTower, ActionGroup.Construction, ActionPermissionTier.Ask, 2, 2, false, true)]
    [InlineData(SystemActionKind.SetRally, ActionGroup.Construction, ActionPermissionTier.Ask, 2, 2, false, false)]
    [InlineData(SystemActionKind.RerouteLogistics, ActionGroup.Logistics, ActionPermissionTier.Ask, 1, 2, false, false)]
    [InlineData(SystemActionKind.SetStockpile, ActionGroup.Logistics, ActionPermissionTier.Ask, 1, 2, false, false)]
    [InlineData(SystemActionKind.FocusFire, ActionGroup.Combat, ActionPermissionTier.Ask, 1, 2, true, false)]
    [InlineData(SystemActionKind.SetTowerMode, ActionGroup.Combat, ActionPermissionTier.Ask, 1, 2, true, false)]
    [InlineData(SystemActionKind.TriggerScroll, ActionGroup.Combat, ActionPermissionTier.Ask, 1, 2, true, false)]
    [InlineData(SystemActionKind.HeroOrder, ActionGroup.Combat, ActionPermissionTier.Ask, 1, 2, true, false)]
    [InlineData(SystemActionKind.AskPlayer, ActionGroup.Management, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.ReportToPlayer, ActionGroup.Management, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.TodoBoard, ActionGroup.Management, ActionPermissionTier.Allow, 0, 2, false, false)]
    [InlineData(SystemActionKind.SpawnScout, ActionGroup.Management, ActionPermissionTier.Allow, 1, 2, false, false)]
    [InlineData(SystemActionKind.SetAlarm, ActionGroup.Management, ActionPermissionTier.Allow, 0, 1, false, false)]
    [InlineData(SystemActionKind.StopTask, ActionGroup.Management, ActionPermissionTier.Allow, 0, 2, false, false)]
    public void Every_verb_pins_its_frozen_metadata_row(
        SystemActionKind verb,
        ActionGroup group,
        ActionPermissionTier tier,
        int pointCost,
        int unlockEra,
        bool doubledOnBossWave,
        bool dangerBit)
    {
        ActionDescriptor source = ActionCatalog.Descriptor(verb);
        Assert.Equal(group, source.Group);
        Assert.Equal(tier, source.Tier);
        Assert.Equal(pointCost, source.PointCost);
        Assert.Equal(unlockEra, source.UnlockEra);
        Assert.Equal(doubledOnBossWave, source.DoubledOnBossWave);
        Assert.Equal(dangerBit, source.RequiresExplicitConfirmation);

        // 派生视图同格一致（C-14 关税视图=源投影，T-070 单源化）
        SystemActionTariff view = SystemActionTariffTable.Get(verb);
        Assert.Equal(pointCost, view.PointCost);
        Assert.Equal(tier, view.DefaultTier);
        Assert.Equal(unlockEra, view.UnlockEra);
        Assert.Equal(doubledOnBossWave, view.DoubledOnBossWave);
        Assert.Equal(dangerBit, view.NeverAutoApprove);
    }

    // ------------------------------------------------ 权限矩阵（F.4 三级引擎逐档）

    [Theory] // legal — 已解锁时代：Allow 档直放、Ask 档弹卡（合法载荷、无危险位触发）
    [InlineData(SystemActionKind.LookAround, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.InspectTile, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.QueryWorld, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.BuildPlan, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.UpgradeTower, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.SellTower, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.SetRally, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.RerouteLogistics, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.SetStockpile, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.FocusFire, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.SetTowerMode, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.TriggerScroll, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.HeroOrder, ApprovalDecisionKind.Ask)]
    [InlineData(SystemActionKind.AskPlayer, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.ReportToPlayer, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.TodoBoard, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.SpawnScout, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.SetAlarm, ApprovalDecisionKind.Allow)]
    [InlineData(SystemActionKind.StopTask, ApprovalDecisionKind.Allow)]
    public void Unlocked_verbs_get_their_tier_verdicts(SystemActionKind verb, ApprovalDecisionKind expected)
    {
        var gate = new ApprovalGate(EraOrdinals.E2);
        ApprovalDecision decision = gate.DecideStep(verb, ActionJsonSamples.All()[verb.ToString()]());
        Assert.Equal(expected, decision.Kind);
    }

    [Theory] // illegal — 能力锁：E1 调 E2 动作 → Deny capability_locked（G.2 解锁时代列）
    [InlineData(SystemActionKind.BuildPlan)]
    [InlineData(SystemActionKind.UpgradeTower)]
    [InlineData(SystemActionKind.SellTower)]
    [InlineData(SystemActionKind.SetRally)]
    [InlineData(SystemActionKind.RerouteLogistics)]
    [InlineData(SystemActionKind.SetStockpile)]
    [InlineData(SystemActionKind.FocusFire)]
    [InlineData(SystemActionKind.SetTowerMode)]
    [InlineData(SystemActionKind.TriggerScroll)]
    [InlineData(SystemActionKind.HeroOrder)]
    [InlineData(SystemActionKind.TodoBoard)]
    [InlineData(SystemActionKind.SpawnScout)]
    [InlineData(SystemActionKind.StopTask)]
    public void Era_locked_verbs_deny_at_e1(SystemActionKind verb)
    {
        var gate = new ApprovalGate(EraOrdinals.E1);
        ApprovalDecision decision = gate.DecideStep(verb, ActionJsonSamples.All()[verb.ToString()]());
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.NotNull(decision.Reason);
        Assert.Contains("capability_locked", decision.Reason, StringComparison.Ordinal);
    }

    [Theory] // legal — E1 开放集恰为侦察三件+管理三件（G.2 解锁时代列的补集验证）
    [InlineData(SystemActionKind.LookAround)]
    [InlineData(SystemActionKind.InspectTile)]
    [InlineData(SystemActionKind.QueryWorld)]
    [InlineData(SystemActionKind.AskPlayer)]
    [InlineData(SystemActionKind.ReportToPlayer)]
    [InlineData(SystemActionKind.SetAlarm)]
    public void E1_available_verbs_pass_the_gate(SystemActionKind verb)
    {
        var gate = new ApprovalGate(EraOrdinals.E1);
        ApprovalDecision decision = gate.DecideStep(verb, ActionJsonSamples.All()[verb.ToString()]());
        Assert.NotEqual(ApprovalDecisionKind.Deny, decision.Kind);
    }

    // ------------------------------------------------ Ask 档关卡级自动允许 + 危险位例外

    [Fact] // legal — 自动允许白名单放行 Ask 档（关卡级，v3.2 §11.4）
    public void Ask_verbs_auto_approve_under_the_level_whitelist()
    {
        var gate = new ApprovalGate(EraOrdinals.E2);
        foreach (SystemActionKind verb in new[]
        {
            SystemActionKind.BuildPlan, SystemActionKind.RerouteLogistics,
            SystemActionKind.FocusFire, SystemActionKind.TriggerScroll, SystemActionKind.HeroOrder,
        })
        {
            gate.SetLevelAutoApprove(verb, enabled: true);
            Assert.Equal(
                ApprovalDecisionKind.Allow,
                gate.DecideStep(verb, ActionJsonSamples.All()[verb.ToString()]()).Kind);
        }
    }

    [Fact] // illegal — 危险位永不自动允许：SellTower 白名单抛、confirm_overwrite 强制 Ask
    public void Danger_bits_never_auto_approve()
    {
        var gate = new ApprovalGate(EraOrdinals.E2);
        Assert.Throws<ArgumentException>(() => gate.SetLevelAutoApprove(SystemActionKind.SellTower, enabled: true));

        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, enabled: true);
        string overwrite = ActionJsonSamples.BuildPlan(confirmOverwrite: true);
        Assert.Equal(ApprovalDecisionKind.Ask, gate.DecideStep(SystemActionKind.BuildPlan, overwrite).Kind);
    }

    // ------------------------------------------------ 边界（schema 侧代表格；SetAlarm 钳制语义见 ActionSchemaClamperTests）

    [Fact] // boundary — 19 动作合法样例全部通过唯一门（单源表驱动的边界全集）
    public void All_legal_samples_pass_the_registry_schema_gate()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2);
        foreach (Func<string> sample in ActionJsonSamples.All().Values)
        {
            ActionOutcome outcome = registry.Invoke(sample());
            Assert.NotEqual(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        }
    }

    [Fact] // illegal — 清单外字段逐 verb 拒绝（封闭 schema，RL6）
    public void Surprise_fields_are_denied_for_every_verb()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2);
        foreach (string verb in ActionJsonSamples.All().Keys)
        {
            ActionOutcome outcome = registry.Invoke(ActionJsonSamples.WithSurpriseField(verb));
            Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        }
    }
}
