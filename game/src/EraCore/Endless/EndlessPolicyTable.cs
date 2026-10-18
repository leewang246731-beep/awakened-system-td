using System.Text.Json;

namespace EraCore.Endless;

/// <summary>
/// Thrown when the endless policy table violates its schema (T-154).
/// Mirrors <c>AudioSchemaException</c>/<c>NarrativeSchemaException</c>: one
/// exception type per content domain, carrying the source label for the
/// loader log.
/// </summary>
public sealed class EndlessPolicySchemaException : Exception
{
    /// <summary>Creates the exception with a fully-formed message.</summary>
    public EndlessPolicySchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The T-154 production policy table of the endless mode（业主 2026-10-18
/// 两项裁定的数值真相源，RL7——数值活注册表，bgm-programs.json 同构先例）:
/// the cross-era mixing ramp（「主场主导+波渐升」：41 波起 9:1（主场:跨代）
/// → 81 波起 3:1 终值，中间线性渐升；终值待 T-158 实测微调——改表即改产，
/// 代码零改动）and the rolling-affix cadence（滚词缀生产节奏=T-143 生产档
/// 数值）。Pure data parsed strictly from
/// <c>game/content/endless/endless-policies.json</c>; the host loads the
/// table and hands it through <see cref="EndlessEntryRequest.PolicyTable"/>,
/// the entry resolves its defaults from it（显式参数 &gt; 本表 &gt; 代码内兜底档）.
/// The engine carries no IO (CS-1.4) — the loader log/_SOURCE label is the
/// caller's.
/// </summary>
public sealed record EndlessPolicyTable(
    EndlessMixPolicy MixPolicy,
    EndlessAffixRollPolicy AffixRollPolicy)
{
    /// <summary>Content schema URN of the endless policy table.</summary>
    public const string SchemaUrn = "urn:eratd:content:endless:policies:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentTypeMarker = "EndlessPolicyTable";

    /// <summary>
    /// Strict parse: closed schema/marker, a mixing block (ramp keys
    /// all-or-nothing) and a rolling-affix block; value validation is
    /// delegated to the policy constructors (single source of truth — the
    /// table only labels violations with the source name). Keys prefixed
    /// with <c>_</c> are annotation keys the loader ignores (bgm-programs
    /// 先例：终值待校注记等元信息走注解键).
    /// </summary>
    /// <param name="json">Raw table JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="EndlessPolicySchemaException">Any schema or value violation.</exception>
    public static EndlessPolicyTable Parse(string json, string sourceName = "endless policies")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: JSON 解析失败（{error.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new EndlessPolicySchemaException($"{sourceName}: 根必须是对象");
            }

            RequireMarker(root, sourceName);

            if (!root.TryGetProperty("mix", out JsonElement mixEl) || mixEl.ValueKind != JsonValueKind.Object)
            {
                throw new EndlessPolicySchemaException($"{sourceName}: 缺少 mix 对象（混编渐升生产档）");
            }

            EndlessMixPolicy mix = ParseMix(mixEl, sourceName);

            if (!root.TryGetProperty("affix_rolls", out JsonElement rollsEl) || rollsEl.ValueKind != JsonValueKind.Object)
            {
                throw new EndlessPolicySchemaException($"{sourceName}: 缺少 affix_rolls 对象（滚词缀生产节奏）");
            }

            EndlessAffixRollPolicy rolls = ParseRolls(rollsEl, sourceName);
            return new EndlessPolicyTable(mix, rolls);
        }
    }

    /// <summary>Mixing block: at-mix knobs mandatory, ramp keys all-or-nothing.</summary>
    private static EndlessMixPolicy ParseMix(JsonElement mixEl, string sourceName)
    {
        int mixFromWave = RequireInt(mixEl, "mix_from_wave", sourceName);
        double startWeight = RequireNumber(mixEl, "start_era_weight", sourceName);
        double otherWeight = RequireNumber(mixEl, "other_era_weight", sourceName);

        bool hasRampEnd = mixEl.TryGetProperty("ramp_end_wave", out JsonElement rampEndEl);
        bool hasEndStart = mixEl.TryGetProperty("end_start_era_weight", out JsonElement endStartEl);
        bool hasEndOther = mixEl.TryGetProperty("end_other_era_weight", out JsonElement endOtherEl);
        if (hasRampEnd != hasEndStart || hasRampEnd != hasEndOther)
        {
            throw new EndlessPolicySchemaException(
                $"{sourceName}: mix 渐升键必须同时给或同时缺（ramp_end_wave/end_start_era_weight/end_other_era_weight）");
        }

        try
        {
            return hasRampEnd
                ? new EndlessMixPolicy(
                    mixFromWave,
                    startWeight,
                    otherWeight,
                    rampEndEl.GetInt32(),
                    endStartEl.GetDouble(),
                    endOtherEl.GetDouble())
                : new EndlessMixPolicy(mixFromWave, startWeight, otherWeight);
        }
        catch (ArgumentException error)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: mix 块数值非法（{error.Message}）");
        }
    }

    /// <summary>Rolling-affix block: the five cadence knobs, all mandatory.</summary>
    private static EndlessAffixRollPolicy ParseRolls(JsonElement rollsEl, string sourceName)
    {
        int rollEveryWaves = RequireInt(rollsEl, "roll_every_waves", sourceName);
        int rollStartWave = RequireInt(rollsEl, "roll_start_wave", sourceName);
        int kindsPerRoll = RequireInt(rollsEl, "kinds_per_roll", sourceName);
        if (!rollsEl.TryGetProperty("allow_consecutive_repeat", out JsonElement allowRepeatEl)
            || allowRepeatEl.ValueKind != JsonValueKind.True && allowRepeatEl.ValueKind != JsonValueKind.False)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: 缺少 allow_consecutive_repeat 布尔值");
        }

        double repeatGain = RequireNumber(rollsEl, "repeat_intensity_gain", sourceName);

        try
        {
            return new EndlessAffixRollPolicy(
                rollEveryWaves,
                rollStartWave,
                kindsPerRoll,
                allowRepeatEl.GetBoolean(),
                repeatGain);
        }
        catch (ArgumentException error)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: affix_rolls 块数值非法（{error.Message}）");
        }
    }

    private static void RequireMarker(JsonElement root, string sourceName)
    {
        string? schema = null;
        if (root.TryGetProperty("$schema", out JsonElement schemaEl) && schemaEl.ValueKind == JsonValueKind.String)
        {
            schema = schemaEl.GetString();
        }

        if (schema != SchemaUrn)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: $schema 必须是 {SchemaUrn}（got {schema ?? "缺"}）");
        }

        if (!root.TryGetProperty("content_type", out JsonElement markerEl)
            || markerEl.ValueKind != JsonValueKind.String
            || markerEl.GetString() != ContentTypeMarker)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: content_type 必须是 {ContentTypeMarker}");
        }
    }

    private static int RequireInt(JsonElement element, string key, string sourceName)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: 缺少整数值 {key}");
        }

        return value.GetInt32();
    }

    private static double RequireNumber(JsonElement element, string key, string sourceName)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new EndlessPolicySchemaException($"{sourceName}: 缺少数值 {key}");
        }

        return value.GetDouble();
    }
}
