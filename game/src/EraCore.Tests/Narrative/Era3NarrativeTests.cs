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
using static EraCore.Tests.Narrative.NarrativeContent;
using static EraCore.Tests.Levels.Era3LevelPlaythroughTests;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-101] [F-102] [F-105] [F-106] [F-107] T-120 E3 叙事段验收：
// 章节装载（三幕=工厂被嘲→蒸汽革新→守城名场面，波锚 2/4/5+信任×人格闲聊池+
// 扮猪压制行+夜枭卡点预告）+黄金循环在真 L3-1 命令流上的三幕时序（tick 公式值+
// 循环跨度 ≤900s 预算）+收束演出时长带 [20,30]s（§12 交付规格·P1 插画/A 层视频
// 资产位接线）+E3 文本量份额执法（≤4000 字，两章 8000 字预算余量口径）+钢料压制
// 对真 e3-02 配置的应用。
public class Era3NarrativeTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public Era3NarrativeTests(ITestOutputHelper output) => _output = output;

    private static HandcraftedLevel Level(string id) =>
        HandcraftedLevelData.Load("e3.json").TryGet(id)!;

    // ---------------------------------------------------------------- legal

    [Fact] // legal — E3 章装载：三幕（波 2 被嘲/波 4 蒸汽革新/波 5 名场面）+扮猪行+夜枭预告
    public void E3_chapter_loads_with_the_golden_three_acts_and_the_pig_arc_row()
    {
        StoryChapter chapter = StoryE3();

        Assert.Equal("narrative:story-e3", chapter.Id);
        Assert.Equal("era3:era-industrial", chapter.Era);
        Assert.Equal("campaign:level-e3-01", chapter.LevelId);
        Assert.Equal(3, chapter.GoldenActs.Acts.Count); // 受辱→觉醒→打脸
        Assert.Equal(900, chapter.GoldenActs.BudgetSeconds); // §12.1 循环预算
        Assert.All(
            chapter.GoldenActs.Acts,
            act => Assert.Equal(ActPredicateKind.WaveStart, act.Predicate.Kind)); // E3 无绑定演出位（era3 awakening_cinematic 全 false）→三幕全波锚
        Assert.Equal(new[] { 2, 4, 5 }, chapter.GoldenActs.Acts.Select(act => act.Predicate.Wave!.Value));

        // 信任三档 × 人格四套：每档四行齐（F.4 × F-109 矩阵满配）
        Assert.Equal(3, chapter.IntermissionPools.Count);
        foreach (TrustBadge badge in StoryChapter.TrustTiers)
        {
            IReadOnlyList<DialogueLine> pool = chapter.IntermissionPools[badge];
            Assert.Equal(4, pool.Count);
            NarrPersona?[] expected =
            {
                NarrPersona.Advisor, NarrPersona.Snarky, NarrPersona.Goofy, NarrPersona.Tsundere,
            };
            Assert.Equal(
                expected.OrderBy(persona => persona).ToArray(),
                pool.Select(line => (NarrPersona?)line.Persona).OrderBy(persona => persona).ToArray());
        }

        // 胜利交底挂收束演出 id（era3 关卡行无 cinematic_hooks 位——两 id 互证见收束时长测试）
        Assert.Equal("cinematic:e3-finale", chapter.Ending.HookId);

        // 卡点预告 E4：era4 未交付→next_era/elite_id 纯文本位；剪影=夜枭隐形类（§4 E4 行对位）
        Assert.Null(chapter.Ending.Tease.NextEra);
        Assert.Null(chapter.Ending.Tease.EliteId);
        Assert.Contains("夜枭", chapter.Ending.Tease.SilhouetteZh);
        Assert.Contains("雷达", chapter.Ending.Tease.SilhouetteZh); // E4 塔防特色（雷达塔）语汇预埋
        Assert.False(string.IsNullOrWhiteSpace(chapter.Ending.Tease.ThreatLineZh));

        // 扮猪行：L3-2 钢料压制（era3 建造货币行跨代递进——E1 木/E2 铜/E3 钢）
        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e3-02"));
        Assert.Equal(MockArcKind.MockArcPig, arc.Kind);
        Assert.NotNull(arc.Suppression);
        Assert.Equal("era3:res-steel", arc.Suppression.Resource);
        Assert.Equal(0.5d, arc.Suppression.Multiplier, 3);
        Assert.Equal(3, arc.Taunts.Count); // 监工/工头嘲讽三拍
        Assert.Equal("gold_extra", arc.HiddenReward.ChestTier);
    }

    [Fact] // legal — 三幕时序：真 L3-1 命令流（e3.json 装配+EnergyRisk 挂载）上波锚 2/4/5 逐拍触发
    public void L3_1_command_flow_fires_the_golden_loop_within_the_fifteen_minute_budget()
    {
        const int batchTicks = 60;
        HandcraftedLevel level = Level("campaign:level-e3-01");
        var session = new LevelSession(
            new LevelSessionConfig(BuildE3Config(level), $"t120.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        var director = new NarrativeDirector(StoryE3(), TrustBadge.Trustworthy, NarrPersona.Advisor);

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, "era3"))
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

        // 幕序：受辱 ≤ 觉醒 ≤ 打脸（E3 三幕全波锚，严格递增）
        long act1 = director.Golden.TickOf("narrative:act-e3-humiliation")!.Value;
        long act2 = director.Golden.TickOf("narrative:act-e3-innovation")!.Value;
        long act3 = director.Golden.TickOf("narrative:act-e3-face-slap")!.Value;
        Assert.True(act1 < act2 && act2 < act3, $"幕序错位 {act1}/{act2}/{act3}");

        // tick 公式：grace+(wave−1)×interval（生产 WaveSpawner 实锚，非叙事自说自话）
        Assert.Equal(graceTicks + intervalTicks, act1); // 波 2 起点
        Assert.Equal(graceTicks + (3 * intervalTicks), act2); // 波 4 起点
        Assert.Equal(graceTicks + (4 * intervalTicks), act3); // 波 5 起点

        // 15 分钟循环预算（§12.1）：第三幕−第一幕=3×240s=720s ≤900s
        double loopSeconds = (act3 - act1) / (double)Simulation.TicksPerSecond;
        Assert.Equal(3 * Campaign.WaveIntervalSeconds(level.Threat), loopSeconds, 6);
        Assert.True(director.Golden.WithinBudget, $"循环跨度 {loopSeconds:F0}s 超预算");

        // 台词链：三幕台词全部下发（受辱/蒸汽革新/名场面）
        Assert.Contains(trace, text => text.Contains("锅炉的压力")); // 受辱·零的吐槽
        Assert.Contains(trace, text => text.Contains("蒸汽代替臂力")); // 蒸汽革新
        Assert.Contains(trace, text => text.Contains("名场面达成")); // 守城名场面
        _output.WriteLine($"E3 golden loop: act1={act1 / 30d:F0}s act2={act2 / 30d:F0}s act3={act3 / 30d:F0}s span={loopSeconds:F0}s");
    }

    [Fact] // legal — 收束演出：20~30s 时长带+P1 插画位/A 层视频位接线+章节交底 id 互证
    public void The_e3_finale_timeline_lands_in_the_20_to_30_second_band()
    {
        CinematicScript finale = CinematicScript.Parse(
            CinematicContent.ContentFile("cinematics/e3-finale.json"), "cinematics/e3-finale.json");

        Assert.Equal("cinematic:e3-finale", finale.Id);
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
        Assert.All(cards, card => Assert.StartsWith("story-e3-", card.ImageKey, StringComparison.Ordinal));

        // A 层母带实锚：素材目录登记+A 层+回退拍数=母片时长×30tps 折算（transition 同式）
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        foreach (CinematicStep.VideoFrame video in videos)
        {
            VisualVideoAsset? asset = catalog.VideoById(video.AssetId);
            Assert.NotNull(asset);
            Assert.Equal("A", asset!.Layer);
            Assert.Equal(catalog.DurationTicksOf(asset), video.DurationTicks);
        }

        // 章节交底 id 互证：story-e3 ending.hook_id=本演出 id（两文件同一锚）
        Assert.Equal(StoryE3().Ending.HookId, finale.Id);

        // 收束台词与章末交底双载体：演出 speak 行兑现归档与夜枭剪影（卡点预告）
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("归档") && speak.Text.Contains("图纸"));
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("夜枭"));
        _output.WriteLine($"E3 finale: {seconds:F2}s ({totalTicks} ticks), cards={cards.Length}, videos={videos.Length}");
    }

    [Fact] // legal — 扮猪压制装配：L3-2 初始 2000 钢×0.5=1000（压制参数应用于真 SimulationConfig）
    public void The_pig_arc_suppression_applies_to_the_real_level_config()
    {
        HandcraftedLevel level = Level("campaign:level-e3-02");
        SimulationConfig config = BuildConfig(level, "era3");
        MockArcRow arc = StoryE3().MockArcFor("campaign:level-e3-02")!;

        double baseStock = config.InitialStock.Single(
            pair => pair.Key.Value == arc.Suppression!.Resource).Value;
        Assert.Equal(2000d, baseStock, 6); // 关卡行实锚

        var suppressed = new List<KeyValuePair<ResourceKey, double>>(
            config.InitialStock.Select(pair =>
                pair.Key.Value == arc.Suppression!.Resource
                    ? new KeyValuePair<ResourceKey, double>(pair.Key, MockArcRulebook.SuppressedStock(pair.Value, arc))
                    : pair));
        Assert.Equal(1000d, suppressed.Single(pair => pair.Key.Value == arc.Suppression!.Resource).Value, 6);

        // 低配阈值=命令流塔数（先定后演：e3-02 build-tower 计数与 low_config 同源）
        int towersInScript = level.CommandScript.Count(step => step.Verb == "build-tower");
        Assert.Equal(towersInScript, arc.LowConfig.MaxTowers);
    }

    [Fact] // legal — 文本量执法：E3 章 ≤4000 字（两章 8000 字预算余量口径，T-120 任务卡）
    public void The_e3_chapter_stays_within_its_4000_character_share()
    {
        int e3Chars = CountNarrativeChars("story-e3.json");

        Assert.InRange(e3Chars, 1, 4000);
        _output.WriteLine($"story-e3.json narrative chars = {e3Chars}");
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
            CinematicContent.ContentFile("cinematics/e3-finale.json"), "e3-finale.json");
        Assert.InRange(ShortestPlaybackSeconds(repoFinale), 20.0, 30.0);
    }

    // ---- helpers ---------------------------------------------------------

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
              "id": "cinematic:e3-finale-x",
              "title_zh": "样例收束",
              "steps": [
                { "kind": "screen", "flavor": "black_fade_in", "duration_ticks": 15 },
                { "kind": "speak", "speaker": "零", "text": "叮！归档样例。", "duration_ticks": 90 },
                { "kind": "video_frame", "asset": "A-04", "skippable": true, "duration_ticks": {{videoTicks}} },
                { "kind": "image_card", "image": "story-e3-tease", "duration_ticks": 75 },
                { "kind": "speak", "speaker": "旁白", "text": "夜枭剪影样例。", "duration_ticks": 75 },
                { "kind": "checkpoint", "id": "x-end" },
                { "kind": "screen", "flavor": "fade_out", "duration_ticks": 15 }
              ]
            }
            """;
    }
}
