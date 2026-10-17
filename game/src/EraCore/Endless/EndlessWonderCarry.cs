namespace EraCore.Endless;

/// <summary>
/// 奇观带入 resolution (T-143, F-006; SP-7 入口规则「奇观带入」+ 战役进度
/// 联动): the endless opening may carry WONDERS THE CAMPAIGN HAS ALREADY
/// BUILT — the host hands the built set over READ-ONLY (its origin is the
/// campaign domain's legacy ledger <c>LegacyLedger.BuiltWonderIds</c> /
/// campaign progress face; this module never touches the transition domain,
/// never writes campaign state — 只读消费). Every requested id must lie in
/// the built set, otherwise the entry refuses (负向锚点：未解锁奇观拒绝
/// 带入). With no campaign linkage supplied (null built set) the request
/// passes through unchanged — the host owns the trust decision (e.g. offline
/// profiles without a ledger).
/// </summary>
public static class EndlessWonderCarry
{
    /// <summary>
    /// Resolves the carried-in wonder set: when <paramref name="builtWonderIds"/>
    /// is null the request is frozen as-is (no campaign linkage — host trust
    /// mode, the T-118 passthrough behaviour); otherwise every requested id
    /// (compared trimmed, ordinal) must appear among the built wonders.
    /// Returns the frozen carry list in request order (original spellings —
    /// the run config freezes and hashes exactly these).
    /// </summary>
    /// <param name="requested">Wonder ids the player asked to carry in.</param>
    /// <param name="builtWonderIds">
    /// Built wonder ids of the campaign progress (READ-ONLY consumption —
    /// e.g. <c>LegacyLedger.BuiltWonderIds</c> handed over by the host), or
    /// null for the no-linkage trust mode.
    /// </param>
    /// <exception cref="ArgumentNullException">requested is null.</exception>
    /// <exception cref="ArgumentException">
    /// A requested id is blank, or not among the built wonders (未解锁).
    /// </exception>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string> requested,
        IReadOnlyList<string>? builtWonderIds)
    {
        ArgumentNullException.ThrowIfNull(requested);

        if (builtWonderIds is null)
        {
            return requested.ToList();
        }

        var built = new HashSet<string>(StringComparer.Ordinal);
        foreach (string wonder in builtWonderIds)
        {
            if (!string.IsNullOrWhiteSpace(wonder))
            {
                built.Add(wonder.Trim());
            }
        }

        foreach (string wonder in requested)
        {
            if (string.IsNullOrWhiteSpace(wonder))
            {
                throw new ArgumentException("Carried-in wonder ids must not be blank.", nameof(requested));
            }

            if (!built.Contains(wonder.Trim()))
            {
                throw new ArgumentException(
                    $"Wonder '{wonder}' is not built in the campaign progress — 奇观带入限于已解锁奇观.",
                    nameof(requested));
            }
        }

        return requested.ToList();
    }
}
