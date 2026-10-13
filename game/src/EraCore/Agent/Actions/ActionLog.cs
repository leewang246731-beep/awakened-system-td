using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Actions;

/// <summary>
/// Structured diagnostics of the action registry (C-03, T-051): one
/// key=value line per registry verdict through <see cref="ILogSink"/> (a
/// separate channel from the K-8 JSONL war report — RL10). Every line is
/// secret-scrubbed BEFORE it is handed to the sink: key-like field
/// assignments, service-key tokens and long opaque blobs are replaced by a
/// fixed marker, so raw payloads never carry credentials into logs (RL5).
/// </summary>
public static partial class ActionLog
{
    /// <summary>Replacement marker for redacted content.</summary>
    public const string RedactedToken = "<redacted>";

    /// <summary>Maximum length of the payload digest embedded in a log line.</summary>
    public const int PayloadDigestMaxLength = 160;

    /// <summary>Deny line: schema gate refused an action (unknown verb, invalid fields, malformed JSON).</summary>
    public static string DenyLine(string? actionName, string reason, string? actionJson)
    {
        string name = Scrub(actionName ?? "-");
        string payload = actionJson is null ? "-" : Digest(actionJson);
        return Scrub($"action-registry verdict=denied reason={reason} action={name} payload={payload}");
    }

    /// <summary>Era-gate line: action known and valid but not unlocked in the current era.</summary>
    public static string EraLockedLine(string actionName, int unlockEra, int currentEra)
    {
        return Scrub(
            $"action-registry verdict=era-locked action={Scrub(actionName)} unlock-era={unlockEra} era={currentEra}");
    }

    /// <summary>Refusal line: valid unlocked action whose handler is not delivered yet (T-070 tier).</summary>
    public static string NotImplementedLine(string actionName, string reason)
    {
        return Scrub($"action-registry verdict=refused reason={reason} action={Scrub(actionName)}");
    }

    /// <summary>Routine line: action executed successfully.</summary>
    public static string OkLine(string actionName, int eraOrdinal)
    {
        return Scrub($"action-registry verdict=ok action={Scrub(actionName)} era={eraOrdinal}");
    }

    /// <summary>
    /// Payload digest: collapses whitespace, truncates to
    /// <see cref="PayloadDigestMaxLength"/> characters, then scrubs — in
    /// that order, so a secret cannot survive by straddling the cut.
    /// </summary>
    public static string Digest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        if (collapsed.Length > PayloadDigestMaxLength)
        {
            collapsed = collapsed[..PayloadDigestMaxLength];
        }

        return Scrub(collapsed);
    }

    /// <summary>
    /// Secret pre-scan (regex pass over the final text): redacts
    /// key/value pairs of credential-looking field names, service-key
    /// token shapes and long opaque blobs. Runs over EVERY registry log
    /// line as the last step before the sink.
    /// </summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string scrubbed = CredentialFieldRegex().Replace(text, $"{RedactedToken}");
        scrubbed = ServiceKeyRegex().Replace(scrubbed, RedactedToken);
        scrubbed = OpaqueBlobRegex().Replace(scrubbed, RedactedToken);
        return scrubbed;
    }

    // api_key=…, "secret": …, Authorization: Bearer …, token=… — the value
    // side ends at the next separator or quote.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?i)\b(?:api[_-]?key|apikey|authorization|bearer|secret|token|password)\b[""']?\s*[:=]\s*[""']?[^\s,;}[""']+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CredentialFieldRegex();

    // Service-key token shapes: sk-…, ghp_…, xox-… with ≥ 8 payload chars.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9_-]{8,}|ghp_[A-Za-z0-9]{8,}|xox[baprs]-[A-Za-z0-9-]{8,})",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ServiceKeyRegex();

    // Long opaque blobs (hex/base64-ish, ≥ 32 chars) with no spaces.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[A-Za-z0-9+/]{32,}={0,2}\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex OpaqueBlobRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();
}
