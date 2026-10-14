using System.Text;
using System.Text.Json;

namespace EraCore.Agent.Actions;

/// <summary>
/// Schema-declared clamp rewriting (T-070, DEBT-4): fields whose
/// <see cref="ActionSchemaTable"/> rule carries
/// <see cref="NumberRangeMode.Clamp"/> (G.2 wording 钳制 — today
/// SetAlarm.after_seconds [30,600]) are rewritten to the nearest boundary
/// instead of being denied. Runs inside <see cref="ActionRegistry.Invoke"/>
/// BEFORE the closed-schema gate, so the executed payload always carries
/// an in-range value and the validator never rejects a clamp-marked field
/// for its range. Pure function: parse, rewrite out-of-range root-level
/// numbers, leave every other token byte-identical (untouched values are
/// copied from the original document, not re-formatted); unparseable
/// payloads, unknown verbs and in-range values return the input string
/// unchanged (the schema gate reports the violation downstream — single
/// reporting path).
/// </summary>
public static class ActionSchemaClamper
{
    /// <summary>
    /// Clamps every schema-declared clamp field of the payload into its
    /// [min,max] window. Returns the input string (same reference when
    /// nothing needed rewriting) for malformed payloads, unknown verbs and
    /// already-in-range values.
    /// </summary>
    /// <param name="actionJson">Raw action payload (closed 19-verb schema).</param>
    public static string Clamp(string actionJson)
    {
        ArgumentNullException.ThrowIfNull(actionJson);
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return actionJson;
        }

        if (!JsonSchemaValidator.TryReadVerb(actionJson, out string? verb)
            || !ActionSchemaTable.TryGet(verb, out ActionSchema schema))
        {
            return actionJson; // unknown verb / malformed — the schema gate reports it
        }

        try
        {
            using var document = JsonDocument.Parse(actionJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return actionJson;
            }

            if (!NeedsClamp(root, schema, out double _))
            {
                return actionJson; // in-range / absent clamp fields — nothing to rewrite
            }

            return Rewrite(root, schema);
        }
        catch (JsonException)
        {
            return actionJson; // malformed JSON — the schema gate reports it
        }
    }

    private static bool NeedsClamp(JsonElement root, ActionSchema schema, out double probed)
    {
        probed = 0;
        foreach (FieldRule rule in schema.Fields)
        {
            if (rule.Type != FieldType.Number || rule.NumberRange != NumberRangeMode.Clamp)
            {
                continue;
            }

            if (!root.TryGetProperty(rule.Name, out JsonElement value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out double number))
            {
                continue; // absent / wrong type — existence+type stay the validator's job
            }

            if ((rule.MinNumber is { } min && number < min) || (rule.MaxNumber is { } max && number > max))
            {
                probed = number;
                return true;
            }
        }

        return false;
    }

    private static string Rewrite(JsonElement root, ActionSchema schema)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                FieldRule? clampRule = null;
                foreach (FieldRule rule in schema.Fields)
                {
                    if (rule.Name == property.Name
                        && rule.Type == FieldType.Number
                        && rule.NumberRange == NumberRangeMode.Clamp)
                    {
                        clampRule = rule;
                        break;
                    }
                }

                if (clampRule is not null
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetDouble(out double number)
                    && ((clampRule.MinNumber is { } min && number < min)
                        || (clampRule.MaxNumber is { } max && number > max)))
                {
                    double clamped = Math.Clamp(
                        number, clampRule.MinNumber ?? double.MinValue, clampRule.MaxNumber ?? double.MaxValue);
                    writer.WritePropertyName(property.Name);
                    writer.WriteNumberValue(clamped);
                }
                else
                {
                    // Every untouched token is copied verbatim from the
                    // original document (numbers keep their exact literal).
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
