using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;

namespace EraCore.Presentation;

/// <summary>
/// Persisted C-22 panel settings (T-075, F-097 设置开关持久化). Only the
/// durable facts live here — the remembered on/off switch position; the
/// in-session layout memory (bubble vs expanded) stays with
/// <see cref="PanelStateMachine"/> and is not a settings artifact.
/// </summary>
public sealed record PanelSettings
{
    /// <summary>The off-switch position persisted across runs (default true).</summary>
    public required bool SystemEnabled { get; init; }

    /// <summary>Factory defaults: the system ships enabled (pillar 4 — off is the player's opt-out).</summary>
    public static PanelSettings Default { get; } = new() { SystemEnabled = true };
}

/// <summary>
/// System.Text.Json source-generation context for the C-22 panel settings
/// (AOT red line 07 D-05, mirrors <c>GovernanceJsonContext</c>): the only
/// serialization entry point for presentation-layer settings. T-102 added
/// the render-tier preference record; enums ride as readable strings
/// (UseStringEnumConverter — bool-only <see cref="PanelSettings"/> output
/// is unchanged).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PanelSettings))]
[JsonSerializable(typeof(RenderTierSettings))]
public sealed partial class PresentationJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the panel settings: one JSON file under the
/// injected <see cref="IFileStore"/> root. Loading is tolerant — a missing
/// or corrupt file falls back to <see cref="PanelSettings.Default"/> (a UI
/// preference must never block startup), and every write is atomic via the
/// store contract.
/// </summary>
public static class PanelSettingsStore
{
    /// <summary>Settings file path relative to the injected data root.</summary>
    public const string RelativePath = "settings/panel.json";

    /// <summary>
    /// Loads the settings; a missing file, an unparsable file, or one that
    /// omits the required <c>SystemEnabled</c> member (source-gen decode
    /// throws — member is <see langword="required"/>) all yield
    /// <see cref="PanelSettings.Default"/>: a corrupt preference must fall
    /// back to factory, never block startup and never silently switch the
    /// system off.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static PanelSettings Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return PanelSettings.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            PanelSettings? parsed = JsonSerializer.Deserialize(
                bytes, PresentationJsonContext.Default.PanelSettings);
            return parsed ?? PanelSettings.Default;
        }
        catch (JsonException)
        {
            return PanelSettings.Default; // corrupt preference file — UI default, never a startup failure
        }
    }

    /// <summary>Round-trips the settings through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static void Save(IFileStore store, PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings, PresentationJsonContext.Default.PanelSettings);
        store.WriteAllBytes(RelativePath, bytes);
    }
}
