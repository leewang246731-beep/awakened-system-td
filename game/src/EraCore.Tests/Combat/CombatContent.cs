using System.Globalization;
using EraCore.Combat;

namespace EraCore.Tests.Combat;

/// <summary>
/// Shared loader of the real era1 tower table for the combat tests
/// (CS-2.3/RL7: engine numbers come from the registry JSON, so the tests
/// consume the very same file). Repository-relative probing only — walks up
/// from the test assembly to <c>game/content/era1</c>, never a drive letter
/// (RL9). Results are cached: the records are immutable.
/// </summary>
internal static class CombatContent
{
    private static readonly Lazy<IReadOnlyList<TowerSpec>> TowersCache = new(LoadTowersCore);

    /// <summary>All four E1 tower definitions from game/content/era1/towers.json.</summary>
    public static IReadOnlyList<TowerSpec> Towers => TowersCache.Value;

    /// <summary>One tower by content id; fails the test if the id is unknown.</summary>
    public static TowerSpec Tower(string id) =>
        Towers.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidOperationException($"Tower '{id}' not present in era1 towers.json.");

    /// <summary>
    /// Builds a synthetic two-tier pair collection for law-violation samples:
    /// the same JSON pipeline as the real table (RL7 — even the deliberately
    /// broken numbers enter through content JSON, never C# literals at the
    /// call site beyond the sample table itself).
    /// </summary>
    public static string PairCollectionJson(
        string cls,
        double dpsLow,
        double dpsHigh,
        int costLow,
        int costHigh,
        double rangeLow,
        double rangeHigh,
        int tierLow = 1,
        int tierHigh = 2,
        double dpsMultiplier = 2.0,
        double costMultiplier = 3.0,
        double rangeMultiplier = 1.25) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        {
          "content_type": "Building",
          "items": [
            {"id":"test:pair","class":"{{cls}}","tier":{{tierLow}},"max_tier":4,
             "cost_total":{{costLow}},"dps":{{dpsLow}},"damage_per_shot":{{dpsLow}},"reload_ticks":30,
             "range_cells":{{rangeLow}},"hp":100,"ammo_item":null,"ammo_per_shot":1,
             "tier_growth":{"dps_multiplier_per_tier":{{dpsMultiplier}},"cost_multiplier_per_tier":{{costMultiplier}},"range_multiplier_per_tier":{{rangeMultiplier}} } },
            {"id":"test:pair","class":"{{cls}}","tier":{{tierHigh}},"max_tier":4,
             "cost_total":{{costHigh}},"dps":{{dpsHigh}},"damage_per_shot":{{dpsHigh}},"reload_ticks":30,
             "range_cells":{{rangeHigh}},"hp":100,"ammo_item":null,"ammo_per_shot":1,
             "tier_growth":{"dps_multiplier_per_tier":{{dpsMultiplier}},"cost_multiplier_per_tier":{{costMultiplier}},"range_multiplier_per_tier":{{rangeMultiplier}} } }
          ]
        }
        """);

    /// <summary>Deserializes a synthetic collection through the real loader.</summary>
    public static IReadOnlyList<TowerSpec> Load(string json) => TowerSpec.LoadCollection(json);

    /// <summary>Loads a two-tier pair: index 0 = lower tier, index 1 = upper tier.</summary>
    public static (TowerSpec Lower, TowerSpec Upper) LoadPair(string json)
    {
        var items = Load(json);
        if (items.Count != 2)
        {
            throw new InvalidOperationException("Pair sample must contain exactly two tiers.");
        }

        return (items[0], items[1]);
    }

    private static IReadOnlyList<TowerSpec> LoadTowersCore()
    {
        // Walk up from the test assembly to the repository root (RL9: no
        // drive-letter paths in test code).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1");
            if (Directory.Exists(probe))
            {
                var path = Path.Combine(probe, "towers.json");
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException("game/content/era1/towers.json is missing.");
                }

                return TowerSpec.LoadCollection(File.ReadAllText(path));
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "game/content/era1 not found (tests must run inside the repository).");
    }
}
