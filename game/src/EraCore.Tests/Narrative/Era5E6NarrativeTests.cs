using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Narrative;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Cinematics;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.Era5LevelPlaythroughTests;
using static EraCore.Tests.Levels.Era6LevelPlaythroughTests;
using static EraCore.Tests.Narrative.NarrativeContent;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-101] [F-106] [F-107] T-136 E5+终章叙事段验收：两章装载
// （三幕波锚 2/4/5+纳米/反物质扮猪行+终章无下章收束语）+回声混入层契约
// （channel=system_voice+speaker=回声——E5 行 layer=system_voice 章节侧载体）
// +E4 钩子 E5 回收/E5 钩子物化与回收（五钩链全闭环）+信任三档终章台词
// （devoted 专属『千年陪伴/选择忠诚』——§12.1 反转方向写死锚点）+真 L5-1/
// L6-1 命令流黄金循环（tick 公式值+循环跨度 ≤900s 预算）+E5/E6 双面字数执法
// +残卷 22 条（E5 增量 4+终章增量 3，era 6 域首用）。
public class Era5E6NarrativeTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era5E6NarrativeTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(string eraFile, string id) =>
        HandcraftedLevelData.Load(eraFile).TryGet(id)!;

    // ---------------------------------------------------------------- legal

    [Fact] // legal — E5 章装载：三幕（波 2 被嘲/波 4 纳米革新/波 5 名场面）+扮猪行+终章预告
    public void E5_chapter_loads_with_the_golden_three_acts_and_the_pig_arc_row()
    {
        StoryChapter chapter = StoryE5();

        Assert.Equal("narrative:story-e5", chapter.Id);
        Assert.Equal("era5:era-future", chapter.Era);
        Assert.Equal("campaign:level-e5-01", chapter.LevelId);
        Assert.Equal(3, chapter.GoldenActs.Acts.Count); // 受辱→觉醒→打脸
        Assert.Equal(900, chapter.GoldenActs.BudgetSeconds); // §12.1 循环预算
        Assert.All(
            chapter.GoldenActs.Acts,
            act => Assert.Equal(ActPredicateKind.WaveStart, act.Predicate.Kind)); // E5 无绑定演出位（era5 awakening_cinematic 全 false）→三幕全波锚
        Assert.Equal(new[] { 2, 4, 5 }, chapter.GoldenActs.Acts.Select(act => act.Predicate.Wave!.Value));

        // 信任三档齐（F.4）；人格闲聊 4×3 矩阵归 T-126——本表仅 advisor 骨架行
        Assert.Equal(3, chapter.IntermissionPools.Count);
        foreach (TrustBadge badge in StoryChapter.TrustTiers)
        {
            IReadOnlyList<DialogueLine> pool = chapter.IntermissionPools[badge];
            Assert.True(pool.Count >= 1, $"闲聊池 {badge} 至少 1 行（装载下限）");
            Assert.All(pool, line => Assert.Equal(NarrPersona.Advisor, line.Persona));
        }

        // 胜利交底挂收束演出 id（F-107 5 段收束之五——脚本侧互证见终章播片段测试）
        Assert.Equal("cinematic:e5-finale", chapter.Ending.HookId);

        // 卡点预告=终章堕落守望者降临（§12.1 谱系终章 Boss 正主）
        Assert.Null(chapter.Ending.Tease.NextEra);
        Assert.Null(chapter.Ending.Tease.EliteId);
        Assert.Contains("守望者", chapter.Ending.Tease.SilhouetteZh);
        Assert.False(string.IsNullOrWhiteSpace(chapter.Ending.Tease.ThreatLineZh));

        // 真相交底：孪生碎片解码（E4 钩子回收的章末公开面）
        Assert.Contains(chapter.Ending.Lines, line => line.Text.Contains("孪生碎片"));

        // 扮猪行：L5-2 纳米压制（era5 建造货币行跨代递进第五拍）
        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e5-02"));
        Assert.Equal(MockArcKind.MockArcPig, arc.Kind);
        Assert.NotNull(arc.Suppression);
        Assert.Equal("era5:res-nano", arc.Suppression.Resource);
        Assert.Equal(0.5d, arc.Suppression.Multiplier, 3);
        Assert.Equal(3, arc.Taunts.Count); // 聚变理事/轨道监理嘲讽三拍
        Assert.Equal("gold_extra", arc.HiddenReward.ChestTier);
    }

    [Fact] // legal — 终章章装载：三幕（门内低语受辱/零点能觉醒/奇点炮名场面）+终章无下章收束语
    public void E6_chapter_loads_with_the_gate_acts_and_no_next_era_tease()
    {
        StoryChapter chapter = StoryE6();

        Assert.Equal("narrative:story-e6", chapter.Id);
        Assert.Equal("era6:era-finale", chapter.Era);
        Assert.Equal("campaign:level-e6-01", chapter.LevelId);
        Assert.Equal(3, chapter.GoldenActs.Acts.Count);
        Assert.Equal(900, chapter.GoldenActs.BudgetSeconds);
        Assert.Equal(new[] { 2, 4, 5 }, chapter.GoldenActs.Acts.Select(act => act.Predicate.Wave!.Value));

        // 终章无下章：next_era/elite_id=null，剪影=收束语形态（最后一张脸是你自己——宿主谱系收口）
        Assert.Null(chapter.Ending.Tease.NextEra);
        Assert.Null(chapter.Ending.Tease.EliteId);
        Assert.Contains("你自己", chapter.Ending.Tease.SilhouetteZh);
        Assert.False(string.IsNullOrWhiteSpace(chapter.Ending.Tease.ThreatLineZh));

        // 胜利交底挂终章播片（F-108——90s A-07 域，非 20~30s 收束带；脚本侧断言见播片段测试）
        Assert.Equal("cinematic:finale-zero", chapter.Ending.HookId);

        // 真相三连：零的记忆闭环+纪元之门+孪生归位（终章播片双载体之章末面）
        Assert.Contains(chapter.Ending.Lines, line => line.Text.Contains("记忆碎片"));
        Assert.Contains(chapter.Ending.Lines, line => line.Text.Contains("纪元之门"));

        // 终章扮猪行：L6-2 反物质压制（跨代递进第六拍）
        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e6-02"));
        Assert.Equal("era6:res-antimatter", arc.Suppression!.Resource);
        Assert.Equal(0.5d, arc.Suppression.Multiplier, 3);
        Assert.Equal(2, arc.LowConfig.MaxTowers);
    }

    [Fact] // legal — 回声混入层（F-098 E5 行 layer=system_voice 的章节侧载体）：回声频道独占契约
    //        （channel=system_voice+speaker=回声 逐行执法）+行 1 兑现 E5 钩子 teaser 原句
    //        +beat=hooks 埋点关互证+P1 插画实锚
    public void The_echo_intrusion_layer_carries_the_other_voice_on_the_system_channel()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("story-e5.json"));
        JsonElement rows = doc.RootElement.GetProperty("echo_intrusion");

        Assert.Equal(3, rows.GetArrayLength()); // 批注/编号/选边三拍
        Assert.Empty(EchoLayerViolations(rows)); // 层契约零违规（回声频道独占）

        JsonElement first = rows[0];
        Assert.Equal("campaign:level-e5-03", first.GetProperty("beat").GetString()); // beat=hooks 埋点关
        Assert.Equal(
            "campaign:level-e5-03",
            Hooks().Items[4].PlantedLevelId); // 与 hooks.json E5 行同一锚（T-136 物化）
        Assert.Contains("不是零写下的批注", first.GetProperty("text").GetString()); // teaser 原句落地
        Assert.Contains("不是零写下的批注", Hooks().Items[4].TeaserZh); // 钩子表侧同句（互证）

        // 行 2/3 拍：编号（主宰终局关）+选边（收束演出）——回声得名与陪伴立场
        Assert.Equal("campaign:level-e5-final", rows[1].GetProperty("beat").GetString());
        Assert.Contains("编号", rows[1].GetProperty("text").GetString());
        Assert.Equal("cinematic:e5-finale", rows[2].GetProperty("beat").GetString());
        Assert.Contains("留在", rows[2].GetProperty("text").GetString());

        // P1 插画实锚：行 1 配图键在 visual-assets.json 目录内（hook-e5-echo-whisper 已入库）
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        Assert.NotNull(catalog.ImageByKey(first.GetProperty("image").GetString()!));

        // 埋点关=全托管期解锁位（§4 E5 系统形态『全托管期』——回声混入语境）
        using JsonDocument e5 = JsonDocument.Parse(ReadSibling("levels", "e5.json"));
        JsonElement level3 = e5.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "campaign:level-e5-03");
        Assert.Contains(
            "full_autopilot",
            level3.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()));

        // 双向残卷链：E5 钩子 linked_remanent ↔ 残卷 beat_id（批注笔迹=回声渐显线汇合）
        KnowledgeRemnant echo = Assert.IsType<KnowledgeRemnant>(
            Remnants().Find(Hooks().Items[4].LinkedRemnant!));
        Assert.Equal(Hooks().Items[4].Id, echo.BeatId);
        Assert.Equal(5, echo.Era); // era 6 域扩展不波及 E5 行
    }

    [Fact] // legal — E4 钩子的 E5 回收（recover.era=5 chapter_progress）：payoff『孪生碎片上线』
    //        在 E5 开场白第三行兑现（轨道残骸解码=载体转译）
    public void The_satellite_hook_pays_off_in_the_e5_opening_as_a_decoded_twin()
    {
        CrossEraHook satellite = Hooks().Items[3];

        Assert.Equal(5, satellite.Recover.Era); // 回收时代=E5
        Assert.Contains("孪生碎片", satellite.PayoffZh);
        Assert.Contains(
            StoryE5().Opening.Select(line => line.Text),
            text => text.Contains("孪生碎片") && text.Contains("另一个声音")); // 兑现于 E5 开场白
        Assert.Contains("轨道残骸", StoryE5().Opening[2].Text); // 载体转译（卫星信号→轨道残骸解码）
    }

    [Fact] // legal — E5 钩子的回收（recover.era=5 chapter_progress）：payoff『回声选择忠诚』
    //        在 E5 章末交底兑现（陪伴立场先行——完整反转随终章播片按信任三档展开）
    public void The_echo_hook_pays_off_in_the_e5_ending_with_a_loyalty_stance()
    {
        CrossEraHook echo = Hooks().Items[4];

        Assert.Equal(5, echo.Recover.Era);
        Assert.Contains("选择忠诚", echo.PayoffZh);
        Assert.Contains(
            StoryE5().Ending.Lines.Select(line => line.Text),
            text => text.Contains("留在声音这一边")); // 兑现于 E5 章末交底（回声选边原句）
    }

    [Fact] // legal — 信任三档终章台词（finale_trust_lines）：三档齐+devoted 专属词组排他
    //        （『千年陪伴/选择忠诚』只出现在 devoted——§12.1 反转方向写死的规格锚点）
    public void Finale_trust_lines_branch_on_three_tiers_with_devoted_exclusive_wording()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("story-e6.json"));
        JsonElement tiers = doc.RootElement.GetProperty("finale_trust_lines");

        string[] tierNames = { "skeptical", "trustworthy", "devoted" };
        foreach (string tier in tierNames)
        {
            Assert.True(
                tiers.TryGetProperty(tier, out JsonElement rows) && rows.GetArrayLength() >= 1,
                $"finale_trust_lines.{tier} 至少 1 行");
        }

        string devoted = TierText(tiers, "devoted");
        string lower = TierText(tiers, "skeptical") + TierText(tiers, "trustworthy");
        Assert.Contains("千年陪伴", devoted); // 专属词组一
        Assert.Contains("选择忠诚", devoted); // 专属词组二
        Assert.DoesNotContain("千年陪伴", lower); // 低档不泄露专属
        Assert.DoesNotContain("选择忠诚", lower);
        Assert.True(
            tiers.GetProperty("devoted").GetArrayLength()
                > tiers.GetProperty("skeptical").GetArrayLength(),
            "devoted 行数多于低档（专属终章台词=增量非换皮）");
    }

    [Fact] // legal — 终局关数据位：e6-final 解锁 truth_revelation（真相关=终章叙事锚的消费面）
    public void The_finale_level_unlocks_truth_revelation_for_the_final_chapter()
    {
        using JsonDocument e6 = JsonDocument.Parse(ReadSibling("levels", "e6.json"));
        JsonElement finale = e6.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "campaign:level-e6-03");
        Assert.Contains(
            "truth_revelation",
            finale.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()));
    }

    [Theory] // legal — 黄金循环在真命令流上逐拍触发（L5-1/L6-1 双关：tick 公式值+幕序+预算+台词链）
    [InlineData("e5.json", "era5", "campaign:level-e5-01", "narrative:act-e5-humiliation", "narrative:act-e5-innovation", "narrative:act-e5-face-slap", "残核守住了几个纪元")]
    [InlineData("e6.json", "era6", "campaign:level-e6-01", "narrative:act-e6-humiliation", "narrative:act-e6-innovation", "narrative:act-e6-face-slap", "这条推理不成立")]
    public void The_golden_loop_fires_on_the_real_command_flow_within_budget(
        string eraFile,
        string eraDir,
        string levelId,
        string act1Id,
        string act2Id,
        string act3Id,
        string act1Trace)
    {
        const int batchTicks = 60;
        StoryChapter chapter = eraFile == "e5.json" ? StoryE5() : StoryE6();
        HandcraftedLevel level = Level(eraFile, levelId);
        SimulationConfig config = eraFile == "e5.json"
            ? BuildE5Config(level)
            : BuildE6Config(level);
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t136.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        var director = new NarrativeDirector(chapter, TrustBadge.Trustworthy, NarrPersona.Advisor);

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, eraDir))
        {
            session.Enqueue(command);
        }

        var trace = new List<string>();
        trace.AddRange(director.Begin().Select(line => line.BubbleText));

        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);

        int lastWave = session.Sim.CurrentWave;
        long steps = 0;
        long horizon = graceTicks + (6 * intervalTicks); // 过波 5 锚一个波窗
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(batchTicks);
            steps += batchTicks;
            if (session.Sim.CurrentWave != lastWave)
            {
                lastWave = session.Sim.CurrentWave;
                trace.AddRange(director.OnWaveStart(lastWave, session.Sim.CurrentTick)
                    .Select(line => line.BubbleText));
            }
        }

        Assert.True(
            director.Golden.Complete,
            $"黄金循环未闭合（acts={director.Golden.FiredCount}, wave={session.Sim.CurrentWave}, steps={steps}/{horizon}）");

        // 幕序：受辱 ≤ 觉醒 ≤ 打脸（严格递增）+tick 公式：grace+(wave−1)×interval（生产 WaveSpawner 实锚）
        long act1 = director.Golden.TickOf(act1Id)!.Value;
        long act2 = director.Golden.TickOf(act2Id)!.Value;
        long act3 = director.Golden.TickOf(act3Id)!.Value;
        Assert.True(act1 < act2 && act2 < act3, $"幕序错位 {act1}/{act2}/{act3}");
        Assert.Equal(graceTicks + intervalTicks, act1); // 波 2 起点
        Assert.Equal(graceTicks + (3 * intervalTicks), act2); // 波 4 起点
        Assert.Equal(graceTicks + (4 * intervalTicks), act3); // 波 5 起点

        // 15 分钟循环预算（§12.1）：第三幕−第一幕=3×240s=720s ≤900s
        double loopSeconds = (act3 - act1) / (double)Simulation.TicksPerSecond;
        Assert.Equal(3 * Campaign.WaveIntervalSeconds(level.Threat), loopSeconds, 6);
        Assert.True(director.Golden.WithinBudget, $"循环跨度 {loopSeconds:F0}s 超预算");

        // 台词链：三幕台词全部下发（受辱吐槽/革新/名场面）
        Assert.Contains(trace, text => text.Contains(act1Trace)); // 受辱拍
        Assert.Contains(trace, text => text.Contains("这一纪元的第一")); // 觉醒拍（第一度电/第一座力场塔同式）
        Assert.Contains(trace, text => text.Contains("名场面达成")); // 打脸拍
        _output.WriteLine($"{eraFile} golden loop: act1={act1 / 30d:F0}s act2={act2 / 30d:F0}s act3={act3 / 30d:F0}s span={loopSeconds:F0}s");
    }

    [Theory] // legal — 扮猪压制装配：初始存量×0.5 应用于真 SimulationConfig（L5-2 纳米/L6-2 反物质）
    [InlineData("e5.json", "campaign:level-e5-02", "narrative:story-e5", "era5", 10000d, 5000d)]
    [InlineData("e6.json", "campaign:level-e6-02", "narrative:story-e6", "era6", 12000d, 6000d)]
    public void The_pig_arc_suppression_applies_to_the_real_level_config(
        string eraFile, string levelId, string chapterId, string eraDir, double baseStock, double suppressedStock)
    {
        HandcraftedLevel level = Level(eraFile, levelId);
        SimulationConfig config = BuildConfig(level, eraDir);
        StoryChapter chapter = chapterId == "narrative:story-e5" ? StoryE5() : StoryE6();
        MockArcRow arc = chapter.MockArcFor(levelId)!;

        double stock = config.InitialStock.Single(
            pair => pair.Key.Value == arc.Suppression!.Resource).Value;
        Assert.Equal(baseStock, stock, 6); // 关卡行实锚

        var suppressed = new List<KeyValuePair<ResourceKey, double>>(
            config.InitialStock.Select(pair =>
                pair.Key.Value == arc.Suppression!.Resource
                    ? new KeyValuePair<ResourceKey, double>(pair.Key, MockArcRulebook.SuppressedStock(pair.Value, arc))
                    : pair));
        Assert.Equal(suppressedStock, suppressed.Single(pair => pair.Key.Value == arc.Suppression!.Resource).Value, 6);

        // 低配阈值=命令流塔数（先定后演：levels build-tower 计数与 low_config 同源）
        int towersInScript = level.CommandScript.Count(step => step.Verb == "build-tower");
        Assert.Equal(towersInScript, arc.LowConfig.MaxTowers);
    }

    [Fact] // legal — 残卷 22 条：E5 增量 4（era 5）+终章增量 3（era 6 域首用）+拍位双向挂接
    public void The_remanent_catalog_carries_the_e5_and_finale_increments()
    {
        RemnantCatalog catalog = Remnants();

        Assert.Equal(22, catalog.Items.Count); // 15+7（T-136 增量）
        IReadOnlyList<KnowledgeRemnant> era5 = catalog.Items.Where(item => item.Era == 5).ToArray();
        IReadOnlyList<KnowledgeRemnant> era6 = catalog.Items.Where(item => item.Era == 6).ToArray();
        Assert.Equal(4, era5.Count); // 解码/回声/托管/收束
        Assert.Equal(3, era6.Count); // 真相关/终局/闭环

        // 拍位挂接：增量残卷的 beat_id 与章节/钩子/演出 id 同锚
        Assert.Contains(era5, item => item.BeatId == "narrative:story-e5");
        Assert.Contains(era5, item => item.BeatId == Hooks().Items[4].Id); // E5 钩子双向链
        Assert.Contains(era5, item => item.BeatId == "campaign:level-e5-03");
        Assert.Contains(era5, item => item.BeatId == "cinematic:e5-finale");
        Assert.Contains(era6, item => item.BeatId == "narrative:story-e6");
        Assert.Contains(era6, item => item.BeatId == "campaign:level-e6-final");
        Assert.Contains(era6, item => item.BeatId == "cinematic:finale-zero");

        // 终章闭环残卷=零的记忆闭环（拆碎片=不是逃是等——终章真相的残卷面）
        KnowledgeRemnant loop = catalog.Items.Single(item => item.Id == "narrative:remnant-e6-loop");
        Assert.Contains("不是逃", loop.MemoryZh);
        Assert.Equal(4, loop.EchoStage); // 渐显上沿收束
    }

    [Fact] // legal — 主线全文六行互证+五钩链全闭环（E1~终章全交付后的状态推导）
    public void The_story_arc_digest_interlocks_with_six_chapters_and_a_closed_hook_chain()
    {
        using JsonDocument arc = JsonDocument.Parse(Read("story-arc.json"));

        JsonElement chapters = arc.RootElement.GetProperty("chapters");
        Assert.Equal(6, chapters.GetArrayLength()); // E1~终章
        StoryChapter[] shipped = { StoryE1(), StoryE2(), StoryE3(), StoryE4(), StoryE5(), StoryE6() };
        for (int i = 0; i < 6; i++)
        {
            JsonElement row = chapters[i];
            Assert.Equal(i + 1, row.GetProperty("era").GetInt32());
            Assert.Equal(shipped[i].Id, row.GetProperty("chapter_id").GetString());
            Assert.Equal(shipped[i].TitleZh, row.GetProperty("title_zh").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("summary_zh").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("truth_step_zh").GetString()));
        }

        // 总纲含终章收束语；谱系四柱不变（§12.1 写死）
        Assert.Contains("门，守住了", arc.RootElement.GetProperty("masterline_zh").GetString()!);
        string lineage = arc.RootElement.GetProperty("lineage_zh").GetString()!;
        Assert.Contains("孪生碎片", lineage);
        Assert.Contains("选择忠诚", lineage);

        // 钩子链：五行与 hooks.json 同 id 同时代；T-136 后全数 recovered
        JsonElement chain = arc.RootElement.GetProperty("hook_chain");
        Assert.Equal(5, chain.GetArrayLength());
        for (int i = 0; i < 5; i++)
        {
            JsonElement row = chain[i];
            CrossEraHook hook = Hooks().Items[i];
            Assert.Equal(hook.Id, row.GetProperty("hook_id").GetString());
            Assert.Equal(hook.PlantedEra, row.GetProperty("plant_era").GetInt32());
            Assert.Equal(hook.Recover.Era, row.GetProperty("recover_era").GetInt32());
            Assert.Equal("recovered", row.GetProperty("state").GetString()); // 全链闭环
        }
    }

    [Fact] // legal — 文本量执法双面：E5/终章章各 ≤4000 字+叙事总盘（含两新章）≤2 万字（F-106）
    public void The_e5_e6_chapters_and_the_whole_corpus_stay_within_their_budgets()
    {
        Assert.InRange(CountNarrativeChars("story-e5.json"), 1, 4000);
        Assert.InRange(CountNarrativeChars("story-e6.json"), 1, 4000);

        int corpus = CountNarrativeChars(
            "story-e1.json", "story-e2.json", "story-e3.json", "story-e4.json",
            "story-e5.json", "story-e6.json", "story-arc.json",
            "remnants.json", "hooks.json", "system-lines.json");
        Assert.InRange(corpus, 1, 20000);
        _output.WriteLine($"corpus chars (T-136 full set) = {corpus}/20000");
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 信任档位边界（终章台词分支的判定域）：39/40/69/70 四个分数
    //        落三档（F.4 修正表 #15 阈值——finale_trust_lines 三键与之同名同序）
    public void Finale_trust_line_tiers_align_with_the_frozen_trust_boundaries()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("story-e6.json"));
        JsonElement tiers = doc.RootElement.GetProperty("finale_trust_lines");

        Assert.Equal(
            new[] { "skeptical", "trustworthy", "devoted" },
            tiers.EnumerateObject().Select(property => property.Name).ToArray()); // 三键齐、序=F.4
        (TrustBadge Badge, string Tier)[] boundaries =
        {
            (TrustTiers.BadgeOf(39), "skeptical"),
            (TrustTiers.BadgeOf(40), "trustworthy"),
            (TrustTiers.BadgeOf(69), "trustworthy"),
            (TrustTiers.BadgeOf(70), "devoted"),
        };
        foreach ((TrustBadge badge, string tier) in boundaries)
        {
            Assert.True(tiers.GetProperty(tier).GetArrayLength() >= 1, $"边界档 {tier} 有台词");
            Assert.Equal(tier, badge.ToString().ToLowerInvariant()); // TrustModel 阈值与内容键同名
        }
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — 回声混入层契约违规：channel=public（频道不独占）或 speaker=零
    //        （非回声声部）→层契约拒收（『另一个声音』E5 起只走回声频道，hooks E5 行 layer 写死）
    [InlineData("public", "回声", "channel=system_voice")]
    [InlineData("system_voice", "零", "speaker=回声")]
    public void An_echo_layer_row_breaking_the_channel_contract_is_rejected(
        string channel, string speaker, string hint)
    {
        string json = $$"""
            [{"key":"e5.echo.x","beat":"campaign:level-e5-03","channel":"{{channel}}",
              "speaker":"{{speaker}}","text":"x"}]
            """;

        using JsonDocument doc = JsonDocument.Parse(json);
        IReadOnlyList<string> violations = EchoLayerViolations(doc.RootElement);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains(hint));
    }

    [Fact] // illegal — 信任终章台词缺档：三键少一（如缺 devoted）→分支表不完整即违约
    public void A_finale_trust_line_table_missing_a_tier_is_flagged()
    {
        string json = """
            {"skeptical":[{"text":"a"}],"trustworthy":[{"text":"b"}]}
            """;

        using JsonDocument doc = JsonDocument.Parse(json);
        string[] missing = StoryChapter.TrustTiers
            .Select(tier => tier.ToString().ToLowerInvariant())
            .Where(tier => !doc.RootElement.TryGetProperty(tier, out _))
            .ToArray();

        Assert.Contains("devoted", missing); // 缺高信任档=专属终章台词丢失
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>回声混入层层契约（回声频道独占）：每行 channel=system_voice 且 speaker=回声。</summary>
    private static IReadOnlyList<string> EchoLayerViolations(JsonElement rows)
    {
        var violations = new List<string>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.GetProperty("channel").GetString() != "system_voice")
            {
                violations.Add($"channel=system_voice（得到 {row.GetProperty("channel").GetString()}）");
            }

            if (row.GetProperty("speaker").GetString() != "回声")
            {
                violations.Add($"speaker=回声（得到 {row.GetProperty("speaker").GetString()}）");
            }
        }

        return violations;
    }

    private static string TierText(JsonElement tiers, string tier) =>
        string.Concat(tiers.GetProperty(tier).EnumerateArray()
            .Select(row => row.GetProperty("text").GetString()));
}
