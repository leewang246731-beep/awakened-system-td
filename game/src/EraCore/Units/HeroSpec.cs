using System.Text.Json;

namespace EraCore.Units;

/// <summary>
/// Active skill definition of one hero (SP-6 L181~L182: exactly one active
/// per hero). Two kinds ship with E1~E2 — <c>warcry</c> (E1 初火守望者:
/// radius 3 cells, enemies slowed 30% for 5 s, cooldown 45 s) and
/// <c>armor_thrust</c> (E2 执戈者: single target 200 damage and −4 armor
/// for 10 s, cooldown 40 s). Fields a kind does not use stay at their
/// neutral zero. Pure data record, immutable.
/// </summary>
/// <param name="Kind">Skill kind discriminator (<c>warcry</c> / <c>armor_thrust</c>).</param>
/// <param name="NameZh">Chinese display name (logging/UI only).</param>
/// <param name="CooldownTicks">Cooldown in 30 Hz ticks.</param>
/// <param name="RadiusCells">Warcry effect radius in cells; 0 = single-target skill.</param>
/// <param name="SlowPercent">Warcry slow in percent.</param>
/// <param name="DurationTicks">Effect duration in ticks.</param>
/// <param name="Damage">armor_thrust damage in points; 0 = no damage component.</param>
/// <param name="ArmorReduction">armor_thrust armor reduction in points.</param>
/// <param name="RangeCells">armor_thrust reach in cells (target must stand inside).</param>
public sealed record HeroActiveSkill(
    string Kind,
    string NameZh,
    int CooldownTicks,
    double RadiusCells,
    int SlowPercent,
    int DurationTicks,
    int Damage,
    int ArmorReduction,
    double RangeCells);

/// <summary>
/// Passive definition of one hero (SP-6: exactly one passive per hero).
/// Two kinds ship with E1~E2 — <c>campfire_affinity</c> (E1: the campfire
/// aura the hero stands in is 50% stronger) and <c>veteran</c> (E2: attack
/// +10% per 5 waves held, capped at +30%). Pure data record, immutable.
/// </summary>
/// <param name="Kind">Passive kind discriminator.</param>
/// <param name="NameZh">Chinese display name (logging/UI only).</param>
/// <param name="BonusPercent">campfire_affinity strength in percent.</param>
/// <param name="WavesPerStep">veteran: waves held per bonus step.</param>
/// <param name="AttackBonusPercentPerStep">veteran: attack bonus per step, in percent.</param>
/// <param name="CapPercent">veteran: total bonus cap in percent.</param>
public sealed record HeroPassive(
    string Kind,
    string NameZh,
    int BonusPercent,
    int WavesPerStep,
    int AttackBonusPercentPerStep,
    int CapPercent);

/// <summary>
/// One hero definition deserialized from content data (K-2 registry file
/// game/content/era2/heroes.json — the SP-6 E1~E2 pair lives in one file
/// with a per-item era field, schema urn:eratd:content:hero:1). Seconds in
/// the file convert to ticks at the file's tick_rate_hz. Pure data record.
/// </summary>
/// <param name="Id">Content id, e.g. "era2:hero-spearwarden" (K-2 namespace).</param>
/// <param name="SpId">Spec-table row id, e.g. "h2_spearwarden" (SP-6).</param>
/// <param name="Era">Era content id the hero belongs to.</param>
/// <param name="NameZh">Chinese display name (logging/UI only).</param>
/// <param name="Hp">Base hit points (> 0).</param>
/// <param name="Armor">Armor points (SP-6 heroes carry none → 0).</param>
/// <param name="SpeedPxPerTick">Movement speed in px per 30 Hz tick (> 0).</param>
/// <param name="Active">The single active skill.</param>
/// <param name="Passive">The single passive.</param>
/// <param name="RespawnTicks">Death-to-respawn delay in ticks (SP-6 L176: 60 s).</param>
public sealed record HeroSpec(
    string Id,
    string SpId,
    string Era,
    string NameZh,
    int Hp,
    int Armor,
    double SpeedPxPerTick,
    HeroActiveSkill Active,
    HeroPassive Passive,
    int RespawnTicks);

/// <summary>
/// Read-only catalog of hero definitions parsed from one heroes.json
/// document (T-066, F-039). Parsing mirrors <see cref="EnemySpecCatalog"/>:
/// strict and total — malformed JSON, a wrong content_type, missing
/// required fields, duplicate ids, non-positive hp/speed or non-integral
/// second→tick conversions all throw. Underscore-prefixed keys are
/// annotation keys and ignored per the loader convention.
/// </summary>
public sealed class HeroSpecCatalog
{
    private readonly Dictionary<string, HeroSpec> _byId;

    private HeroSpecCatalog(Dictionary<string, HeroSpec> byId, int tickRateHz)
    {
        _byId = byId;
        TickRateHz = tickRateHz;
    }

    /// <summary>Tick rate the file's second-based fields convert against (K-1: 30).</summary>
    public int TickRateHz { get; }

    /// <summary>Number of hero definitions in the catalog.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// Parses one heroes.json document. Caller owns the IO (EraCore stays
    /// IO-pure).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Structure or value violations: wrong content_type, missing required
    /// field, duplicate id, non-positive hp/speed, or a seconds field that
    /// does not convert to a whole number of ticks.
    /// </exception>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static HeroSpecCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Hero catalog JSON must not be null or blank.", nameof(json));
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        RequireObject(root, "root");
        if (!root.TryGetProperty("content_type", out JsonElement type) || type.GetString() != "Hero")
        {
            throw new ArgumentException("Hero catalog root must declare content_type 'Hero'.", nameof(json));
        }

        int tickRateHz = ReadInt(root, "tick_rate_hz", "root");
        if (tickRateHz <= 0)
        {
            throw new ArgumentException($"tick_rate_hz must be positive (got {tickRateHz}).", nameof(json));
        }

        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Hero catalog needs an 'items' array.", nameof(json));
        }

        var byId = new Dictionary<string, HeroSpec>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            RequireObject(item, "items[]");
            if (!item.TryGetProperty("content_type", out JsonElement itemType) || itemType.GetString() != "Hero")
            {
                throw new ArgumentException("Hero catalog items[] must declare content_type 'Hero'.", nameof(json));
            }

            string id = ReadString(item, "id", "items[]");
            if (!byId.TryAdd(id, ParseItem(item, id, tickRateHz)))
            {
                throw new ArgumentException($"Duplicate hero id '{id}'.", nameof(json));
            }
        }

        if (byId.Count == 0)
        {
            throw new ArgumentException("Hero catalog 'items' must not be empty.", nameof(json));
        }

        return new HeroSpecCatalog(byId, tickRateHz);
    }

    /// <summary>Looks up one definition by content id.</summary>
    /// <exception cref="KeyNotFoundException">The id is not in the catalog.</exception>
    public HeroSpec Get(string id)
    {
        if (!_byId.TryGetValue(id, out HeroSpec? spec))
        {
            throw new KeyNotFoundException($"Hero id '{id}' is not in the catalog.");
        }

        return spec;
    }

    /// <summary>Non-throwing lookup variant.</summary>
    public bool TryGet(string id, out HeroSpec spec) => _byId.TryGetValue(id, out spec!);

    private static HeroSpec ParseItem(JsonElement item, string id, int tickRateHz)
    {
        string spId = ReadString(item, "sp_id", id);
        string era = ReadString(item, "era", id);
        string nameZh = ReadString(item, "name_zh", id);
        int hp = ReadInt(item, "hp", id);
        int armor = item.TryGetProperty("armor", out JsonElement armorEl) ? armorEl.GetInt32() : 0;
        double speed = ReadDouble(item, "speed_px_per_tick", id);
        int respawnTicks = SecondsToTicks(item, "respawn_seconds", id, tickRateHz);

        if (!item.TryGetProperty("active", out JsonElement activeEl) || activeEl.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Hero '{id}' needs an 'active' skill object.", nameof(id));
        }

        if (!item.TryGetProperty("passive", out JsonElement passiveEl) || passiveEl.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Hero '{id}' needs a 'passive' object.", nameof(id));
        }

        var active = new HeroActiveSkill(
            ReadString(activeEl, "kind", id + ".active"),
            ReadString(activeEl, "name_zh", id + ".active"),
            SecondsToTicks(activeEl, "cooldown_s", id + ".active", tickRateHz),
            TryReadDouble(activeEl, "radius_cells"),
            TryReadInt(activeEl, "slow_percent"),
            SecondsToTicks(activeEl, "duration_s", id + ".active", tickRateHz),
            TryReadInt(activeEl, "damage"),
            TryReadInt(activeEl, "armor_reduction"),
            TryReadDouble(activeEl, "range_cells"));

        var passive = new HeroPassive(
            ReadString(passiveEl, "kind", id + ".passive"),
            ReadString(passiveEl, "name_zh", id + ".passive"),
            TryReadInt(passiveEl, "bonus_percent"),
            TryReadInt(passiveEl, "waves_per_step"),
            TryReadInt(passiveEl, "attack_bonus_percent_per_step"),
            TryReadInt(passiveEl, "cap_percent"));

        if (hp <= 0)
        {
            throw new ArgumentException($"Hero '{id}' must have positive hp (got {hp}).", nameof(id));
        }

        if (armor < 0)
        {
            throw new ArgumentException($"Hero '{id}' must have non-negative armor (got {armor}).", nameof(id));
        }

        if (speed <= 0)
        {
            throw new ArgumentException($"Hero '{id}' must have positive speed (got {speed}).", nameof(id));
        }

        if (respawnTicks <= 0)
        {
            throw new ArgumentException($"Hero '{id}' must have a positive respawn delay (got {respawnTicks}).", nameof(id));
        }

        if (active.CooldownTicks <= 0)
        {
            throw new ArgumentException($"Hero '{id}' active skill needs a positive cooldown (got {active.CooldownTicks}).", nameof(id));
        }

        return new HeroSpec(id, spId, era, nameZh, hp, armor, speed, active, passive, respawnTicks);
    }

    private static int SecondsToTicks(JsonElement parent, string name, string where, int tickRateHz)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Hero catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        double ticks = value.GetDouble() * tickRateHz;
        if (ticks <= 0 || Math.Abs(ticks - Math.Round(ticks)) > double.Epsilon)
        {
            throw new ArgumentException(
                $"Hero catalog {where} field '{name}' must convert to a whole positive number of ticks (got {value.GetDouble()} s).",
                nameof(name));
        }

        return (int)Math.Round(ticks);
    }

    private static int TryReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static double TryReadDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static void RequireObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Hero catalog {where} must be a JSON object.", nameof(where));
        }
    }

    private static string ReadString(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Hero catalog {where} needs a non-empty string '{name}'.", nameof(name));
        }

        return value.GetString()!;
    }

    private static int ReadInt(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Hero catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetInt32();
    }

    private static double ReadDouble(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Hero catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetDouble();
    }
}
