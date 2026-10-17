using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Cinematics;
using EraCore.Narrative;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Tests.Narrative;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Narrative.NarrativeContent;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-107] [F-108] [F-098] T-136 终章播片验收（播片断言=任务卡判据）：
// e6-final 胜利拍钩子行→finale-zero 单发接线（CinematicHookRouter 真关卡行）
// +A-07 母带 2706 ticks/e6→e1 回环/保护下限 90 三面同构 +真相四锚词两版
// parity（关闭系统字幕版=旁白载体完整传达，R2 硬标准播片面）+信任三档键
// 与内容表互证（FinalePlayback 纯决策）+E5 收束段 20~30s 带+A-06 母带+
// 章末交底双载体。boundary=终章长片在收束带外；illegal=未知钩子认领/
// 双脚本抢同一钩子行/字幕版破载体契约。
public class FinaleCinematicsTests
{
    private readonly ITestOutputHelper _output;

    public FinaleCinematicsTests(ITestOutputHelper output) => _output = output;

    private static double Seconds(int ticks) => ticks / (double)Simulation.TicksPerSecond;

    private static int StepDuration(CinematicStep step) => step switch
    {
        CinematicStep.Speak speak => speak.DurationTicks,
        CinematicStep.Screen screen => screen.DurationTicks,
        CinematicStep.VideoFrame video => video.DurationTicks,
        CinematicStep.PaletteShift palette => palette.DurationTicks,
        CinematicStep.ImageCard card => card.DurationTicks,
        _ => 0,
    };

    private static string SpeakText(CinematicScript script) => string.Concat(
        script.Steps.OfType<CinematicStep.Speak>().Select(speak => speak.Text));

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 触发位接线：e6-final 胜利拍钩子行（真关卡文件）→finale 单发路由
    public void The_finale_hook_row_of_e6_final_routes_the_truth_performance_once()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e6"), "levels/e6.json");

        CinematicHookRow truth = Assert.Single(rows, row =>
            row.HookId == FinalePlayback.FinaleHookId);
        Assert.Equal(CinematicBeat.Victory, truth.Beat); // 胜利拍=守望者坠落时刻=真相揭示时刻
        Assert.Null(truth.Wave); // 胜利拍无波锚

        var router = new CinematicHookRouter(rows, new[] { CinematicContent.Finale() });
        Assert.Single(router.Bindings);
        Assert.Equal(FinalePlayback.OnScriptId, router.OnVictory().Single()); // 单发
        Assert.Empty(router.OnVictory()); // 二次触发静默（同 session 不重播）

        // 关闭系统字幕版不认领钩子（开关选择变体——一接入点一演出语义）
        Assert.Null(CinematicContent.FinaleOff().HookId);
    }

    [Fact] // legal — 播片断言（F-108 验收判据）：两版同构三面——A-07 母带 2706 ticks+
    //        e6→e1 回环+保护下限 90；跳过安全（无 choice/零世界命令/checkpoint 在场）
    public void Both_finale_variants_share_the_A07_tape_the_color_loop_and_the_floor()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        foreach (CinematicScript script in new[] { CinematicContent.Finale(), CinematicContent.FinaleOff() })
        {
            CinematicStep.VideoFrame video = Assert.IsType<CinematicStep.VideoFrame>(
                Assert.Single(script.Steps, step => step is CinematicStep.VideoFrame));
            Assert.Equal("A-07", video.AssetId); // 唯一长片母带（90.21s 尾帧链）
            VisualVideoAsset asset = catalog.VideoById(video.AssetId)!;
            Assert.Equal("A", asset.Layer);
            Assert.Equal(catalog.DurationTicksOf(asset), video.DurationTicks); // 2706=90.21×30 折算
            Assert.Equal(2706, video.DurationTicks);

            CinematicStep.PaletteShift shift = Assert.IsType<CinematicStep.PaletteShift>(
                Assert.Single(script.Steps, step => step is CinematicStep.PaletteShift));
            Assert.Equal("e6", shift.FromEra); // 星门靛蓝
            Assert.Equal("e1", shift.ToEra); // 褪回晨曦赭金（§1.3 首尾回环）

            Assert.Equal(90, script.MinimumPlayTicks); // 保护下限同拍（长按加速不可全跳）
            Assert.DoesNotContain(script.Steps, step => step is CinematicStep.Choice);
            Assert.Empty(script.TerminalCommands); // 零世界命令
            Assert.Contains(script.Steps, step => step is CinematicStep.SkipPoint);

            int total = script.Steps.Sum(StepDuration);
            Assert.True(total > video.DurationTicks, "总时长须盖过母带（3206 ticks=106.9s 长片）");
            _output.WriteLine($"{script.Id}: total={Seconds(total):F1}s video={Seconds(video.DurationTicks):F1}s");
        }
    }

    [Fact] // legal — 真相两载体 parity（R2 播片面）：四锚词两版逐词齐——关闭系统
    //        不掉真相；on 版=零人格台词，off 版=旁白字幕（无人格/无 ding）
    public void Truth_anchors_reach_the_player_in_both_switch_positions()
    {
        CinematicScript on = CinematicContent.Finale();
        CinematicScript off = CinematicContent.FinaleOff();

        string onText = SpeakText(on);
        string offText = SpeakText(off);
        foreach (string anchor in FinalePlayback.TruthAnchors)
        {
            Assert.Contains(anchor, onText); // 记忆碎片/孪生/忠诚/纪元之门
            Assert.Contains(anchor, offText); // 字幕版同词完整传达
        }

        // 载体契约：on=零亲述（真相线主角），off=旁白字幕（零不出声——系统关闭）
        Assert.Contains(on.Steps.OfType<CinematicStep.Speak>(), speak => speak.Speaker == "零");
        Assert.DoesNotContain(off.Steps.OfType<CinematicStep.Speak>(), speak => speak.Speaker == "零");
        Assert.All(off.Steps.OfType<CinematicStep.Speak>(), speak => Assert.Equal("旁白", speak.Speaker));
        Assert.Empty(off.Steps.OfType<CinematicStep.Sound>()); // 无 ding 音效位（中性字幕口径）
    }

    [Fact] // legal — 开关选择与信任档键（FinalePlayback 纯决策 ↔ 内容表互证）：
    //        两 id 可装载+三档键在 finale_trust_lines 逐键有行+阈值边界四分数落三档
    public void The_switch_and_trust_decisions_resolve_into_loadable_content()
    {
        Assert.Equal("cinematic:finale-zero", FinalePlayback.ScriptIdFor(systemEnabled: true));
        Assert.Equal("cinematic:finale-zero-off", FinalePlayback.ScriptIdFor(systemEnabled: false));
        Assert.Equal(CinematicContent.Finale().Id, FinalePlayback.ScriptIdFor(true));
        Assert.Equal(CinematicContent.FinaleOff().Id, FinalePlayback.ScriptIdFor(false));

        using JsonDocument doc = JsonDocument.Parse(Read("story-e6.json"));
        JsonElement tiers = doc.RootElement.GetProperty("finale_trust_lines");
        foreach (TrustBadge badge in StoryChapter.TrustTiers)
        {
            JsonElement rows = tiers.GetProperty(FinalePlayback.TrustTierKey(badge));
            Assert.True(rows.GetArrayLength() >= 1, $"终章台词档 {badge} 至少 1 行");
        }

        // 档位判定域（F.4 修正表 #15）：39/40/69/70 四分数的三档归属
        Assert.Equal("skeptical", FinalePlayback.TrustTierKey(TrustTiers.BadgeOf(39)));
        Assert.Equal("trustworthy", FinalePlayback.TrustTierKey(TrustTiers.BadgeOf(40)));
        Assert.Equal("trustworthy", FinalePlayback.TrustTierKey(TrustTiers.BadgeOf(69)));
        Assert.Equal("devoted", FinalePlayback.TrustTierKey(TrustTiers.BadgeOf(70)));
    }

    [Fact] // legal — 播片回放：终章时间轴全程放完（保护下限之上）——video/palette/
    //        speak 效果面逐位下发（播片断言的运行时面）
    public void The_finale_timeline_plays_through_with_every_truth_beat()
    {
        CinematicScript finale = CinematicContent.Finale();
        var timeline = new CinematicTimeline(finale);

        var effects = new List<CinematicEffect>(timeline.Start());
        int guard = 0;
        while (timeline.Phase != CinematicPhase.Completed && guard < 4000)
        {
            effects.AddRange(timeline.Tick());
            guard++;
        }

        Assert.Equal(CinematicPhase.Completed, timeline.Phase);
        Assert.True(timeline.ElapsedTicks >= finale.MinimumPlayTicks!.Value); // 保护下限生效
        Assert.Equal(
            2706,
            Assert.IsType<CinematicEffect.VideoFrameStarted>(
                effects.Single(effect => effect is CinematicEffect.VideoFrameStarted)).DurationTicks);
        CinematicEffect.PaletteShiftStarted shift = Assert.IsType<CinematicEffect.PaletteShiftStarted>(
            effects.Single(effect => effect is CinematicEffect.PaletteShiftStarted));
        Assert.Equal("e6", shift.FromEra);
        Assert.Equal("e1", shift.ToEra);
        Assert.Equal(3, effects.Count(effect => effect is CinematicEffect.Speak)); // 真相三连
        Assert.Contains(effects, effect => effect is CinematicEffect.SkipPointReached);
        _output.WriteLine($"finale timeline: ticks={timeline.ElapsedTicks} effects={effects.Count}");
    }

    [Fact] // legal — E5 收束段（F-107 §12 交付规格 5 段收束之五）：20~30s 带+A-06 母带
    //        实锚+章末交底 id 互证+双载体同文（归档/选边）+终章卡点预告
    public void The_e5_finale_timeline_lands_in_the_20_to_30_second_band()
    {
        CinematicScript finale = CinematicContent.E5Finale();

        Assert.Equal("cinematic:e5-finale", finale.Id);
        Assert.Null(finale.HookId); // explicit-launch 语境（e3/e4-finale 同款）
        Assert.Empty(finale.TerminalCommands);
        Assert.NotNull(finale.MinimumPlayTicks);

        int totalTicks = finale.Steps.Sum(StepDuration);
        Assert.InRange(Seconds(totalTicks), 20.0, 30.0); // §12 收束带

        // A 层母带实锚：A-06=E5→终章跃迁像素片 20.06s×30tps=602（transition 同式）
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        CinematicStep.VideoFrame video = Assert.IsType<CinematicStep.VideoFrame>(
            Assert.Single(finale.Steps, step => step is CinematicStep.VideoFrame));
        VisualVideoAsset asset = catalog.VideoById(video.AssetId)!;
        Assert.Equal("A-06", video.AssetId);
        Assert.Equal(catalog.DurationTicksOf(asset), video.DurationTicks);

        // P1 插画位：终章守望者预告卡（story-e5-tease 键位先行）
        Assert.All(
            finale.Steps.OfType<CinematicStep.ImageCard>(),
            card => Assert.StartsWith("story-e5-", card.ImageKey, StringComparison.Ordinal));

        // 章末交底 id 互证+双载体：归档行与选边行在两文件同文互证
        Assert.Equal(StoryE5().Ending.HookId, finale.Id);
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("归档") && speak.Text.Contains("回声"));
        Assert.Contains(
            finale.Steps.OfType<CinematicStep.Speak>(),
            speak => speak.Text.Contains("留在声音这一边")); // E5 钩子 payoff 演出面
        _output.WriteLine($"e5-finale: {Seconds(totalTicks):F2}s ({totalTicks} ticks)");
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 规格带分界：E5 收束在 20~30s 带内，终章播片（F-108 唯一长片）
    //        在带外（≥90s 母带）——两条交付规格（§12：5 段收束 20~30s+终章播片）不同带
    public void The_wrap_band_and_the_finale_long_form_are_two_different_bands()
    {
        double wrap = Seconds(CinematicContent.E5Finale().Steps.Sum(StepDuration));
        double finale = Seconds(CinematicContent.Finale().Steps.Sum(StepDuration));

        Assert.InRange(wrap, 20.0, 30.0);
        Assert.True(finale >= 90.0, $"终章播片须 ≥90s（得到 {finale:F1}s——A-07 母带 90.21s）");
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 未知钩子认领：脚本引用关卡未声明的钩子行→装载即拒（先落钩子行）
    public void A_script_claiming_an_undeclared_hook_is_rejected()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e6"), "levels/e6.json");
        CinematicScript imposter = CinematicScript.Parse(
            CinematicContent.ContentFile("cinematics/finale-zero.json")
                .Replace("\"campaign:hook-e6-finale-truth\"", "\"campaign:hook-e6-nonexistent\""),
            "inline");

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => new CinematicHookRouter(rows, new[] { imposter }));
        Assert.Contains("未声明的钩子", failure.Message);
    }

    [Fact] // illegal — 双脚本抢同一接入点：字幕版也挂同一钩子行→一接入点两演出即拒
    public void Two_scripts_claiming_one_hook_row_is_rejected()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e6"), "levels/e6.json");
        string off = CinematicContent.ContentFile("cinematics/finale-zero-off.json");
        CinematicScript greedy = CinematicScript.Parse(
            off.Insert(off.IndexOf("  \"title_zh\"", StringComparison.Ordinal),
                $"  \"hook\": \"{FinalePlayback.FinaleHookId}\",\n"),
            "inline");

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => new CinematicHookRouter(rows, new[] { CinematicContent.Finale(), greedy }));
        Assert.Contains("被多个脚本认领", failure.Message);
    }

    [Fact] // illegal — 字幕版载体契约违规：旁白版夹带零人格台词或 ding 音效位
    //        →关闭系统≠静默掉真相也≠偷跑人格面（两向都不许）
    public void An_off_variant_breaking_the_neutral_subtitle_contract_is_flagged()
    {
        string on = CinematicContent.ContentFile("cinematics/finale-zero.json");
        CinematicScript disguised = CinematicScript.Parse(
            on.Replace("\"id\": \"cinematic:finale-zero\"", "\"id\": \"cinematic:finale-zero-x\""),
            "inline"); // 零亲述版冒充字幕版（speaker=零+人格面仍在）

        Assert.Contains(disguised.Steps.OfType<CinematicStep.Speak>(), speak => speak.Speaker == "零");
        IReadOnlyList<string> violations = OffContractViolations(disguised);
        Assert.Contains(violations, violation => violation.Contains("speaker=零"));
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>关闭系统字幕版载体契约：全部 speak 行 speaker=旁白，且无 sound 步。</summary>
    private static IReadOnlyList<string> OffContractViolations(CinematicScript script)
    {
        var violations = new List<string>();
        foreach (CinematicStep.Speak speak in script.Steps.OfType<CinematicStep.Speak>())
        {
            if (speak.Speaker != "旁白")
            {
                violations.Add($"speaker=旁白（得到 speaker={speak.Speaker}）");
            }
        }

        if (script.Steps.OfType<CinematicStep.Sound>().Any())
        {
            violations.Add("无 sound 步（字幕版不带 ding 音效位）");
        }

        return violations;
    }
}
