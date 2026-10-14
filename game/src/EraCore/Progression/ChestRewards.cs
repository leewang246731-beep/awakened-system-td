using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;

namespace EraCore.Progression;

/// <summary>
/// In-box rarity of one chest item (SP-8c): 普通/稀有/传说 with the frozen
/// 60/30/10 percent distribution. JSON camelCase spellings
/// <c>common</c>/<c>rare</c>/<c>legendary</c>.
/// </summary>
[JsonConverter(typeof(RewardRarityJsonConverter))]
public enum RewardRarity
{
    /// <summary>普通 — 60% of a box's items (SP-8c).</summary>
    Common = 0,

    /// <summary>稀有 — 30% of a box's items (SP-8c).</summary>
    Rare = 1,

    /// <summary>传说 — 10% of a box's items (SP-8c).</summary>
    Legendary = 2,
}

/// <summary>
/// String converter binding <see cref="RewardRarity"/> to the lowercase
/// JSON spellings of chests.json <c>rarity</c> (AOT-safe generic form,
/// 07 D-05; same pattern as <c>TowerClassJsonConverter</c>).
/// </summary>
public sealed class RewardRarityJsonConverter : JsonStringEnumConverter<RewardRarity>
{
    /// <summary>Camel-case policy: common/rare/legendary.</summary>
    public RewardRarityJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Frozen whitelist of decorative reward classes (T-073): the R2 P1 six
/// {decoration, title, voice, personality, presentation, hidden_quest}
/// (docs/engineering/19-r2-easter-eggs.md L165), the direct continuation of
/// the §19-R9 precedent 打脸/签到止于演出不碰数值平衡 — chest and sign-in
/// rewards may ONLY be of these classes; anything touching core balance
/// (DPS/成本/系统点/掉率/科技加速) is banned outright (D21-7). Loaders
/// enforce this at parse time.
/// </summary>
public static class RewardWhitelist
{
    /// <summary>The six frozen class keys, in document order.</summary>
    public static readonly IReadOnlyList<string> Classes = new[]
    {
        "decoration", "title", "voice", "personality", "presentation", "hidden_quest",
    };

    /// <summary>True when <paramref name="rewardClass"/> is one of the six.</summary>
    /// <exception cref="ArgumentNullException">rewardClass is null.</exception>
    public static bool IsAllowed(string rewardClass)
    {
        ArgumentNullException.ThrowIfNull(rewardClass);
        return Classes.Contains(rewardClass.Trim());
    }
}

/// <summary>
/// One decorative item of the chest pools (chests.json items[] row): an id,
/// its rarity bucket and its whitelisted reward class. Pure content —
/// names ride along for the UI.
/// </summary>
/// <param name="Id">Content id (<c>campaign:cos-banner-militia</c>).</param>
/// <param name="Rarity">In-box rarity bucket.</param>
/// <param name="RewardClass">One of the <see cref="RewardWhitelist"/> six.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Aliases">Colloquial names (commander grammar seam).</param>
public sealed record ChestItemSpec(
    string Id,
    RewardRarity Rarity,
    string RewardClass,
    string? NameZh = null,
    string? NameEn = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// One tier row of the chest table (chests.json tiers[]): how many item
/// rolls a box of this tier makes, plus the bonus rolls of the 黄金+额外
/// tier (0 everywhere else).
/// </summary>
/// <param name="Tier">Tier id (<c>bronze</c>/<c>silver</c>/<c>gold</c>/<c>gold_extra</c>).</param>
/// <param name="ItemRolls">Standard item rolls of the box.</param>
/// <param name="BonusRolls">Extra rolls (the 额外奖励 of tier 3; 0 otherwise).</param>
public sealed record ChestTierSpec(string Tier, int ItemRolls, int BonusRolls);

/// <summary>
/// The validated chest table: rarity weights in frozen order
/// (Common→Rare→Legendary, cumulative walk is deterministic), the four
/// tier rows and the decorative item pools grouped per rarity (ordered by
/// id — registration order determinism, K-2).
/// </summary>
/// <param name="OrderedWeights">Weights per rarity in Common,Rare,Legendary order (the 60/30/10 of SP-8c).</param>
/// <param name="Tiers">The four tier rows.</param>
/// <param name="ItemsByRarity">Item pools per rarity, id-sorted.</param>
public sealed record ChestTable(
    IReadOnlyList<(RewardRarity Rarity, double Weight)> OrderedWeights,
    IReadOnlyList<ChestTierSpec> Tiers,
    IReadOnlyDictionary<RewardRarity, IReadOnlyList<ChestItemSpec>> ItemsByRarity);

/// <summary>JSON shape of one chests.json items[] row.</summary>
public sealed record ChestItemDto(
    string? Id,
    RewardRarity? Rarity,
    string? RewardClass,
    string? NameZh,
    string? NameEn,
    IReadOnlyList<string>? Aliases);

/// <summary>JSON shape of one chests.json tiers[] row.</summary>
public sealed record ChestTierDto(string? Tier, int? ItemRolls, int? BonusRolls);

/// <summary>Root shape of chests.json.</summary>
/// <param name="ContentType">K-2 content kind declaration ("DropTable").</param>
/// <param name="RarityWeights">Weight per rarity key (<c>common</c>…).</param>
/// <param name="Tiers">Tier rows.</param>
/// <param name="Items">Decorative item rows.</param>
public sealed record ChestsDocument(
    string? ContentType,
    IReadOnlyDictionary<string, double>? RarityWeights,
    IReadOnlyList<ChestTierDto>? Tiers,
    IReadOnlyList<ChestItemDto>? Items);

/// <summary>
/// Loader + validator of the chest document (T-073): enforces the frozen
/// shape — all three rarities weighted positive, every rarity has at least
/// one item, exactly the four canonical tiers, every reward class inside
/// <see cref="RewardWhitelist"/> (parse-time rejection of balance-touching
/// rewards), non-negative roll counts.
/// </summary>
public static class ChestRewardsData
{
    /// <summary>The four canonical tier ids the objective-tier mapping addresses (SP-8c).</summary>
    public static readonly IReadOnlyList<string> CanonicalTierIds = new[]
    {
        "bronze", "silver", "gold", "gold_extra",
    };

    /// <summary>
    /// Parses and validates a chests document. Damaged payloads throw
    /// <see cref="JsonException"/>; shape violations throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="JsonException">Payload is not valid JSON for the shape.</exception>
    /// <exception cref="ArgumentException">Shape/whitelist validation failed (message names the row).</exception>
    public static ChestTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ChestsDocument document = JsonSerializer.Deserialize(
            json, ChestsJsonContext.Default.ChestsDocument)
            ?? throw new JsonException("Chests document deserialized to null.");

        var weights = new List<(RewardRarity, double)>();
        foreach ((string key, double weight) in document.RarityWeights ?? new Dictionary<string, double>())
        {
            if (!Enum.TryParse<RewardRarity>(key.Trim(), ignoreCase: true, out RewardRarity rarity))
            {
                throw new ArgumentException($"Unknown rarity weight key '{key}' (expected common/rare/legendary).");
            }

            if (!double.IsFinite(weight) || weight <= 0)
            {
                throw new ArgumentException($"Rarity {key} needs a positive finite weight (got {weight}).");
            }

            weights.Add((rarity, weight));
        }

        if (weights.Count != Enum.GetValues<RewardRarity>().Length)
        {
            throw new ArgumentException($"Exactly one weight per rarity is required (got {weights.Count} of {Enum.GetValues<RewardRarity>().Length}).");
        }

        var tiers = new List<ChestTierSpec>();
        foreach (ChestTierDto? row in document.Tiers ?? Array.Empty<ChestTierDto>())
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Tier))
            {
                throw new ArgumentException("A chest tier row is missing its tier id.");
            }

            if (row.ItemRolls is < 0 || row.BonusRolls is < 0)
            {
                throw new ArgumentException($"Tier {row.Tier} carries a negative roll count.");
            }

            tiers.Add(new ChestTierSpec(row.Tier.Trim(), row.ItemRolls ?? 0, row.BonusRolls ?? 0));
        }

        foreach (string canonical in CanonicalTierIds)
        {
            if (tiers.All(tier => tier.Tier != canonical))
            {
                throw new ArgumentException($"Chest table is missing canonical tier '{canonical}' (SP-8c 0/1/2/3 档).");
            }
        }

        var byRarity = new Dictionary<RewardRarity, List<ChestItemSpec>>();
        var seenIds = new HashSet<string>();
        foreach (ChestItemDto? row in document.Items ?? Array.Empty<ChestItemDto>())
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id))
            {
                throw new ArgumentException("A chest item row is missing its id.");
            }

            string id = row.Id.Trim();
            if (!seenIds.Add(id))
            {
                throw new ArgumentException($"Duplicate chest item id {id}.");
            }

            if (row.Rarity is null)
            {
                throw new ArgumentException($"Chest item {id} is missing its rarity.");
            }

            if (string.IsNullOrWhiteSpace(row.RewardClass))
            {
                throw new ArgumentException($"Chest item {id} is missing reward_class.");
            }

            if (!RewardWhitelist.IsAllowed(row.RewardClass))
            {
                throw new ArgumentException(
                    $"Chest item {id} reward_class '{row.RewardClass}' is outside the frozen decorative whitelist {string.Join("/", RewardWhitelist.Classes)} (R9: 不碰核心数值平衡).");
            }

            if (!byRarity.TryGetValue(row.Rarity.Value, out List<ChestItemSpec>? pool))
            {
                pool = new List<ChestItemSpec>();
                byRarity[row.Rarity.Value] = pool;
            }

            pool.Add(new ChestItemSpec(id, row.Rarity.Value, row.RewardClass.Trim(), row.NameZh, row.NameEn, row.Aliases));
        }

        foreach (RewardRarity rarity in Enum.GetValues<RewardRarity>())
        {
            if (!byRarity.TryGetValue(rarity, out List<ChestItemSpec>? pool) || pool.Count == 0)
            {
                throw new ArgumentException($"Rarity {rarity} has an empty item pool — every rarity bucket must draw something.");
            }

            pool.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
        }

        return new ChestTable(
            weights.OrderBy(line => line.Item1).Select(line => (line.Item1, line.Item2)).ToList(),
            tiers,
            byRarity.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ChestItemSpec>)pair.Value));
    }
}

/// <summary>
/// One item a chest actually granted (T-073): the settled reward — the
/// presentation layer only replays it (CS-4.9 奖励先定后演).
/// </summary>
/// <param name="ItemId">Granted item id.</param>
/// <param name="Rarity">Its in-box rarity.</param>
/// <param name="RewardClass">Its whitelisted reward class.</param>
public sealed record GrantedChestItem(string ItemId, RewardRarity Rarity, string RewardClass);

/// <summary>
/// Chest roller (T-073, F-002): opens the tier's box with a deterministic
/// seed — same (tier, seed) ⇒ byte-identical item list, so a replay of the
/// settlement reproduces the same chest. Each roll picks a rarity by the
/// table's weights (the frozen 60/30/10) then an item uniformly from that
/// rarity's pool.
/// </summary>
public sealed class ChestRewards
{
    private readonly ChestTable _table;
    private readonly IRng _rng;

    /// <param name="table">Validated table (<see cref="ChestRewardsData.Parse"/> output).</param>
    /// <param name="rng">Seeded random port (CS-1.4) — reseeded per open.</param>
    /// <exception cref="ArgumentNullException">table or rng is null.</exception>
    public ChestRewards(ChestTable table, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rng);
        _table = table;
        _rng = rng;
    }

    /// <summary>
    /// Opens one chest of a tier: rolls <c>item_rolls + bonus_rolls</c>
    /// items. The seed mixes the tier id with the caller's run seed
    /// (<see cref="CampaignHash.Combine"/>) so two tiers never share a
    /// stream by accident.
    /// </summary>
    /// <exception cref="ArgumentException">tierId unknown.</exception>
    public IReadOnlyList<GrantedChestItem> Open(string tierId, long runSeed)
    {
        ChestTierSpec tier = _table.Tiers.FirstOrDefault(candidate => candidate.Tier == tierId)
            ?? throw new ArgumentException($"Unknown chest tier '{tierId}'.", nameof(tierId));

        _rng.Reseed(CampaignHash.Combine(tierId, runSeed));
        var granted = new List<GrantedChestItem>(tier.ItemRolls + tier.BonusRolls);
        for (int i = 0; i < tier.ItemRolls + tier.BonusRolls; i++)
        {
            RewardRarity rarity = RollRarity();
            IReadOnlyList<ChestItemSpec> pool = _table.ItemsByRarity[rarity];
            ChestItemSpec item = pool[_rng.Next(0, pool.Count)];
            granted.Add(new GrantedChestItem(item.Id, item.Rarity, item.RewardClass));
        }

        return granted;
    }

    /// <summary>
    /// The table this roller draws from (host UI previews the pools).
    /// </summary>
    public ChestTable Table => _table;

    private RewardRarity RollRarity()
    {
        double total = 0.0;
        foreach ((_, double weight) in _table.OrderedWeights)
        {
            total += weight;
        }

        double roll = _rng.NextDouble() * total;
        double cumulative = 0.0;
        foreach ((RewardRarity rarity, double weight) in _table.OrderedWeights)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return rarity;
            }
        }

        return _table.OrderedWeights[^1].Rarity;
    }
}

/// <summary>
/// System.Text.Json source-generation context for chest content (AOT red
/// line, 07 D-05). Kept in this file as a secondary type so its coverage
/// rides on ChestRewardsTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ChestsDocument))]
[JsonSerializable(typeof(ChestItemDto))]
[JsonSerializable(typeof(ChestTierDto))]
[JsonSerializable(typeof(RewardRarity))]
public sealed partial class ChestsJsonContext : JsonSerializerContext;
