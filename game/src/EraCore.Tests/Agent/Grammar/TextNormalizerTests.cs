using EraCore.Agent.Grammar;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] L1 文法归一化（T-052）：全半角/大小写/空白标点折叠为唯一规范形。
// 归一是文法的第一段：句式模板、词典表面与玩家输入三方共用同一规范形。
public class TextNormalizerTests
{
    [Theory] // legal
    [InlineData("Build TWO arrow towers", "buildtwoarrowtowers")]
    [InlineData("守住 东门！", "守住东门")]
    [InlineData("ｂｕｉｌｄ ｔｗｏ", "buildtwo")]
    [InlineData("２座", "2座")]
    [InlineData("What's next?", "whatsnext")]
    [InlineData("东3门", "东3门")]
    [InlineData("俩", "俩")]
    public void Normalize_folds_width_case_and_punctuation(string text, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(text));
    }

    [Theory] // boundary
    [InlineData("", "")]
    [InlineData("，。！？：", "")]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("  \t\n", "")]
    [InlineData("「守住东门」", "守住东门")]
    public void Normalize_handles_boundaries(string text, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(text));
    }

    [Theory] // illegal（无 keepable 字符的输入 → 空串，由 parser 走澄清兜底）
    [InlineData("———")]
    [InlineData("···")]
    [InlineData("…")]
    public void Normalize_punctuation_only_input_yields_empty(string text)
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(text));
    }

    [Fact] // illegal：null 是程序员错误，显式拒绝
    public void Normalize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => TextNormalizer.Normalize(null!));
    }

    [Fact] // [C-15] 同输入同输出（纯函数，无隐藏状态）
    public void Normalize_is_deterministic_across_calls()
    {
        const string text = "Ｂｕｉｌｄ ｔｗｏ ａｔ ｔｈｅ ｅａｓｔ ｇａｔｅ";
        Assert.Equal(TextNormalizer.Normalize(text), TextNormalizer.Normalize(text));
    }
}
