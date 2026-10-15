using EraCore.Cinematics;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] [T-152·31 号 §三（视觉扩充批次不占特性序）] 三新动词（image_card/video_frame/palette_shift）：
// 封闭动词表解析（素材键/资产 id/六时代键/可跳过位/节拍规则逐项正反例）、
// 时间轴纯数据效果与节奏、零污染契约（三新步跳过=同终态命令集·零世界写）、
// 时代跃迁 5 条+终章内容脚本（受保护演出+E6→E1 补色回环）、transition 拍
// 路由（会话内单发）、DialogueLine 配图位（story 两章 15 行插画键）。
public class VisualExpansionTests
{
    // ---- ② 三新动词：解析 ------------------------------------------------

    private const string ScriptImageCard =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"image_card","image":"story-e1-act2-awakening-1","duration_ticks":45}]}""";

    private const string ScriptImageCardBlank =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"image_card","image":" ","duration_ticks":45}]}""";

    private const string ScriptImageCardZeroDuration =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"image_card","image":"story-e1-tease","duration_ticks":0}]}""";

    private const string ScriptVideoFrameFull =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"video_frame","asset":"A-02","skippable":false,"duration_ticks":602}]}""";

    private const string ScriptVideoFrameDefaultSkippable =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"video_frame","asset":"A-02","duration_ticks":602}]}""";

    private const string ScriptVideoFrameBlankAsset =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"video_frame","asset":" ","duration_ticks":10}]}""";

    private const string ScriptPaletteShift =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"palette_shift","from_era":"e6","to_era":"e1","duration_ticks":90}]}""";

    private const string ScriptPaletteShiftBadFrom =
        """{"$schema":"eratd","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"palette_shift","from_era":"e7","to_era":"e1","duration_ticks":90}]}""";

    private const string ScriptPaletteShiftBadTo =
        """{"$schema":"eratd","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"palette_shift","from_era":"e1","to_era":"远古","duration_ticks":90}]}""";

    private const string ScriptThreeNewSteps =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[{"kind":"image_card","image":"story-e1-tease","duration_ticks":5},{"kind":"video_frame","asset":"A-01","duration_ticks":20},{"kind":"palette_shift","from_era":"e1","to_era":"e2","duration_ticks":10},{"kind":"checkpoint","id":"end"}]}""";

    [Fact] // legal — image_card：素材键+节拍入步（T-152）
    public void ImageCard_parses_with_key_and_duration()
    {
        CinematicScript script = CinematicScript.Parse(ScriptImageCard, "t");

        var card = Assert.IsType<CinematicStep.ImageCard>(script.Steps[0]);
        Assert.Equal("story-e1-act2-awakening-1", card.ImageKey);
        Assert.Equal(45, card.DurationTicks);
    }

    [Fact] // illegal — image_card.image 空白拒绝
    public void ImageCard_blank_image_is_rejected()
    {
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(ScriptImageCardBlank, "t"));
    }

    [Fact] // illegal — image_card 零节拍拒绝（节拍字段必填纪律同 speak）
    public void ImageCard_zero_duration_is_rejected()
    {
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(ScriptImageCardZeroDuration, "t"));
    }

    [Fact] // legal — video_frame：资产 id+可跳过位+回退节拍
    public void VideoFrame_parses_with_skippable_slot()
    {
        CinematicScript script = CinematicScript.Parse(ScriptVideoFrameFull, "t");

        var frame = Assert.IsType<CinematicStep.VideoFrame>(script.Steps[0]);
        Assert.Equal("A-02", frame.AssetId);
        Assert.False(frame.Skippable);
        Assert.Equal(602, frame.DurationTicks);
    }

    [Fact] // legal — video_frame.skippable 缺省=true（可跳过位默认开）
    public void VideoFrame_skippable_defaults_to_true()
    {
        CinematicScript script = CinematicScript.Parse(ScriptVideoFrameDefaultSkippable, "t");

        Assert.True(Assert.IsType<CinematicStep.VideoFrame>(script.Steps[0]).Skippable);
    }

    [Fact] // illegal — video_frame.asset 空白拒绝
    public void VideoFrame_blank_asset_is_rejected()
    {
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(ScriptVideoFrameBlankAsset, "t"));
    }

    [Fact] // legal — palette_shift：六时代封闭键 e1~e6
    public void PaletteShift_parses_with_closed_era_keys()
    {
        CinematicScript script = CinematicScript.Parse(ScriptPaletteShift, "t");

        var shift = Assert.IsType<CinematicStep.PaletteShift>(script.Steps[0]);
        Assert.Equal("e6", shift.FromEra);
        Assert.Equal("e1", shift.ToEra);
        Assert.Equal(90, shift.DurationTicks);
    }

    [Fact] // illegal — palette_shift 两端时代都必须在封闭表内
    public void PaletteShift_unknown_era_is_rejected_on_both_sides()
    {
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(ScriptPaletteShiftBadFrom, "t"));
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(ScriptPaletteShiftBadTo, "t"));
    }

    [Fact] // legal — 封闭动词表=16（13 既有演出动词+T-152 三新步；checkpoint=标记不计入）
    public void Closed_verb_table_has_sixteen_kinds()
    {
        Assert.Equal(16, CinematicScript.StepKinds.Count);
        Assert.Contains("image_card", CinematicScript.StepKinds);
        Assert.Contains("video_frame", CinematicScript.StepKinds);
        Assert.Contains("palette_shift", CinematicScript.StepKinds);
        Assert.DoesNotContain("checkpoint", CinematicScript.StepKinds);
    }

    // ---- ② 三新步：时间轴效果与零污染 -----------------------------------

    [Fact] // legal — 三新步按激活拍发纯数据效果；节拍倒数；过期后续步激活
    public void Three_new_steps_emit_data_effects_on_their_rhythm()
    {
        var timeline = new CinematicTimeline(CinematicScript.Parse(ScriptThreeNewSteps, "t"));

        var start = timeline.Start();
        var card = Assert.Single(start.OfType<CinematicEffect.ImageCardShown>());
        Assert.Equal("story-e1-tease", card.ImageKey);
        Assert.Equal(5, card.DurationTicks);

        // 倒数 5 拍后 video_frame 激活（激活拍+5 倒数拍）
        CinematicEffect.VideoFrameStarted? frame = null;
        for (int i = 0; i < 6 && frame is null; i++)
        {
            frame = timeline.Tick().OfType<CinematicEffect.VideoFrameStarted>().FirstOrDefault();
        }

        Assert.NotNull(frame);
        Assert.Equal("A-01", frame!.AssetId);
        Assert.True(frame.Skippable);

        // video_frame 20 拍后 palette_shift 激活
        CinematicEffect.PaletteShiftStarted? shift = null;
        for (int i = 0; i < 22 && shift is null; i++)
        {
            shift = timeline.Tick().OfType<CinematicEffect.PaletteShiftStarted>().FirstOrDefault();
        }

        Assert.NotNull(shift);
        Assert.Equal("e1", shift!.FromEra);
        Assert.Equal("e2", shift.ToEra);

        // checkpoint 即时步：激活拍自完成
        for (int i = 0; i < 12 && timeline.Phase != CinematicPhase.Completed; i++)
        {
            timeline.Tick();
        }

        Assert.Equal(CinematicPhase.Completed, timeline.Phase);
    }

    [Fact] // legal — 零污染：video_frame 中途跳过=同终态命令集（零世界写不变）
    public void Skipping_mid_video_lands_on_the_same_terminal_state()
    {
        CinematicScript script = CinematicScript.Parse(ScriptThreeNewSteps, "t");
        var full = new CinematicTimeline(script);
        _ = full.Start();
        while (full.Phase != CinematicPhase.Completed)
        {
            full.Tick();
        }

        var skipped = new CinematicTimeline(script);
        _ = skipped.Start();
        skipped.Tick(); // video_frame 前的 image_card 激活后立即跳过
        IReadOnlyList<CinematicEffect> effects = skipped.Skip();

        Assert.Contains(effects, effect => effect is CinematicEffect.Skipped);
        Assert.Equal(full.TerminalCommands, skipped.TerminalCommands);
        Assert.Empty(skipped.TakePendingCommands()); // 零世界写
    }

    // ---- ③d 时代跃迁/终章内容脚本 ----------------------------------------

    public static TheoryData<string, string> TransitionPairs => new()
    {
        { "e1", "e2" },
        { "e2", "e3" },
        { "e3", "e4" },
        { "e4", "e5" },
        { "e5", "e6" },
    };

    [Theory] // legal — 五条跃迁脚本真装载：video_frame(跃迁片)+palette_shift(时代端点) 同拍面
    [MemberData(nameof(TransitionPairs))]
    public void Transition_scripts_carry_video_then_palette_shift(string fromEra, string toEra)
    {
        CinematicScript script = CinematicContent.Transition(fromEra, toEra);

        Assert.Null(script.HookId); // manual/campaign 通道（无关卡钩子行）
        var video = Assert.IsType<CinematicStep.VideoFrame>(script.Steps[1]);
        Assert.StartsWith("A-0", video.AssetId);
        Assert.True(video.Skippable);
        var shift = Assert.IsType<CinematicStep.PaletteShift>(script.Steps[2]);
        Assert.Equal(fromEra, shift.FromEra);
        Assert.Equal(toEra, shift.ToEra);
    }

    [Fact] // legal — 终章脚本：受保护演出（minimum_play_ticks）+E6→E1 补色回环
    public void Finale_script_is_protected_and_closes_the_color_loop()
    {
        CinematicScript finale = CinematicContent.Finale();

        Assert.NotNull(finale.MinimumPlayTicks);
        var video = Assert.IsType<CinematicStep.VideoFrame>(finale.Steps[1]);
        Assert.Equal("A-07", video.AssetId);
        var shift = Assert.IsType<CinematicStep.PaletteShift>(finale.Steps[2]);
        Assert.Equal("e6", shift.FromEra); // 星门靛蓝
        Assert.Equal("e1", shift.ToEra);   // 褪回晨曦赭金（§1.3 首尾回环）

        var timeline = new CinematicTimeline(finale);
        _ = timeline.Start();
        Assert.Throws<InvalidOperationException>(() => timeline.Skip()); // 不可全跳
    }

    // ---- ③d transition 拍路由 ---------------------------------------------

    private const string TransitionHookJson =
        """{"items":[{"id":"campaign:level-e9-01","cinematic_hooks":[{"id":"hook-e9-transition","trigger":{"beat":"transition"}}]}]}""";

    [Fact] // legal — transition 拍入封闭表：钩子行解析+路由单发
    public void Transition_beat_parses_and_routes_once()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            TransitionHookJson, "t");
        Assert.Equal(CinematicBeat.Transition, Assert.Single(rows).Beat);

        CinematicScript transition = CinematicContent.Transition("e1", "e2");
        var router = new CinematicHookRouter(
            new[] { new CinematicHookRow("hook-e9-transition", CinematicBeat.Transition, null) },
            new[] { transition with { HookId = "hook-e9-transition" } });

        Assert.Equal(transition.Id, Assert.Single(router.OnTransition()));
        Assert.Empty(router.OnTransition()); // 会话内单发
        Assert.Empty(router.OnLevelStart());
        Assert.Empty(router.OnVictory());
    }

    // ---- ③b DialogueLine 配图位 -------------------------------------------

    [Fact] // legal — story 内容真装载：15 行配图键跨两章（ImageKey 与图批 id 一致）
    public void Story_chapters_carry_illustration_keys_for_all_fifteen_images()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        foreach (string era in new[] { "e1", "e2" })
        {
            var chapter = EraCore.Narrative.StoryChapter.Parse(
                CinematicContent.ContentFile($"narrative/story-{era}.json"), $"story-{era}.json");
            int keyed = chapter.GoldenActs.Acts.SelectMany(act => act.Lines)
                .Concat(chapter.Opening)
                .Concat(chapter.Ending.Lines)
                .Count(line => line.ImageKey is not null);
            Assert.True(keyed >= (era == "e1" ? 8 : 7), $"{era} 配图行 {keyed}");
        }

        // 键全部可解析到目录 story 图批
        var chapterE1 = EraCore.Narrative.StoryChapter.Parse(
            CinematicContent.ContentFile("narrative/story-e1.json"), "story-e1.json");
        Assert.All(
            chapterE1.GoldenActs.Acts.SelectMany(act => act.Lines).Concat(chapterE1.Opening),
            line => Assert.True(line.ImageKey is null || catalog.ImageByKey(line.ImageKey) is not null));
    }
}
