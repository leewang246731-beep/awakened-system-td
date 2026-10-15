using System.Text.Json;
using EraCore.Contracts.Foundation;

namespace EraCore.Legacy;

/// <summary>
/// One legacy-refresh declaration of a target era's resource row
/// (T-115, F-011): v3.2 §4 跨代资源行「上代 2 种基础资源刷新（丰度 ×0.5）」.
/// </summary>
/// <param name="Resource">Pooled resource key (K-2; keeps the OLD-era namespace, e.g. era2:res-iron inside era3).</param>
/// <param name="FromEraIndex">Era the resource is refreshed from (1=E1 … 6=终章; strictly the adjacent predecessor of the declaring era).</param>
/// <param name="AbundanceMultiplier">Fraction of the home-era abundance the refresh carries (era content declares 0.5; band (0,1)).</param>
public sealed record LegacyRefreshRow(
    ResourceKey Resource,
    int FromEraIndex,
    double AbundanceMultiplier);

/// <summary>
/// Cross-era resource policy derived from one target era's content row
/// (T-115, F-011): the single derivation of the two facts every era jump
/// needs — WHICH resources stay useful in the target era (the
/// <see cref="EraTransitionRequest.NewEraResources"/> set of the T-074
/// transaction: everything else folds 3:1 into the civilization fund) and
/// WHICH old resources refresh on the new era's maps at which abundance
/// (v3.2 §4 跨代资源行). Parsed strictly from the era document
/// (<c>game/content/eraN/era.json</c> — the resources array incl.
/// <c>legacy_refresh</c> rows); annotation keys prefixed <c>_</c> are
/// ignored (repo-wide convention). No design number is hardcoded here:
/// the ×0.5 multiplier, the era pairings and the resource sets all come
/// from the content row (RL7 via <see cref="LegacyDataLoader"/> style).
/// </summary>
public sealed record CrossEraResourcePolicy
{
    /// <summary>
    /// Resources still useful in the target era — the levy's
    /// <c>useful</c> predicate source. Includes legacy-refreshed rows
    /// (they keep the old namespace: era2:res-iron IS useful in E3 as the
    /// steel/bullet recipe input).
    /// </summary>
    public required IReadOnlySet<ResourceKey> NewEraResources { get; init; }

    /// <summary>Legacy-refresh rows in content order (declaration-only view).</summary>
    public required IReadOnlyList<LegacyRefreshRow> LegacyRefresh { get; init; }

    /// <summary>Era index of the declaring row (the jump's target era).</summary>
    public required int TargetEraIndex { get; init; }

    /// <summary>
    /// Abundance multiplier of one resource: the declared
    /// <see cref="LegacyRefreshRow.AbundanceMultiplier"/> for refreshed
    /// rows, 1.0 for native resources (no row halves them).
    /// </summary>
    public double AbundanceOf(ResourceKey resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (LegacyRefreshRow row in LegacyRefresh)
        {
            if (row.Resource.Equals(resource))
            {
                return row.AbundanceMultiplier;
            }
        }

        return 1.0;
    }

    /// <summary>
    /// Parses one era document (<c>era.json</c>) into the policy. Every
    /// item's <c>resources</c> rows are read; <c>legacy_refresh</c> is
    /// optional per row and must declare a predecessor era
    /// (<c>1 ≤ from_era &lt; era_index</c>) and an abundance multiplier in
    /// (0,1) — a refresh never boosts and a zero multiplier would be a
    /// dead row. Malformed shapes throw (a red test, never a silent
    /// partial load — same contract as <see cref="LegacyDataLoader"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="InvalidOperationException">Shape violation (message names the field/row).</exception>
    public static CrossEraResourcePolicy Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("era document root must be an object.");
        }

        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("era document items must be an array.");
        }

        var useful = new HashSet<ResourceKey>();
        var refresh = new List<LegacyRefreshRow>();
        int targetEra = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("era items rows must be objects.");
            }

            int eraIndex = IntOf(item, "era_index", min: 1, max: 6);
            if (targetEra != 0 && targetEra != eraIndex)
            {
                throw new InvalidOperationException(
                    $"era document mixes era_index {targetEra} and {eraIndex} (one era per document).");
            }

            targetEra = eraIndex;
            if (!item.TryGetProperty("resources", out JsonElement resources)
                || resources.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"era {eraIndex} resources must be a non-empty array.");
            }

            foreach (JsonElement row in resources.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"era {eraIndex} resource rows must be objects.");
                }

                var resource = new ResourceKey(StringOf(row, "id"));
                if (!useful.Add(resource))
                {
                    throw new InvalidOperationException($"Duplicate resource id '{resource.Value}' in era {eraIndex}.");
                }

                if (!row.TryGetProperty("legacy_refresh", out JsonElement legacy))
                {
                    continue;
                }

                if (legacy.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"legacy_refresh of '{resource.Value}' must be an object.");
                }

                int fromEra = IntOf(legacy, "from_era", min: 1, max: 6);
                if (fromEra >= eraIndex)
                {
                    throw new InvalidOperationException(
                        $"legacy_refresh.from_era {fromEra} of '{resource.Value}' must be a predecessor of era {eraIndex}.");
                }

                double multiplier = NumberOf(legacy, "abundance_multiplier");
                if (multiplier <= 0 || multiplier >= 1)
                {
                    throw new InvalidOperationException(
                        $"legacy_refresh.abundance_multiplier of '{resource.Value}' must be in (0,1) — a refresh never boosts (got {multiplier}).");
                }

                refresh.Add(new LegacyRefreshRow(resource, fromEra, multiplier));
            }
        }

        if (targetEra == 0 || useful.Count == 0)
        {
            throw new InvalidOperationException("era document carries no resource rows.");
        }

        return new CrossEraResourcePolicy
        {
            NewEraResources = useful,
            LegacyRefresh = refresh,
            TargetEraIndex = targetEra,
        };
    }

    private static string StringOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Missing or non-string field '{name}'.");
        }

        return value.GetString()!;
    }

    private static double NumberOf(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Missing or non-numeric field '{name}'.");
        }

        return value.GetDouble();
    }

    private static int IntOf(JsonElement parent, string name, int min, int max)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || !value.TryGetInt32(out int parsed)
            || parsed < min
            || parsed > max)
        {
            throw new InvalidOperationException($"Field '{name}' must be an integer in [{min},{max}].");
        }

        return parsed;
    }
}
