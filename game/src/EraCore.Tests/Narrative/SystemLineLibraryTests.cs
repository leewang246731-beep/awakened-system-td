using EraCore.Narrative;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-24] [F-105] T-089 台词库：15 条模板装载（恰好/键唯一）、槽位解析与填充
// （缺槽拒绝/未闭合拒绝/无槽直通）、40% 警报行与 F-111 HUD 阈值联动逐字一致
// （零漂移）、cue 封闭表。
public class SystemLineLibraryTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — 15 条模板装载：恰好 15、键唯一、cue 全在封闭表内
    public void The_library_loads_exactly_fifteen_templates()
    {
        SystemLineLibrary library = NarrativeContent.SystemLines();

        Assert.Equal(15, library.Items.Count);
        Assert.Equal(library.Items.Count, library.Items.Select(template => template.Key).Distinct().Count());
        Assert.All(
            library.Items.Where(template => template.Cue is not null),
            template => Assert.Contains(template.Cue!, StoryChapter.SoundCues));
    }

    [Fact] // legal — 槽位填充：占位符全替换、多槽按名取值
    public void Resolve_fills_every_placeholder()
    {
        SystemLineLibrary library = NarrativeContent.SystemLines();

        string resolved = library.Resolve(
            "sys-detect",
            new Dictionary<string, string>
            {
                ["状态"] = "弹药断供",
                ["事件"] = "断弹危机警报",
            });

        Assert.Equal("叮！检测到宿主弹药断供，触发断弹危机警报。", resolved);
        Assert.DoesNotContain("【", resolved);
    }

    [Fact] // legal — 无槽模板直通（任务完成行没有占位符）
    public void A_slotless_template_resolves_verbatim()
    {
        SystemLineLibrary library = NarrativeContent.SystemLines();

        Assert.Equal("叮！任务完成，奖励发放中……", library.Resolve("sys-quest-done", new Dictionary<string, string>()));
    }

    [Fact] // legal — [F-105]/[F-111] 联动：40% 警报行与 HUD 警报文案逐字一致（阈值同源 0.40）
    public void The_integrity_alarm_line_is_pinned_to_the_hud_wording()
    {
        SystemLineLibrary library = NarrativeContent.SystemLines();
        SystemLineTemplate alarm = Assert.IsType<SystemLineTemplate>(
            library.Find(SystemLineLibrary.IntegrityAlarmKey));

        Assert.Equal(OffModeDowngrade.PersonaAlarmLine, alarm.Text); // 零漂移
        Assert.Equal("ding_crisis", alarm.Cue); // 危机叮
        Assert.Equal("警告！防线完整度低于 40%，建议立即加固！", OffModeDowngrade.PersonaAlarmLine);
        Assert.Equal(0.40d, OffModeDowngrade.DefenseAlarmBelow, 15); // 阈值口径同源
    }

    [Theory] // legal — §12.1 列举的句式框架五条全部在库（检测/任务发布/任务完成/40% 警报/吐槽前缀）
    [InlineData("sys-detect", "叮！检测到宿主")]
    [InlineData("sys-quest-issue", "叮！任务发布")]
    [InlineData("sys-quest-done", "叮！任务完成")]
    [InlineData("sys-alarm-integrity", "警告！防线完整度低于 40%")]
    [InlineData("sys-snark-prefix", "宿主，本系统检测到")]
    public void The_spec_listed_sentence_frames_are_all_present(string key, string prefix)
    {
        SystemLineTemplate template = Assert.IsType<SystemLineTemplate>(
            NarrativeContent.SystemLines().Find(key));

        Assert.StartsWith(prefix, template.Text);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 未知模板 key：拒绝
    public void An_unknown_key_is_rejected()
    {
        Assert.Contains(
            "未知台词模板",
            Assert.Throws<ArgumentException>(
                () => NarrativeContent.SystemLines().Resolve("sys-nonexistent", new Dictionary<string, string>())).Message);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 缺槽值：拒绝（禁留未填占位符）
    public void A_missing_slot_value_is_rejected()
    {
        SystemLineLibrary library = NarrativeContent.SystemLines();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => library.Resolve("sys-detect", new Dictionary<string, string> { ["状态"] = "x" }));

        Assert.Contains("事件", ex.Message); // 缺哪个槽点名
    }

    [Fact] // illegal — 库规模漂移：14 条/16 条都拒绝（§12.1 冻结 15 条）
    public void A_library_size_drift_is_rejected()
    {
        Assert.Contains("恰好 15 条", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(14), "inline")).Message);
        Assert.Contains("恰好 15 条", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(16), "inline")).Message);
    }

    [Fact] // illegal — 槽位未闭合/槽位名空/未登记 cue/键重复：全拒绝
    public void Template_violations_are_rejected()
    {
        Assert.Contains("未闭合", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(15, lastText: "【没关"), "inline")).Message);
        Assert.Contains("槽位名为空", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(15, lastText: "【】"), "inline")).Message);
        Assert.Contains("cue 未登记", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(15, lastCue: "ding_boom"), "inline")).Message);
        Assert.Contains("key 重复", Assert.Throws<NarrativeSchemaException>(
            () => SystemLineLibrary.Parse(LibraryJson(15, duplicate: true), "inline")).Message);
    }

    // ---- inline library scaffold ------------------------------------------

    private static string LibraryJson(int count, string? lastText = null, string? lastCue = null, bool duplicate = false)
    {
        var rows = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string key = duplicate && (i == 0 || i == count / 2) ? "sys-detect" : $"sys-x-{i}";
            string text = i == count - 1 && lastText is not null ? lastText : $"模板{i}【槽位】";
            string cue = i == count - 1 && lastCue is not null
                ? $", \"cue\": \"{lastCue}\""
                : string.Empty;
            rows.Add($"{{\"key\": \"{key}\", \"text\": \"{text}\"{cue}}}");
        }

        return $$"""
            {"content_type": "SystemLineLibrary", "items": [{{string.Join(",", rows)}}]}
            """;
    }
}
