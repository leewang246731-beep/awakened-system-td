using EraCore.Contracts.Agent;

namespace EraCore.Agent.Actions;

/// <summary>
/// ReportToPlayer handler (C-03 third real handler, T-051): turns the
/// schema-validated report payload into the player-facing
/// <see cref="AgentReport"/> (K-5 contract record). Bounds are re-checked
/// defensively (the schema layer already enforces them; a direct caller
/// violating them fails fast instead of shipping an over-long report),
/// and the mandatory anti-injection text filter strips suspicious links
/// from every player-visible text line (v2.1 §11.3 report channel rule).
/// </summary>
public sealed partial class ReportToPlayerAction
{
    /// <summary>Hard cap of the summary field (K-3 frozen table).</summary>
    public const int SummaryMaxLength = 200;

    /// <summary>Hard cap of the findings list (K-3 frozen table).</summary>
    public const int FindingsMaxCount = 8;

    /// <summary>Replacement token for filtered link-like content.</summary>
    public const string FilteredLinkToken = "[link-filtered]";

    /// <summary>
    /// Formats the report: scrubs suspicious links, maps the wire verdict
    /// onto every finding line and returns the AgentReport.
    /// </summary>
    /// <exception cref="ArgumentException">Bounds violated (length, count, verdict).</exception>
    public AgentReport Format(string summary, IReadOnlyList<string>? findings, string verdict)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Length == 0)
        {
            throw new ArgumentException("summary must not be empty.", nameof(summary));
        }

        if (summary.Length > SummaryMaxLength)
        {
            throw new ArgumentException(
                $"summary must be ≤ {SummaryMaxLength} characters (got {summary.Length}).", nameof(summary));
        }

        if (findings is { Count: > FindingsMaxCount })
        {
            throw new ArgumentException(
                $"findings must hold ≤ {FindingsMaxCount} items (got {findings.Count}).", nameof(findings));
        }

        FindingVerdict parsedVerdict = ParseVerdict(verdict);
        var lines = new List<Finding>(findings?.Count ?? 0);
        if (findings is not null)
        {
            foreach (string finding in findings)
            {
                ArgumentNullException.ThrowIfNull(finding);
                if (finding.Length == 0)
                {
                    throw new ArgumentException("finding texts must not be empty.", nameof(findings));
                }

                lines.Add(new Finding(FilterSuspiciousLinks(finding), parsedVerdict));
            }
        }

        return new AgentReport(FilterSuspiciousLinks(summary), lines);
    }

    /// <summary>
    /// Wire verdict to contract verdict ("CONFIRMED"/"RUMOR", exact — the
    /// enum membership itself was already schema-checked).
    /// </summary>
    internal static FindingVerdict ParseVerdict(string verdict) => verdict switch
    {
        "CONFIRMED" => FindingVerdict.Confirmed,
        "RUMOR" => FindingVerdict.Rumor,
        _ => throw new ArgumentException($"verdict must be CONFIRMED or RUMOR (got '{verdict}').", nameof(verdict)),
    };

    /// <summary>
    /// Text filter: replaces link-like runs (http/https/www and bare
    /// domain-looking tokens) with a fixed marker. Over-filtering is
    /// acceptable here — the report channel is plain prose, never a link
    /// transport (anti-injection, F.4/F-096).
    /// </summary>
    internal static string FilterSuspiciousLinks(string text) =>
        LinkRegex().Replace(text, FilteredLinkToken);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:https?://|www\.)\S+|[A-Za-z0-9][A-Za-z0-9-]*\.(?:com|net|org|io|cn|xyz|top|example)\b\S*",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LinkRegex();
}
