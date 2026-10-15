using System.Text.Json;

namespace EraCore.Units;

/// <summary>
/// One enemy definition deserialized from content data (K-2 registry file
/// game/content/era{N}/enemies.json, schema urn:eratd:content:enemy:1;
/// SP-3a/SP-3b anchor tables). Field units follow the file's own _units
/// annotations: hp/armor in points, speed in px per tick at the K-1 30 Hz
/// tick domain (1 cell = 32 px). Pure data record, immutable.
/// <para>
/// T-066 additions beyond the E1 thin set: the optional individual shield
/// points (framework slot — SP-3b rows carry none, so E2 data keeps it 0),
/// the building-damage multiplier, the war-drum aura pair (SP-3b L120) and
/// the charge tuning (SP-3b L121). T-114 adds the war-cry quad (era3 boss
/// trait war_cry: radius/bonus/duration/cooldown — the active periodic
/// ally buff the fortress fires in the boss battle). Zero values mean
/// "trait absent".
/// </para>
/// </summary>
/// <param name="Id">Content id, e.g. "era1:enemy-boss" (K-2 namespace).</param>
/// <param name="SpId">Spec-table row id, e.g. "e1_boss" (SP-3a # column).</param>
/// <param name="NameZh">Chinese display name (logging/UI only).</param>
/// <param name="IsBoss">Whether the row is a boss (is_boss in the file).</param>
/// <param name="Hp">Base hit points (> 0).</param>
/// <param name="Armor">Armor points (≥ 0).</param>
/// <param name="SpeedPxPerTick">Movement speed in px per 30 Hz tick (> 0).</param>
/// <param name="TrampleRadiusCells">Trample aura radius in cells; 0 = no trample.</param>
/// <param name="CanvasSizePx">Render canvas size in px (64~96 for bosses).</param>
/// <param name="ShieldPoints">Individual shield points (framework slot; SP-3b carries none → 0).</param>
/// <param name="BuildingDamageMultiplier">Multiplier on damage dealt to buildings (1 = none; SP-3b L115 攻城锤 ×3).</param>
/// <param name="WarDrumsRadiusCells">War-drum aura radius in cells; 0 = no aura (SP-3b L120).</param>
/// <param name="WarDrumsSpeedBonusPercent">War-drum aura speed bonus in percent (SP-3b L120: +15).</param>
/// <param name="ChargeIntervalTicks">Charge period in ticks; 0 = no charge (SP-3b L121: 20 s = 600).</param>
/// <param name="ChargeDamage">Charge ram damage in points.</param>
/// <param name="ChargeReachCells">Charge ram reach in cells.</param>
/// <param name="ChargeDurationTicks">Charge burst length in ticks.</param>
/// <param name="ChargeSpeedBonusPercent">Charge burst speed bonus in percent.</param>
/// <param name="WarCryRadiusCells">War-cry radius in cells; 0 = no cry (T-114, era3 boss trait).</param>
/// <param name="WarCrySpeedBonusPercent">War-cry ally speed bonus in percent (era3 boss: +15).</param>
/// <param name="WarCryDurationTicks">War-cry buff length in ticks (era3 boss: 180 = 6 s).</param>
/// <param name="WarCryCooldownTicks">War-cry cooldown in ticks (era3 boss: 600 = 20 s).</param>
public sealed record EnemySpec(
    string Id,
    string SpId,
    string NameZh,
    bool IsBoss,
    int Hp,
    int Armor,
    double SpeedPxPerTick,
    double TrampleRadiusCells,
    int CanvasSizePx,
    int ShieldPoints = 0,
    double BuildingDamageMultiplier = 1.0,
    double WarDrumsRadiusCells = 0,
    int WarDrumsSpeedBonusPercent = 0,
    int ChargeIntervalTicks = 0,
    int ChargeDamage = 0,
    double ChargeReachCells = 0,
    int ChargeDurationTicks = 0,
    int ChargeSpeedBonusPercent = 0,
    double WarCryRadiusCells = 0,
    int WarCrySpeedBonusPercent = 0,
    int WarCryDurationTicks = 0,
    int WarCryCooldownTicks = 0);

/// <summary>
/// Read-only catalog of enemy definitions parsed from one enemies.json
/// document (C-10 E1 thin, T-039). Parsing is strict and total: malformed
/// JSON, a wrong content_type, missing required fields, duplicate ids or
/// non-positive hp/speed all throw — a content defect must surface as a
/// load failure, never as a silently-zeroed spec. Keys starting with
/// underscore are annotation keys and ignored, per the loader convention
/// declared inside the content files themselves.
/// </summary>
public sealed class EnemySpecCatalog
{
    private readonly Dictionary<string, EnemySpec> _byId;

    private EnemySpecCatalog(Dictionary<string, EnemySpec> byId, int tickRateHz, int cellSizePx)
    {
        _byId = byId;
        TickRateHz = tickRateHz;
        CellSizePx = cellSizePx;
    }

    /// <summary>Tick rate the file's speeds are expressed against (K-1: 30).</summary>
    public int TickRateHz { get; }

    /// <summary>Cell size in px the file's radii are expressed against (1 cell = 32 px).</summary>
    public int CellSizePx { get; }

    /// <summary>Number of enemy definitions in the catalog.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// Parses one enemies.json document. Caller owns the IO (IFileStore /
    /// test harness reads the text; EraCore stays IO-pure).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Structure or value violations: wrong content_type, missing required
    /// field, duplicate id, non-positive hp/speed, negative armor.
    /// </exception>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static EnemySpecCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Enemy catalog JSON must not be null or blank.", nameof(json));
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        RequireObject(root, "root");
        RequireContentType(root, "Enemy", "root");

        int tickRateHz = ReadInt(root, "tick_rate_hz", "root");
        int cellSizePx = ReadInt(root, "cell_size_px", "root");
        if (tickRateHz <= 0)
        {
            throw new ArgumentException($"tick_rate_hz must be positive (got {tickRateHz}).", nameof(json));
        }

        if (cellSizePx <= 0)
        {
            throw new ArgumentException($"cell_size_px must be positive (got {cellSizePx}).", nameof(json));
        }

        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Enemy catalog needs an 'items' array.", nameof(json));
        }

        var byId = new Dictionary<string, EnemySpec>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            RequireObject(item, "items[]");
            RequireContentType(item, "Enemy", "items[]");
            string id = ReadString(item, "id", "items[]");
            if (!byId.TryAdd(id, ParseItem(item, id)))
            {
                throw new ArgumentException($"Duplicate enemy id '{id}'.", nameof(json));
            }
        }

        if (byId.Count == 0)
        {
            throw new ArgumentException("Enemy catalog 'items' must not be empty.", nameof(json));
        }

        return new EnemySpecCatalog(byId, tickRateHz, cellSizePx);
    }

    /// <summary>Looks up one definition by content id.</summary>
    /// <exception cref="KeyNotFoundException">The id is not in the catalog.</exception>
    public EnemySpec Get(string id)
    {
        if (!_byId.TryGetValue(id, out EnemySpec? spec))
        {
            throw new KeyNotFoundException($"Enemy id '{id}' is not in the catalog.");
        }

        return spec;
    }

    /// <summary>Non-throwing lookup variant.</summary>
    public bool TryGet(string id, out EnemySpec spec) => _byId.TryGetValue(id, out spec!);

    private static EnemySpec ParseItem(JsonElement item, string id)
    {
        string spId = ReadString(item, "sp_id", id);
        string nameZh = ReadString(item, "name_zh", id);
        bool isBoss = item.TryGetProperty("is_boss", out JsonElement bossFlag)
            && bossFlag.ValueKind == JsonValueKind.True;
        int hp = ReadInt(item, "hp", id);
        int armor = item.TryGetProperty("armor", out JsonElement armorEl)
            ? armorEl.GetInt32()
            : 0;
        double speed = ReadDouble(item, "speed_px_per_tick", id);
        int canvas = item.TryGetProperty("canvas_size_px", out JsonElement canvasEl)
            ? canvasEl.GetInt32()
            : 32;
        int shieldPoints = item.TryGetProperty("shield", out JsonElement shieldEl)
            ? shieldEl.GetInt32()
            : 0;

        double trampleRadius = 0;
        double buildingDamageMultiplier = 1.0;
        double warDrumsRadius = 0;
        int warDrumsBonus = 0;
        int chargeInterval = 0;
        int chargeDamage = 0;
        double chargeReach = 0;
        int chargeDuration = 0;
        int chargeBonus = 0;
        double warCryRadius = 0;
        int warCryBonus = 0;
        int warCryDuration = 0;
        int warCryCooldown = 0;
        if (item.TryGetProperty("traits", out JsonElement traits)
            && traits.ValueKind == JsonValueKind.Object)
        {
            if (traits.TryGetProperty("trample", out JsonElement trample)
                && trample.ValueKind == JsonValueKind.Object
                && trample.TryGetProperty("radius_cells", out JsonElement radius))
            {
                trampleRadius = radius.GetDouble();
            }

            if (traits.TryGetProperty("building_damage_multiplier", out JsonElement building)
                && building.ValueKind == JsonValueKind.Number)
            {
                buildingDamageMultiplier = building.GetDouble();
            }

            if (traits.TryGetProperty("war_drums", out JsonElement drums)
                && drums.ValueKind == JsonValueKind.Object)
            {
                warDrumsRadius = ReadTraitDouble(drums, "radius_cells");
                warDrumsBonus = ReadTraitInt(drums, "speed_bonus_percent");
            }

            if (traits.TryGetProperty("charge", out JsonElement charge)
                && charge.ValueKind == JsonValueKind.Object)
            {
                chargeInterval = ReadTraitInt(charge, "interval_ticks");
                chargeDamage = ReadTraitInt(charge, "damage");
                chargeReach = ReadTraitDouble(charge, "reach_cells");
                chargeDuration = ReadTraitInt(charge, "duration_ticks");
                chargeBonus = ReadTraitInt(charge, "speed_bonus_percent");
            }

            if (traits.TryGetProperty("war_cry", out JsonElement cry)
                && cry.ValueKind == JsonValueKind.Object)
            {
                warCryRadius = ReadTraitDouble(cry, "radius_cells");
                warCryBonus = ReadTraitInt(cry, "speed_bonus_percent");
                warCryDuration = ReadTraitInt(cry, "duration_ticks");
                warCryCooldown = ReadTraitInt(cry, "cooldown_ticks");
            }
        }

        if (hp <= 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have positive hp (got {hp}).", nameof(id));
        }

        if (armor < 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have non-negative armor (got {armor}).", nameof(id));
        }

        if (speed <= 0)
        {
            throw new ArgumentException($"Enemy '{id}' must have positive speed (got {speed}).", nameof(id));
        }

        if (trampleRadius < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative trample radius (got {trampleRadius}).", nameof(id));
        }

        if (shieldPoints < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative shield points (got {shieldPoints}).", nameof(id));
        }

        if (buildingDamageMultiplier <= 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have a positive building damage multiplier (got {buildingDamageMultiplier}).",
                nameof(id));
        }

        if (warDrumsRadius < 0 || warDrumsBonus < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative war drums radius/bonus (got {warDrumsRadius}/{warDrumsBonus}).",
                nameof(id));
        }

        if (chargeInterval < 0 || chargeDamage < 0 || chargeReach < 0 || chargeDuration < 0 || chargeBonus < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative charge tuning (got {chargeInterval}/{chargeDamage}/{chargeReach}/{chargeDuration}/{chargeBonus}).",
                nameof(id));
        }

        if (warCryRadius < 0 || warCryBonus < 0 || warCryDuration < 0 || warCryCooldown < 0)
        {
            throw new ArgumentException(
                $"Enemy '{id}' must have non-negative war cry tuning (got {warCryRadius}/{warCryBonus}/{warCryDuration}/{warCryCooldown}).",
                nameof(id));
        }

        if (warCryRadius > 0 && (warCryBonus <= 0 || warCryDuration <= 0 || warCryCooldown <= 0))
        {
            throw new ArgumentException(
                $"Enemy '{id}' war cry needs a positive bonus/duration/cooldown when it has a radius " +
                $"(got {warCryRadius}/{warCryBonus}/{warCryDuration}/{warCryCooldown}).",
                nameof(id));
        }

        return new EnemySpec(
            id,
            spId,
            nameZh,
            isBoss,
            hp,
            armor,
            speed,
            trampleRadius,
            canvas,
            shieldPoints,
            buildingDamageMultiplier,
            warDrumsRadius,
            warDrumsBonus,
            chargeInterval,
            chargeDamage,
            chargeReach,
            chargeDuration,
            chargeBonus,
            warCryRadius,
            warCryBonus,
            warCryDuration,
            warCryCooldown);
    }

    private static int ReadTraitInt(JsonElement trait, string name) =>
        trait.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static double ReadTraitDouble(JsonElement trait, string name) =>
        trait.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static void RequireObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Enemy catalog {where} must be a JSON object.", nameof(where));
        }
    }

    private static void RequireContentType(JsonElement element, string expected, string where)
    {
        if (!element.TryGetProperty("content_type", out JsonElement type)
            || type.GetString() != expected)
        {
            throw new ArgumentException(
                $"Enemy catalog {where} must declare content_type '{expected}'.", nameof(expected));
        }
    }

    private static string ReadString(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Enemy catalog {where} needs a non-empty string '{name}'.", nameof(name));
        }

        return value.GetString()!;
    }

    private static int ReadInt(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Enemy catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetInt32();
    }

    private static double ReadDouble(JsonElement parent, string name, string where)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Enemy catalog {where} needs a numeric '{name}'.", nameof(name));
        }

        return value.GetDouble();
    }
}
