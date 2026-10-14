using System.Text.Json;
using EraCore.IO;

namespace EraCore.Presentation;

/// <summary>
/// Persisted hint read set (T-093, F-110 已读持久化, 附录 G.3「持久化」):
/// which once-hints the player already dismissed. Mirrors the
/// <see cref="PanelSettingsStore"/> pattern — one JSON file under the
/// injected D-03 root, tolerant load (a corrupt preference must never
/// block startup), atomic write.
/// </summary>
public sealed record HintProgress
{
    /// <summary>Read hint ids (empty = fresh player).</summary>
    public required IReadOnlyList<string> Read { get; init; }

    /// <summary>Factory default: nothing read yet.</summary>
    public static HintProgress Default { get; } = new() { Read = Array.Empty<string>() };
}

/// <summary>
/// D-03 store binding of the hint read set.
/// </summary>
public static class HintProgressStore
{
    /// <summary>File path relative to the injected data root.</summary>
    public const string RelativePath = "settings/hint-progress.json";

    /// <summary>
    /// Loads the read set; missing/corrupt/unparsable files yield
    /// <see cref="HintProgress.Default"/> (fresh player — the worst case is
    /// re-showing guidance, never a crash).
    /// </summary>
    /// <exception cref="ArgumentNullException">store is null.</exception>
    public static HintProgress Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return HintProgress.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            HintProgress? parsed = JsonSerializer.Deserialize(
                bytes, UiContentJsonContext.Default.HintProgress);
            return parsed ?? HintProgress.Default;
        }
        catch (JsonException)
        {
            return HintProgress.Default; // corrupt preference — fresh default, never a startup failure
        }
    }

    /// <summary>Round-trips the read set through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    public static void Save(IFileStore store, HintProgress progress)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(progress);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            progress, UiContentJsonContext.Default.HintProgress);
        store.WriteAllBytes(RelativePath, bytes);
    }
}
