using EraCore.Contracts.Actions;

namespace EraCore.Agent.Actions;

/// <summary>Value shape a field rule accepts (closed set of shapes used by K-3).</summary>
internal enum FieldType
{
    /// <summary>JSON string (non-empty when a min length is declared).</summary>
    String,

    /// <summary>Integral JSON number (no fraction digits after parsing).</summary>
    Integer,

    /// <summary>Any finite JSON number.</summary>
    Number,

    /// <summary>JSON true/false.</summary>
    Boolean,

    /// <summary>Object <c>{x,y}</c> of int32 grid coordinates.</summary>
    Cell,

    /// <summary>Array of <c>{x,y}</c> cells with an era-dependent count cap.</summary>
    CellArray,

    /// <summary>Array of non-empty strings.</summary>
    StringArray,

    /// <summary>Array of positive int64 entity ids.</summary>
    IntegerArray,

    /// <summary>Array of option objects, each carrying one bounded <c>label</c>.</summary>
    OptionArray,
}

/// <summary>
/// Out-of-range semantics of a numeric field (T-070 DEBT-4 ruling,
/// registered in docs/engineering/11-spec-tables.md 变更记录 v1.4):
/// <see cref="Reject"/> is the default closed-schema behavior (violation
/// reported, action denied); <see cref="Clamp"/> rewrites out-of-range
/// values to the nearest boundary instead of denying — the frozen wording
/// of the fields the spec itself marks 钳制 (G.2: SetAlarm
/// after_seconds 钳制[30,600], SP-2 row 10). Clamp rewriting runs in
/// <see cref="ActionSchemaClamper"/> before the closed-schema gate; the
/// validator then never sees an out-of-range value on that field.
/// </summary>
internal enum NumberRangeMode
{
    /// <summary>Out-of-range values are schema violations (default).</summary>
    Reject,

    /// <summary>Out-of-range values are clamped to [MinNumber, MaxNumber] before validation.</summary>
    Clamp,
}

/// <summary>
/// One field constraint of one action verb — the code form of one cell of
/// the recovered v2.1 §11.3 field table (docs/engineering/
/// 10-recovered-v21-architecture.md §三, T-004). Only the constraint slots
/// the frozen table documents are populated; undocumented slots stay null
/// so no bound is invented here.
/// </summary>
/// <param name="Name">Field name on the wire.</param>
/// <param name="Type">Expected value shape.</param>
/// <param name="Required">True when the field must be present.</param>
/// <param name="EnumValues">Closed enum of exact wire strings, when the table fixes one.</param>
/// <param name="MaxLength">Maximum string length in characters.</param>
/// <param name="MinInt">Inclusive lower integer bound.</param>
/// <param name="MaxInt">Inclusive upper integer bound.</param>
/// <param name="MinNumber">Inclusive lower numeric bound.</param>
/// <param name="MaxNumber">Inclusive upper numeric bound.</param>
/// <param name="MustBeTrue">True when an explicit <c>true</c> is mandatory (danger bit).</param>
/// <param name="MinCount">Inclusive lower bound on array item count.</param>
/// <param name="MaxCount">Inclusive upper bound on array item count (fixed cap).</param>
/// <param name="CountCapBase">Era-dependent cap: <c>Base + PerEra × era</c> overrides <see cref="MaxCount"/>.</param>
/// <param name="CountCapPerEra">Era multiplier of the era-dependent cap.</param>
/// <param name="ItemMaxLength">Per-item string length bound inside arrays.</param>
/// <param name="NumberRange">Out-of-range semantics of a Number field: Reject (default) or Clamp (T-070 DEBT-4).</param>
internal sealed record FieldRule(
    string Name,
    FieldType Type,
    bool Required,
    string[]? EnumValues = null,
    int? MaxLength = null,
    long? MinInt = null,
    long? MaxInt = null,
    double? MinNumber = null,
    double? MaxNumber = null,
    bool MustBeTrue = false,
    int? MinCount = null,
    int? MaxCount = null,
    int? CountCapBase = null,
    int? CountCapPerEra = null,
    int? ItemMaxLength = null,
    NumberRangeMode NumberRange = NumberRangeMode.Reject)
{
    /// <summary>Effective array item-count cap at one era ordinal (fixed cap when no era terms exist).</summary>
    public int EffectiveMaxCount(int eraOrdinal)
    {
        if (CountCapBase is { } baseCap && CountCapPerEra is { } perEra)
        {
            return baseCap + (perEra * eraOrdinal);
        }

        return MaxCount ?? int.MaxValue;
    }
}

/// <summary>The closed schema of one verb: its field rules (empty for LookAround's empty interface).</summary>
internal sealed record ActionSchema(string Verb, IReadOnlyList<FieldRule> Fields);

/// <summary>
/// The 19-verb closed-schema table (K-3 code form, T-051): field
/// existence/type/enum/range constraints transcribed from the frozen T-004
/// table with v3.2 overwrites. One row per <see cref="SystemActionKind"/>
/// verb; the discriminating <c>action</c> field itself is validated by
/// <see cref="JsonSchemaValidator"/> before a row is consulted. The verb
/// list is closed — unknown verbs and unknown fields are both violations.
/// </summary>
internal static class ActionSchemaTable
{
    /// <summary>Wire verb of the discriminator field (ISchemaValidator contract).</summary>
    public const string Discriminator = "action";

    /// <summary>Schema lookup by exact wire verb name.</summary>
    public static bool TryGet(string? verb, out ActionSchema schema)
    {
        if (verb is not null && Schemas.TryGetValue(verb, out ActionSchema? found))
        {
            schema = found;
            return true;
        }

        schema = null!;
        return false;
    }

    /// <summary>All 19 schemas keyed by wire verb (SystemActionKind names).</summary>
    public static readonly IReadOnlyDictionary<string, ActionSchema> Schemas =
        new Dictionary<string, ActionSchema>(StringComparer.Ordinal)
        {
            // Recon — v2.1: empty interface; cell+aspect; key+scope+head_limit≤20.
            [nameof(SystemActionKind.LookAround)] = new(
                nameof(SystemActionKind.LookAround), Array.Empty<FieldRule>()),
            [nameof(SystemActionKind.InspectTile)] = new(
                nameof(SystemActionKind.InspectTile),
                new FieldRule[]
                {
                    new("cell", FieldType.Cell, Required: true),
                    new("aspect", FieldType.String, Required: true, EnumValues: new[] { "stats", "threat", "supply" }),
                }),
            [nameof(SystemActionKind.QueryWorld)] = new(
                nameof(SystemActionKind.QueryWorld),
                new FieldRule[]
                {
                    new("key", FieldType.String, Required: true, EnumValues: new[] { "wave", "resource", "threat", "paths" }),
                    new("scope", FieldType.String, Required: true),
                    new("head_limit", FieldType.Integer, Required: false, MinInt: 1, MaxInt: 20), // ci-allow: R7 T-051 K-3 head_limit≤20 行数上限（v2.1 §11.3 冻结域常量）
                }),

            // Construction — tower_id+cells (cap 4+2×era)+confirm_overwrite;
            // target_id+to_tier 1..4; SellTower's explicit danger bit;
            // lane+stance.
            [nameof(SystemActionKind.BuildPlan)] = new(
                nameof(SystemActionKind.BuildPlan),
                new FieldRule[]
                {
                    new("tower_id", FieldType.String, Required: true),
                    new("cells", FieldType.CellArray, Required: true, MinCount: 1, CountCapBase: 4, CountCapPerEra: 2), // ci-allow: R7 T-051 K-3 cells≤4+2×时代序号（v2.1 §11.3 冻结域常量，每时代+2 的 2）
                    new("confirm_overwrite", FieldType.Boolean, Required: false),
                }),
            [nameof(SystemActionKind.UpgradeTower)] = new(
                nameof(SystemActionKind.UpgradeTower),
                new FieldRule[]
                {
                    new("target_id", FieldType.Integer, Required: true, MinInt: 1),
                    new("to_tier", FieldType.Integer, Required: true, MinInt: 1, MaxInt: 4),
                }),
            [nameof(SystemActionKind.SellTower)] = new(
                nameof(SystemActionKind.SellTower),
                new FieldRule[]
                {
                    new("target_id", FieldType.Integer, Required: true, MinInt: 1),
                    new("confirm_sell", FieldType.Boolean, Required: true, MustBeTrue: true),
                }),
            [nameof(SystemActionKind.SetRally)] = new(
                nameof(SystemActionKind.SetRally),
                new FieldRule[]
                {
                    new("lane", FieldType.String, Required: true),
                    new("stance", FieldType.String, Required: true, EnumValues: new[] { "hold", "fall", "push" }),
                }),

            // Logistics — from/to/item/rate 0..1; item_id/min_amount.
            [nameof(SystemActionKind.RerouteLogistics)] = new(
                nameof(SystemActionKind.RerouteLogistics),
                new FieldRule[]
                {
                    new("from", FieldType.Integer, Required: true, MinInt: 1),
                    new("to", FieldType.Integer, Required: true, MinInt: 1),
                    new("item", FieldType.String, Required: true),
                    new("rate", FieldType.Number, Required: true, MinNumber: 0.0, MaxNumber: 1.0),
                }),
            [nameof(SystemActionKind.SetStockpile)] = new(
                nameof(SystemActionKind.SetStockpile),
                new FieldRule[]
                {
                    new("item_id", FieldType.String, Required: true),
                    new("min_amount", FieldType.Number, Required: true, MinNumber: 0.0),
                }),

            // Combat — target_id; target_id[]≤6+mode; scroll_id; order.
            [nameof(SystemActionKind.FocusFire)] = new(
                nameof(SystemActionKind.FocusFire),
                new FieldRule[]
                {
                    new("target_id", FieldType.Integer, Required: true, MinInt: 1),
                }),
            [nameof(SystemActionKind.SetTowerMode)] = new(
                nameof(SystemActionKind.SetTowerMode),
                new FieldRule[]
                {
                    new("target_id", FieldType.IntegerArray, Required: true, MinCount: 1, MaxCount: 6), // ci-allow: R7 T-051 K-3 target_id[]≤6（v2.1 §11.3 冻结域常量）
                    new("mode", FieldType.String, Required: true, EnumValues: new[] { "first", "last", "strong", "closest" }),
                }),
            [nameof(SystemActionKind.TriggerScroll)] = new(
                nameof(SystemActionKind.TriggerScroll),
                new FieldRule[]
                {
                    new("scroll_id", FieldType.String, Required: true),
                }),
            [nameof(SystemActionKind.HeroOrder)] = new(
                nameof(SystemActionKind.HeroOrder),
                new FieldRule[]
                {
                    new("order", FieldType.String, Required: true, EnumValues: new[] { "move", "skill", "retreat" }),
                }),

            // Management — question+options 2..4 (label≤8)+multi_select;
            // summary≤200+findings≤8+verdict; todos rewrite; lane+duration
            // [15,120]; after_seconds [30,600]+reason; task_id.
            [nameof(SystemActionKind.AskPlayer)] = new(
                nameof(SystemActionKind.AskPlayer),
                new FieldRule[]
                {
                    new("question", FieldType.String, Required: true),
                    new("options", FieldType.OptionArray, Required: true, MinCount: 2, MaxCount: 4, ItemMaxLength: 8), // ci-allow: R7 T-051 K-3 options 2~4 项/label≤8 字（v2.1 §11.3 冻结域常量）
                    new("multi_select", FieldType.Boolean, Required: true),
                }),
            [nameof(SystemActionKind.ReportToPlayer)] = new(
                nameof(SystemActionKind.ReportToPlayer),
                new FieldRule[]
                {
                    new("summary", FieldType.String, Required: true, MaxLength: 200),
                    new("findings", FieldType.StringArray, Required: false, MaxCount: 8, ItemMaxLength: 200), // ci-allow: R7 T-051 K-3 findings≤8 条（v2.1 §11.3 冻结域常量）
                    new("verdict", FieldType.String, Required: true, EnumValues: new[] { "CONFIRMED", "RUMOR" }),
                }),
            [nameof(SystemActionKind.TodoBoard)] = new(
                nameof(SystemActionKind.TodoBoard),
                new FieldRule[]
                {
                    new("todos", FieldType.StringArray, Required: true),
                }),
            [nameof(SystemActionKind.SpawnScout)] = new(
                nameof(SystemActionKind.SpawnScout),
                new FieldRule[]
                {
                    new("lane", FieldType.String, Required: true),
                    new("duration", FieldType.Number, Required: true, MinNumber: 15.0, MaxNumber: 120.0),
                }),
            [nameof(SystemActionKind.SetAlarm)] = new(
                nameof(SystemActionKind.SetAlarm),
                new FieldRule[]
                {
                    // 钳制语义（T-070 DEBT-4 裁定，SP-2 #10/G.2 原文"钳制[30,600]"）：
                    // 越界值由 ActionSchemaClamper 钳到边界后过闸，而非拒绝。
                    new("after_seconds", FieldType.Number, Required: true, MinNumber: 30.0, MaxNumber: 600.0, NumberRange: NumberRangeMode.Clamp), // ci-allow: R7 T-051 K-3 after_seconds 钳制[30,600]s（G.2 冻结域常量）
                    new("reason", FieldType.String, Required: true),
                }),
            [nameof(SystemActionKind.StopTask)] = new(
                nameof(SystemActionKind.StopTask),
                new FieldRule[]
                {
                    new("task_id", FieldType.String, Required: true),
                }),
        };
}
