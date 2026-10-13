using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 Key 掩码（UI 只回显尾 4 位）——矩阵：合法/边界/非法
public class KeyMaskerTests
{
    // ---------------------------------------------------------------- legal

    [Theory]
    [InlineData("sk-1234567890abcdef", "sk-****cdef")]     // 方案 §11.6 示例形态
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abc****wxyz")] // 长键：头 3 + 尾 4
    [InlineData("0123456789", "012****6789")]              // 无前缀键同样处理
    public void Mask_keeps_head3_filler_and_tail4(string key, string expected)
    {
        Assert.Equal(expected, KeyMasker.Mask(key));
    }

    [Fact]
    public void Mask_of_long_keys_never_reveals_the_middle()
    {
        // 非法面（泄漏防护）：掩码结果不得包含中段字符——对每个长度断言
        // 结果只由头 3、固定填充与尾 4 组成。
        for (var length = 9; length <= 40; length++)
        {
            var key = new string('q', length - 4) + "tail";
            var masked = KeyMasker.Mask(key);

            Assert.Matches(@"^qqq\*\*\*\*tail$", masked);
        }
    }

    // -------------------------------------------------------------- boundary

    [Theory]
    [InlineData(null, "")]            // 未存键 → UI 无可回显
    [InlineData("", "")]              // 空键同上
    [InlineData("abcd", "****")]      // 恰 4 位：尾 4 即全量 → 全遮
    [InlineData("abcde", "****bcde")] // 5 位：去头留尾
    [InlineData("abcdefgh", "****efgh")] // 8 位：头 3+尾 4 会重叠 → 仍只留尾
    [InlineData("abcdefghi", "abc****fghi")] // 9 位：头尾首次不重叠
    public void Mask_boundary_lengths(string? key, string expected)
    {
        Assert.Equal(expected, KeyMasker.Mask(key));
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Mask_never_returns_the_input_unchanged()
    {
        // 非法面：任何非空键位经掩码后必须与原文不同（掩码即遮蔽）。
        // 例外说明：输入恰为 "****" 时映射到自身，属掩码字面量碰撞，
        // 不在本用例取值范围。
        foreach (var key in new[] { "a", "abc", "abcde", "sk-1234567890abcdef" })
        {
            Assert.NotEqual(key, KeyMasker.Mask(key));
        }
    }

    [Fact]
    public void Mask_output_length_is_bounded_by_the_display_rule()
    {
        // 边界：无论键多长，掩码结果长度恒为 3+4+4（头 3+填充 4+尾 4）
        // 或更短——UI 回显宽度因此有上界。
        var longKey = new string('k', 512);
        Assert.Equal(11, KeyMasker.Mask(longKey).Length);
        Assert.Equal("kkk****" + longKey[^4..], KeyMasker.Mask(longKey));
    }
}
