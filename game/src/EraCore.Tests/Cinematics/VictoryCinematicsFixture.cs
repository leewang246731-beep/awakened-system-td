using EraCore.Agent.Governance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.Sim;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Cinematics;

// T-087 (C-24) 测试基建：结算演出套件共用夹具——真实 campaign 内容
// （objectives.json/chests.json）装载 + 四档 CampaignSettlement 合成器
// （判分 outcome 直接按档位构造，宝箱走真实 ChestRewards 滚轮；全链真实
// 判分链在 VictoryCinematicsIntegrationTests 走 LevelSession 公共 API）。
// 纯辅助类，不含测试方法（T3 不适用）。

/// <summary>
/// Shared helpers of the victory-settlement suite: real campaign content
/// loaders and a tier-parameterized <see cref="CampaignSettlement"/> builder
/// (judge outcome synthesized per tier; chest items rolled by the REAL
/// <see cref="ChestRewards"/> off the given seed).
/// </summary>
internal static class VictoryCinematicsFixture
{
    public const long RunSeed = 20261014;

    /// <summary>真实宝箱表（chests.json）。</summary>
    public static ChestTable ChestTable() =>
        ChestRewardsData.Parse(ReadCampaignContent("chests.json"));

    /// <summary>真实目标池（objectives.json）。</summary>
    public static ObjectivePools Pools() =>
        ObjectivesData.Parse(ReadCampaignContent("objectives.json"));

    /// <summary>
    /// One settlement of the given tier: the outcome rows synthesized from
    /// the completed count (survival first, strategy then challenge), the
    /// chest rolled for real. Unit-level seam — the integration suite goes
    /// through the real judge instead.
    /// </summary>
    public static CampaignSettlement Settlement(
        ObjectiveTier tier,
        long runSeed = RunSeed,
        string levelId = "campaign:level-e1-01")
    {
        int completed = (int)tier;
        ObjectivePools pools = Pools();
        var draw = new ObjectiveDraw(pools.Strategy[0], pools.Challenge[0]);
        var outcome = new ObjectiveOutcome(
            draw,
            Survived: completed >= 1,
            StrategyPassed: completed >= 2,
            ChallengePassed: completed >= 3,
            CompletedCount: completed,
            Tier: tier);
        string chestTierId = CampaignProgression.TierToChestId(tier);
        IReadOnlyList<GrantedChestItem> items = new ChestRewards(ChestTable(), new SimRng(runSeed))
            .Open(chestTierId, runSeed);
        return new CampaignSettlement(levelId, outcome, chestTierId, items, new List<ResourceGrant>());
    }

    /// <summary>Decides a performance for one tier (defaults: 参谋/可托付→可托付, no echo).</summary>
    public static DecidedVictoryPerformance Decided(
        ObjectiveTier tier,
        long runSeed = RunSeed,
        int wheelSegments = 6,
        SystemPersona persona = SystemPersona.Advisor,
        TrustBadge badgeAtVictory = TrustBadge.Trustworthy,
        TrustBadge badgeAtLevelStart = TrustBadge.Trustworthy,
        string levelId = "campaign:level-e1-01") =>
        VictorySettlement.Decide(
            Settlement(tier, runSeed, levelId),
            ChestTable(),
            new SimRng(runSeed),
            wheelSegments,
            runSeed,
            persona,
            badgeAtVictory,
            badgeAtLevelStart);
}
