using System.Text;
using System.Text.Json;
using EraCore.Contracts.Content;
using EraCore.Scrolls;

namespace EraCore.Mods;

/// <summary>Value shape one content field accepts (closed set of shapes, K-2 style).</summary>
public enum ModFieldType
{
    /// <summary>Non-empty JSON string.</summary>
    Text,

    /// <summary>Finite JSON number inside declared bounds.</summary>
    Number,

    /// <summary>Integral JSON number inside declared bounds.</summary>
    Integer,

    /// <summary>JSON true/false.</summary>
    Boolean,

    /// <summary>JSON string inside a closed enum (declared values).</summary>
    Enum,

    /// <summary>Array of non-empty strings.</summary>
    TextArray,

    /// <summary>Nullable number: number, null, or absent (weapon-less towers etc.).</summary>
    NullableNumber,

    /// <summary>Nullable integer: number, null, or absent.</summary>
    NullableInteger,

    /// <summary>Nested object with a closed key set (declared child rules).</summary>
    ClosedObject,

    /// <summary>Nested object with closed key set where each value is a number/bool (trait block).</summary>
    TraitObject,
}

/// <summary>One field constraint of one content kind (mirrors the K-3 FieldRule shape, T-051 style).</summary>
/// <param name="Name">Field name on the wire.</param>
/// <param name="Type">Expected value shape.</param>
/// <param name="Required">True when the field must be present.</param>
/// <param name="EnumValues">Closed enum wire strings, when the kind fixes one.</param>
/// <param name="Min">Inclusive lower numeric bound (nullable shapes apply when the value is non-null).</param>
/// <param name="Max">Inclusive upper numeric bound; null = unbounded.</param>
/// <param name="Children">Closed child rules of <see cref="ModFieldType.ClosedObject"/> shapes.</param>
/// <param name="TraitKeys">Closed key set of <see cref="ModFieldType.TraitObject"/> shapes.</param>
public sealed record ModFieldRule(
    string Name,
    ModFieldType Type,
    bool Required = false,
    IReadOnlySet<string>? EnumValues = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<ModFieldRule>? Children = null,
    IReadOnlySet<string>? TraitKeys = null);

/// <summary>
/// Per-ContentType closed item schemas of the mod pipeline (T-103, F-156 /
/// F-165): the schema matrix a content item must pass before it may enter
/// the K-2 registry. First-party and modded items run through the SAME
/// matrix (CS-2.5). Deep tables exist for the kinds that carry gameplay
/// anchors (Building/Projectile/Enemy/Era/Scroll — fields transcribed from
/// the live first-party documents, game/content/era*/towers.json shape);
/// the remaining kinds enforce the common block + DSL placement only
/// (their deep validation stays with the owning first-party loader).
/// Unknown ordinary keys are ignored (mod extension seam, TowerSpec
/// precedent); closed blocks (enum fields, trait keys, DSL ops) reject
/// unknown members with file+line.
/// </summary>
public static class ModContentSchema
{
    private const double TierMax = 4.0; // upgrade ceiling per line (K-3 to_tier 1..4)
    private const double EraIndexMax = 6.0; // six eras (ContentType.Era doc)

    private static readonly IReadOnlySet<string> TowerClassValues = new HashSet<string>(
        Enum.GetNames<EraCore.Combat.TowerClass>().Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TierLineValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "standard", "specialization",
    };

    private static readonly IReadOnlySet<string> TraitKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "burst_windup_ticks", "one_shot", "root_ticks", "slow_percent", "slow_ticks",
        "aura_radius_cells", "aura_effects", "aura_regen_hp_per_s",
        "splash_radius_cells", "burn_dps", "burn_ticks",
    };

    private static ModFieldRule Text(string name, bool required = true) => new(name, ModFieldType.Text, required);

    private static ModFieldRule Num(string name, bool required = true, double min = 0, double? max = null) =>
        new(name, ModFieldType.Number, required, Min: min, Max: max);

    private static ModFieldRule Int(string name, bool required = true, double min = 0, double? max = null) =>
        new(name, ModFieldType.Integer, required, Min: min, Max: max);

    private static ModFieldRule EnumField(string name, IReadOnlySet<string> values, bool required = true) =>
        new(name, ModFieldType.Enum, required, EnumValues: values);

    /// <summary>Building kind deep table (towers.json item shape, SP-1b).</summary>
    public static readonly IReadOnlyList<ModFieldRule> BuildingFields = new[]
    {
        Text("id"),
        Text("content_type"),
        EnumField("class", TowerClassValues),
        Text("name_zh"),
        Text("name_en"),
        new ModFieldRule("aliases", ModFieldType.TextArray, Required: false),
        new ModFieldRule("tier", ModFieldType.Integer, Required: true, Min: 1, Max: TierMax),
        new ModFieldRule("max_tier", ModFieldType.Integer, Required: true, Min: 1, Max: TierMax),
        Num("cost_total", min: 0),
        Num("dps", min: 0),
        Num("damage_per_shot", min: 0),
        new ModFieldRule("reload_ticks", ModFieldType.NullableInteger, Required: false, Min: 0),
        Num("range_cells", min: 0),
        Int("hp", min: 1),
        Text("ammo_item", required: false),
        Int("ammo_per_shot", required: false, min: 0),
        new ModFieldRule(
            "tier_growth", ModFieldType.ClosedObject, Required: false,
            Children: new[]
            {
                Num("dps_multiplier_per_tier", min: 0),
                Num("cost_multiplier_per_tier", min: 0),
                Num("range_multiplier_per_tier", min: 0),
            }),
        new ModFieldRule("traits", ModFieldType.TraitObject, Required: false, TraitKeys: TraitKeys),
        Text("upgrades_to", required: false),
        EnumField("tier_line", TierLineValues, required: false),
    };

    /// <summary>Projectile kind deep table (Projectile content; fields _new pending first-party file).</summary>
    public static readonly IReadOnlyList<ModFieldRule> ProjectileFields = new[]
    {
        Text("id"),
        Text("content_type"),
        Text("name_zh"),
        Text("name_en"),
        new ModFieldRule("aliases", ModFieldType.TextArray, Required: false),
        Num("damage_per_shot", min: 0),
        Num("speed_px_per_tick", min: 0),
        Int("pierce_count", required: false, min: 1),
    };

    /// <summary>Enemy kind deep table (enemies.json item shape, SP-3a).</summary>
    public static readonly IReadOnlyList<ModFieldRule> EnemyFields = new[]
    {
        Text("id"),
        Text("content_type"),
        Text("name_zh"),
        Text("name_en"),
        new ModFieldRule("aliases", ModFieldType.TextArray, Required: false),
        Num("hp", min: 1),
        Num("armor", min: 0),
        Num("speed_px_per_tick", min: 0),
        Text("sp_id", required: false),
    };

    /// <summary>Era kind deep table (era.json item shape).</summary>
    public static readonly IReadOnlyList<ModFieldRule> EraFields = new[]
    {
        Text("id"),
        Text("content_type"),
        Text("name_zh"),
        Text("name_en"),
        new ModFieldRule("aliases", ModFieldType.TextArray, Required: false),
        new ModFieldRule("era_index", ModFieldType.Integer, Required: true, Min: 1, Max: EraIndexMax),
    };

    /// <summary>
    /// Deep-table lookup per kind; null kinds enforce the common block only.
    /// </summary>
    public static IReadOnlyList<ModFieldRule>? FieldsOf(ContentType type) => type switch
    {
        ContentType.Building => BuildingFields,
        ContentType.Projectile => ProjectileFields,
        ContentType.Enemy => EnemyFields,
        ContentType.Era => EraFields,
        _ => null,
    };

    /// <summary>True when the kind requires display-name fields (common block depth).</summary>
    public static bool RequiresNames(ContentType type) =>
        type is not (ContentType.ActionParamSpace or ContentType.Localization);

    /// <summary>DSL family allowed on one content kind (null = no DSL field allowed at all).</summary>
    public static ModDslFamily? DslFamilyOf(ContentType type) => type switch
    {
        ContentType.Building or ContentType.Projectile or ContentType.StatusEffect => ModDslFamily.EffectPrimitive,
        ContentType.Era or ContentType.WaveGroup or ContentType.Level => ModDslFamily.WorldOperation,
        ContentType.Scroll or ContentType.Hero => ModDslFamily.SystemCommand,
        _ => null,
    };

    /// <summary>Wire field name carrying the family array for a kind (null when none allowed).</summary>
    public static string? DslFieldOf(ContentType type) => DslFamilyOf(type) switch
    {
        ModDslFamily.EffectPrimitive => "on_hit",
        ModDslFamily.WorldOperation => "world_ops",
        ModDslFamily.SystemCommand => "system_commands",
        _ => null,
    };

    /// <summary>
    /// Validates ONE content item against its kind's schema matrix. Checks
    /// (order stable, all violations collected — never throw-and-stop):
    /// common block (id/content_type/names/aliases/patch), the kind's deep
    /// field table, the traits/tier_growth closed blocks, and the DSL family
    /// placement (<c>on_hit</c>/<c>world_ops</c>/<c>system_commands</c> only
    /// on the kinds that own them; the DSL array itself validates through
    /// <see cref="ModDslTable.ValidateEntry"/> per entry). Scroll items are
    /// delegated to the first-party <see cref="ScrollDataLoader"/> (same
    /// pipeline as T-071) on a synthetic single-item document.
    /// </summary>
    /// <param name="type">Declared kind of the containing document.</param>
    /// <param name="item">The item object element.</param>
    /// <param name="pointer">JSON pointer of the item (<c>/items/2</c>).</param>
    /// <param name="lines">Line map from <see cref="ModJsonLineMap.Index"/>.</param>
    public static IReadOnlyList<ModSchemaViolation> ValidateItem(
        ContentType type,
        JsonElement item,
        string pointer,
        IReadOnlyDictionary<string, int> lines)
    {
        var violations = new List<ModSchemaViolation>();
        int line = ModJsonLineMap.LineOf(lines, pointer);
        if (item.ValueKind != JsonValueKind.Object)
        {
            violations.Add(new ModSchemaViolation(pointer, line, $"{type} item must be a JSON object."));
            return violations;
        }

        ValidateCommon(type, item, pointer, lines, violations);

        if (type == ContentType.Scroll)
        {
            ValidateScrollItem(item, pointer, line, violations);
        }
        else
        {
            IReadOnlyList<ModFieldRule>? fields = FieldsOf(type);
            if (fields is not null)
            {
                foreach (ModFieldRule rule in fields)
                {
                    if (rule.Name is "id" or "content_type" or "name_zh" or "name_en" or "aliases")
                    {
                        continue; // common block already validated
                    }

                    ValidateField(rule, item, pointer, lines, violations);
                }
            }
        }

        ValidateDslPlacement(type, item, pointer, lines, violations);
        return violations;
    }

    private static void ValidateCommon(
        ContentType type,
        JsonElement item,
        string pointer,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        ValidateField(Text("id"), item, pointer, lines, violations);

        // content_type must repeat the document kind exactly (documents
        // declare it at root; items repeat it in the live files).
        string typePointer = pointer + "/content_type";
        if (!item.TryGetProperty("content_type", out JsonElement declared)
            || declared.ValueKind != JsonValueKind.String
            || declared.GetString() != type.ToString())
        {
            violations.Add(new ModSchemaViolation(
                typePointer, ModJsonLineMap.LineOf(lines, typePointer),
                $"{type} item must carry content_type '{type}' (got {Summarize(declared)})."));
        }

        if (RequiresNames(type))
        {
            ValidateField(Text("name_zh"), item, pointer, lines, violations);
            ValidateField(Text("name_en"), item, pointer, lines, violations);
        }

        ValidateField(new ModFieldRule("aliases", ModFieldType.TextArray, Required: false), item, pointer, lines, violations);
        ValidateField(new ModFieldRule("patch", ModFieldType.Boolean, Required: false), item, pointer, lines, violations);
    }

    private static void ValidateScrollItem(
        JsonElement item, string pointer, int line, List<ModSchemaViolation> violations)
    {
        // Same pipeline as first-party scrolls (T-071): wrap the single item
        // into a minimal document and run the frozen scroll validator.
        try
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("content_type", nameof(ContentType.Scroll));
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                item.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            _ = ScrollDataLoader.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            violations.Add(new ModSchemaViolation(
                pointer, line, $"Scroll item fails the frozen scroll schema: {ex.Message}"));
        }
    }

    private static void ValidateDslPlacement(
        ContentType type,
        JsonElement item,
        string pointer,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        ModDslFamily? allowed = DslFamilyOf(type);
        string? allowedField = DslFieldOf(type);
        string[] present = new[] { "on_hit", "world_ops", "system_commands" }
            .Where(f => item.TryGetProperty(f, out _))
            .ToArray();

        foreach (string field in present)
        {
            string fieldPointer = pointer + "/" + field;
            int fieldLine = ModJsonLineMap.LineOf(lines, fieldPointer);
            if (field != allowedField)
            {
                violations.Add(new ModSchemaViolation(
                    fieldPointer, fieldLine,
                    $"DSL field '{field}' is not valid on {type} content (allowed: {allowedField ?? "none"}).",
                    ModErrorKind.Dsl));
                continue;
            }

            if (item.TryGetProperty(field, out JsonElement array)
                && array.ValueKind != JsonValueKind.Array)
            {
                violations.Add(new ModSchemaViolation(
                    fieldPointer, fieldLine, $"DSL field '{field}' must be an array of op objects.",
                    ModErrorKind.Dsl));
                continue;
            }

            int index = 0;
            foreach (JsonElement entry in item.GetProperty(field).EnumerateArray())
            {
                violations.AddRange(ModDslTable.ValidateEntry(
                    allowed!.Value, entry, fieldPointer + "/" + index, lines));
                index++;
            }
        }
    }

    private static void ValidateField(
        ModFieldRule rule,
        JsonElement item,
        string pointer,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        string fieldPointer = pointer + "/" + rule.Name;
        if (!item.TryGetProperty(rule.Name, out JsonElement value)
            || (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (rule.Required && rule.Type is not (ModFieldType.NullableNumber or ModFieldType.NullableInteger))
            {
                violations.Add(new ModSchemaViolation(
                    pointer, ModJsonLineMap.LineOf(lines, pointer),
                    $"missing required field '{rule.Name}'."));
            }

            return;
        }

        int line = ModJsonLineMap.LineOf(lines, fieldPointer);
        switch (rule.Type)
        {
            case ModFieldType.Text:
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    violations.Add(new ModSchemaViolation(fieldPointer, line, $"'{rule.Name}' must be a non-empty string."));
                }

                break;
            case ModFieldType.Enum:
                if (value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } text
                    || rule.EnumValues is not { } values
                    || !values.Contains(text))
                {
                    violations.Add(new ModSchemaViolation(
                        fieldPointer, line,
                        $"'{rule.Name}' must be one of {string.Join('|', (rule.EnumValues ?? new HashSet<string>()).OrderBy(static v => v, StringComparer.Ordinal))}."));
                }

                break;
            case ModFieldType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    violations.Add(new ModSchemaViolation(fieldPointer, line, $"'{rule.Name}' must be a boolean."));
                }

                break;
            case ModFieldType.Number:
            case ModFieldType.Integer:
            case ModFieldType.NullableNumber:
            case ModFieldType.NullableInteger:
                ValidateNumber(rule, value, fieldPointer, line, violations);
                break;
            case ModFieldType.TextArray:
                if (value.ValueKind != JsonValueKind.Array
                    || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
                {
                    violations.Add(new ModSchemaViolation(
                        fieldPointer, line, $"'{rule.Name}' must be an array of non-empty strings."));
                }

                break;
            case ModFieldType.ClosedObject:
                ValidateClosedObject(rule, value, fieldPointer, line, lines, violations);
                break;
            case ModFieldType.TraitObject:
                ValidateTraitObject(rule, value, fieldPointer, line, lines, violations);
                break;
            default:
                violations.Add(new ModSchemaViolation(fieldPointer, line, $"field kind {rule.Type} has no validation arm."));
                break;
        }
    }

    private static void ValidateNumber(
        ModFieldRule rule,
        JsonElement value,
        string fieldPointer,
        int line,
        List<ModSchemaViolation> violations)
    {
        bool integral = rule.Type is ModFieldType.Integer or ModFieldType.NullableInteger;
        double number = 0;
        bool isNumber = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
        if (!isNumber
            || !double.IsFinite(number)
            || number < (rule.Min ?? double.NegativeInfinity)
            || number > (rule.Max ?? double.PositiveInfinity)
            || (integral && number != Math.Floor(number)))
        {
            string domain = rule.Min is { } min && rule.Max is { } max
                ? $" in [{min}, {max}]"
                : rule.Min is { } onlyMin ? $" >= {onlyMin}" : string.Empty;
            violations.Add(new ModSchemaViolation(
                fieldPointer, line,
                $"'{rule.Name}' must be {(integral ? "an integer" : "a finite number")}{domain} (got {Summarize(value)})."));
        }
    }

    private static void ValidateClosedObject(
        ModFieldRule rule,
        JsonElement value,
        string fieldPointer,
        int line,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            violations.Add(new ModSchemaViolation(fieldPointer, line, $"'{rule.Name}' must be an object."));
            return;
        }

        foreach (ModFieldRule child in rule.Children ?? Array.Empty<ModFieldRule>())
        {
            ValidateField(child, value, fieldPointer, lines, violations);
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
            {
                continue;
            }

            if ((rule.Children ?? Array.Empty<ModFieldRule>()).All(c => c.Name != property.Name))
            {
                violations.Add(new ModSchemaViolation(
                    fieldPointer + "/" + property.Name,
                    ModJsonLineMap.LineOf(lines, fieldPointer + "/" + property.Name),
                    $"'{rule.Name}' carries unknown key '{property.Name}' (closed block)."));
            }
        }
    }

    private static void ValidateTraitObject(
        ModFieldRule rule,
        JsonElement value,
        string fieldPointer,
        int line,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            violations.Add(new ModSchemaViolation(fieldPointer, line, $"'{rule.Name}' must be an object."));
            return;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
            {
                continue;
            }

            if (rule.TraitKeys is not { } keys || !keys.Contains(property.Name))
            {
                violations.Add(new ModSchemaViolation(
                    fieldPointer + "/" + property.Name,
                    ModJsonLineMap.LineOf(lines, fieldPointer + "/" + property.Name),
                    $"'{rule.Name}' carries unknown trait '{property.Name}' (closed SP-1b trait set)."));
                continue;
            }

            bool isTextList = property.Name == "aura_effects";
            bool ok = isTextList
                ? property.Value.ValueKind == JsonValueKind.Array
                  && property.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
            if (!ok)
            {
                violations.Add(new ModSchemaViolation(
                    fieldPointer + "/" + property.Name,
                    ModJsonLineMap.LineOf(lines, fieldPointer + "/" + property.Name),
                    $"'{rule.Name}.{property.Name}' must be {(isTextList ? "an array of strings" : "a number or boolean")}."));
            }
        }
    }

    private static string Summarize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"'{value.GetString()}'",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True or JsonValueKind.False => value.ToString(),
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "absent",
        _ => value.ValueKind.ToString(),
    };
}
