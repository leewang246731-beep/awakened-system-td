using System.Globalization;
using System.Text.Json;
using EraCore.Combat;

namespace EraCore.Presentation;

/// <summary>
/// Upgrade preview of the tower detail card (T-093, 三层信息① 补充:
/// 点击塔弹出详情卡+升级预览[下一级 DPS/成本差]).
/// </summary>
/// <param name="NextDps">Projected next-tier DPS.</param>
/// <param name="DpsDelta">Next DPS − current DPS (> 0).</param>
/// <param name="NextCostTotal">Projected next-tier total cost (成本合计 ×3/级, v3.2 §6).</param>
/// <param name="CostDelta">Next total cost − current cost (> 0).</param>
/// <param name="NextRangeCells">Projected next-tier range in cells.</param>
public sealed record TowerUpgradePreview(
    double NextDps,
    double DpsDelta,
    double NextCostTotal,
    double CostDelta,
    double NextRangeCells);

/// <summary>
/// The full detail-card view model of one tower (T-093, F-120 三层信息①
/// 静态面板): title + stat lines + counter line + optional upgrade
/// preview. Pure projection of <see cref="TowerSpec"/> + the inverted
/// counter index — every decision after the raw spec lives here,
/// testable without Godot (CS-1.6).
/// </summary>
/// <param name="TowerId">Content id of the tower.</param>
/// <param name="Title">Display title (zh name + tier).</param>
/// <param name="Lines">Stat lines (DPS/单发/射程/弹种/HP).</param>
/// <param name="CounterLine">克制 line listing countered enemy names.</param>
/// <param name="Upgrade">Upgrade preview, or null at the tier ceiling.</param>
public sealed record TowerCardView(
    string TowerId,
    string Title,
    IReadOnlyList<string> Lines,
    string CounterLine,
    TowerUpgradePreview? Upgrade);

/// <summary>
/// Builds tower detail-card views from the content registry (T-093,
/// F-120). All numbers come from <see cref="TowerSpec"/> (towers.json,
/// RL7); the next-tier projection multiplies by the spec's own
/// <c>tier_growth</c> block (DPS ×2、成本合计 ×3、射程 ×1.25 anchors)
/// and prefers the wired successor row's real values when
/// <c>upgrades_to</c> resolves in the catalog.
/// </summary>
public static class TowerInfoCard
{
    /// <summary>
    /// Chinese labels of the six frozen tower classes (SP-1a) — enum
    /// display names, not balance values (TrustBadgeLabels precedent).
    /// </summary>
    public static string ClassLabelZh(TowerClass towerClass) => towerClass switch
    {
        TowerClass.Physical => "物理",
        TowerClass.Pierce => "穿刺",
        TowerClass.Splash => "溅射",
        TowerClass.Energy => "能量",
        TowerClass.Slow => "减速",
        TowerClass.Support => "支援",
        _ => towerClass.ToString(),
    };

    /// <summary>
    /// Builds the card view of one tower spec.
    /// </summary>
    /// <param name="spec">The tower registry row.</param>
    /// <param name="names">Display-name lookup (towers.json name_zh), keyed by content id.</param>
    /// <param name="counters">The inverted enemy-counter index.</param>
    /// <param name="successorRows">Full spec rows for upgrade-successor resolution (pass the TowerCardCatalog lookup).</param>
    /// <param name="maxCounterNames">Counter-line name cap (overflow folds into「等N种」).</param>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxCounterNames &lt; 1.</exception>
    public static TowerCardView Build(
        TowerSpec spec,
        IReadOnlyDictionary<string, string> names,
        CounterIndex counters,
        IReadOnlyDictionary<string, TowerSpec> successorRows,
        int maxCounterNames = 3)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(successorRows);
        if (maxCounterNames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCounterNames), maxCounterNames, "must be >= 1.");
        }

        string display = names.TryGetValue(spec.Id, out string? nameZh) ? nameZh : spec.Id;
        string title = $"{display} T{spec.Tier}·{ClassLabelZh(spec.Class)}";

        var lines = new List<string>
        {
            $"DPS {Fmt(spec.Dps)}",
            $"单发 {spec.DamagePerShot.ToString(CultureInfo.InvariantCulture)}",
            $"射程 {Fmt(spec.RangeCells)} 格",
            $"弹种 {(spec.AmmoItem ?? "无")}",
            $"HP {spec.Hp.ToString(CultureInfo.InvariantCulture)}",
        };

        IReadOnlyList<CounterRow> rows = counters.CounteredBy(spec.Class);
        string counterLine = rows.Count == 0
            ? "克制 —"
            : "克制 " + JoinCapped(rows, maxCounterNames);
        return new TowerCardView(spec.Id, title, lines, counterLine, PreviewUpgrade(spec, successorRows));
    }

    /// <summary>
    /// Projects the next tier of the spec's upgrade line: prefers the
    /// <c>upgrades_to</c> successor row when wired, else multiplies the
    /// current row by its own <c>tier_growth</c> block. Returns null at
    /// the line ceiling (<c>tier &gt;= max_tier</c>) or when the spec
    /// declares no growth block.
    /// </summary>
    /// <param name="spec">Current tier row.</param>
    /// <param name="successor">The catalog lookup for the successor row (pass an empty/throwing map to force the multiplier path).</param>
    /// <exception cref="ArgumentNullException">spec is null.</exception>
    public static TowerUpgradePreview? PreviewUpgrade(
        TowerSpec spec, IReadOnlyDictionary<string, TowerSpec> successor)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(successor);
        if (spec.Tier >= spec.MaxTier || spec.TierGrowth is null)
        {
            return null;
        }

        double nextDps;
        double nextCost;
        double nextRange;
        if (spec.UpgradesTo is { } nextId && successor.TryGetValue(nextId, out TowerSpec? next))
        {
            nextDps = next.Dps;
            nextCost = next.CostTotal;
            nextRange = next.RangeCells;
        }
        else
        {
            nextDps = spec.Dps * spec.TierGrowth.DpsMultiplierPerTier;
            nextCost = spec.CostTotal * spec.TierGrowth.CostMultiplierPerTier;
            nextRange = spec.RangeCells * spec.TierGrowth.RangeMultiplierPerTier;
        }

        return new TowerUpgradePreview(
            nextDps, nextDps - spec.Dps,
            nextCost, nextCost - spec.CostTotal,
            nextRange);
    }

    private static string JoinCapped(IReadOnlyList<CounterRow> rows, int cap)
    {
        var names = new List<string>(Math.Min(cap, rows.Count));
        for (int i = 0; i < rows.Count && names.Count < cap; i++)
        {
            names.Add($"{rows[i].NameZh}(甲{rows[i].Armor})");
        }

        return names.Count == rows.Count
            ? string.Join("、", names)
            : string.Join("、", names) + $"等{rows.Count}种";
    }

    private static string Fmt(double value) =>
        value.ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
/// Display-name lookup built from towers.json (T-093): content id →
/// name_zh (+ the successor-row catalog feeding
/// <see cref="TowerInfoCard.PreviewUpgrade"/>). Secondary type of this
/// file; parses the raw name/upgrade fields the combat
/// <see cref="TowerSpec"/> record intentionally does not carry.
/// </summary>
public sealed class TowerCardCatalog
{
    private const string ExpectedContentType = "Building";

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TowerSpec> _specsById = new(StringComparer.Ordinal);

    private TowerCardCatalog()
    {
    }

    /// <summary>Name lookup by content id (TryGetValue shape).</summary>
    public IReadOnlyDictionary<string, string> Names => _names;

    /// <summary>Full spec lookup by content id (upgrade successor resolution).</summary>
    public IReadOnlyDictionary<string, TowerSpec> SpecsById => _specsById;

    /// <summary>
    /// Parses one towers.json document into the display catalog. The spec
    /// rows deserialize through the combat loader (strict); this parse
    /// adds the display names and cross-references <c>upgrades_to</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Wrong content_type or a dangling upgrades_to id.</exception>
    public static TowerCardCatalog FromTowersJson(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        TowerNamesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, UiContentJsonContext.Default.TowerNamesDocument);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{sourceName}: invalid JSON ({ex.Message}).", nameof(json));
        }

        if (document is null)
        {
            throw new ArgumentException($"{sourceName}: deserialized to null.", nameof(json));
        }

        if (document.ContentType != ExpectedContentType)
        {
            throw new ArgumentException(
                $"{sourceName}: content_type must be '{ExpectedContentType}', got '{document.ContentType}'.");
        }

        var catalog = new TowerCardCatalog();
        IReadOnlyList<TowerSpec> specs = TowerSpec.LoadCollection(json);
        foreach (TowerSpec spec in specs)
        {
            catalog._specsById[spec.Id] = spec;
        }

        if (document.Items is not { Length: > 0 })
        {
            throw new ArgumentException($"{sourceName}: items must be a non-empty array.");
        }

        foreach (TowerNameRowJson item in document.Items)
        {
            string id = item.Id ?? string.Empty;
            if (id.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[].id must be non-blank.");
            }

            catalog._names[id] = string.IsNullOrWhiteSpace(item.NameZh) ? id : item.NameZh!;
        }

        // Upgrade-line integrity: every upgrades_to must resolve (悬空升级线=内容缺陷).
        foreach (TowerSpec spec in specs)
        {
            if (spec.UpgradesTo is { } next && !catalog._specsById.ContainsKey(next))
            {
                throw new ArgumentException($"{sourceName}: items[{spec.Id}].upgrades_to '{next}' does not resolve.");
            }
        }

        return catalog;
    }
}

/// <summary>Root shape of the towers.json display-name parse.</summary>
internal sealed record TowerNamesDocument(string? ContentType, TowerNameRowJson[]? Items);

/// <summary>Raw tower row shape for the display-name parse.</summary>
internal sealed record TowerNameRowJson(string? Id, string? NameZh);
