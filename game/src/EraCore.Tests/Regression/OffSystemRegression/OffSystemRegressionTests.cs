using EraCore.CampaignLoop;
using EraCore.Presentation;
using EraCore.Progression;
using EraCore.Tests.Levels;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Regression.OffSystemRegression;

// [C-30][F-097] T-084 关闭系统 E1 自动通关回归（R2 硬标准首证，v3.2 §2
// 支柱 4 / §19-R2：关闭系统可全通关、可全成就、警报功能不缺失）。装置=
// OffSystemRegressionFixture 的三局完整战役（关闭局/关闭局复跑/开启对照）。
// 断言面：①四关 headless 全通关（T-085 命令流，纯玩家建造）；②全程军令
// 管线零调用（K-8 审计计数=0）+系统点账本零消耗；③警报事件照发（真实
// 读数流>0）且双开关位照发等价、关闭态载荷为中性文案；④E1 全成就清单
// 枚举扫描无一依赖系统开启（R2 裁定：无"与系统对话"成就）+逐条授予见证；
//⑤对照组（军师在场不用）终态与关闭局逐字段等价；⑥关闭局双跑哈希全等。
[Collection(OffSystemRegressionCollection.Name)]
public class OffSystemRegressionTests : IDisposable
{
    private readonly OffSystemRegressionFixture _fixture;

    public OffSystemRegressionTests(OffSystemRegressionFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 装置为类级共享（ICollectionFixture），此处无逐用例资源。
    }

    // ---- ① 关闭系统通关 E1 全关（通关半边，R2 首证） -----------------------

    [Fact] // legal — 关闭局四关全部 Victory：人格面全隐/军令通道关闭/HUD 中性
    public void Off_system_run_clears_all_four_E1_levels_headless()
    {
        OffSystemRunReport run = _fixture.OffRun;

        Assert.False(run.SystemEnabled);
        Assert.Equal(PanelState.Hidden, run.PanelEndState); // 开关位=关（人格表面全隐）
        Assert.False(run.OrderInputVisible); // 军令输入面隐藏（军令通道关闭）
        Assert.False(run.TrustBadgeVisible); // 信任徽记隐藏
        Assert.True(run.HudNeutral); // HUD 中性横幅皮
        Assert.Equal(AlarmChannel.NeutralBanner, run.AlarmChannel);

        HandcraftedLevelSet set = HandcraftedLevelData.Load("e1.json");
        Assert.Equal(4, run.Levels.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, run.Levels.Select(level => level.Slot).ToArray()); // 按槽序通关
        Assert.All(run.Levels, level => Assert.Equal(GameFlowState.Victory, level.EndState));
        Assert.All(run.Levels, level =>
            Assert.Equal(
                set.TryGet(level.LevelId)!.WinWaves, level.WinWaves)); // 胜波=关卡行 win_waves（§7 双面）
        Assert.All(run.Levels, level => Assert.Equal(
            set.TryGet(level.LevelId)!.CommandScript.Count(step => step.Verb == "build-tower"),
            level.TowerCount)); // 终局塔数=脚本建造数（零出售）
        Assert.Equal(
            set.Items.Sum(level => level.CommandScript.Count), run.IssuedCommands); // 命令流全量签发
        Assert.Equal(run.IssuedCommands, run.AppliedCommands); // 零拒收（经济面全合法）

        // 战役完成半边：占领序=关序、四关全占（主城+4=5 格）。
        Assert.Equal(set.Items.Select(level => level.Id).ToArray(), run.CapturedOrder.ToArray());
        Assert.Equal(5, run.OccupiedCells);
    }

    // ---- ② 零军令/零系统动作/零系统点（审计计数） ---------------------------

    [Fact] // legal — 军令管线零调用：K-8 审计计数=0、系统点账本零消耗
    public void Off_system_run_audits_zero_agent_activity()
    {
        OffSystemRunReport run = _fixture.OffRun;

        Assert.Equal(0, run.JournalInputEvents); // 军令零条（AgentPipeline 零调用）
        Assert.Equal(0, run.JournalExecutionEvents); // 系统动作零次（C4 审计面=0）
        Assert.Equal(0, run.JournalTotalEvents); // 关闭局无管线：连回充噪声都不存在
        Assert.True(run.LedgerPointsAlwaysZero); // 系统点账本零消耗（全程余额恒 0）
        Assert.True(run.LedgerObservations >= 4 * 60, "观察须覆盖四关全程（每关 ≥60 次采样）");
    }

    // ---- ③ 警报功能不缺失（事件照发+中性载荷） ------------------------------

    [Fact] // legal+boundary — 警报事件照发计数>0；40% 边界之下双态同判且关闭态载荷中性
    public void Alarm_channel_stays_live_and_neutral_when_system_off()
    {
        OffSystemRunReport run = _fixture.OffRun;

        // 事件照发（真实读数流：四关全程按 20s 采样持续评估，通道未被关闭静默）。
        Assert.True(run.AlarmEvaluations > 0);
        Assert.Equal(0, run.AlarmParityViolations); // 每一读数双开关位照发等价（无一被静默）
        // 天然局 fired=0 与完整度恒 1.0 是当前引擎事实（E1 无敌方→建筑伤害路径），
        // 如实钉住：危机路径的照发证据由下面的降级演练补齐（同一决策核、同一边界）。
        Assert.Equal(0, run.NaturalAlarmFired);
        Assert.Equal(1.0, run.DefenseIntegrityMin, 9);

        // 降级演练：对完整度 100%→0% 按 5% 步进扫过决策核（T-075 单测矩阵的
        // E2E 面复证——关闭态载荷必须始终是中性事实句，非军师口吻）。
        for (int percent = 100; percent >= 0; percent -= 5)
        {
            double integrity = percent / 100.0;
            DefenseAlarmView offView = OffModeDowngrade.DefenseAlarmAt(false, integrity);
            DefenseAlarmView onView = OffModeDowngrade.DefenseAlarmAt(true, integrity);

            Assert.Equal(onView.Fires, offView.Fires); // 警报不缺失：两态同刻同判
            if (percent >= 40)
            {
                Assert.False(offView.Fires); // boundary — 恰 40%（及以上）不发
                Assert.Equal(string.Empty, offView.Text);
            }
            else
            {
                Assert.True(offView.Fires); // boundary — 35% 及以下照发（中性通道）
                Assert.StartsWith("防线完整度 ", offView.Text);
                Assert.EndsWith("%", offView.Text);
                Assert.Matches("^防线完整度 [0-9]+%$", offView.Text); // 中性事实句（数字不变式）
                Assert.DoesNotContain("建议", offView.Text); // 非军师口吻（中性措辞）
                Assert.DoesNotContain("！", offView.Text);
                Assert.NotEqual(OffModeDowngrade.PersonaAlarmLine, offView.Text);
                Assert.Equal(OffModeDowngrade.PersonaAlarmLine, onView.Text); // 开启侧=人格台词（对照锚）
            }
        }
    }

    // ---- ④ 全成就可得（枚举扫描 + 局内授予见证） ----------------------------

    [Fact] // legal+illegal 面扫描 — 成就度量词表无一"与系统对话"类条目（R2 裁定）
    public void E1_achievement_catalog_has_no_system_dependent_entry()
    {
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));

        // illegal 面扫描（负空间断言）：16 个判分度量全部是玩家行为事实
        // （建造/击杀/占领/无损/节奏），词表不存在对话/人格/信任类成员。
        string[] names = Enum.GetNames<ObjectiveMetric>();
        Assert.Equal(16, names.Length);
        Assert.DoesNotContain(names, name =>
            name.Contains("Dialog", StringComparison.Ordinal)
            || name.Contains("Chat", StringComparison.Ordinal)
            || name.Contains("Order", StringComparison.Ordinal)
            || name.Contains("Persona", StringComparison.Ordinal)
            || name.Contains("Trust", StringComparison.Ordinal));

        // 引用系统用量的两条（C4 不使用系统建造/C5 不使用卷轴）必须是节制型
        // at_most —— 关闭局平凡满足、开启局不用即满足：两态同可得，不锁任何一边。
        foreach (ObjectiveEntry entry in pools.Strategy.Concat(pools.Challenge))
        {
            if (entry.Metric is ObjectiveMetric.SystemBuildPlanCount or ObjectiveMetric.ScrollTriggerCount)
            {
                Assert.Equal(ObjectiveComparison.AtMost, entry.Comparison);
            }
        }

        // E1 关行声明的目标实例全部落在两池内（关卡内容面无野生成就条目）。
        HandcraftedLevelSet set = HandcraftedLevelData.Load("e1.json");
        var strategyIds = pools.Strategy.Select(entry => entry.Id).ToHashSet();
        var challengeIds = pools.Challenge.Select(entry => entry.Id).ToHashSet();
        Assert.All(set.Items, level =>
        {
            Assert.Subset(strategyIds, level.Objectives.Strategy.ToHashSet());
            Assert.Subset(challengeIds, level.Objectives.Challenge.ToHashSet());
        });
    }

    [Fact] // legal — 关闭局逐条授予：首通成就/三重目标判分/开箱/占领（可达性局内见证）
    public void Off_system_run_grants_every_E1_achievement_path()
    {
        OffSystemRunReport run = _fixture.OffRun;
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));
        var strategyIds = pools.Strategy.Select(entry => entry.Id).ToHashSet();
        var challengeIds = pools.Challenge.Select(entry => entry.Id).ToHashSet();

        ChestTable chestTable = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
        Assert.All(run.Levels, level =>
        {
            // 首通成就：零托管参与（关闭局军令通道不存在）⇒ T-081 钩子全放行。
            Assert.Equal(new[] { "first-clear:" + level.LevelId }, level.FirstClearGrants);
            Assert.True(level.Survived); // 生存目标=Victory 事实（玩家守波）
            Assert.Contains(level.StrategyObjectiveId, strategyIds); // 判分目标=池内条目
            Assert.Contains(level.ChallengeObjectiveId, challengeIds);
            Assert.InRange(level.CompletedCount, 1, 3); // 生存至少 1（无静默默认）
            Assert.Equal(TripleObjectiveJudge.TierFor(level.CompletedCount), level.Tier);
            Assert.Equal(CampaignProgression.TierToChestId(level.Tier), level.ChestTierId);
            int expectedRolls = chestTable.Tiers.Single(tier => tier.Tier == level.ChestTierId).ItemRolls
                + chestTable.Tiers.Single(tier => tier.Tier == level.ChestTierId).BonusRolls;
            Assert.Equal(expectedRolls, level.ChestItemIds.Count); // 开箱件数=档位表
        });
        Assert.Equal(run.Levels.Sum(level => level.ChestItemIds.Count), run.CosmeticsCount); // 装饰全入账

        // E1 战役里程碑：四关全占（占领序=关序，见通关用例；此处=成就面同锚）。
        Assert.Equal(
            HandcraftedLevelData.Load("e1.json").Items.Select(level => level.Id).ToArray(),
            run.CapturedOrder.ToArray());
    }

    [Fact] // legal — 首证锚点（2026-10-14 冻结结果：内容/平衡/种子漂移即红）
    public void Off_system_run_pins_the_first_proof_outcomes()
    {
        OffSystemRunReport run = _fixture.OffRun;

        // (slot, winWaves, victoryTick, strategyId, strategyPassed, challengeId, challengePassed,
        //  completed, tier, chestTierId, chestItems, towers, lootWoodGranted)
        var expected = new (int, int, long, string, bool, string, bool, int, ObjectiveTier, string, int, int, double)[]
        {
            (1, 13, 115541, "campaign:obj-s5", true, "campaign:obj-c8", false, 2, ObjectiveTier.Gold, "gold", 4, 3, 120),
            (2, 13, 108462, "campaign:obj-s6", true, "campaign:obj-c8", false, 2, ObjectiveTier.Gold, "gold", 4, 4, 1177),
            (3, 14, 101266, "campaign:obj-s1", true, "campaign:obj-c7", true, 3, ObjectiveTier.GoldExtra, "gold_extra", 5, 4, 120),
            (4, 16, 97733, "campaign:obj-s1", true, "campaign:obj-c7", true, 3, ObjectiveTier.GoldExtra, "gold_extra", 5, 6, 90),
        };
        Assert.Equal(expected.Length, run.Levels.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            (int slot, int win, long tick, string strategy, bool strategyPassed, string challenge, bool challengePassed,
                int completed, ObjectiveTier tier, string chestTier, int chestItems, int towers, double loot) = expected[i];
            OffSystemLevelRecord level = run.Levels[i];
            Assert.Equal(slot, level.Slot);
            Assert.Equal(win, level.WinWaves);
            Assert.Equal(tick, level.VictoryTick); // 同 seed+命令流的通关时刻锚
            Assert.Equal(strategy, level.StrategyObjectiveId);
            Assert.Equal(strategyPassed, level.StrategyPassed);
            Assert.Equal(challenge, level.ChallengeObjectiveId);
            Assert.Equal(challengePassed, level.ChallengePassed);
            Assert.Equal(completed, level.CompletedCount);
            Assert.Equal(tier, level.Tier);
            Assert.Equal(chestTier, level.ChestTierId);
            Assert.Equal(chestItems, level.ChestItemIds.Count);
            Assert.Equal(towers, level.TowerCount);
            Assert.Single(level.LootLines);
            Assert.Equal("era1:res-wood", level.LootLines[0].Resource);
            Assert.Equal(loot, level.LootLines[0].Granted, 6); // 结算入库行（经济回归锚）
            Assert.True(level.HashLedger.Count >= 100, "采样哈希账本不得为空（防空账本等价假绿）");
        }

        // 战役池抵达 E1 仓库上限（入池只计上限内）；装饰=四箱合计；警报流/审计读数锚。
        Assert.Equal(("era1:res-wood", 2000.0), run.PoolTotals.Single());
        Assert.Equal(18, run.CosmeticsCount);
        Assert.Equal(706, run.AlarmEvaluations);
        Assert.Equal(21, run.FirstWaveLeaksTotal);
        Assert.Equal(45, run.IssuedCommands);
    }

    // ---- ⑤ 对照组等价（可选性不改可达性） -----------------------------------

    [Fact] // legal — 军师在场（玩家不用）终态与关闭局逐字段等价
    public void Control_run_with_advisor_present_ends_identical()
    {
        OffSystemRunReport off = _fixture.OffRun;
        OffSystemRunReport on = _fixture.ControlRun;

        Assert.True(on.SystemEnabled);
        Assert.Equal(PanelState.Bubble, on.PanelEndState); // 对照锚：开启侧开关位=开
        Assert.True(on.OrderInputVisible);
        Assert.Equal(0, on.JournalInputEvents); // 对照局零军令（军师在场但不用）
        Assert.Equal(0, on.JournalExecutionEvents); // 零系统动作
        Assert.Equal(60, on.JournalTotalEvents); // K-8 仅波界回充噪声（4 关波数 14+14+15+17）
        Assert.True(on.LedgerPointsAlwaysZero); // 零系统点消耗

        Assert.Equal(off.Levels.Count, on.Levels.Count);
        for (int i = 0; i < off.Levels.Count; i++)
        {
            OffSystemLevelRecord offLevel = off.Levels[i];
            OffSystemLevelRecord onLevel = on.Levels[i];

            Assert.Equal(offLevel.LevelId, onLevel.LevelId);
            Assert.Equal(offLevel.EndState, onLevel.EndState); // 关卡进度等价
            Assert.Equal(offLevel.VictoryTick, onLevel.VictoryTick); // 同刻胜利
            Assert.Equal(offLevel.WinWaves, onLevel.WinWaves);
            Assert.Equal(offLevel.LootLines, onLevel.LootLines); // 资源入库等价
            Assert.Equal(offLevel.FinalStateHash, onLevel.FinalStateHash); // 终局状态哈希全等
            Assert.Equal(offLevel.HashLedger, onLevel.HashLedger);
            Assert.Equal(offLevel.StrategyObjectiveId, onLevel.StrategyObjectiveId); // 判分同抽
            Assert.Equal(offLevel.StrategyPassed, onLevel.StrategyPassed);
            Assert.Equal(offLevel.ChallengeObjectiveId, onLevel.ChallengeObjectiveId);
            Assert.Equal(offLevel.ChallengePassed, onLevel.ChallengePassed);
            Assert.Equal(offLevel.CompletedCount, onLevel.CompletedCount);
            Assert.Equal(offLevel.Tier, onLevel.Tier);
            Assert.Equal(offLevel.ChestTierId, onLevel.ChestTierId);
            Assert.Equal(offLevel.ChestItemIds, onLevel.ChestItemIds); // 开箱同物
            Assert.Equal(offLevel.FirstClearGrants, onLevel.FirstClearGrants); // 首通成就同授
            Assert.Equal(offLevel.TowerCount, onLevel.TowerCount);
        }

        Assert.Equal(off.PoolTotals, on.PoolTotals); // 战役池逐资源等价
        Assert.Equal(off.CapturedOrder, on.CapturedOrder); // 占领序等价
        Assert.Equal(off.CosmeticsCount, on.CosmeticsCount);
        Assert.Equal(off.OccupiedCells, on.OccupiedCells);
    }

    // ---- ⑥ 确定性（关闭局双跑） ---------------------------------------------

    [Fact] // legal — 同开关位双跑：采样哈希账本全等 + 终局指纹全等
    public void Off_system_double_run_hashes_are_identical()
    {
        OffSystemRunReport first = _fixture.OffRun;
        OffSystemRunReport second = _fixture.OffRunAgain;

        Assert.Equal(first.Levels.Count, second.Levels.Count);
        for (int i = 0; i < first.Levels.Count; i++)
        {
            Assert.Equal(first.Levels[i].HashLedger, second.Levels[i].HashLedger); // 采样账本全等
            Assert.Equal(first.Levels[i].FinalStateHash, second.Levels[i].FinalStateHash);
            Assert.Equal(first.Levels[i].VictoryTick, second.Levels[i].VictoryTick);
            Assert.Equal(first.Levels[i].LootLines, second.Levels[i].LootLines);
        }

        Assert.Equal(first.PoolTotals, second.PoolTotals); // 战役池等价
        Assert.Equal(first.CapturedOrder, second.CapturedOrder);
        Assert.Equal(first.CosmeticsCount, second.CosmeticsCount);
    }
}
