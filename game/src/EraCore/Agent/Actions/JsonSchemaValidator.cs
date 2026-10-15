using System.Text;
using System.Text.Json;
using EraCore.Contracts.Actions;

namespace EraCore.Agent.Actions;

/// <summary>
/// Real closed-schema validator for the 19 system actions (K-3, C-03,
/// T-051): validates field existence, value types, enum membership, string
/// lengths and numeric ranges against <see cref="ActionSchemaTable"/> using
/// System.Text.Json's <see cref="JsonDocument"/>. Pure function of the
/// input plus the era ordinal the instance was built with (the era feeds
/// the BuildPlan cell cap 4 + 2 × era): no state mutation, no I/O.
/// <para>
/// Errors carry the JSON pointer of the violated constraint plus the 1-based
/// source line of the offending token (a Utf8JsonReader pre-pass indexes
/// every property/array-element position; unknown positions report 0 per
/// the K-3 contract).
/// </para>
/// </summary>
public sealed class JsonSchemaValidator : ISchemaValidator
{
    private readonly Func<int> _eraOrdinal;
    private readonly IReadOnlySet<string>? _buildPlanTowerIds;

    /// <param name="eraOrdinal">
    /// Current era ordinal provider (E1=1 … finale=6). Defaults to E1 —
    /// the caller advances it as the campaign progresses.
    /// </param>
    /// <param name="buildPlanTowerIds">
    /// F-095 dynamic tower enum (T-104): the builtin ∪ registry tower-id
    /// set (<c>ModParamSpace.BuildableTowerIds</c>). When non-null the
    /// BuildPlan <c>tower_id</c> field is additionally validated against
    /// this union — mod towers enter the closed schema's parameter space
    /// ("内置+注册表动态并集"). Null (every existing caller) keeps the
    /// historical behavior EXACTLY: no enum check at the schema gate, the
    /// level-catalog check at the handler stays the backstop — 无模组时
    /// 零漂移. The verb list itself is never mod-extensible (K-3); the
    /// cells cap 4+2×era is frozen law and does not adapt (v2.1 §16
    /// item 4 widens parameter SPACES, not domain constants).
    /// </param>
    public JsonSchemaValidator(Func<int>? eraOrdinal = null, IReadOnlySet<string>? buildPlanTowerIds = null)
    {
        _eraOrdinal = eraOrdinal ?? (static () => EraOrdinals.E1);
        _buildPlanTowerIds = buildPlanTowerIds;
    }

    /// <summary>
    /// Reads the discriminating <c>action</c> verb without running the full
    /// validation. False when the payload is not parseable JSON, not an
    /// object, or carries no string verb (callers then deny as schema
    /// violations; the full error detail still comes from
    /// <see cref="Validate"/>).
    /// </summary>
    public static bool TryReadVerb(string actionJson, out string? verb)
    {
        verb = null;
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(actionJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty(ActionSchemaTable.Discriminator, out JsonElement verbElement)
                || verbElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            verb = verbElement.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public SchemaValidationResult Validate(string actionJson)
    {
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return new SchemaValidationResult(false, new[]
            {
                new SchemaValidationError(string.Empty, 0, "payload must be a non-empty JSON object"),
            });
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(actionJson);
        }
        catch (JsonException ex)
        {
            int line = ex.LineNumber is { } zeroBased ? (int)zeroBased + 1 : 0;
            return new SchemaValidationResult(false, new[]
            {
                new SchemaValidationError(string.Empty, line, "payload is not well-formed JSON (closed-schema gate)"),
            });
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SchemaValidationResult(false, new[]
                {
                    new SchemaValidationError(string.Empty, 1, "root must be a JSON object carrying the 'action' verb"),
                });
            }

            // Line pre-pass: pointer -> 1-based line of the value token.
            Dictionary<string, int> lines = JsonLineIndex.Index(actionJson);
            var errors = new List<SchemaValidationError>();
            JsonElement root = document.RootElement;
            int rootLine = lines.TryGetValue(string.Empty, out int found) ? found : 0;

            // 1) Discriminating verb: present, string, inside the closed list.
            if (!root.TryGetProperty(ActionSchemaTable.Discriminator, out JsonElement verbElement))
            {
                errors.Add(new SchemaValidationError(
                    "/" + ActionSchemaTable.Discriminator, rootLine, "'action' verb is required (closed 19-verb list)"));
            }
            else if (verbElement.ValueKind != JsonValueKind.String)
            {
                errors.Add(new SchemaValidationError(
                    "/" + ActionSchemaTable.Discriminator,
                    LineOf(lines, ActionSchemaTable.Discriminator),
                    "'action' verb must be a string"));
            }
            else if (!ActionSchemaTable.TryGet(verbElement.GetString(), out ActionSchema _))
            {
                errors.Add(new SchemaValidationError(
                    "/" + ActionSchemaTable.Discriminator,
                    LineOf(lines, ActionSchemaTable.Discriminator),
                    "verb is outside the closed 19-action list"));
            }
            else
            {
                // 2) Field rules of the resolved verb.
                ActionSchema schema = ActionSchemaTable.TryGet(verbElement.GetString(), out ActionSchema resolved)
                    ? resolved
                    : null!;
                int eraOrdinal = _eraOrdinal();
                foreach (FieldRule rule in schema.Fields)
                {
                    if (!root.TryGetProperty(rule.Name, out JsonElement value))
                    {
                        if (rule.Required)
                        {
                            errors.Add(new SchemaValidationError(
                                "/" + rule.Name, rootLine, $"required field '{rule.Name}' is missing"));
                        }

                        continue;
                    }

                    ValidateField(rule, value, "/" + rule.Name, lines, eraOrdinal, errors);
                }

                // 2.5) F-095 dynamic tower enum (T-104): only when the caller
                // wired the builtin ∪ registry union — mod tower ids pass the
                // closed schema as first-class parameter values; ids outside
                // the union are schema denials at the gate (the handler's
                // level-catalog check remains the second, era-aware backstop).
                // Unwired (null) = historical behavior, byte-for-byte.
                if (_buildPlanTowerIds is not null
                    && schema.Verb == nameof(SystemActionKind.BuildPlan)
                    && root.TryGetProperty("tower_id", out JsonElement towerIdElement)
                    && towerIdElement.ValueKind == JsonValueKind.String
                    && towerIdElement.GetString() is { Length: > 0 } towerId
                    && !_buildPlanTowerIds.Contains(towerId))
                {
                    errors.Add(new SchemaValidationError(
                        "/tower_id",
                        LineOf(lines, "tower_id"),
                        $"'tower_id' '{towerId}' is outside the builtin+registry tower union (F-095 mod parameter space)"));
                }

                // 3) Closed schema: fields outside the table are violations too.
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (property.Name == ActionSchemaTable.Discriminator)
                    {
                        continue;
                    }

                    bool declared = false;
                    foreach (FieldRule rule in schema.Fields)
                    {
                        if (rule.Name == property.Name)
                        {
                            declared = true;
                            break;
                        }
                    }

                    if (!declared)
                    {
                        errors.Add(new SchemaValidationError(
                            "/" + property.Name,
                            LineOf(lines, property.Name),
                            $"field '{property.Name}' is outside the closed schema of '{schema.Verb}'"));
                    }
                }
            }

            return errors.Count == 0
                ? new SchemaValidationResult(true, Array.Empty<SchemaValidationError>())
                : new SchemaValidationResult(false, errors);
        }
    }

    /// <summary>Line of a bare root field name (adds the leading pointer slash).</summary>
    private static int LineOf(Dictionary<string, int> lines, string pointerOrField) =>
        lines.TryGetValue("/" + pointerOrField.TrimStart('/'), out int line) ? line : 0;

    /// <summary>Line of an already-formed pointer (e.g. <c>/cells/3</c>).</summary>
    private static int LineAt(Dictionary<string, int> lines, string pointer) =>
        lines.TryGetValue(pointer, out int line) ? line : 0;

    private static void ValidateField(
        FieldRule rule,
        JsonElement value,
        string pointer,
        Dictionary<string, int> lines,
        int eraOrdinal,
        List<SchemaValidationError> errors)
    {
        int line = lines.TryGetValue(pointer, out int found) ? found : 0;
        switch (rule.Type)
        {
            case FieldType.String:
                ValidateString(rule, value, pointer, line, errors);
                break;

            case FieldType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long integer))
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be an integer"));
                    break;
                }

                ValidateIntRange(rule, integer, pointer, line, errors);
                break;

            case FieldType.Number:
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDouble(out double number)
                    || !double.IsFinite(number))
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be a finite number"));
                    break;
                }

                // Clamp fields (T-070 DEBT-4): out-of-range values are
                // rewritten to the boundary by ActionSchemaClamper BEFORE
                // this gate — the declared semantic is 钳制 not rejection
                // (SP-2 #10/G.2), so the validator enforces type/finiteness
                // only and leaves the range to the clamp rewrite.
                if (rule.NumberRange == NumberRangeMode.Reject
                    && ((rule.MinNumber is { } min && number < min) || (rule.MaxNumber is { } max && number > max)))
                {
                    errors.Add(new SchemaValidationError(
                        pointer, line,
                        $"'{rule.Name}' must lie in [{rule.MinNumber?.ToString() ?? "-∞"},{rule.MaxNumber?.ToString() ?? "∞"}]"));
                }

                break;

            case FieldType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be a boolean"));
                    break;
                }

                if (rule.MustBeTrue && value.ValueKind != JsonValueKind.True)
                {
                    errors.Add(new SchemaValidationError(
                        pointer, line, $"'{rule.Name}' must be explicitly true (danger bit)"));
                }

                break;

            case FieldType.Cell:
                ValidateCell(value, pointer, line, errors);
                break;

            case FieldType.CellArray:
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be an array of cells"));
                    break;
                }

                int count = value.GetArrayLength();
                int maxCount = rule.EffectiveMaxCount(eraOrdinal);
                if (count < rule.MinCount || count > maxCount)
                {
                    errors.Add(new SchemaValidationError(
                        pointer, line,
                        $"'{rule.Name}' item count must lie in [{rule.MinCount},{maxCount}] at era {eraOrdinal}"));
                }

                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ValidateCell(item, $"{pointer}/{index}", LineAt(lines, $"{pointer}/{index}"), errors);
                    index++;
                }

                break;
            }

            case FieldType.StringArray:
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be an array of strings"));
                    break;
                }

                ValidateArrayCount(rule, value.GetArrayLength(), eraOrdinal, pointer, line, errors);
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ValidateString(
                        rule with { Name = rule.Name + "[" + index + "]", MaxLength = rule.ItemMaxLength ?? rule.MaxLength },
                        item,
                        $"{pointer}/{index}",
                        LineAt(lines, $"{pointer}/{index}"),
                        errors);
                    index++;
                }

                break;
            }

            case FieldType.IntegerArray:
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be an array of entity ids"));
                    break;
                }

                ValidateArrayCount(rule, value.GetArrayLength(), eraOrdinal, pointer, line, errors);
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string itemPointer = $"{pointer}/{index}";
                    int itemLine = LineAt(lines, itemPointer);
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out long id))
                    {
                        errors.Add(new SchemaValidationError(itemPointer, itemLine, $"'{rule.Name}' items must be integers"));
                    }
                    else
                    {
                        ValidateIntRange(rule with { Name = rule.Name + "[" + index + "]" }, id, itemPointer, itemLine, errors);
                    }

                    index++;
                }

                break;
            }

            case FieldType.OptionArray:
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be an array of option objects"));
                    break;
                }

                ValidateArrayCount(rule, value.GetArrayLength(), eraOrdinal, pointer, line, errors);
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string itemPointer = $"{pointer}/{index}";
                    int itemLine = LineAt(lines, itemPointer);
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(new SchemaValidationError(itemPointer, itemLine, $"'{rule.Name}' items must be objects with a 'label'"));
                        index++;
                        continue;
                    }

                    if (!item.TryGetProperty("label", out JsonElement label)
                        || label.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(label.GetString()))
                    {
                        errors.Add(new SchemaValidationError(
                            $"{itemPointer}/label", itemLine, $"'{rule.Name}' items require a non-empty 'label' string"));
                    }
                    else if (rule.ItemMaxLength is { } limit && label.GetString()!.Length > limit)
                    {
                        errors.Add(new SchemaValidationError(
                            $"{itemPointer}/label", itemLine, $"'label' length must be ≤ {limit} characters"));
                    }

                    foreach (JsonProperty property in item.EnumerateObject())
                    {
                        if (property.Name != "label")
                        {
                            errors.Add(new SchemaValidationError(
                                $"{itemPointer}/{property.Name}", itemLine,
                                $"'{rule.Name}' items carry no field but 'label'"));
                        }
                    }

                    index++;
                }

                break;
            }

            default:
                errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' has an unsupported field shape"));
                break;
        }
    }

    private static void ValidateString(FieldRule rule, JsonElement value, string pointer, int line, List<SchemaValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be a string"));
            return;
        }

        string text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' must be a non-empty string"));
            return;
        }

        if (rule.MaxLength is { } maxLength && text.Length > maxLength)
        {
            errors.Add(new SchemaValidationError(pointer, line, $"'{rule.Name}' length must be ≤ {maxLength} characters"));
            return;
        }

        if (rule.EnumValues is { } values)
        {
            bool inside = false;
            foreach (string candidate in values)
            {
                if (string.Equals(candidate, text, StringComparison.Ordinal))
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                errors.Add(new SchemaValidationError(
                    pointer, line, $"'{rule.Name}' must be one of [{string.Join("|", values)}]"));
            }
        }
    }

    private static void ValidateIntRange(FieldRule rule, long integer, string pointer, int line, List<SchemaValidationError> errors)
    {
        if ((rule.MinInt is { } min && integer < min) || (rule.MaxInt is { } max && integer > max))
        {
            errors.Add(new SchemaValidationError(
                pointer, line,
                $"'{rule.Name}' must lie in [{rule.MinInt?.ToString() ?? "-∞"},{rule.MaxInt?.ToString() ?? "∞"}]"));
        }
    }

    private static void ValidateArrayCount(FieldRule rule, int count, int eraOrdinal, string pointer, int line, List<SchemaValidationError> errors)
    {
        int maxCount = rule.EffectiveMaxCount(eraOrdinal);
        if (count < rule.MinCount || count > maxCount)
        {
            errors.Add(new SchemaValidationError(
                pointer, line,
                $"'{rule.Name}' item count must lie in [{rule.MinCount},{maxCount}]"));
        }
    }

    private static void ValidateCell(JsonElement value, string pointer, int line, List<SchemaValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SchemaValidationError(pointer, line, "cell must be an object {x,y}"));
            return;
        }

        if (!value.TryGetProperty("x", out JsonElement x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out _))
        {
            errors.Add(new SchemaValidationError(pointer + "/x", line, "cell 'x' must be an int32"));
        }

        if (!value.TryGetProperty("y", out JsonElement y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out _))
        {
            errors.Add(new SchemaValidationError(pointer + "/y", line, "cell 'y' must be an int32"));
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name is not ("x" or "y"))
            {
                errors.Add(new SchemaValidationError(
                    pointer + "/" + property.Name, line, "cell carries no fields but 'x' and 'y'"));
            }
        }
    }

    /// <summary>
    /// One Utf8JsonReader pass recording the byte offset
    /// (<see cref="Utf8JsonReader.TokenStartIndex"/>) of every value token
    /// under its JSON pointer ("" root, "/action", "/cells/3",
    /// "/cells/3/x", …), then resolved to 1-based lines against the raw
    /// bytes (net8's reader exposes no line property — offsets are exact
    /// and the newline scan is linear once). Used so validation errors can
    /// cite source lines (K-3 SchemaValidationError contract).
    /// </summary>
    private sealed class JsonLineIndex
    {
        public static Dictionary<string, int> Index(string json)
        {
            var offsets = new Dictionary<string, long>();
            try
            {
                var reader = new Utf8JsonReader(
                    Encoding.UTF8.GetBytes(json),
                    new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
                if (!reader.Read())
                {
                    return new Dictionary<string, int>();
                }

                IndexValue(ref reader, offsets, string.Empty);
            }
            catch (JsonException)
            {
                // Malformed payloads never reach the line lookup — the
                // JsonDocument parse reports them first; keep what was indexed.
            }

            // Resolve byte offsets to 1-based lines: line 1 starts at 0,
            // every '\n' opens the next line.
            var lineStarts = new List<long> { 0 };
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    lineStarts.Add(i + 1L);
                }
            }

            var lines = new Dictionary<string, int>(offsets.Count);
            foreach (KeyValuePair<string, long> entry in offsets)
            {
                int line = lineStarts.BinarySearch(entry.Value);
                if (line < 0)
                {
                    line = ~line - 1; // index of the last line start ≤ offset
                }

                lines[entry.Key] = line + 1;
            }

            return lines;
        }

        private static void IndexValue(ref Utf8JsonReader reader, Dictionary<string, long> offsets, string pointer)
        {
            long offset = reader.TokenStartIndex;
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (!offsets.ContainsKey(pointer))
                    {
                        offsets[pointer] = offset;
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        string name = reader.GetString() ?? string.Empty;
                        string childPointer = pointer + "/" + name;
                        offsets[childPointer] = reader.TokenStartIndex;
                        if (!reader.Read())
                        {
                            return;
                        }

                        IndexValue(ref reader, offsets, childPointer);
                    }

                    break;

                case JsonTokenType.StartArray:
                    if (!offsets.ContainsKey(pointer))
                    {
                        offsets[pointer] = offset;
                    }

                    int index = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        string childPointer = pointer + "/" + index;
                        offsets[childPointer] = reader.TokenStartIndex;
                        IndexValue(ref reader, offsets, childPointer);
                        index++;
                    }

                    break;

                default:
                    // Scalars: the value token's offset is the most precise cite.
                    offsets[pointer] = offset;
                    break;
            }
        }
    }
}
