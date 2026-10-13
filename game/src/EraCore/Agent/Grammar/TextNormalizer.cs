using System.Text;

namespace EraCore.Agent.Grammar;

/// <summary>
/// Surface-text normalization for the L1 military-order grammar (C-15, T-052).
/// One canonical form shared by patterns, lexicon surfaces and player input:
/// Unicode width folding (full-width → half-width via FormKC), case folding,
/// then whitespace/punctuation stripping — only ASCII letters/digits and CJK
/// ideographs survive. This is what makes 「ｂｕｉｌｄ ＴＷＯ」, "Build TWO" and
/// "build two" comparable, and why pattern templates may be written with
/// natural punctuation and spacing (both sides run through this function).
/// Pure and deterministic: same input always yields the same output.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Normalizes one order surface. Whitespace and punctuation (both ASCII
    /// and CJK forms — commas, full stops, apostrophes, colons, …) are
    /// dropped; letters are lower-cased; full-width forms are folded to
    /// half-width. The result is either empty (no keepable char) or a dense
    /// alnum+CJK string.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var folded = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            var c = char.ToLowerInvariant(ch);
            if (IsKeepable(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keepable alphabet: ASCII letters and digits (after width folding) plus
    /// CJK Unified Ideographs blocks (base + extension A) — covers numerals
    /// (一二三…/两/俩), measure words (座/个) and every tower/region surface.
    /// </summary>
    private static bool IsKeepable(char c)
        => char.IsAsciiLetterOrDigit(c)
           || ('\u4E00' <= c && c <= '\u9FFF')
           || ('\u3400' <= c && c <= '\u4DBF');
}
