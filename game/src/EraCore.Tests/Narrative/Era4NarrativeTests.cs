using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Narrative;
using EraCore.Sim;
using EraCore.Tests.Cinematics;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.Era4LevelPlaythroughTests;
using static EraCore.Tests.Narrative.NarrativeContent;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-101] [F-102] [F-105] [F-106] [F-107] T-127 E4 叙事段+主线全文验收：
// 章节装载（三幕=卫星信号被嘲→电网革新→夜枭拦截名场面，波锚 2/4/5+混凝土压制
// 扮猪行+终章零的真相预告+军师私人台词层『另一个声音』双载体契约）+黄金循环在真
// L4-1 命令流上的三幕时序（tick 公式值+循环跨度 ≤900s 预算）+收束演出时长带
// [20,30]s（A-05 母带+P1 插画位接线）+E4/总盘双面字数执法+E3 钩子 E4 回收在章节
// 文本兑现+story-arc 主线全文摘要与四章/五钩互证。
public class Era4NarrativeTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era4NarrativeTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(string id) =>
        HandcraftedLevelData.Load("e4.json").TryGet(id)!;

    // ---------------------------------------------------------------- legal

    [Fact] // legal — E4 章装载：三幕（波 2 被嘲/波 4 电网革新/波 5 名场面）+扮猪行+终章预告
    public void E4_chapter_loads_with_the_golden_three_acts_and_the_pig_arc_row()
    {
        StoryChapter chapter = StoryE4();

        Assert.Equal("narrative:story-e4", chapter.Id);
        Assert.Equal("era4:era-modern", chapter.Era);
        Assert.Equal("campaign:level-e4-01", chapter.LevelId);
        Assert.Equal(3, chapter.GoldenActs.Acts.Count); // 受辱→觉醒→打脸
        Assert.Equal(900, chapter.GoldenActs.BudgetSeconds); // §12.1 循环预算
        Assert.All(
            chapter.GoldenActs.Acts,
            act => Assert.Equal(ActPredicateKind.WaveStart, act.Predicate.Kind)); // E4 无绑定演出位（era4 awakening_cinematic 全 false）→三幕全波锚
        Assert.Equal(new[] { 2, 4, 5 }, chapter.GoldenActs.Acts.Select(act => act.Predicate.Wave!.Value));

        // 信任三档齐（F.4）；人格闲聊 4×3 矩阵归 T-126（并行泳道分工）——本表仅 advisor 骨架行
        Assert.Equal(3, chapter.IntermissionPools.Count);
        foreach (TrustBadge badge in StoryChapter.TrustTiers)
        {
            IReadOnlyList<DialogueLine> pool = chapter.IntermissionPools[badge];
            Assert.True(pool.Count >= 1, $"闲聊池 {badge} 至少 1 行（装载下限）");
            Assert.All(pool, line => Assert.Equal(NarrPersona.Advisor, line.Persona));
        }

        // 胜利交底挂收束演出 id（era4 关卡行无 cinematic_hooks 位——两 id 互证见收束时长测试）
        Assert.Equal("cinematic:e4-finale", chapter.Ending.HookId);

        // 卡点预告=E5/终章未交付纯文本位；预告指向终章零的真相（初代守望者苏醒，§12.1 谱系）
        Assert.Null(chapter.Ending.Tease.NextEra);
        Assert.Null(chapter.Ending.Tease.EliteId);
        Assert.Contains("守望者", chapter.Ending.Tease.SilhouetteZh);
        Assert.Contains("门", chapter.Ending.Tease.ThreatLineZh);
        Assert.False(string.IsNullOrWhiteSpace(chapter.Ending.Tease.ThreatLineZh));

        // 真相交底：卫星应答叫出零的编号（『另一个声音』公开面）
        Assert.Contains(chapter.Ending.Lines, line => line.Text.Contains("编号"));

        // 扮猪行：L4-2 混凝土压制（era4 建造货币行跨代递进——E1 木/E2 铜/E3 钢/E4 混凝土）
        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e4-02"));
        Assert.Equal(MockArcKind.MockArcPig, arc.Kind);
        Assert.NotNull(arc.Suppression);
        Assert.Equal("era4:res-concrete", arc.Suppression.Resource);
        Assert.Equal(0.5d, arc.Suppression.Multiplier, 3);
        Assert.Equal(3, arc.Taunts.Count); // 电网监理/军代表嘲讽三拍
        Assert.Equal("gold_extra", arc.HiddenReward.ChestTier);
    }

    [Fact] // legal — 军师私人台词层（F-098『另一个声音』双载体之台词层，hooks E4 行 layer=advisor_private）：
    //        军师频道独占契约（channel=private+persona=advisor 逐行执法）+行 1 兑现 E4 钩子 teaser
    //        原句+beat=hooks 埋点关互证+P1 插画实锚
    public void The_advisor_private_layer_carries_the_other_voice_on_a_private_channel()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("story-e4.json"));
        JsonElement rows = doc.RootElement.GetProperty("advisor_private");

        Assert.Equal(3, rows.GetArrayLength()); // 频段/应答/守口三拍
        Assert.Empty(PrivateLayerViolations(rows)); // 层契约零违规（军师频道独占）

        JsonElement first = rows[0];
        Assert.Equal("campaign:level-e4-03", first.GetProperty("beat").GetString()); // beat=hooks 埋点关
        Assert.Equal(
            "campaign:level-e4-03",
            Hooks().Items[3].PlantedLevelId); // 与 hooks.json E4 行同一锚（双载体单一链面）
        Assert.Contains("那个频段，我以前用过", first.GetProperty("text").GetString()); // teaser 原句落地
        Assert.Contains("那个频段，我以前用过", Hooks().Items[3].TeaserZh); // 钩子表侧同句（互证）

        // 行 2/3 拍：应答（夜枭终局关）+守口（收束演出）——孪生碎片伏笔渐强
        Assert.Equal("campaign:level-e4-final", rows[1].GetProperty("beat").GetString());
        Assert.Contains("编号", rows[1].GetProperty("text").GetString());
        Assert.Equal("cinematic:e4-finale", rows[2].GetProperty("beat").GetString());
        Assert.Contains("另一个声音", rows[2].GetProperty("text").GetString());

        // P1 插画实锚：行 1 配图键在 visual-assets.json 目录内（hook-e4-satellite-signal 已入库）
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        Assert.NotNull(catalog.ImageByKey(first.GetProperty("image").GetString()!));

        // 埋点关=多核期解锁位（§4 E4 行：多核期（巡逻子代理；『另一个声音』钩子））
        using JsonDocument e4 = JsonDocument.Parse(ReadSibling("levels", "e4.json"));
        JsonElement level3 = e4.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "campaign:level-e4-03");
        Assert.Contains(
            "multicore_patrol",
            level3.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()));

        // 双向残卷链：E4 钩子 linked_remanent ↔ 残卷 beat_id（共用出厂编号=孪生碎片最早实锚）
        KnowledgeRemnant signal = Assert.IsType<KnowledgeRemnant>(
            Remnants().Find(Hooks().Items[3].LinkedRemnant!));
        Assert.Equal(Hooks().Items[3].Id, signal.BeatId);
        Assert.Contains("出厂编号", signal.EchoWhisperZh);
    }

    [Fact] // legal — E3 钩子的 E4 回收（recover.era=4 chapter_progress）：payoff『图纸是给零的』
    //        在 E4 开场白第三行兑现，且图纸→卫星完成载体转译（双载体之事件层）
    public void The_blueprint_hook_pays_off_in_the_e4_opening_as_a_satellite_map()
    {
        CrossEraHook blueprint = Hooks().Items[2];

        Assert.Equal(4, blueprint.Recover.Era); // 回收时代=E4
        Assert.Contains("给零的", blueprint.PayoffZh);
        Assert.Contains(
            StoryE4().Opening.Select(line => line.Text),
            text => text.Contains("给零的") && text.Contains("卫星")); // 兑现于 E4 开场白+图纸→卫星转译
        Assert.Contains("落款", StoryE4().Opening[2].Text); // 与 E3 交底『落款早了一千年』同词回扣
    }

    [Fact] // legal — 三幕时序：真 L4-1 命令流（e4.json 装配+EnergyRisk 挂载）上波锚 2/4/5 逐拍触发
    public void L4_1_command_flow_fires_the_golden_loop_within_the_fifteen_minute_budget()
    {
        const int batchTicks = 60;
        HandcraftedLevel level = Level("campaign:level-e4-01");
        var session = new LevelSession(
            new LevelSessionConfig(BuildE4Config(level), $"t127.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        var director = new NarrativeDirector(StoryE4(), TrustBadge.Trustworthy, NarrPersona.Advisor);

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, "era4"))
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

        // 幕序：受辱 ≤ 觉醒 ≤ 打脸（E4 三幕全波锚，严格递增）
        long act1 = director.Golden.TickOf("narrative:act-e4-humiliation")!.Value;
        long act2 = director.Golden.TickOf("narrative:act-e4-innovation")!.Value;
        long act3 = director.Golden.TickOf("narrative:act-e4-face-slap")!.Value;
        Assert.True(act1 < act2 && act2 < act3, $"幕序错位 {act1}/{act2}/{act3}");

        // tick 公式：grace+(wave−1)×interval（生产 WaveSpawner 实锚，非叙事自说自话）
        Assert.Equal(graceTicks + intervalTicks, act1); // 波 2 起点
        Assert.Equal(graceTicks + (3 * intervalTicks), act2); // 波 4 起点
        Assert.Equal(graceTicks + (4 * intervalTicks), act3); // 波 5 起点

        // 15 分钟循环预算（§12.1）：第三幕−第一幕=3×240s=720s ≤900s
        double loopSeconds = (act3 - act1) / (double)Simulation.TicksPerSecond;
        Assert.Equal(3 * Campaign.WaveIntervalSeconds(level.Threat), loopSeconds, 6);
        Assert.True(director.Golden.WithinBudget, $"循环跨度 {loopSeconds:F0}s 超预算");

        // 台词链：三幕台词全部下发（受辱/电网革新/名场面）
        Assert.Contains(trace, text => text.Contains("一千年还在应答")); // 受辱·零的吐槽
        Assert.Contains(trace, text => text.Contains("第一度电")); // 电网革新
        Assert.Contains(trace, text => text.Contains("名场面达成")); // 夜枭拦截名场面
        _output.WriteLine($"E4 golden loop: act1={act1 / 30d:F0}s act2={act2 / 30d:F0}s act3={act3 / 30d:F0}s span={loopSeconds:F0}s");
    }

    [Fact] // legal — 收束演出：20~30s 时长带+P1 插画位/A 层视频位接线+章节交底 id 互证
    public void The_e4_finale_timeline_lands_in_the_20_to_30_second_band()
    {
        CinematicScript finale = CinematicScript.Parse(
            CinematicContent.ContentFile("cinematics/e4-finale.json"), "cinematics/e4-finale.json");

        Assert.Equal("cinematic:e4-finale", finale.Id);
        Assert.Null(finale.HookId); // explicit-launch 语境（victory.json 同款，无关卡钩子行）
        Assert.Empty(finale.TerminalCommands); // 零世界命令（纯回放，零污染纪律）
        Assert.DoesNotContain(finale.Steps, step => step is CinematicStep.Choice);
        Assert.NotNull(finale.MinimumPlayTicks); // 保护下限声明（长按加速不可全跳）
        Assert.Contains(finale.Steps, step => step is CinematicStep.SkipPoint);

        // 时长带 [20,30]s（§12 交付规格：5 段时代收束演出 20~30s——固定节拍步求和/30Hz）
        int totalTicks = finale.Steps.Sum(StepDuration);
        double seconds = totalTicks / (double)Simulation.TicksPerSecond;
        Assert.InRange(seconds, 20.0, 30.0);

        // P1 插画位（image_card 剪影卡）+A 层视频位（video_frame 母带）
        var cards = finale.Steps.OfType<CinematicStep.ImageCard>().ToArray();
        var videos = finale.Steps.OfType<CinematicStep.VideoFrame>().ToArray();
        Assert.True(cards.Length >= 1, "收束须接 P1 插画资产位（image_card）");
        Assert.True(videos.Length >= 1, "收束须接 A 层视频资产位（video_frame）");
        Assert.All(cards, card => Assert.StartsWith("story-e4-", card.ImageKey, StringComparison.Ordinal));

        // A 层母带实锚：素材目录登记+A 层+回退拍数=母片时长×30tps 折算（transition 同式）
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        foreach (CinematicStep.VideoFrame video in videos)
        {
            VisualVideoAsset? asset = catalog.VideoById(video.AssetId);
            Assert.NotNull(asset);
            Assert.Equal("A", asset!.Layer);
            Assert.Equal(catalog.DurationTicksOf(asset), video.DurationTicks);
        }

        // 章节交底 id 互证：story-e4 ending.hook_id=本演出 id（两文件同一锚）
        Assert.Equal(StoryE4().Ending.HookId, finale.Id);

        // 收束台词与章末交底双载体：演出 speak 行兑现归档与卫星应答（『叫出了零的编号』）
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("归档") && speak.Text.Contains("卫星"));
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("编号"));
        _output.WriteLine($"E4 finale: {seconds:F2}s ({totalTicks} ticks), cards={cards.Length}, videos={videos.Length}");
    }

    [Fact] // legal — 扮猪压制装配：L4-2 初始 2800 混凝土×0.5=1400（压制参数应用于真 SimulationConfig）
    public void The_pig_arc_suppression_applies_to_the_real_level_config()
    {
        HandcraftedLevel level = Level("campaign:level-e4-02");
        SimulationConfig config = BuildConfig(level, "era4");
        MockArcRow arc = StoryE4().MockArcFor("campaign:level-e4-02")!;

        double baseStock = config.InitialStock.Single(
            pair => pair.Key.Value == arc.Suppression!.Resource).Value;
        Assert.Equal(2800d, baseStock, 6); // 关卡行实锚

        var suppressed = new List<KeyValuePair<ResourceKey, double>>(
            config.InitialStock.Select(pair =>
                pair.Key.Value == arc.Suppression!.Resource
                    ? new KeyValuePair<ResourceKey, double>(pair.Key, MockArcRulebook.SuppressedStock(pair.Value, arc))
                    : pair));
        Assert.Equal(1400d, suppressed.Single(pair => pair.Key.Value == arc.Suppression!.Resource).Value, 6);

        // 低配阈值=命令流塔数（先定后演：e4-02 build-tower 计数与 low_config 同源）
        int towersInScript = level.CommandScript.Count(step => step.Verb == "build-tower");
        Assert.Equal(towersInScript, arc.LowConfig.MaxTowers);
    }

    [Fact] // legal — 文本量执法双面：E4 章 ≤4000 字（章份额）+叙事总盘 ≤2 万字（F-106 主线预算，
    //        T-127 执法更新口径：+story-e4+story-arc，六新承载字段计入）
    public void The_e4_chapter_and_the_whole_corpus_stay_within_their_budgets()
    {
        int e4Chars = CountNarrativeChars("story-e4.json");
        Assert.InRange(e4Chars, 1, 4000);

        int corpus = CountNarrativeChars(
            "story-e1.json", "story-e2.json", "story-e3.json", "story-e4.json", "story-arc.json",
            "remnants.json", "hooks.json", "system-lines.json");
        Assert.InRange(corpus, 1, 20000);
        _output.WriteLine($"story-e4.json narrative chars = {e4Chars}; corpus chars = {corpus}/20000");
    }

    [Fact] // legal — 主线全文（story-arc.json）：E1~E4 摘要与 story-e1~e4 同 id 同章名互证+
    //        钩子链五行与 hooks.json 同 id 同时代互证+状态推导（T-136 后 E5/终章章与全闭环
    //        互证移交 Era5E6NarrativeTests——本测试只锚 E1~E4 行不再钉总数）
    public void The_story_arc_digest_interlocks_with_chapters_and_hooks()
    {
        using JsonDocument arc = JsonDocument.Parse(Read("story-arc.json"));

        // 四章摘要行：era 1..4、chapter_id/title_zh 与四章装载值逐行相等、摘要/三幕/进度文本齐
        JsonElement chapters = arc.RootElement.GetProperty("chapters");
        Assert.True(chapters.GetArrayLength() >= 4); // E5/终章行随 T-136 追加（六行互证见 Era5E6NarrativeTests）
        StoryChapter[] shipped = { StoryE1(), StoryE2(), StoryE3(), StoryE4() };
        for (int i = 0; i < 4; i++)
        {
            JsonElement row = chapters[i];
            Assert.Equal(i + 1, row.GetProperty("era").GetInt32());
            Assert.Equal(shipped[i].Id, row.GetProperty("chapter_id").GetString());
            Assert.Equal(shipped[i].TitleZh, row.GetProperty("title_zh").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("summary_zh").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("acts_zh").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("truth_step_zh").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("persona_step_zh").GetString()));
        }

        // 总纲+谱系齐；谱系含 §12.1 四柱语汇（孪生碎片/初代守望者/忠诚——反转方向写死）
        Assert.False(string.IsNullOrWhiteSpace(arc.RootElement.GetProperty("masterline_zh").GetString()));
        string lineage = arc.RootElement.GetProperty("lineage_zh").GetString()!;
        Assert.Contains("孪生碎片", lineage);
        Assert.Contains("初代守望者", lineage);
        Assert.Contains("选择忠诚", lineage);

        // 钩子链：五行与 hooks.json 同 id 同 plant/recover 时代；E1~E4 行状态=recovered
        //（T-136 全链闭环后五行皆 recovered——E5 行断言归 Era5E6NarrativeTests）
        JsonElement chain = arc.RootElement.GetProperty("hook_chain");
        Assert.Equal(5, chain.GetArrayLength());
        for (int i = 0; i < 5; i++)
        {
            JsonElement row = chain[i];
            CrossEraHook hook = Hooks().Items[i];
            Assert.Equal(hook.Ordinal, row.GetProperty("ordinal").GetInt32());
            Assert.Equal(hook.Id, row.GetProperty("hook_id").GetString());
            Assert.Equal(hook.PlantedEra, row.GetProperty("plant_era").GetInt32());
            Assert.Equal(hook.Recover.Era, row.GetProperty("recover_era").GetInt32());
            if (i < 4)
            {
                Assert.Equal("recovered", row.GetProperty("state").GetString());
            }
        }
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 收束时长带边界：19s/31s 变体在带外（装载层不执法时长带——节拍自由 RL7，
    //        带由内容验收断言执法），仓库脚本在带内
    public void An_out_of_band_finale_duration_is_flagged_by_the_band_assertion()
    {
        CinematicScript nineteenSeconds = CinematicScript.Parse(FinaleJson(totalDurationTicks: 570), "inline");
        Assert.True(ShortestPlaybackSeconds(nineteenSeconds) < 20.0);

        CinematicScript thirtyOneSeconds = CinematicScript.Parse(FinaleJson(totalDurationTicks: 930), "inline");
        Assert.True(ShortestPlaybackSeconds(thirtyOneSeconds) > 30.0);

        CinematicScript repoFinale = CinematicScript.Parse(
            CinematicContent.ContentFile("cinematics/e4-finale.json"), "e4-finale.json");
        Assert.InRange(ShortestPlaybackSeconds(repoFinale), 20.0, 30.0);
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — 军师私人台词层契约违规：channel=public（频道不独占）或 persona=snarky
    //        （非军师人格）→层契约拒收（『另一个声音』只走军师频道，§12 E4 行双载体写死）
    [InlineData("public", "advisor", "channel=private")]
    [InlineData("private", "snarky", "persona=advisor")]
    public void A_private_layer_row_breaking_the_channel_contract_is_rejected(
        string channel, string persona, string hint)
    {
        string json = $$"""
            [{"key":"e4.private.x","beat":"campaign:level-e4-03","channel":"{{channel}}",
              "persona":"{{persona}}","speaker":"军师","text":"x"}]
            """;

        using JsonDocument doc = JsonDocument.Parse(json);
        IReadOnlyList<string> violations = PrivateLayerViolations(doc.RootElement);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains(hint));
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>军师私人台词层层契约（军师频道独占）：每行 channel=private 且 persona=advisor。</summary>
    private static IReadOnlyList<string> PrivateLayerViolations(JsonElement rows)
    {
        var violations = new List<string>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.GetProperty("channel").GetString() != "private")
            {
                violations.Add($"channel=private（得到 {row.GetProperty("channel").GetString()}）");
            }

            if (row.GetProperty("persona").GetString() != "advisor")
            {
                violations.Add($"persona=advisor（得到 {row.GetProperty("persona").GetString()}）");
            }
        }

        return violations;
    }

    private static double ShortestPlaybackSeconds(CinematicScript script) =>
        script.Steps.Sum(StepDuration) / (double)Simulation.TicksPerSecond;

    private static int StepDuration(CinematicStep step) => step switch
    {
        CinematicStep.CameraMove camera => camera.DurationTicks,
        CinematicStep.Highlight highlight => highlight.DurationTicks,
        CinematicStep.Speak speak => speak.DurationTicks,
        CinematicStep.Screen screen => screen.DurationTicks,
        CinematicStep.Ui ui => ui.DurationTicks,
        CinematicStep.VerdictReveal verdict => verdict.DurationTicks,
        CinematicStep.TierReveal tier => tier.DurationTicks,
        CinematicStep.WheelSpin wheel => wheel.DurationTicks,
        CinematicStep.ChestOpen chest => chest.DurationTicks,
        CinematicStep.PersonaComment comment => comment.DurationTicks,
        CinematicStep.ImageCard card => card.DurationTicks,
        CinematicStep.VideoFrame video => video.DurationTicks,
        CinematicStep.PaletteShift palette => palette.DurationTicks,
        _ => 0, // wait_event/sound/choice/checkpoint：非节拍步（阻塞/瞬时/标记）
    };

    /// <summary>收束骨架（合法 schema、节拍注入违规点）：video 段拍数可调以探时长带。</summary>
    private static string FinaleJson(int totalDurationTicks)
    {
        int videoTicks = totalDurationTicks - 15 - 90 - 75 - 75 - 15; // 其余段=仓库收束脚本同款节拍
        return $$"""
            {
              "$schema": "urn:eratd:content:cinematics:1",
              "content_type": "Cinematic",
              "id": "cinematic:e4-finale-x",
              "title_zh": "样例收束",
              "steps": [
                { "kind": "screen", "flavor": "black_fade_in", "duration_ticks": 15 },
                { "kind": "speak", "speaker": "零", "text": "叮！归档样例。", "duration_ticks": 90 },
                { "kind": "video_frame", "asset": "A-05", "skippable": true, "duration_ticks": {{videoTicks}} },
                { "kind": "image_card", "image": "story-e4-tease", "duration_ticks": 75 },
                { "kind": "speak", "speaker": "旁白", "text": "初代守望者样例。", "duration_ticks": 75 },
                { "kind": "checkpoint", "id": "x-end" },
                { "kind": "screen", "flavor": "fade_out", "duration_ticks": 15 }
              ]
            }
            """;
    }
}
