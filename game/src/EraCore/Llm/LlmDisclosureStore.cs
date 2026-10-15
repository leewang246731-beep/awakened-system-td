using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;

namespace EraCore.Llm;

/// <summary>
/// Persisted first-run disclosure state (T-109, F-147 首启流向声明): whether
/// the player has already acknowledged the AI data-flow notice. One boolean
/// per install — acknowledged means the dialog never shows again. The
/// factory default is 未启 (not acknowledged) because the notice must
/// precede the first AI enable, never be skipped silently.
/// </summary>
public sealed record LlmDisclosureSettings
{
    /// <summary>True once the player confirmed the data-flow notice.</summary>
    public required bool Acknowledged { get; init; }

    /// <summary>Factory default: not acknowledged yet (the dialog is due).</summary>
    public static LlmDisclosureSettings Default { get; } = new() { Acknowledged = false };
}

/// <summary>
/// System.Text.Json source-generation context for the disclosure state
/// (D-05, mirrors <c>PresentationJsonContext</c> — its own context so this
/// file stays self-contained instead of growing the llm.config schema
/// context).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LlmDisclosureSettings))]
public sealed partial class LlmDisclosureJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the disclosure state: one JSON file under the
/// injected <see cref="IFileStore"/> root. Loading is tolerant — a missing
/// or corrupt file falls back to <see cref="LlmDisclosureSettings.Default"/>
/// (未启): a corrupt state must re-show the notice, never silently mark it
/// acknowledged (the honest failure direction for a disclosure).
/// </summary>
public static class LlmDisclosureStore
{
    /// <summary>State file path relative to the injected data root (PanelSettingsStore 先例).</summary>
    public const string RelativePath = "settings/llm-disclosure.json";

    /// <summary>
    /// Loads the state; missing/corrupt/null documents yield the factory
    /// default (未启) — a broken preference file never suppresses the
    /// disclosure dialog.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static LlmDisclosureSettings Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return LlmDisclosureSettings.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            LlmDisclosureSettings? parsed = JsonSerializer.Deserialize(
                bytes, LlmDisclosureJsonContext.Default.LlmDisclosureSettings);
            return parsed ?? LlmDisclosureSettings.Default;
        }
        catch (JsonException)
        {
            return LlmDisclosureSettings.Default; // corrupt state file — treat as never acknowledged
        }
    }

    /// <summary>Round-trips the state through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="settings"/> is null.</exception>
    public static void Save(IFileStore store, LlmDisclosureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings, LlmDisclosureJsonContext.Default.LlmDisclosureSettings);
        store.WriteAllBytes(RelativePath, bytes);
    }
}

/// <summary>
/// Pure first-run gate of the data-flow notice (方案 §16.3 "首次启用弹数据
/// 流向声明"): the dialog is due exactly when the AI-side system switch is
/// on (关闭 AI ⇒ 永不弹), the cloud tier is usable on this platform (Web
/// ⇒ L3 禁用 ⇒ 无可启用的 AI 功能 ⇒ 不弹), and the player has not
/// acknowledged yet (已确认 ⇒ 永不再弹). All inputs are supplied by the
/// presenter — this class never touches I/O.
/// </summary>
public static class LlmDisclosureGate
{
    /// <summary>
    /// True when the first-run notice must show before the player enables
    /// any AI feature.
    /// </summary>
    /// <param name="aiSystemEnabled">The C-22 system switch (PanelSettings.SystemEnabled) — off means the player opted out, 永不弹.</param>
    /// <param name="cloudTierAvailable">The platform tier fact (AgentTierAvailability.L3CloudByok) — Web exports have no AI tier to enable.</param>
    /// <param name="acknowledged">The persisted acknowledgment state.</param>
    public static bool PromptRequired(bool aiSystemEnabled, bool cloudTierAvailable, bool acknowledged) =>
        aiSystemEnabled && cloudTierAvailable && !acknowledged;
}
