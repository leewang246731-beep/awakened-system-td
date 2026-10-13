namespace EraCore.Agent.Grammar;

/// <summary>
/// Number and duration parsing for the L1 grammar's <c>qty</c> / <c>after</c>
/// slots (C-15, T-052): Chinese numerals with measure words (两座→2, 俩→2,
/// 十二→12), Arabic digits, English number words (two→2, ten→10, a/an→1) and
/// duration units (三分钟→180 s, 10s→10 s, threeminutes→180 s). Surfaces are
/// already TextNormalizer-normalized (no spaces). Pure table logic, no I/O.
/// Range: quantities 0..999 (CJK path composes 0..99 via 十), durations are
/// unclamped seconds — the 30~600 s clamp belongs to the SetAlarm action
/// layer (SP-2 #10), not the parser.
/// </summary>
public static class ChineseNumber
{
    private const char TenChar = '十';
    private const int TenValue = 10;
    private const int MaxQuantity = 999;

    private static readonly IReadOnlyDictionary<char, int> DigitChars = new Dictionary<char, int>
    {
        ['零'] = 0,
        ['一'] = 1,
        ['二'] = 2,
        ['两'] = 2,
        ['俩'] = 2,
        ['三'] = 3,
        ['四'] = 4,
        ['五'] = 5,
        ['六'] = 6,
        ['七'] = 7,
        ['八'] = 8,
        ['九'] = 9,
    };

    private static readonly IReadOnlyDictionary<string, int> EnglishWords = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["an"] = 1,
        ["a"] = 1,
    };

    /// <summary>Measure words absorbed into a captured quantity (SP-2 #2 qty: 中文数字/量词).</summary>
    private static readonly string[] MeasureWords = { "座", "个", "根", "处", "批" };

    /// <summary>
    /// Duration units paired as (normalized suffix, seconds factor). Longer
    /// suffixes first so "minutes" wins over "min" and "min" over "s" when
    /// splitting a surface; CJK units before their single-char prefixes
    /// (分钟 before 分, 秒钟 before 秒).
    /// </summary>
    private static readonly (string Suffix, double SecondsPerUnit)[] DurationUnits =
    {
        ("minutes", 60.0), ("minute", 60.0), ("mins", 60.0), ("min", 60.0),
        ("seconds", 1.0), ("second", 1.0), ("secs", 1.0), ("sec", 1.0), ("s", 1.0),
        ("分钟", 60.0), ("秒钟", 1.0), ("分", 60.0), ("秒", 1.0),
    };

    /// <summary>
    /// Parses a quantity surface (optional trailing measure word) into an
    /// integer. Returns null for anything outside the supported forms — the
    /// caller treats that as a non-match, never a guess.
    /// </summary>
    public static int? TryParseQuantity(string surface)
    {
        if (string.IsNullOrEmpty(surface))
        {
            return null;
        }

        var body = StripMeasureWord(surface);
        if (body.Length == 0)
        {
            return null;
        }

        if (body.All(char.IsAsciiDigit))
        {
            return int.TryParse(body, out var value) && value <= MaxQuantity ? value : null;
        }

        if (EnglishWords.TryGetValue(body, out var english))
        {
            return english;
        }

        if (!body.All(c => DigitChars.ContainsKey(c) || c == TenChar))
        {
            return null;
        }

        return ParseCjkCompound(body);
    }

    /// <summary>
    /// Parses a duration surface (number + unit, e.g. 三分钟 / 5分钟 / 90秒 /
    /// threeminutes / 10s) into unclamped seconds. Returns null when no unit
    /// suffix or no valid number part is present.
    /// </summary>
    public static int? TryParseDuration(string surface)
    {
        if (string.IsNullOrEmpty(surface))
        {
            return null;
        }

        foreach (var (suffix, secondsPerUnit) in DurationUnits)
        {
            if (!surface.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var numberPart = surface[..^suffix.Length];
            var value = TryParseQuantity(numberPart);
            if (value is null)
            {
                return null;
            }

            return (int)(value.Value * secondsPerUnit);
        }

        return null;
    }

    /// <summary>
    /// Regex alternation (source text, to be embedded in a group) matching
    /// every quantity surface: 1-3 digits, 1-3 CJK numeral chars (covers
    /// 零~十, 两/俩 and compounds like 二十三), English words ordered longest
    /// first so "an" wins over "a", plus an optional trailing measure word.
    /// </summary>
    public static string QuantityAlternation()
        => "(?:\\d{1,3}|[零一二两三四五六七八九十俩]{1,3}|ten|nine|eight|seven|six|five|four|three|two|one|zero|an|a)(?:座|个|根|处|批)?";

    /// <summary>
    /// Regex alternation matching every duration surface: number part (same
    /// vocabulary as quantities minus a/an, which reads as an article next to
    /// units) followed by a mandatory unit, longer units before shorter ones
    /// and CJK units before their single-char prefixes.
    /// </summary>
    public static string DurationAlternation()
        => "(?:\\d{1,3}|[零一二两三四五六七八九十俩]{1,3}|ten|nine|eight|seven|six|five|four|three|two|one|zero)(?:minutes?|mins?|min|seconds?|secs?|sec|s|分钟|秒钟|分|秒)";

    private static string StripMeasureWord(string body)
    {
        foreach (var measure in MeasureWords)
        {
            if (body.EndsWith(measure, StringComparison.Ordinal))
            {
                return body[..^measure.Length];
            }
        }

        return body;
    }

    /// <summary>
    /// Composes a CJK numeral compound of 0..99: [D], 十, 十D, D十, D十D.
    /// Everything else (二五, 十十, 零五, …) is invalid → null.
    /// </summary>
    private static int? ParseCjkCompound(string body)
    {
        var tenIndex = body.IndexOf(TenChar);
        if (tenIndex < 0)
        {
            return body.Length == 1 && DigitChars.TryGetValue(body[0], out var single) ? single : null;
        }

        if (body.IndexOf(TenChar, tenIndex + 1) >= 0)
        {
            return null;
        }

        var left = body[..tenIndex];
        var right = body[(tenIndex + 1)..];
        if (left.Length > 1 || right.Length > 1)
        {
            return null;
        }

        var tens = 1;
        var ones = 0;
        if (left.Length == 1 && !DigitChars.TryGetValue(left[0], out tens))
        {
            return null;
        }

        if (right.Length == 1 && !DigitChars.TryGetValue(right[0], out ones))
        {
            return null;
        }

        return (tens * TenValue) + ones;
    }
}
