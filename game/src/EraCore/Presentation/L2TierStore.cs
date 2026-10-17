using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;
using EraCore.Llm.L2;

namespace EraCore.Presentation;

/// <summary>
/// Persisted T-137 L2 settings (settings/l2-tier.json): the advisor tier
/// selection, optional path overrides and the local port. Overrides exist
/// for the DEV reuse case (a pre-downloaded GGUF / unpacked llama.cpp on the
/// dev machine — 配置指路不重复下载) and for power users; blank = default
/// layout under the injected data root (players install on any drive — the
/// code never assumes one).
/// </summary>
public sealed record L2TierSettings
{
    /// <summary>Selected advisor tier (default L1 — L2/L3 are opt-in).</summary>
    public required L2TierSelection Selection { get; init; }

    /// <summary>Optional absolute path of the model GGUF (blank = data-root/models/&lt;manifest name&gt;).</summary>
    public string ModelPathOverride { get; init; } = string.Empty;

    /// <summary>Optional absolute directory of the llama-server executable (blank = bundled/data-root/runtime).</summary>
    public string RuntimePathOverride { get; init; } = string.Empty;

    /// <summary>Local llama-server port.</summary>
    public int Port { get; init; } = L2Facts.DefaultPort;

    /// <summary>Factory defaults: L1, no overrides, spike port.</summary>
    public static L2TierSettings Default { get; } = new() { Selection = L2TierSelection.L1 };
}

/// <summary>Persisted tier selection (wire strings, stable across saves).</summary>
[JsonConverter(typeof(L2TierSelectionJsonConverter))]
public enum L2TierSelection
{
    /// <summary>L1 军令语法（默认）.</summary>
    L1 = 0,

    /// <summary>L2 本地小模型（可选下载）.</summary>
    L2 = 1,

    /// <summary>L3 BYOK 云端.</summary>
    L3 = 2,
}

/// <summary>Wire codec for <see cref="L2TierSelection"/> ("l1"/"l2"/"l3"; anything else throws — F-080 no-silent-default discipline).</summary>
public sealed class L2TierSelectionJsonConverter : JsonConverter<L2TierSelection>
{
    /// <inheritdoc />
    public override L2TierSelection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "l1" => L2TierSelection.L1,
            "l2" => L2TierSelection.L2,
            "l3" => L2TierSelection.L3,
            var unknown => throw new JsonException($"tier 必须是 l1/l2/l3，收到 '{unknown}'"),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, L2TierSelection value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            L2TierSelection.L1 => "l1",
            L2TierSelection.L2 => "l2",
            L2TierSelection.L3 => "l3",
            _ => throw new JsonException($"unserializable tier {value}"),
        });
}

/// <summary>Source-gen context for the L2 settings (D-05; enums as readable strings where applicable).</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(L2TierSettings))]
public sealed partial class L2TierJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the T-137 L2 settings — one JSON file under the
/// injected root. Tolerant load (missing/corrupt/unknown-tier ⇒ factory
/// default): a broken preference must degrade to L1, never block startup
/// (同 <see cref="PanelSettingsStore"/> 纪律).
/// </summary>
public static class L2TierStore
{
    /// <summary>Settings file path relative to the injected data root.</summary>
    public const string RelativePath = "settings/l2-tier.json";

    /// <summary>Loads the settings; any defect falls back to <see cref="L2TierSettings.Default"/> (L1).</summary>
    public static L2TierSettings Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return L2TierSettings.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            L2TierSettings? parsed = JsonSerializer.Deserialize(
                bytes, L2TierJsonContext.Default.L2TierSettings);
            return parsed ?? L2TierSettings.Default;
        }
        catch (JsonException)
        {
            return L2TierSettings.Default; // corrupt preference — factory L1, never a startup failure
        }
    }

    /// <summary>Round-trips the settings through the source-generated codec.</summary>
    public static void Save(IFileStore store, L2TierSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings, L2TierJsonContext.Default.L2TierSettings);
        store.WriteAllBytes(RelativePath, bytes);
    }

    /// <summary>Maps the persisted selection onto the routing enum (the single switch point between the two vocabularies).</summary>
    public static LlmTierKind RoutingKind(L2TierSettings settings) => settings.Selection switch
    {
        L2TierSelection.L1 => LlmTierKind.L1Grammar,
        L2TierSelection.L2 => LlmTierKind.L2Local,
        L2TierSelection.L3 => LlmTierKind.L3Cloud,
        _ => LlmTierKind.L1Grammar,
    };
}
