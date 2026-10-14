using EraCore.Agent.Governance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Persistence;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Cinematics;

// [F-002][F-104][RL12] T-087 集成全链（真实判分→演出→入库，零污染）：
// 选图→进关→胜波→SettleVictory（判分+宝箱+占领入库）→VictorySettlement.Decide
// （转盘+点评先定）→存档先存（重进不丢）→演出时间轴只读回放。三态等价
// （先定后演断言）：全放演/4× 加速（跳过转盘的合法最快路径）/中途关游戏
// （弃演+存档重载+回看重放）三路的既定演出逐字节相同、转盘落点相同、
// 模拟哈希不变（演出零污染——胜利后世界冻结，演出零世界命令）。
public class VictoryCinematicsIntegrationTests : SqliteSavePortTestBase
{
    private sealed record ChainOutcome(
        byte[] DecidedBytes,
        long SimHashBeforePlay,
        long SimHashAfterPlay,
        IReadOnlyList<string> EffectOrder,
        int WheelStop,
        string WheelGrantedClass,
        string ChestTierId,
        IReadOnlyList<string> ChestItemIds,
        bool CaptureRegistered,
        int CosmeticsAtSettle,
        int ReviewOrderStart);

    private enum PlaybackMode
    {
        Full,
        Boosted,
        QuitRestore,
    }

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
        FirstWaveLeaks: 0); // 主机侧审计计数（C-13/C-07 审计缝口径同 CampaignProgressionTests）

    /// <summary>
    /// The full chain once: real level victory through the LevelSession
    /// public API → real judge settlement → decided performance saved to a
    /// fresh store root → the victory script performed per
    /// <paramref name="mode"/> (full / boosted / quit-restore).
    /// </summary>
    private ChainOutcome RunChain(PlaybackMode mode, string storeRoot)
    {
        CampaignState campaign = NewCampaign();
        LevelNodeInfo node = CheapestSelectableNode(campaign.Grid);
        ResourceStock pool = campaign.Pool;
        CampaignProgression progression = NewProgression(pool);

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

        session.RunFor(VictoryHorizonTicks(node.Threat));
        Assert.Equal(GameFlowState.Victory, session.Machine.State);

        LevelRunFacts facts = CampaignProgression.ObserveFacts(
            session,
            levelBudgetSeconds: WinWaveStartTick(node.Threat) / (double)Simulation.TicksPerSecond * 2.0,
            researchCompletedCount: 0,
            host: ThinHostCounters());

        // —— 先定后演的事件序（验收断言）：入库→定演→存档→才允许第一帧演出。
        CampaignSettlement settlement = progression.SettleVictory(session, levelId, node.Threat, facts);
        int cosmeticsAtSettle = progression.Cosmetics.Count;

        long runSeed = session.Victory!.Seed;
        DecidedVictoryPerformance decided = VictorySettlement.Decide(
            settlement,
            ChestRewardsData.Parse(ReadCampaignContent("chests.json")),
            new SimRng(runSeed),
            Assert.IsType<CinematicStep.WheelSpin>(
                Assert.Single(CinematicContent.Victory().Steps, step => step is CinematicStep.WheelSpin)).Segments,
            runSeed,
            SystemPersona.Advisor,
            TrustBadge.Trustworthy,
            TrustBadge.Trustworthy);

        var store = new DevFileStore(storeRoot);
        VictorySettlementArchive archive = VictorySettlementStore.Upsert(
            VictorySettlementArchive.Default,
            new VictorySettlementRecord { LevelId = levelId, Decided = decided, Viewed = false });
        VictorySettlementStore.Save(store, archive);
        byte[] archiveBytesOnDisk = store.ReadAllBytes(VictorySettlementStore.RelativePath);

        byte[] decidedBytes = VictorySettlementStore.Encode(decided);
        long simHashBeforePlay = session.Sim.ComputeStateHash();

        // —— 三态：全放演 / 4× 加速 / 中途关游戏（弃演→存档重载→回看重放）
        var effectOrder = new List<string>();
        int reviewOrderStart = 0;

        void Collect(CinematicTimeline source, IReadOnlyList<CinematicEffect> effects)
        {
            foreach (CinematicEffect effect in effects)
            {
                effectOrder.Add(effect switch
                {
                    CinematicEffect.SettlementVerdict => "verdict",
                    CinematicEffect.SettlementTierRevealed => "tier",
                    CinematicEffect.WheelSpinStarted => "spinStart",
                    CinematicEffect.WheelLanded => "landed",
                    CinematicEffect.SettlementChestOpened => "chest",
                    CinematicEffect.SettlementCommentarySpoken => "comment",
                    CinematicEffect.Completed => "completed",
                    _ => "beat",
                });
            }

            Assert.Empty(source.TakePendingCommands()); // 演出零世界命令（入库已先完成）
        }

        CinematicTimeline timeline = new(CinematicContent.Victory(), decided);
        Collect(timeline, timeline.Start());
        if (mode == PlaybackMode.Boosted)
        {
            timeline.SetBoost(CinematicTimeline.MaxBoost);
        }

        int quitAt = mode == PlaybackMode.QuitRestore ? 100 : int.MaxValue;
        int tick = 0;
        while (timeline.Phase != CinematicPhase.Completed && tick < 2000 && tick < quitAt)
        {
            Collect(timeline, timeline.Tick());
            tick++;
        }

        if (mode == PlaybackMode.QuitRestore)
        {
            // 中途关游戏：弃掉时间轴，盘上既定演出原样；回看通道以重载数据重放
            Assert.NotEqual(CinematicPhase.Completed, timeline.Phase);
            Assert.Equal(archiveBytesOnDisk, store.ReadAllBytes(VictorySettlementStore.RelativePath)); // 演出不改存档
            VictorySettlementArchive restoredArchive = VictorySettlementStore.Load(store);
            DecidedVictoryPerformance restored =
                VictorySettlementStore.LatestFor(restoredArchive, levelId)!.Decided;
            Assert.Equal(decidedBytes, VictorySettlementStore.Encode(restored));

            reviewOrderStart = effectOrder.Count; // 回看段从弃演处起算（事件序对照回看腿）
            var review = new CinematicTimeline(CinematicContent.Victory(), restored);
            Collect(review, review.Start());
            while (review.Phase != CinematicPhase.Completed)
            {
                Collect(review, review.Tick());
            }
        }
        else
        {
            Assert.Equal(CinematicPhase.Completed, timeline.Phase);
            Assert.True(timeline.ElapsedTicks >= CinematicContent.Victory().MinimumPlayTicks!.Value);
            Assert.Equal(archiveBytesOnDisk, store.ReadAllBytes(VictorySettlementStore.RelativePath));
        }

        return new ChainOutcome(
            decidedBytes,
            simHashBeforePlay,
            session.Sim.ComputeStateHash(),
            effectOrder,
            decided.Wheel.StopIndex,
            decided.Wheel.Granted.RewardClass,
            settlement.ChestTierId,
            decided.ChestItems.Select(item => item.ItemId).ToArray(),
            progression.CapturedLevelIds.Contains(levelId),
            cosmeticsAtSettle,
            reviewOrderStart);
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 三态等价（RL12 验收）：4× 加速过转盘/全放演/中途关游戏（重载回看），
           // 既定演出逐字节相同、转盘落点相同、模拟哈希前后不变（零污染）
    public void Full_boosted_and_quit_restored_playbacks_decide_identical_bytes()
    {
        string root = Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ChainOutcome full = RunChain(PlaybackMode.Full, root);
            ChainOutcome boosted = RunChain(PlaybackMode.Boosted, Path.Combine(root, "b"));
            ChainOutcome quit = RunChain(PlaybackMode.QuitRestore, Path.Combine(root, "c"));

            Assert.Equal(full.DecidedBytes, boosted.DecidedBytes);
            Assert.Equal(full.DecidedBytes, quit.DecidedBytes); // 逐字节相同（先定后演）
            Assert.Equal(full.WheelStop, boosted.WheelStop);
            Assert.Equal(full.WheelStop, quit.WheelStop);
            Assert.Equal(full.ChestItemIds, quit.ChestItemIds);
            Assert.Equal(full.SimHashBeforePlay, full.SimHashAfterPlay); // 演出不写模拟
            Assert.Equal(full.SimHashBeforePlay, boosted.SimHashBeforePlay); // 同 seed 同命令流同判分
            Assert.Equal(full.SimHashBeforePlay, quit.SimHashBeforePlay);

            // 事件序：三态同序（判分→档位→转盘起→转盘落→宝箱→点评→完结）
            string[] expectedTail =
            {
                "verdict", "tier", "spinStart", "landed", "chest", "comment", "completed",
            };
            Assert.Equal(expectedTail, full.EffectOrder.Where(name => name != "beat").ToArray());
            Assert.Equal(expectedTail, boosted.EffectOrder.Where(name => name != "beat").ToArray());
            Assert.Equal(expectedTail, quit.EffectOrder.Skip(quit.ReviewOrderStart).Where(name => name != "beat").ToArray());
            Assert.Contains("verdict", quit.EffectOrder.Take(quit.ReviewOrderStart)); // 弃演腿已见判分拍
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact] // legal — 全链入库先于演出：占领登记+宝箱入账+档位件数=档位表（SettleVictory 已冻结）
    public void The_chain_banks_before_the_first_performance_beat()
    {
        string root = Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ChainOutcome outcome = RunChain(PlaybackMode.Full, root);

            Assert.True(outcome.CaptureRegistered); // 占领（关卡绑定闸输入）
            ChestTable table = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
            ChestTierSpec tier = table.Tiers.Single(row => row.Tier == outcome.ChestTierId);
            Assert.Equal(tier.ItemRolls + tier.BonusRolls, outcome.ChestItemIds.Count); // 件数=档位表
            Assert.Equal(outcome.ChestItemIds.Count, outcome.CosmeticsAtSettle); // 装饰清单已含全部宝箱件
            Assert.All(outcome.ChestItemIds, id => Assert.Contains(id,
                table.ItemsByRarity.Values.SelectMany(pool => pool).Select(item => item.Id)));
            Assert.Contains(outcome.WheelGrantedClass, RewardWhitelist.Classes); // R9 白名单
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
