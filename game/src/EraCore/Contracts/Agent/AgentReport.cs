// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>Verdict of a report finding (K-6 draft; ReportToPlayer schema).</summary>
public enum FindingVerdict
{
    /// <summary>Confirmed observation.</summary>
    Confirmed,

    /// <summary>Unverified rumor — flagged as such to the player.</summary>
    Rumor,
}

/// <summary>One finding line of a report (K-6 draft).</summary>
/// <param name="Text">Finding text (already filtered for length and links).</param>
/// <param name="Verdict">Confirmed or rumor.</param>
public sealed record Finding(string Text, FindingVerdict Verdict);

/// <summary>
/// Final report of a handled order (K-6 draft). Bounds mirror the
/// ReportToPlayer action schema: summary ≤ 200 chars, findings ≤ 8 items —
/// the schema layer enforces the bounds plus text-length and
/// suspicious-link filtering (anti-injection, F.4).
/// </summary>
/// <param name="Summary">Summary text, ≤ 200 chars.</param>
/// <param name="Findings">Findings, ≤ 8 items.</param>
public sealed record AgentReport(
    string Summary,
    IReadOnlyList<Finding> Findings);
