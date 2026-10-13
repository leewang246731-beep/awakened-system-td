using EraCore.Agent.Grammar;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] 中文数字/量词+英文数词+时长（T-052；v3.2 §11.5「中文数字/量词」）。
// qty/after 槽位的全部数值路径：两座/2 座/俩→2；三分钟→180s。
public class ChineseNumberTests
{
    [Theory] // legal
    [InlineData("两", 2)]
    [InlineData("两座", 2)]
    [InlineData("俩", 2)]
    [InlineData("2", 2)]
    [InlineData("2座", 2)]
    [InlineData("3个", 3)]
    [InlineData("三", 3)]
    [InlineData("十", 10)]
    [InlineData("十二", 12)]
    [InlineData("二十", 20)]
    [InlineData("二十三", 23)]
    [InlineData("ten", 10)]
    [InlineData("three", 3)]
    [InlineData("a", 1)]
    [InlineData("an", 1)]
    [InlineData("an座", 1)]
    public void TryParseQuantity_accepts_supported_forms(string surface, int expected)
    {
        Assert.Equal(expected, ChineseNumber.TryParseQuantity(surface));
    }

    [Theory] // boundary
    [InlineData("零", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("一", 1)]
    [InlineData("九十九", 99)]
    [InlineData("十座", 10)]
    [InlineData("999", 999)]
    [InlineData("zero", 0)]
    public void TryParseQuantity_handles_boundaries(string surface, int expected)
    {
        Assert.Equal(expected, ChineseNumber.TryParseQuantity(surface));
    }

    [Theory] // illegal：不支持的形式一律 null（调用方按未命中处理，绝不猜）
    [InlineData("")]
    [InlineData("座")]
    [InlineData("三点五")]
    [InlineData("百")]
    [InlineData("一百")]
    [InlineData("1000")]
    [InlineData("二五")]
    [InlineData("十十")]
    [InlineData("abc")]
    [InlineData("a lot")]
    [InlineData("twenty")]
    public void TryParseQuantity_rejects_unsupported_forms(string surface)
    {
        Assert.Null(ChineseNumber.TryParseQuantity(surface));
    }

    [Theory] // legal（时长：分/秒 ×中英文×数字词）
    [InlineData("三分钟", 180)]
    [InlineData("1分钟", 60)]
    [InlineData("5分钟", 300)]
    [InlineData("90秒", 90)]
    [InlineData("十秒", 10)]
    [InlineData("2秒钟", 2)]
    [InlineData("threeminutes", 180)]
    [InlineData("tenminutes", 600)]
    [InlineData("90seconds", 90)]
    [InlineData("10s", 10)]
    [InlineData("2min", 120)]
    [InlineData("fivesecs", 5)]
    public void TryParseDuration_accepts_unit_forms(string surface, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, ChineseNumber.TryParseDuration(surface));
    }

    [Theory] // illegal：缺单位或缺数字 → null（30~600s 钳制属动作层，不在解析层）
    [InlineData("")]
    [InlineData("三")]
    [InlineData("分钟")]
    [InlineData("minutes")]
    [InlineData("一百分钟")]
    [InlineData("xyminutes")]
    public void TryParseDuration_rejects_incomplete_surfaces(string surface)
    {
        Assert.Null(ChineseNumber.TryParseDuration(surface));
    }

    [Fact] // [C-15] 交替项是给 {qty}/{after} 槽位嵌入的正则源文本：非空且含数字/量词类
    public void Alternations_are_nonempty_regex_sources()
    {
        Assert.Contains("两", ChineseNumber.QuantityAlternation());
        Assert.Contains("minutes", ChineseNumber.DurationAlternation());
        Assert.Contains("{1,3}", ChineseNumber.QuantityAlternation());
    }
}
