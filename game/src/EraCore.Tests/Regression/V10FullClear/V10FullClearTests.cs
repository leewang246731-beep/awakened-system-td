using EraCore.CampaignLoop;
using EraCore.Legacy;
using EraCore.Narrative;
using EraCore.Presentation;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.Levels;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Legacy.LegacyFixture;
using static EraCore.Tests.Levels.HandcraftedLevelContent;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Regression.V10FullClear;

// [C-30][F-097][F-003][F-011][F-055] W7 出口① v1.0 硬验收用例——关闭系统
// 自动通关 E1→E2→E3→E4 全四章端到端（08-task-plan W7 行出口①字面：关闭系统
// 可全通关 E1~E4+全成就+警报 100%）。装置=V10FullClearChain 静态链（每章
// Lazy 只跑自己的前缀；孪生链整链复跑供确定性对照；对照组=E2 抽查一关开启
// 系统零军令局）。断言面分七个：①~④逐章通关+跃迁事务（每章一测试——超长
// E2E 的拆分形态，共享静态链状态，见 REV 耗时注记）；⑤全成就（四时代枚举
// 扫描零系统依赖+局内逐条授予见证）；⑥对照组终态等价（T-084 先例缩小口径）；
// ⑦同 seed 双跑终局哈希全等；⑧警报通道双态同判+中性载荷（边界 40% 演练）。
public class V10FullClearTests
{
    private readonly ITestOutputHelper _output;

    public V10FullClearTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<HandcraftedLevel> LevelRows(int eraIndex) =>
        HandcraftedLevelData.Load($"e{eraIndex}.json").Items.OrderBy(row => row.Slot).ToList();

    private static HandcraftedLevel FinalRowOf(int eraIndex) =>
        LevelRows(eraIndex).Single(row => row.Id == Transition().FinalLevels[eraIndex]);

    /// <summary>共享断言：一章的通关面（Victory/胜波/零拒收/塔数/终局 Boss 锚/零系统事件/占领）。</summary>
    private void AssertEraCleared(V10EraSegment segment)
    {
        IReadOnlyList<HandcraftedLevel> rows = LevelRows(segment.EraIndex);
        Assert.Equal(rows.Count, segment.Levels.Count);
        Assert.Equal(
            rows.Select(row => row.Id).ToArray(),
            segment.Levels.Select(level => level.LevelId).ToArray()); // 按槽序通关
        Assert.All(segment.Levels, level => Assert.Equal(GameFlowState.Victory, level.EndState));
        Assert.All(segment.Levels.Zip(rows), pair =>
        {
            (V10LevelRecord level, HandcraftedLevel row) = pair;
            Assert.Equal(row.WinWaves, level.WinWaves); // 胜波=数据行（§7 双面）
            Assert.Equal(row.CommandScript.Count, level.IssuedCommands); // 命令流全量签发
            Assert.Equal(level.IssuedCommands, level.AppliedCommands); // 零拒收
            Assert.Equal(row.CommandScript.Count(step => step.Verb == "build-tower"), level.TowerCount);
            Assert.True(level.HashLedger.Count >= 100, $"采样哈希账本过薄: {level.LevelId} ({level.HashLedger.Count})");
        });

        // 终局关 Boss 手算锚（v3.2 §6 Boss 状态 ×1.5 × 威胁 HP 乘子，全内容驱动）。
        HandcraftedLevel finalRow = FinalRowOf(segment.EraIndex);
        V10LevelRecord finalLevel = segment.Levels.Single(level => level.LevelId == finalRow.Id);
        int expectedBossHp = (int)Math.Ceiling(
            Enemies(segment.EraDir)[finalRow.Waves.BossEnemyId!].Hp
            * EraCore.Balance.Threat.Multipliers(finalRow.Threat).Hp
            * 1.5);
        Assert.Equal(expectedBossHp, finalLevel.BossMaxHp);
        Assert.NotNull(finalLevel.BossSpawnTick);

        // —— 审计三轨（关闭局）：零 K-8 事件（零军令/零系统动作/零回充）、警报照发、占领面。
        Assert.Equal(0, segment.JournalTotalEvents);
        Assert.True(segment.AlarmEvaluations > 0, "警报通道读数流不得为空（照发面）");
        Assert.Equal(0, segment.AlarmParityViolations); // 双开关位同刻同判（无一被静默）
        Assert.Equal(0, segment.NeutralTextViolations); // 照发载荷恒中性事实句
        Assert.Equal(rows.Count + 1, segment.OccupiedCells); // 主城+N 关全占（占领序=关序，驱动侧逐关 Occupy）
        int cumulativeCaptures = Enumerable.Range(1, segment.EraIndex).Sum(era => LevelRows(era).Count);
        Assert.Equal(cumulativeCaptures, segment.ProgressionCapturedCount); // 结算链占领入库（跨章累积：E1..E{n} 全关）

        _output.WriteLine(
            $"E{segment.EraIndex} {segment.EraDir}: levels={rows.Count} wall={segment.WallMillis / 1000.0:F1}s " +
            $"alarmEval={segment.AlarmEvaluations} naturalFired={segment.NaturalAlarmFired} " +
            $"integrityMin={segment.DefenseIntegrityMin:F3}");
        foreach (V10LevelRecord level in segment.Levels)
        {
            _output.WriteLine(
                $"  {level.LevelId} {level.NameZh}: win={level.WinWaves} victoryTick={level.VictoryTick} " +
                $"({level.VictoryTick / (double)Simulation.TicksPerSecond:F0}s) towers={level.TowerCount} " +
                $"cmds={level.IssuedCommands}/{level.AppliedCommands} bossHp={level.BossMaxHp?.ToString() ?? "-"} " +
                $"objectives={level.CompletedCount} chest={level.ChestItemIds.Count}");
        }
    }

    /// <summary>共享断言：一次跃迁事务（30% 课税守恒+奇观前置+排期+单写+meta）。</summary>
    private void AssertTransition(V10EraSegment segment)
    {
        V10TransitionRecord jump = segment.Transition!;
        EraTransitionSpec spec = Transition();
        Assert.Equal(segment.EraIndex, jump.FromEra);
        Assert.Equal(segment.EraIndex + 1, jump.ToEra);

        // 奇观=真前置：未建硬拒（点名奇观行），建成后事务成功。
        Assert.Contains(jump.WonderId, jump.WonderRefusalMessage, StringComparison.Ordinal);
        Assert.Equal(
            Wonders().Single(wonder => wonder.EraIndex == segment.EraIndex).Id,
            jump.WonderId);

        // 30% 课税守恒：逐资源 carried=floor(share×old)、留存恰 70%、无用 3:1 折基金。
        Assert.All(jump.Levy, line =>
        {
            Assert.Equal((long)Math.Floor(line.OldAmount * spec.CarriedShare), line.Carried);
            Assert.Equal(line.OldAmount - line.Carried, line.Retained, 6);
            Assert.Equal(line.UsefulInNewEra ? 0 : line.Carried / spec.UselessFoldDivisor, line.FundFolded);
        });
        Assert.Equal(jump.Levy.Sum(line => line.FundFolded), jump.FundGained);

        // 仓库自动 +1（§5）+ 新时代前三关排期（target_era 行）+ campaign 单写 + 文档时代。
        Assert.Equal(jump.OldWarehouseLevel + spec.WarehouseLevelGain, jump.NewWarehouseLevel);
        Assert.Equal(
            spec.ScheduleFor(jump.ToEra).Select(slot => slot.LevelId).ToArray(),
            jump.ScheduleLevelIds.ToArray());
        Assert.Equal(1, jump.CampaignWrites); // K-7 一次写（事务唯一持久化点）
        Assert.Equal(jump.ToEra, jump.WrittenMetaEraIndex);
        Assert.True(jump.RelicCityBuildings > 0); // 跃迁快照真带旧城事实
        Assert.True(jump.DigSiteCount >= 0); // 挖掘位面（可 0=无降级废墟，如实）

        _output.WriteLine(
            $"跃迁 E{jump.FromEra}→E{jump.ToEra} 奇观={jump.WonderId}: levy=" +
            string.Join("; ", jump.Levy.Select(line =>
                $"{line.Resource} {line.OldAmount}→带{line.Carried}/留{line.Retained}" +
                (line.FundFolded > 0 ? $"折基金{line.FundFolded}" : ""))) +
            $" fundGained={jump.FundGained} 仓库{jump.OldWarehouseLevel}→{jump.NewWarehouseLevel} " +
            $"city={jump.RelicCityBuildings} digSites={jump.DigSiteCount} 拒绝面=[{jump.WonderRefusalMessage}]");
    }

    /// <summary>共享断言：章内叙事（黄金三章照常驱动——零军令依赖）。</summary>
    private void AssertNarrative(V10EraSegment segment)
    {
        V10NarrativeRecord narrative = segment.Narrative;
        StoryChapter chapter = V10FullClearChain.ChapterOf(segment.EraIndex);
        Assert.Equal(chapter.Id, narrative.ChapterId);
        Assert.Equal(chapter.LevelId, narrative.BoundLevelId);
        Assert.Equal(StoryChapter.ActCount, narrative.ActsFired); // 三幕齐（受辱→觉醒→打脸）
        Assert.All(narrative.ActTicks, tick => Assert.NotNull(tick));
        Assert.True(narrative.ActTicks[0] <= narrative.ActTicks[1] && narrative.ActTicks[1] < narrative.ActTicks[2],
            $"幕序错位 {string.Join("/", narrative.ActTicks.Select(tick => tick?.ToString() ?? "-"))}");
        Assert.True(narrative.PhaseEnded, "章末交底+卡点预告未发出（叙事未收口）");
        Assert.True(narrative.EndingLineCount >= chapter.Ending.Lines.Count + 2, "胜利拍台词=交底+剪影+威胁行");
        int maxActWave = chapter.GoldenActs.Acts
            .Where(act => act.Predicate.Kind == ActPredicateKind.WaveStart)
            .Select(act => act.Predicate.Wave ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(narrative.WaveStartBeatsFed >= maxActWave, "波拍供给须覆盖最晚幕锚");

        // 循环跨度=真波锚跨度（实拍见证）：act3−act1 == (波锚差)×波间隔（绑定关威胁，
        // §6 公式驱动；600 tick 采样滞后上界 ±60s）。§12.1 预算：E1/E3/E4 落带；
        // E2=7×240s=1680s 结构性超预算——内容面挂账（story-e2.json 波锚 2/5/9 vs
        // budget_seconds 900，REV §六登记），此处钉住违反事实：内容修正（挪锚或调
        // 预算）后本断言翻转即红，锚面随之更新（T-084 c1 先例口径——如实钉，不静默）。
        HandcraftedLevel boundRow = LevelRows(segment.EraIndex).Single(row => row.Id == chapter.LevelId);
        double interval = EraCore.Balance.Campaign.WaveIntervalSeconds(boundRow.Threat);
        int[] waveAnchors = chapter.GoldenActs.Acts
            .Where(act => act.Predicate.Kind == ActPredicateKind.WaveStart)
            .Select(act => act.Predicate.Wave ?? 0)
            .ToArray();
        int firstWave = waveAnchors.Min();
        int lastWave = waveAnchors.Max();
        double spanSeconds = (narrative.ActTicks[2]!.Value - narrative.ActTicks[0]!.Value)
            / (double)Simulation.TicksPerSecond;
        double expectedSpan = (lastWave - firstWave) * interval;
        Assert.InRange(spanSeconds, expectedSpan - 60.0, expectedSpan + 60.0);
        bool withinBudget = spanSeconds <= chapter.GoldenActs.BudgetSeconds;
        if (segment.EraIndex == 2)
        {
            Assert.False(narrative.LoopWithinBudget,
                $"E2 章波锚跨度 {spanSeconds:F0}s 超自身 budget_seconds {chapter.GoldenActs.BudgetSeconds}s——"
                + "内容挂账钉面（若此断言翻红=内容已修正，请同步更新本钉与 REV）");
        }
        else
        {
            Assert.True(narrative.LoopWithinBudget, $"循环跨度 {spanSeconds:F0}s 超预算");
        }

        _output.WriteLine(
            $"E{segment.EraIndex} 叙事: acts={narrative.ActsFired} span={spanSeconds:F0}s " +
            $"budget={chapter.GoldenActs.BudgetSeconds}s withinBudget={narrative.LoopWithinBudget} " +
            $"endingLines={narrative.EndingLineCount} beats={narrative.WaveStartBeatsFed}");
    }

    // ---- ① E1 全 5 关（含 e1-final 踏群者）→ 金字塔 → E1→E2 ------------------

    [Fact] // legal — [F-097][F-003][F-055] E1 全五关关闭局通关+金字塔跃迁放行
    public void E1_five_level_full_clear_unlocks_the_pyramid_transition()
    {
        V10EraSegment segment = V10FullClearChain.Era1.Value;

        AssertEraCleared(segment);
        AssertTransition(segment);
        AssertNarrative(segment);

        Assert.Equal("campaign:wonder-pyramid", segment.Transition!.WonderId);
        Assert.Equal(
            new[] { "campaign:level-e2-01", "campaign:level-e2-02", "campaign:level-e2-03" },
            segment.Transition.ScheduleLevelIds.ToArray());
        Assert.Single(segment.WondersBuiltAfter); // 金字塔（奇观链第 1 环）
    }

    // ---- ② E2 全 4 关（含 e2-final 铁蹄）→ 大图书馆 → E2→E3 ------------------

    [Fact] // legal — [F-097][F-003][F-011] E2 全四关关闭局通关+大图书馆跃迁放行（点经济开启章依然零系统事件）
    public void E2_four_level_full_clear_unlocks_the_great_library_transition()
    {
        V10EraSegment segment = V10FullClearChain.Era2.Value;

        AssertEraCleared(segment);
        AssertTransition(segment);
        AssertNarrative(segment);

        Assert.Equal("campaign:wonder-great-library", segment.Transition!.WonderId);
        Assert.Equal(
            new[] { "campaign:level-e3-01", "campaign:level-e3-02", "campaign:level-e3-03" },
            segment.Transition.ScheduleLevelIds.ToArray());
        Assert.Equal(2, segment.WondersBuiltAfter.Count); // 金字塔+大图书馆
    }

    // ---- ③ E3 全 5 关（含 e3-final 堡垒号）→ 世博会 → E3→E4 ------------------

    [Fact] // legal — [F-097][F-003][F-011] E3 全五关关闭局通关（过热表挂载）+世博会跃迁放行
    public void E3_five_level_full_clear_unlocks_the_world_expo_transition()
    {
        V10EraSegment segment = V10FullClearChain.Era3.Value;

        AssertEraCleared(segment);
        AssertTransition(segment);
        AssertNarrative(segment);

        Assert.Equal("campaign:wonder-expo", segment.Transition!.WonderId);
        Assert.Equal(
            new[] { "campaign:level-e4-01", "campaign:level-e4-02", "campaign:level-e4-03" },
            segment.Transition.ScheduleLevelIds.ToArray());
        Assert.Equal(3, segment.WondersBuiltAfter.Count); // 金字塔+大图书馆+世博会
    }

    // ---- ④ E4 全 5 关（含 e4-final 夜枭）→ 互联网 = 全四章通关 ---------------

    [Fact] // legal — [F-097][F-003] E4 全五关关闭局通关：夜枭击杀=全四章通关达成+互联网奇观位（四奇观全建）
    public void E4_five_level_full_clear_completes_all_four_chapters()
    {
        V10EraSegment segment = V10FullClearChain.Era4.Value;

        AssertEraCleared(segment);
        AssertNarrative(segment);

        // 全四章通关达成点：E4-final 夜枭 Boss 已死（AssertEraCleared 的场清面）+
        // 互联网奇观位落账（附录 F.3 奇观链闭环）+ 链终态=19 关全占领。
        Assert.Null(segment.Transition); // v1.0 链止于 E4（E4→E5=终章域，era5 内容未交付）
        Assert.Equal("campaign:wonder-internet", segment.WondersBuiltAfter[^1]);
        Assert.Equal(4, segment.WondersBuiltAfter.Count);
        Assert.Equal(
            new[]
            {
                "campaign:wonder-pyramid",
                "campaign:wonder-great-library",
                "campaign:wonder-expo",
                "campaign:wonder-internet",
            },
            segment.WondersBuiltAfter.ToArray()); // 奇观链全建（跃迁前置四环+收口环）
        Assert.Equal(19, segment.ProgressionCapturedCount); // 5+4+5+5 全占领
        Assert.Equal(4, segment.NextState!.EraIndex); // 链终态时代=E4

        // 四章通关 tick 表（REV 留档面）：终局关击杀时刻并列输出。
        foreach ((int eraIndex, string eraDir) in new[] { (1, "era1"), (2, "era2"), (3, "era3"), (4, "era4") })
        {
            V10EraSegment era = V10FullClearChain.Era(eraIndex);
            V10LevelRecord final = era.Levels.Single(level => level.LevelId == Transition().FinalLevels[eraIndex]);
            _output.WriteLine(
                $"终局关 E{eraIndex} ({eraDir}): {final.LevelId} victoryTick={final.VictoryTick} " +
                $"({final.VictoryTick / (double)Simulation.TicksPerSecond:F0}s) bossHp={final.BossMaxHp}");
        }

        // 耗时护栏（诚实上限断言）：单章 ≤10 分钟、全链 ≤20 分钟（实跑数字见输出与 REV）。
        Assert.All(Enumerable.Range(1, 4), eraIndex =>
            Assert.True(V10FullClearChain.Era(eraIndex).WallMillis < 600_000, $"E{eraIndex} 单章超 10 分钟"));
        Assert.True(
            Enumerable.Range(1, 4).Sum(eraIndex => V10FullClearChain.Era(eraIndex).WallMillis) < 1_200_000,
            "全四章链超 20 分钟（超长 E2E 拆分阈值已注记——见 REV）");
    }

    // ---- ⑤ 全成就：四时代枚举扫描零系统依赖 + 局内逐条授予 -------------------

    [Fact] // legal+illegal 面扫描 — [F-097] 成就度量词表无一"与系统对话"类条目（T-084 裁定扩展至四时代）
    public void Achievement_catalog_across_all_four_eras_has_no_system_dependent_entry()
    {
        ObjectivePools pools = ObjectivesData.Parse(ReadCampaignContent("objectives.json"));

        // illegal 面扫描（负空间断言）：判分度量全部是玩家行为事实。
        string[] names = Enum.GetNames<ObjectiveMetric>();
        Assert.Equal(16, names.Length);
        Assert.DoesNotContain(names, name =>
            name.Contains("Dialog", StringComparison.Ordinal)
            || name.Contains("Chat", StringComparison.Ordinal)
            || name.Contains("Order", StringComparison.Ordinal)
            || name.Contains("Persona", StringComparison.Ordinal)
            || name.Contains("Trust", StringComparison.Ordinal));

        // 引用系统用量的两条必须是节制型 at_most——两态同可得，不锁任何一边。
        foreach (ObjectiveEntry entry in pools.Strategy.Concat(pools.Challenge))
        {
            if (entry.Metric is ObjectiveMetric.SystemBuildPlanCount or ObjectiveMetric.ScrollTriggerCount)
            {
                Assert.Equal(ObjectiveComparison.AtMost, entry.Comparison);
            }
        }

        // 四个关行文件声明的目标实例全部落在两池内（四时代内容面无野生成就条目）。
        var strategyIds = pools.Strategy.Select(entry => entry.Id).ToHashSet();
        var challengeIds = pools.Challenge.Select(entry => entry.Id).ToHashSet();
        foreach (int eraIndex in Enumerable.Range(1, 4))
        {
            Assert.All(LevelRows(eraIndex), level =>
            {
                Assert.Subset(strategyIds, level.Objectives.Strategy.ToHashSet());
                Assert.Subset(challengeIds, level.Objectives.Challenge.ToHashSet());
            });
        }

        // 局内逐条授予见证（19 关全链）：首通全授（零托管参与）、判分全执行、开箱件数=档位表。
        ChestTable chestTable = ChestRewardsData.Parse(ReadCampaignContent("chests.json"));
        int totalChestItems = 0;
        foreach (int eraIndex in Enumerable.Range(1, 4))
        {
            V10EraSegment segment = V10FullClearChain.Era(eraIndex);
            Assert.All(segment.Levels, level =>
            {
                Assert.Equal(new[] { "first-clear:" + level.LevelId }, level.FirstClearGrants);
                Assert.True(level.Survived); // 生存目标=Victory 事实
                Assert.Contains(level.StrategyObjectiveId, strategyIds); // 判分目标=池内条目
                Assert.Contains(level.ChallengeObjectiveId, challengeIds);
                Assert.InRange(level.CompletedCount, 1, 3); // 生存至少 1（无静默默认）
                Assert.Equal(TripleObjectiveJudge.TierFor(level.CompletedCount), level.Tier);
                Assert.Equal(CampaignProgression.TierToChestId(level.Tier), level.ChestTierId);
                int expectedRolls = chestTable.Tiers.Single(tier => tier.Tier == level.ChestTierId).ItemRolls
                    + chestTable.Tiers.Single(tier => tier.Tier == level.ChestTierId).BonusRolls;
                Assert.Equal(expectedRolls, level.ChestItemIds.Count);
            });
            totalChestItems += segment.Levels.Sum(level => level.ChestItemIds.Count);
        }

        // 战役里程碑面：19 关全占领+装饰全入账（E4 段=链终态读数）。
        V10EraSegment endgame = V10FullClearChain.Era4.Value;
        Assert.Equal(19, endgame.ProgressionCapturedCount);
        Assert.Equal(totalChestItems, endgame.ProgressionCosmeticsCount);
    }

    // ---- ⑥ 对照组：开启系统零军令局与关闭局终态等价（E2 抽查） ----------------

    [Fact] // legal — [F-097] 军师在场（不用）终态与关闭局逐字段等价（T-084 先例缩小到 E2 一关）
    public void Control_E2_spot_check_with_zero_orders_ends_identical()
    {
        V10ControlSpotRun control = V10FullClearChain.ControlSpot.Value;
        V10LevelRecord baseline = V10FullClearChain.Era2.Value.Levels.Single(level => level.Slot == 1);

        // 开关位=开（对照锚）；军令零条、系统动作零次、系统点零消耗（era=2 点经济
        // 开启——回充到账但整局未花 1 点，WaveSpent 恒 0=零军令的账面事实）。
        Assert.Equal(PanelState.Bubble, control.PanelEndState);
        Assert.True(control.OrderInputVisible);
        Assert.Equal(0, control.JournalInputEvents);
        Assert.Equal(0, control.JournalExecutionEvents);
        Assert.True(control.JournalTotalEvents > 0); // 仅波界回充噪声（K-8 事件面照走）
        Assert.True(control.LedgerNeverSpent);
        Assert.Equal(0, control.AlarmParityViolations);

        // 关卡进度/入库/判分/开箱/首通/终局哈希逐字段等价。
        V10LevelRecord level = control.Level;
        Assert.Equal(baseline.LevelId, level.LevelId);
        Assert.Equal(baseline.EndState, level.EndState);
        Assert.Equal(baseline.VictoryTick, level.VictoryTick); // 同刻胜利
        Assert.Equal(baseline.WinWaves, level.WinWaves);
        Assert.Equal(baseline.LootLines, level.LootLines); // 资源入库等价
        Assert.Equal(baseline.FinalStateHash, level.FinalStateHash); // 终局状态哈希全等
        Assert.Equal(baseline.HashLedger, level.HashLedger); // 采样哈希账本全等
        Assert.Equal(baseline.IssuedCommands, level.IssuedCommands);
        Assert.Equal(baseline.AppliedCommands, level.AppliedCommands);
        Assert.Equal(baseline.TowerCount, level.TowerCount);
        Assert.Equal(baseline.StrategyObjectiveId, level.StrategyObjectiveId); // 判分同抽
        Assert.Equal(baseline.StrategyPassed, level.StrategyPassed);
        Assert.Equal(baseline.ChallengeObjectiveId, level.ChallengeObjectiveId);
        Assert.Equal(baseline.ChallengePassed, level.ChallengePassed);
        Assert.Equal(baseline.CompletedCount, level.CompletedCount);
        Assert.Equal(baseline.Tier, level.Tier);
        Assert.Equal(baseline.ChestTierId, level.ChestTierId);
        Assert.Equal(baseline.ChestItemIds, level.ChestItemIds); // 开箱同物
        Assert.Equal(baseline.FirstClearGrants, level.FirstClearGrants); // 首通同授

        _output.WriteLine(
            $"对照组 {control.LevelId}: input={control.JournalInputEvents} exec={control.JournalExecutionEvents} " +
            $"journalTotal={control.JournalTotalEvents} pointsMax={control.LedgerPointsMax} " +
            $"victoryTick={level.VictoryTick} stateHash={level.FinalStateHash}");
    }

    // ---- ⑦ 确定性：主链同 seed 双跑终局哈希全等 -------------------------------

    [Fact] // legal — [F-055] 同 seed+命令流整链双跑：19 关逐关指纹/跃迁课税/战役终态全等
    public void Twin_chain_double_run_is_identical_end_to_end()
    {
        var main = new[]
        {
            V10FullClearChain.Era1.Value,
            V10FullClearChain.Era2.Value,
            V10FullClearChain.Era3.Value,
            V10FullClearChain.Era4.Value,
        };
        var twin = new[]
        {
            V10FullClearChain.TwinEra1.Value,
            V10FullClearChain.TwinEra2.Value,
            V10FullClearChain.TwinEra3.Value,
            V10FullClearChain.TwinEra4.Value,
        };

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(main[i].EraIndex, twin[i].EraIndex);
            Assert.Equal(main[i].Levels.Count, twin[i].Levels.Count);
            for (int j = 0; j < main[i].Levels.Count; j++)
            {
                V10LevelRecord a = main[i].Levels[j];
                V10LevelRecord b = twin[i].Levels[j];
                Assert.Equal(a.LevelId, b.LevelId);
                Assert.Equal(a.VictoryTick, b.VictoryTick); // 同刻胜利
                Assert.Equal(a.FinalStateHash, b.FinalStateHash); // 终局哈希全等
                Assert.Equal(a.HashLedger, b.HashLedger); // 采样账本全等（防空账本假绿）
                Assert.Equal(a.LootLines, b.LootLines); // 入库行全等
                Assert.Equal((a.WinWaves, a.IssuedCommands, a.AppliedCommands, a.TowerCount, a.BossMaxHp, a.BossSpawnTick),
                    (b.WinWaves, b.IssuedCommands, b.AppliedCommands, b.TowerCount, b.BossMaxHp, b.BossSpawnTick));
                Assert.Equal((a.StrategyObjectiveId, a.StrategyPassed, a.ChallengeObjectiveId, a.ChallengePassed, a.CompletedCount),
                    (b.StrategyObjectiveId, b.StrategyPassed, b.ChallengeObjectiveId, b.ChallengePassed, b.CompletedCount));
                Assert.Equal(a.ChestItemIds, b.ChestItemIds); // 开箱同物（先定后演）
            }

            // 跃迁课税/排期/单写全等（E1~E3 三跳；E4 双侧同无跃迁）+ 战役面全等。
            if (i < 3)
            {
                Assert.NotNull(main[i].Transition);
                Assert.NotNull(twin[i].Transition);
                Assert.Equal(main[i].Transition!.Levy, twin[i].Transition!.Levy);
                Assert.Equal(main[i].Transition!.FundGained, twin[i].Transition!.FundGained);
                Assert.Equal(main[i].Transition!.ScheduleLevelIds, twin[i].Transition!.ScheduleLevelIds);
                Assert.Equal(main[i].Transition!.CampaignWrites, twin[i].Transition!.CampaignWrites);
            }
            else
            {
                Assert.Null(main[i].Transition);
                Assert.Null(twin[i].Transition);
            }

            Assert.Equal(main[i].PoolTotalsAfter, twin[i].PoolTotalsAfter);
            Assert.Equal(main[i].LedgerStateHash, twin[i].LedgerStateHash);
            Assert.Equal(main[i].FundBalanceAfter, twin[i].FundBalanceAfter);
            Assert.Equal(main[i].WondersBuiltAfter, twin[i].WondersBuiltAfter);
            Assert.Equal(main[i].OccupiedCells, twin[i].OccupiedCells);
            Assert.Equal(main[i].ProgressionCapturedCount, twin[i].ProgressionCapturedCount);
            Assert.Equal(main[i].ProgressionCosmeticsCount, twin[i].ProgressionCosmeticsCount);
            Assert.Equal(main[i].JournalTotalEvents, twin[i].JournalTotalEvents); // 双跑同样零系统事件
        }

        long totalTicks = main.Sum(segment => segment.Levels.Sum(level => level.VictoryTick));
        double totalMillis = main.Sum(segment => segment.WallMillis);
        _output.WriteLine(
            $"双跑全等：19 关 / 3 次跃迁 / ΣvictoryTick={totalTicks} / 主链墙钟={totalMillis / 1000.0:F1}s");
    }

    // ---- ⑧ 警报：双态同判边界演练 + 全链照发计数 ------------------------------

    [Fact] // legal+boundary — [F-097] 警报功能不缺失：40% 边界双态同判、35% 及以下照发且关闭态载荷中性
    public void Alarm_channel_parity_and_neutral_text_hold_across_the_chain()
    {
        // boundary 演练：完整度 100%→0% 按 5% 扫过决策核（T-075/T-084 同款）。
        for (int percent = 100; percent >= 0; percent -= 5)
        {
            double integrity = percent / 100.0;
            DefenseAlarmView offView = OffModeDowngrade.DefenseAlarmAt(false, integrity);
            DefenseAlarmView onView = OffModeDowngrade.DefenseAlarmAt(true, integrity);

            Assert.Equal(onView.Fires, offView.Fires); // 开关位不改变是否照发
            if (percent >= 40)
            {
                Assert.False(offView.Fires); // boundary — 恰 40%（及以上）不发
                Assert.Equal(string.Empty, offView.Text);
            }
            else
            {
                Assert.True(offView.Fires); // boundary — 35% 及以下照发
                Assert.Matches("^防线完整度 [0-9]+%$", offView.Text); // 中性事实句
                Assert.DoesNotContain("建议", offView.Text); // 非军师口吻
                Assert.DoesNotContain("！", offView.Text);
                Assert.NotEqual(OffModeDowngrade.PersonaAlarmLine, offView.Text);
                Assert.Equal(OffModeDowngrade.PersonaAlarmLine, onView.Text); // 开启侧=人格台词（对照锚）
            }
        }

        // 全链读数流：四章每章照发评估>0、双态零静默差异、载荷零违规；天然 fired
        // 计数如实并列（E2+ 践踏可致塔损→完整度可跌破 40%——照发即中性实证）。
        foreach (int eraIndex in Enumerable.Range(1, 4))
        {
            V10EraSegment segment = V10FullClearChain.Era(eraIndex);
            Assert.True(segment.AlarmEvaluations > 0);
            Assert.Equal(0, segment.AlarmParityViolations);
            Assert.Equal(0, segment.NeutralTextViolations);
            _output.WriteLine(
                $"E{eraIndex}: alarmEvaluations={segment.AlarmEvaluations} naturalFired={segment.NaturalAlarmFired} " +
                $"integrityMin={segment.DefenseIntegrityMin:F3}");
        }
    }
}
