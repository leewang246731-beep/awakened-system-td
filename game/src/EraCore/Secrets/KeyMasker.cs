namespace EraCore.Secrets;

/// <summary>
/// Display masking for provider API keys (C-17 / F-084; 方案 §11.6 "UI 只回显
/// 尾 4 位"). The masked form is the ONLY shape of a key that may reach UI or
/// logs: <c>sk-1234567890abcdef</c> → <c>sk-****cdef</c> (first 3 + fixed
/// filler + last 4 characters). Keys too short to hide anything behind those
/// rules collapse to a fully masked tail form.
/// </summary>
public static class KeyMasker
{
    /// <summary>Fixed filler between the visible head and tail.</summary>
    public const string Filler = "****";

    /// <summary>Number of trailing characters kept visible (§11.6).</summary>
    public const int TailLength = 4;

    /// <summary>Number of leading characters kept visible (prefix such as "sk-").</summary>
    public const int HeadLength = 3;

    /// <summary>
    /// Returns the masked display form of <paramref name="key"/>:
    /// <list type="bullet">
    /// <item>null / empty → empty string (nothing stored, nothing to show);</item>
    /// <item>length ≤ 4 → <c>****</c> (a tail of 4 would reveal everything);</item>
    /// <item>length ≤ 8 → <c>****</c> + last 4 (head dropped, tail kept);</item>
    /// <item>length &gt; 8 → first 3 + <c>****</c> + last 4.</item>
    /// </list>
    /// The method never throws and never returns more than the tail of the
    /// input.
    /// </summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (key.Length <= TailLength)
        {
            return Filler;
        }

        var tail = key[^TailLength..];
        if (key.Length <= HeadLength + TailLength + 1)
        {
            return Filler + tail;
        }

        return key[..HeadLength] + Filler + tail;
    }
}
