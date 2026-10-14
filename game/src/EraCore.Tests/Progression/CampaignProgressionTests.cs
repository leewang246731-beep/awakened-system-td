using EraCore.CampaignLoop;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Persistence;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-002][F-003][F-004][F-024][F-050][F-051][F-054] 集成全链（T-073）：
// 选图→进关→胜波→Victory 钩子（LevelSession 公共 API）→三重目标判分→
// 档位宝箱→占领（关卡绑定转绿）→科技可研→材料+分期扣池→资源池守恒，
// 全链确定性（同 seed 双跑逐位一致）。[C-19][C-12]
public class CampaignProgressionTests : SqliteSavePortTestBase
{
    private sealed record ChainResult(
        ObjectiveTier Tier,
        int Completed,
        string ChestTierId,
        IReadOnlyList<string> ChestItemIds,
        IReadOnlyList<(string Resource, double Accepted)> LootLines,
        long PoolHash,
        IReadOnlyList<string> UnlockedTechs);

    private static CampaignProgression NewProgression(ResourceStock pool) => new(
        pool,
        new TechTree(TechTreeData.Parse(ReadCampaignContent("techtree.json")), pool, eraIndex: 1),
        new TripleObjectiveJudge(ObjectivesData.Parse(ReadCampaignContent("objectives.json")), new SimRng(Seed)),
        new ChestRewards(ChestRewardsData.Parse(ReadCampaignContent("chests.json")), new SimRng(Seed)));

    private static HostRunCounters ThinHostCounters() => new(
        StationsLost: 0,
        DefenseIntegrityMin: 1.0,
        TowersSoldCount: 0,
        AmmoStarvedSeconds: 0.0,
        WaveEndEfficiencyMin: 1.0,
        KeyBuildingsDamagedCount: 0,
        LogisticsDeadlockCount: 0,
        SystemBuildPlanCount: 0,
        ScrollTriggerCount: 0,
        FirstWaveLeaks: 0); // 主机侧审计计数（C-13/C-07 审计缝落地前的薄版声明值）

    /// <summary>
    /// Runs the whole chain once: campaign grid → level victory (real sim run)
    /// → settlement (judge + chest + capture) → tech research paid off the
    /// very pool the victory loot banked into.
    /// </summary>
    private ChainResult RunFullChain()
    {
        CampaignState campaign = NewCampaign();
        CampaignGrid grid = campaign.Grid;
        LevelNodeInfo node = CheapestSelectableNode(grid);
        ResourceStock pool = campaign.Pool;
        CampaignProgression progression = NewProgression(pool);
        TechTree tech = progression.Tech;

        // —— 进关（LevelSession 公共 API，T-059 循环复用）：槽 id=科技绑定用的关卡 id。
        string levelId = "campaign:level-e1-01";
        var levelConfig = new LevelSessionConfig(
            LevelConfig(node.Threat), levelId, node.Threat, LevelSeed, StateHashInterval: 30);
        SqliteSavePort port = CreatePort();
        var session = new LevelSession(levelConfig, new SimRng(LevelSeed), port, pool);
        session.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        // —— 胜波达成且场清 → Victory 钩子（结算入库已由 LevelSession.OnVictory 完成）。
        session.RunFor(VictoryHorizonTicks(node.Threat));
        Assert.Equal(GameFlowState.Victory, session.Machine.State);
        double woodAfterLoot = pool.AmountOf(ProgressionFixture.Wood);
        Assert.True(woodAfterLoot > 0.0, "胜利入库应大于零");

        // —— 三重目标结算：观察事实（公共 API 派生 + 主机审计计数）→ 判分 → 开箱。
        LevelRunFacts facts = CampaignProgression.ObserveFacts(
            session,
            levelBudgetSeconds: WinWaveStartTick(node.Threat) / (double)Simulation.TicksPerSecond * 2.0, // 夹具口径：胜波起点×2
            researchCompletedCount: 0,
            host: ThinHostCounters());
        Assert.True(facts.Survived);
        Assert.Equal(session.Machine.History.Count(t => t.Kind == GameFlowTransitionKind.Pause), facts.PauseCount); // 0：全链未暂停
        Assert.Equal(session.Sim.Towers.Count, facts.NetTowerCount); // 里程碑链双塔
        Assert.Equal(2, facts.TowerBuildsByClass!["pierce"]); // 木箭塔=pierce（towers.json）

        CampaignSettlement settlement = progression.SettleVictory(session, levelId, node.Threat, facts);
        Assert.Equal(levelId, settlement.LevelId);
        Assert.Equal(facts.Survived, settlement.Objectives.Survived); // 生存目标=胜利事实
        Assert.Equal(
            TripleObjectiveJudge.TierFor(settlement.Objectives.CompletedCount),
            settlement.Objectives.Tier);
        Assert.Equal(CampaignProgression.TierToChestId(settlement.Objectives.Tier), settlement.ChestTierId);
        ChestTable chestTable = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
        int expectedRolls = chestTable.Tiers.Single(tier => tier.Tier == settlement.ChestTierId).ItemRolls
            + chestTable.Tiers.Single(tier => tier.Tier == settlement.ChestTierId).BonusRolls;
        Assert.Equal(expectedRolls, settlement.ChestItems.Count); // 件数=档位表
        Assert.All(settlement.ChestItems, item => Assert.Contains(item.RewardClass, RewardWhitelist.Classes)); // R9 白名单
        Assert.Equal(settlement.ChestItems.Count, progression.Cosmetics.Count); // 装饰清单入账
        Assert.Contains(levelId, progression.CapturedLevelIds); // 占领登记（关卡绑定闸输入）

        // —— 占领后：绑定该关的科技节点关卡闸转绿（e1-rockfall 绑 level-e1-02 仍红）。
        TechGateReport arrow = tech.CheckGates("campaign:tech-e1-arrow");
        Assert.True(arrow.CanBegin); // 根节点：无前置/无绑定
        TechGateReport rockfall = tech.CheckGates("campaign:tech-e1-rockfall");
        Assert.False(rockfall.TopologyPassed); // 仍需 e1-arrow（拓扑闸红）
        Assert.False(rockfall.LevelBindingPassed); // 绑 level-e1-02 未占领
        Assert.Contains("campaign:level-e1-02", rockfall.UncapturedLevels);

        // —— 研究支付扣池（F-024/F-051）：补给池→开研扣材料→分摊分期付清
        //    （e1 箭塔 Σ成本=30 → 总价 932，按 0.5/0.5 分摊=木 466+石 466；
        //    F.5 锚点 n=90→≈3013 在 TechTreeTests 公式表单测覆盖）。
        _ = pool.Deposit(ProgressionFixture.Wood, 1600.0); // 主机侧补给（战役事件入池）
        _ = pool.Deposit(ProgressionFixture.Stone, 1600.0);
        double woodBeforeResearch = pool.AmountOf(ProgressionFixture.Wood);
        double stoneBeforeResearch = pool.AmountOf(ProgressionFixture.Stone);
        tech.BeginResearch("campaign:tech-e1-arrow"); // 材料 20 木
        Assert.Equal(932, tech.Active!.TotalCost); // 60+30^1.11×20（手算 932.30）
        Assert.Equal(466.0, tech.Active.Balances.Single(b => b.Resource.Equals(ProgressionFixture.Wood)).Charge, 0); // floor(932×0.5)
        Assert.Equal(466.0, tech.Active.Balances.Single(b => b.Resource.Equals(ProgressionFixture.Stone)).Charge, 0); // 末行补差
        Assert.Equal(woodBeforeResearch - 20.0, pool.AmountOf(ProgressionFixture.Wood), 9); // 材料即扣

        InstallmentResult woodFirst = tech.PayInstallment(ProgressionFixture.Wood, 460.0); // 木线部分付款
        Assert.False(woodFirst.Completed);
        Assert.False(tech.CheckGates("campaign:tech-e2-arrow").EraPassed); // E2 节点时代闸仍红
        InstallmentResult woodLast = tech.PayInstallment(ProgressionFixture.Wood, 100.0); // 木线尾款 6（超额拒收）
        Assert.Equal(6.0, woodLast.Applied, 0);
        Assert.False(woodLast.Completed); // 石线未付 → 不解锁
        InstallmentResult stoneFinal = tech.PayInstallment(ProgressionFixture.Stone, 466.0); // 石线一次付清 → 完成
        Assert.True(stoneFinal.Completed);
        Assert.Equal(new[] { "campaign:tech-e1-arrow" }, tech.UnlockedIds);
        Assert.Equal(woodBeforeResearch - 20.0 - 466.0, pool.AmountOf(ProgressionFixture.Wood), 6); // 守恒：材料+木线全款
        Assert.Equal(stoneBeforeResearch - 466.0, pool.AmountOf(ProgressionFixture.Stone), 6); // 守恒：石线全款

        // —— 占领第二格绑定关（解锁 rockfall 的关卡闸输入）→ 三闸全绿可开研。
        grid.Occupy(node.Cell.X, node.Cell.Y); // E2E 选中的第一格（战役环本职）
        progression.RegisterCapture("campaign:level-e1-02"); // 该关占领（主机事件）
        TechGateReport rockfallOpen = tech.CheckGates("campaign:tech-e1-rockfall");
        Assert.True(rockfallOpen.TopologyPassed);
        Assert.True(rockfallOpen.LevelBindingPassed);
        Assert.True(rockfallOpen.CanBegin);
        tech.BeginResearch("campaign:tech-e1-rockfall"); // 石 40 材料即扣
        Assert.Equal(stoneBeforeResearch - 466.0 - 40.0, pool.AmountOf(ProgressionFixture.Stone), 6);

        return new ChainResult(
            settlement.Objectives.Tier,
            settlement.Objectives.CompletedCount,
            settlement.ChestTierId,
            settlement.ChestItems.Select(item => item.ItemId).ToArray(),
            settlement.Loot.Select(grant => (grant.Resource.Value, grant.Accepted)).ToArray(),
            pool.ComputeStateHash(),
            tech.UnlockedIds.ToArray());
    }

    [Fact] // legal — 占领→判分→宝箱→科技→资源池 全链 + 池守恒
    [Trait("Feature", "F-054")]
    public void Full_chain_from_occupation_to_research_paid_off_the_pool()
    {
        ChainResult result = RunFullChain();
        Assert.Equal(new[] { "campaign:tech-e1-arrow" }, result.UnlockedTechs); // rockfall 在研未付清
        Assert.True(result.ChestItemIds.Count >= 2, "档位宝箱至少青铜 2 件（表驱动）");
        Assert.True(result.Completed >= 0 && result.Completed <= 3);
    }

    [Fact] // legal — 全链确定性：同 seed 双跑——判分/宝箱/战利品/科技/池哈希逐位一致
    [Trait("Feature", "F-002")]
    public void Full_chain_is_deterministic_for_the_same_seeds()
    {
        ChainResult first = RunFullChain();
        ChainResult second = RunFullChain();
        Assert.Equal(first.Tier, second.Tier); // 判分档位（先定后演 CS-4.9）
        Assert.Equal(first.Completed, second.Completed);
        Assert.Equal(first.ChestTierId, second.ChestTierId);
        Assert.Equal(first.ChestItemIds, second.ChestItemIds); // 宝箱逐件一致
        Assert.Equal(first.LootLines, second.LootLines); // 战利品入池逐行一致
        Assert.Equal(first.PoolHash, second.PoolHash); // 资源池终态一致
        Assert.Equal(first.UnlockedTechs, second.UnlockedTechs);
    }

    [Fact] // boundary — 未胜利的会话不可结算（Victory 钩子门禁）
    [Trait("Feature", "F-002")]
    public void Settle_requires_a_won_session()
    {
        CampaignState campaign = NewCampaign();
        LevelNodeInfo node = CheapestSelectableNode(campaign.Grid);
        CampaignProgression progression = NewProgression(campaign.Pool);
        var config = new LevelSessionConfig(
            LevelConfig(node.Threat), "campaign:level-e1-09", node.Threat, LevelSeed);
        var session = new LevelSession(config, new SimRng(LevelSeed), CreatePort(), campaign.Pool);
        session.Machine.StartLevel();
        session.RunFor(50); // 远未到胜波

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() =>
            progression.SettleVictory(session, "campaign:level-e1-09", node.Threat, FactsOf(session)));
        Assert.Contains("victory hook", refusal.Message);
        Assert.Throws<InvalidOperationException>(() =>
            CampaignProgression.ObserveFacts(session, 100.0, 0, ThinHostCounters()));

        static LevelRunFacts FactsOf(LevelSession live) => new(Survived: false); // 仅作参数，前置于异常
    }

    [Fact] // illegal — 重复占领 / 空关卡 id / null 会话
    [Trait("Feature", "F-050")]
    public void Illegal_progression_inputs_are_rejected()
    {
        CampaignProgression progression = NewProgression(FreshPool(new MutableWallClock()));
        progression.RegisterCapture("campaign:level-e1-01");
        Assert.Throws<InvalidOperationException>(() => progression.RegisterCapture("campaign:level-e1-01"));
        Assert.Throws<ArgumentException>(() => progression.RegisterCapture(" "));
        Assert.Throws<ArgumentNullException>(() => progression.RegisterCapture(null!));
    }
}
