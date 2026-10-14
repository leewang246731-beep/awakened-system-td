using EraCore.CampaignLoop;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Progression;
using EraCore.Tests.CampaignLoop;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-011] E1→E2 集成链（T-074）：E1 真通关（全循环）→ 跃迁事务
// → E2 可续（科技时代根+E2 节点可研+残卷减免入价）→ 同 seed 双跑确定性。
// 入 CheckpointHeavyCollection 串行（全循环耗时同 CampaignLoopE2ETests）。
[Collection(CheckpointHeavyCollection.Name)]
public class EraTransitionIntegrationTests : SqliteSavePortTestBase
{
    private static IReadOnlyList<TechNodeSpec> RealNodes()
    {
        string campaignDir = Path.Combine(Directory.GetParent(LegacyFixture.FindLegacyDir())!.FullName, "campaign");
        return TechTreeData.Parse(File.ReadAllText(Path.Combine(campaignDir, "techtree.json")));
    }

    [Fact] // [F-003][F-011] legal — 集成全链：E1 通关→占领→终局关+奇观→跃迁→E2 科技可续+考古可挖
    public void e1_victory_to_transition_to_e2_continuation()
    {
        // ---- E1 真通关（复用 E2E 夹具链路）：选图→进关→命令流→胜波→结算→占领
        CampaignState campaign = NewCampaign();
        CampaignGrid grid = campaign.Grid;
        LevelNodeInfo node = CheapestSelectableNode(grid);
        var levelConfig = new LevelSessionConfig(
            LevelConfig(node.Threat), LoopSlotId, node.Threat, LevelSeed, StateHashInterval: 30);
        SqliteSavePort port = CreatePort();
        var session = new LevelSession(levelConfig, new SimRng(LevelSeed), port, campaign.Pool);
        session.Machine.StartLevel();
        foreach (SimCommand command in SimE1Fixture.MilestoneCommands())
        {
            session.Enqueue(command);
        }

        session.RunFor(VictoryHorizonTicks(node.Threat));
        Assert.Equal(GameFlowState.Victory, session.Machine.State);
        Assert.NotNull(session.Victory);
        session.CompleteLevel();
        _ = grid.Occupy(node.Cell.X, node.Cell.Y); // 占领入库
        Assert.True(grid.IsOccupied(node.Cell.X, node.Cell.Y));

        // ---- 备战跃迁：补足已知存量（矿石给 E2 材料闸门）+ 终局关 + 奇观
        _ = campaign.Pool.Deposit(new Contracts.Foundation.ResourceKey("era1:res-stone"), 600);
        _ = campaign.Pool.Deposit(new Contracts.Foundation.ResourceKey("era1:res-food"), 300);
        double woodBefore = campaign.Pool.AmountOf(Wood);
        double stoneBefore = campaign.Pool.AmountOf(new Contracts.Foundation.ResourceKey("era1:res-stone"));
        double foodBefore = campaign.Pool.AmountOf(new Contracts.Foundation.ResourceKey("era1:res-food"));

        var tech = new TechTree(RealNodes(), campaign.Pool, eraIndex: 1);
        tech.RegisterCapture(node.Cell.X + ":" + node.Cell.Y); // 循环关占领入树（关卡绑定闸门输入）
        tech.RegisterCapture(LegacyFixture.Transition().FinalLevels[1]); // 终局关
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt(LegacyFixture.E1Wonder().Id); // E1 奇观落成
        var counting = new CountingSavePort(port);

        TechGateReport e2GateBefore = tech.CheckGates("campaign:tech-e2-arrow");
        Assert.False(e2GateBefore.EraPassed); // E1 时代根下 E2 节点时代闸门关

        // ---- 跃迁事务（E1 城事实=夹具城；wood/stone 有用、food 3:1 折基金）
        var city = LegacyFixture.CityFacts(7, 40, 13, LegacyFixture.Mapping().Orientations);
        EraTransitionResult result = new EraTransitionService().Execute(LegacyFixture.Request(
            campaign.Pool, tech, ledger, city, counting, LegacyFixture.EmptyMeta(WorldSeed, 1)));

        // 恰 70%/30%：实盘池（含通关战利品）逐资源守恒
        Assert.All(result.Levy, line => Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9));
        Assert.Equal(Math.Floor(woodBefore * 0.3), (double)result.Levy.Single(l => l.Resource.Equals(Wood)).Carried, 9);
        Assert.Equal(Math.Floor(stoneBefore * 0.3), (double)result.Levy.Single(l => l.Resource.Value == "era1:res-stone").Carried, 9);
        Assert.Equal(Math.Floor(Math.Floor(foodBefore * 0.3) / 3.0), (double)result.FundGained, 9); // 带走 30% 再 3:1
        Assert.Equal(1, counting.CampaignWrites); // campaign 域恰一次写

        // ---- E2 可续：时代根已切换，E2 节点时代闸门开，占领 e2-01 后可开研
        Assert.Equal(2, tech.EraIndex);
        TechGateReport e2Gate = tech.CheckGates("campaign:tech-e2-arrow");
        Assert.True(e2Gate.EraPassed);
        Assert.False(e2Gate.LevelBindingPassed); // 还差占领 campaign:level-e2-01（E2 首关可玩）
        tech.RegisterCapture("campaign:level-e2-01");
        TechGateReport ready = tech.CheckGates("campaign:tech-e2-arrow");
        Assert.True(ready.CanBegin); // 材料（石 60 ≤ 带走存量）+时代+绑定+拓扑全绿

        // 残卷减免入价（F-059 接入）：读 siege 残卷 → tech-e2-arrow 九折开研
        _ = ledger.Scrolls.Read("legacy:scroll-mem-e1-siege");
        TechNodeSpec arrow = tech.Find("campaign:tech-e2-arrow")!;
        double reliefShare = StackingRule.CombinedShare(new[] { ledger.Scrolls.ReliefFor("campaign:tech-e2-arrow") });
        tech.BeginResearch(arrow.Id, reliefShare);
        Assert.Equal(tech.ResearchCost(arrow, reliefShare), tech.Active!.TotalCost);
        Assert.True(tech.Active.TotalCost < tech.ResearchCost(arrow)); // 折价生效
        var installment = tech.PayInstallment(new Contracts.Foundation.ResourceKey("era1:res-stone"), 100);
        Assert.Equal(100, installment.Applied, 9); // E2 分期可付=可续

        // ---- 考古可挖：跃迁抽象清单已入账本，挖掘服务在 E2 侧照常运转
        Assert.Equal(result.Mapping.AbstractList, ledger.AbstractList);
        var dig = new ArchaeologyService(LegacyFixture.Archaeology()).Dig(
            siteTier: 3, wreckValue: 500, new EraCore.Tests.World.LcgRng(WorldSeed + 1), ledger.Memorials);
        Assert.True(dig.Channel is ArchaeologyChannelKind.KnowledgeScroll
            or ArchaeologyChannelKind.AncientBlueprint
            or ArchaeologyChannelKind.Restoration);

        // ---- 存储侧：读回的 meta 即 E2 战役（时代 2+资源+遗迹注册表+账本槽位）
        ICampaignMeta stored = counting.ReadCampaign(SaveVersion.Current);
        Assert.Equal(2, stored.EraIndex);
        Assert.True(stored.ResourcePool["era1:res-stone"] >= 100); // 分期后仍为正
    }

    [Fact] // [F-003][F-056] legal — 确定性：同 seed 两次跃迁，新大陆+遗迹+账本哈希全等
    public void same_seed_transition_twice_is_bit_identical()
    {
        EraTransitionResult RunOnce()
        {
            var pool = LegacyFixture.Pool(500, 400, 300);
            var tech = LegacyFixture.Tree(1, LegacyFixture.Transition().FinalLevels[1]);
            var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
            ledger.MarkWonderBuilt(LegacyFixture.E1Wonder().Id);
            var city = LegacyFixture.CityFacts(7, 40, 13, LegacyFixture.Mapping().Orientations);
            var localPort = new CountingSavePort(CreatePort());
            localPort.WriteCampaign(LegacyFixture.EmptyMeta(WorldSeed, 1), SaveVersion.Current);
            localPort.CampaignWrites = 0;
            return new EraTransitionService().Execute(
                LegacyFixture.Request(pool, tech, ledger, city, localPort, LegacyFixture.EmptyMeta(WorldSeed, 1)));
        }

        EraTransitionResult first = RunOnce();
        EraTransitionResult second = RunOnce();

        Assert.Equal(first.Continent.MapHash, second.Continent.MapHash); // 新大陆全等
        Assert.Equal(first.Mapping.ComputeOutcomeHash(), second.Mapping.ComputeOutcomeHash()); // 遗迹回放全等
        Assert.Equal(first.Mapping.RelicRegistry, second.Mapping.RelicRegistry);
        Assert.Equal(first.Mapping.AbstractList, second.Mapping.AbstractList);
        Assert.Equal(first.FundGained, second.FundGained);
        Assert.Equal(
            first.WrittenMeta.LevelLayoutSnapshots.Single(s => s.LevelId == LegacyLedger.LedgerSlotId).LayoutPayload,
            second.WrittenMeta.LevelLayoutSnapshots.Single(s => s.LevelId == LegacyLedger.LedgerSlotId).LayoutPayload); // 账本载荷字节全等
        Assert.Equal(
            first.WrittenMeta.LevelLayoutSnapshots.Single(s => s.LevelId == EraTransitionService.RelicCitySlotId(1)).LayoutPayload,
            second.WrittenMeta.LevelLayoutSnapshots.Single(s => s.LevelId == EraTransitionService.RelicCitySlotId(1)).LayoutPayload); // 遗迹城快照字节全等
    }
}
