using System.Text.Json;
using EraCore.Contracts.Actions;

namespace EraCore.Mods;

/// <summary>
/// The closed vocabulary families mod content may script (T-103, F-094 /
/// F-158): 效果原语 (effect primitives) / 世界操作 (world operations) /
/// 系统命令 (system commands) — recovered v2.1 §16 item 5, G.1 item 5.
/// Every family is CLOSED: an unknown op is a schema violation reported with
/// file + line, never a silent skip; the verb list of family 3 IS the K-3
/// 19-verb list (动词清单不可由模组扩展, v2.1 §16 item 4) — sourced from
/// <see cref="SystemActionKind"/> so it cannot drift from the action
/// registry.
/// </summary>
public enum ModDslFamily
{
    /// <summary>
    /// 效果原语 — <c>on_hit[]</c> arrays on Building / Projectile /
    /// StatusEffect items: numeric combat effect primitives (grounded in the
    /// F.1 scroll effect set and the SP-1b trait block vocabulary).
    /// </summary>
    EffectPrimitive,

    /// <summary>
    /// 世界操作 — <c>world_ops[]</c> arrays on Era / WaveGroup / Level
    /// items: deterministic operations on world/simulation state, grounded
    /// in existing engine capabilities (economy grants, recon reveals,
    /// rally stances, wave rosters, terrain costs).
    /// </summary>
    WorldOperation,

    /// <summary>
    /// 系统命令 — <c>system_commands[]</c> arrays on Scroll / Hero items:
    /// advisor verbs from the closed K-3 list. Field-level validation of the
    /// arguments happens at dispatch time by the K-3 validator
    /// (透传校验 — the mod gate checks verb membership and entry shape only).
    /// </summary>
    SystemCommand,
}

/// <summary>
/// One closed-schema violation of a mod content item or DSL entry: JSON
/// pointer of the offending token plus the 1-based source line resolved
/// through <see cref="ModJsonLineMap"/>. Shared by the DSL family validator
/// (<see cref="ModDslTable"/>) and the per-type item schema
/// (<see cref="ModContentSchema"/>) so the loader aggregates one error shape.
/// </summary>
/// <param name="Pointer">JSON pointer of the offending entry or arg (e.g. <c>/items/2/on_hit/1</c>).</param>
/// <param name="Line">1-based source line; 0 when unresolvable.</param>
/// <param name="Message">Human-readable violation detail.</param>
/// <param name="Kind">Error category (DSL family violations mark themselves).</param>
public sealed record ModSchemaViolation(
    string Pointer,
    int Line,
    string Message,
    ModErrorKind Kind = ModErrorKind.Schema);

/// <summary>
/// One arg rule of an effect-primitive op: name, required, numeric kind and
/// bounds (all bounds stored as doubles so no integer literal can collide
/// with content numeric anchors — RL7 spirit inside schema tables too).
/// </summary>
/// <param name="Name">Arg name on the wire.</param>
/// <param name="Kind">Numeric kind: int or number.</param>
/// <param name="Required">True when the arg must be present.</param>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound; null = unbounded.</param>
public sealed record ModDslArgRule(
    string Name,
    ModDslArgKind Kind,
    bool Required,
    double Min,
    double? Max = null);

/// <summary>Value shape a DSL arg accepts.</summary>
public enum ModDslArgKind
{
    /// <summary>Integral number (no fraction).</summary>
    Int,

    /// <summary>Any finite number.</summary>
    Number,

    /// <summary>Non-empty string.</summary>
    Text,

    /// <summary>Non-empty string inside a closed enum set.</summary>
    Enum,
}

/// <summary>Immutable op rule: op name + arg rules (+ enum values for enum args).</summary>
public sealed record ModDslOpRule(string Op, IReadOnlyList<ModDslArgRule> Args);

/// <summary>
/// Closed DSL three-family tables + per-entry validation (T-103). Pure
/// functions: no state, no I/O; every rejection carries a pointer + line so
/// the mod loader can surface 行级报错 for unknown ops and malformed args.
/// </summary>
public static class ModDslTable
{
    private const double PercentMax = 100.0; // percent domain ceiling (schema bound, not a game number)

    private static ModDslArgRule Num(string name, double min, double? max = null) =>
        new(name, ModDslArgKind.Number, Required: true, Min: min, Max: max);

    private static ModDslArgRule IntArg(string name, double min) =>
        new(name, ModDslArgKind.Int, Required: true, Min: min);

    private static ModDslArgRule Txt(string name) =>
        new(name, ModDslArgKind.Text, Required: true, Min: 0);

    /// <summary>
    /// 效果原语 op table (family 1). Grounded: slow/root/splash/burn in the
    /// SP-1b trait block vocabulary (spike trap slow 30% / 120 ticks, splash
    /// radius cells, burn dps+ticks), attack-speed/heal/range/zone-damage in
    /// the F.1 scroll effect set (tower_class_attack_speed, tower_heal_percent,
    /// support_aura_range_bonus, zone_damage_bonus).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ModDslOpRule> EffectPrimitives =
        new Dictionary<string, ModDslOpRule>(StringComparer.Ordinal)
        {
            ["damage_bonus_percent"] = new("damage_bonus_percent", new[] { Num("percent", 0, PercentMax) }),
            ["zone_damage_bonus"] = new("zone_damage_bonus", new[] { Num("percent", 0, PercentMax) }),
            ["slow_percent"] = new("slow_percent", new[] { Num("percent", 0, PercentMax), IntArg("ticks", 0) }),
            ["root_ticks"] = new("root_ticks", new[] { IntArg("ticks", 0) }),
            ["burn_dps"] = new("burn_dps", new[] { Num("dps", 0), IntArg("ticks", 0) }),
            ["splash_radius_cells"] = new("splash_radius_cells", new[] { Num("radius_cells", 0) }),
            ["heal_percent"] = new("heal_percent", new[] { Num("percent", 0, PercentMax) }),
            ["attack_speed_bonus_percent"] = new("attack_speed_bonus_percent", new[] { Num("percent", 0) }),
            ["range_bonus_cells"] = new("range_bonus_cells", new[] { Num("cells", 0) }),
        };

    /// <summary>世界操作 op table (family 2), grounded in engine capabilities.</summary>
    public static readonly IReadOnlyDictionary<string, ModDslOpRule> WorldOperations =
        new Dictionary<string, ModDslOpRule>(StringComparer.Ordinal)
        {
            ["grant_resource"] = new("grant_resource", new[] { Txt("resource"), Num("amount", 0) }),
            ["reveal_zone"] = new("reveal_zone", new[] { Txt("zone") }),
            ["set_lane_stance"] = new("set_lane_stance", new[]
            {
                Txt("lane"),
                new ModDslArgRule("stance", ModDslArgKind.Enum, Required: true, Min: 0),
            }),
            ["spawn_wave_group"] = new("spawn_wave_group", new[] { Txt("wave_group") }),
            ["set_terrain_cost"] = new("set_terrain_cost", new[]
            {
                IntArg("x", 0),
                IntArg("y", 0),
                Num("cost", 0),
            }),
        };

    /// <summary>Closed enum of the <c>set_lane_stance.stance</c> arg (K-3 SetRally values).</summary>
    public static readonly IReadOnlySet<string> LaneStanceValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "hold", "fall", "push",
    };

    /// <summary>
    /// 系统命令 verb set (family 3) — exactly the K-3 19 verbs, derived from
    /// <see cref="SystemActionKind"/> so mod content can never extend it.
    /// </summary>
    public static readonly IReadOnlySet<string> SystemVerbs = new HashSet<string>(
        Enum.GetNames<SystemActionKind>(), StringComparer.Ordinal);

    /// <summary>Entry field carrying the op/verb name, per family.</summary>
    public static string OpField(ModDslFamily family) => family switch
    {
        ModDslFamily.SystemCommand => "verb",
        _ => "op",
    };

    /// <summary>Names the closed op table of one family (error copy).</summary>
    public static string FamilyName(ModDslFamily family) => family switch
    {
        ModDslFamily.EffectPrimitive => "effect primitives (on_hit)",
        ModDslFamily.WorldOperation => "world operations (world_ops)",
        ModDslFamily.SystemCommand => "system commands (system_commands)",
        _ => family.ToString(),
    };

    /// <summary>
    /// Validates ONE DSL array entry against its family's closed table.
    /// Violations: unknown op; entry not an object; missing/ill-typed/
    /// out-of-range args; unknown non-annotation entry keys (closed shape —
    /// keys are op/verb + declared args + <c>_</c>-prefixed annotations).
    /// Array-level shape (must be an array of objects) is the caller's.
    /// </summary>
    /// <param name="family">Family whose table applies.</param>
    /// <param name="entry">The array entry element.</param>
    /// <param name="pointer">JSON pointer of the entry (e.g. <c>/items/0/on_hit/1</c>).</param>
    /// <param name="lines">Line map from <see cref="ModJsonLineMap.Index"/>.</param>
    public static IReadOnlyList<ModSchemaViolation> ValidateEntry(
        ModDslFamily family,
        JsonElement entry,
        string pointer,
        IReadOnlyDictionary<string, int> lines)
    {
        // Every violation this validator emits is a DSL-family violation —
        // tag the kind once at the boundary (loader maps it to ModErrorKind.Dsl).
        return ValidateEntryCore(family, entry, pointer, lines)
            .Select(v => v with { Kind = ModErrorKind.Dsl })
            .ToList();
    }

    private static IReadOnlyList<ModSchemaViolation> ValidateEntryCore(
        ModDslFamily family,
        JsonElement entry,
        string pointer,
        IReadOnlyDictionary<string, int> lines)
    {
        var violations = new List<ModSchemaViolation>();
        int line = ModJsonLineMap.LineOf(lines, pointer);

        if (entry.ValueKind != JsonValueKind.Object)
        {
            violations.Add(new ModSchemaViolation(pointer, line, $"{FamilyName(family)} entry must be a JSON object."));
            return violations;
        }

        string opField = OpField(family);
        if (!entry.TryGetProperty(opField, out JsonElement opElement)
            || opElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(opElement.GetString()))
        {
            violations.Add(new ModSchemaViolation(
                pointer, line, $"{FamilyName(family)} entry requires a non-empty '{opField}'."));
            return violations;
        }

        string op = opElement.GetString()!;
        if (family == ModDslFamily.SystemCommand)
        {
            if (!SystemVerbs.Contains(op))
            {
                violations.Add(new ModSchemaViolation(
                    pointer, line,
                    $"{FamilyName(family)}: unknown verb '{op}' — the 19-verb K-3 list is closed (动词不可扩展)."));
                return violations;
            }
        }
        else
        {
            IReadOnlyDictionary<string, ModDslOpRule> table =
                family == ModDslFamily.EffectPrimitive ? EffectPrimitives : WorldOperations;
            if (!table.TryGetValue(op, out ModDslOpRule? rule))
            {
                violations.Add(new ModSchemaViolation(
                    pointer, line,
                    $"{FamilyName(family)}: unknown op '{op}' (closed set: {string.Join('|', table.Keys.OrderBy(static k => k, StringComparer.Ordinal))})."));
                return violations;
            }

            ValidateArgs(rule, entry, pointer, lines, violations);
        }

        // Closed entry shape: op/verb + declared args + '_'-annotations only.
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            bool known = property.Name == opField || property.Name.StartsWith('_');
            if (!known)
            {
                known = family switch
                {
                    ModDslFamily.SystemCommand => property.Name == "args",
                    _ => TryGetRule(family, op)?.Args.Any(a => a.Name == property.Name) == true,
                };
            }

            if (!known)
            {
                violations.Add(new ModSchemaViolation(
                    pointer + "/" + property.Name,
                    ModJsonLineMap.LineOf(lines, pointer + "/" + property.Name),
                    $"{FamilyName(family)} entry '{op}' carries undeclared key '{property.Name}' (closed shape)."));
            }
        }

        return violations;
    }

    private static ModDslOpRule? TryGetRule(ModDslFamily family, string op) =>
        family == ModDslFamily.EffectPrimitive && EffectPrimitives.TryGetValue(op, out ModDslOpRule? effect)
            ? effect
            : family == ModDslFamily.WorldOperation && WorldOperations.TryGetValue(op, out ModDslOpRule? world)
                ? world
                : null;

    private static void ValidateArgs(
        ModDslOpRule rule,
        JsonElement entry,
        string pointer,
        IReadOnlyDictionary<string, int> lines,
        List<ModSchemaViolation> violations)
    {
        foreach (ModDslArgRule arg in rule.Args)
        {
            string argPointer = pointer + "/" + arg.Name;
            if (!entry.TryGetProperty(arg.Name, out JsonElement value))
            {
                if (arg.Required)
                {
                    violations.Add(new ModSchemaViolation(
                        pointer, ModJsonLineMap.LineOf(lines, pointer),
                        $"op '{rule.Op}' requires arg '{arg.Name}'."));
                }

                continue;
            }

            int line = ModJsonLineMap.LineOf(lines, argPointer);
            switch (arg.Kind)
            {
                case ModDslArgKind.Int:
                    double intAsDouble = 0;
                    if (value.ValueKind != JsonValueKind.Number
                        || !value.TryGetDouble(out intAsDouble)
                        || !double.IsFinite(intAsDouble)
                        || intAsDouble != Math.Floor(intAsDouble)
                        || intAsDouble < arg.Min
                        || (arg.Max is { } maxInt && intAsDouble > maxInt))
                    {
                        violations.Add(new ModSchemaViolation(
                            argPointer, line, $"arg '{arg.Name}' of '{rule.Op}' must be an integer >= {arg.Min}."));
                    }

                    break;
                case ModDslArgKind.Number:
                    double number = 0;
                    if (value.ValueKind != JsonValueKind.Number
                        || !value.TryGetDouble(out number)
                        || !double.IsFinite(number)
                        || number < arg.Min
                        || (arg.Max is { } maxNumber && number > maxNumber))
                    {
                        violations.Add(new ModSchemaViolation(
                            argPointer, line,
                            $"arg '{arg.Name}' of '{rule.Op}' must be a finite number in [{arg.Min}, {arg.Max?.ToString() ?? "∞"}]."));
                    }

                    break;
                case ModDslArgKind.Text:
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        violations.Add(new ModSchemaViolation(
                            argPointer, line, $"arg '{arg.Name}' of '{rule.Op}' must be a non-empty string."));
                    }

                    break;
                case ModDslArgKind.Enum:
                    if (value.ValueKind != JsonValueKind.String
                        || value.GetString() is not { } text
                        || !LaneStanceValues.Contains(text))
                    {
                        violations.Add(new ModSchemaViolation(
                            argPointer, line,
                            $"arg '{arg.Name}' of '{rule.Op}' must be one of {string.Join('|', LaneStanceValues.OrderBy(static v => v, StringComparer.Ordinal))}."));
                    }

                    break;
                default:
                    violations.Add(new ModSchemaViolation(argPointer, line, $"arg kind {arg.Kind} has no validation arm."));
                    break;
            }
        }
    }
}
