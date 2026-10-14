namespace EraCore.Llm;

/// <summary>
/// Order-of-magnitude token estimator for context budgeting (F-090, 方案
/// §11.7 上下文裁剪): every CJK character counts as ≈1 token (中文≈1/字),
/// every four non-CJK characters as ≈1 token. Deliberately NOT a BPE
/// tokenizer — the trimmer needs a stable, conservative estimate to decide
/// which rounds to drop, never an exact invoice. Deterministic pure
/// function; the accepted error band is pinned by
/// <c>LlmTokenEstimatorTests</c> (CJK-exact, non-CJK within one quarter
/// of the character count).
/// </summary>
public static class LlmTokenEstimator
{
    /// <summary>
    /// Estimated token count of one text. Empty/null ⇒ 0. Rounding on the
    /// non-CJK part is up (conservative: a budget check rather overestimate).
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        return cjk + ((other + 3) >> 2);
    }

    /// <summary>
    /// CJK coverage for the estimator: CJK unified (+ext-A), compatibility
    /// ideographs, fullwidth forms and CJK punctuation/symbols — the script
    /// families whose providers tokenize at roughly one token per character.
    /// </summary>
    public static bool IsCjk(char ch) =>
        (ch >= '\u3000' && ch <= '\u303F')     // CJK 标点（，。：）
        || (ch >= '\u3400' && ch <= '\u4DBF')  // 扩展 A
        || (ch >= '\u4E00' && ch <= '\u9FFF')  // 统一表意文字（常用汉字）
        || (ch >= '\uF900' && ch <= '\uFAFF')  // 兼容表意文字
        || (ch >= '\uFF00' && ch <= '\uFFEF'); // 全角形式（，！？）
}
