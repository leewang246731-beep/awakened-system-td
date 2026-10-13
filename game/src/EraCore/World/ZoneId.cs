using System.Text.RegularExpressions;

namespace EraCore.World;

/// <summary>
/// Zone/lane id validation — the single source of the rule (F-117 分区命名
/// 标注层, T-032): one regex, <c>^[a-z0-9-]+$</c> — one or more lowercase ASCII
/// letters, digits or hyphens. <see cref="GridWorld"/> zones and the action
/// Schema <c>lane</c> parameter (G.2 SetRally/SpawnScout, frozen by K-3/T-021)
/// are the same id family and MUST validate through this one type, so a level's
/// zone names and its rally lanes can never drift apart (01-feature-inventory
/// F-117: "地图分区名与 SetRally lane 枚举同一数据源").
/// Deliberately excludes underscore, uppercase, spaces and non-ASCII so ids
/// stay stable across locales — the localizable display name lives in content
/// data, never in the id.
/// </summary>
public static class ZoneId
{
    private static readonly Regex Pattern =
        new("^[a-z0-9-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="zoneId"/> is a well-formed zone/lane id.
    /// Never throws; null and empty are not ids.
    /// </summary>
    public static bool IsValid(string? zoneId) =>
        !string.IsNullOrEmpty(zoneId) && Pattern.IsMatch(zoneId);
}
