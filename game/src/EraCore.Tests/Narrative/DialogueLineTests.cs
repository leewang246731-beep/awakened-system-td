using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-24] [F-109] T-089 台词行：气泡文本拼接（SystemPanel 字幕先例同形）、
// 人格四套封闭表映射（snarky/advisor/goofy/tsundere，只改表达不改能力）、
// 未登记人格即拒绝。
public class DialogueLineTests
{
    [Fact] // legal — 气泡文本=『说话人：内容』（字幕先例同形），cue/persona 原样携带
    public void Bubble_text_joins_speaker_and_text()
    {
        var line = new DialogueLine(
            "e1.act1.system", "系统", "叮——检测到防线完整度骤降。", null, "ding_crisis");

        Assert.Equal("系统：叮——检测到防线完整度骤降。", line.BubbleText);
        Assert.Null(line.Persona);
        Assert.Equal("ding_crisis", line.SoundCue);
    }

    [Theory] // legal — 人格四套封闭表：枚举↔snake_case 名↔中文标签往返
    [InlineData(SystemPersona.Snarky, "snarky", "毒舌")]
    [InlineData(SystemPersona.Advisor, "advisor", "参谋")]
    [InlineData(SystemPersona.Goofy, "goofy", "沙雕")]
    [InlineData(SystemPersona.Tsundere, "tsundere", "傲娇")]
    public void Persona_names_and_labels_round_trip(SystemPersona persona, string name, string zh)
    {
        Assert.Equal(persona, DialogueLine.ParsePersona(name, "inline"));
        Assert.Equal(zh, DialogueLine.PersonaZh(persona));
    }

    [Fact] // legal — 封闭表完备：内容面人格名恰为四套（F-109 首切片）
    public void The_persona_name_list_covers_exactly_four_personas()
    {
        Assert.Equal(
            new[] { "advisor", "snarky", "goofy", "tsundere" },
            DialogueLine.PersonaNames());
    }

    [Fact] // illegal — 未登记人格名：带字段路径拒绝
    public void An_unregistered_persona_name_is_rejected()
    {
        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => DialogueLine.ParsePersona("kuudere", "narrative/story-x.json"));

        Assert.Contains("未登记人格 kuudere", ex.Message);
        Assert.Contains("advisor", ex.Message); // 封闭表名列出
    }
}
