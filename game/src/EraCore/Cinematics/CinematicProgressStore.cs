using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;

namespace EraCore.Cinematics;

/// <summary>
/// Persisted cinematic completion markers (T-086, F-113/F-114): the set of
/// script ids the player has already watched — 不重复播放 (hooked beats skip
/// completed scripts) + 可回看 (explicit replay bypasses the set, Forget
/// re-arms the hook). Durable facts only; playback state stays with the
/// timeline.
/// </summary>
public sealed record CinematicProgress
{
    /// <summary>Script ids whose performance completed at least once, first-completion order.</summary>
    public required IReadOnlyList<string> CompletedIds { get; init; }

    /// <summary>Factory state: nothing watched yet.</summary>
    public static CinematicProgress Default { get; } = new() { CompletedIds = Array.Empty<string>() };
}

/// <summary>
/// System.Text.Json source-generation context for the C-24 progress file
/// (AOT red line 07 D-05, mirrors <c>PresentationJsonContext</c>).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CinematicProgress))]
public sealed partial class CinematicsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the cinematic progress: one JSON file under the
/// injected <see cref="IFileStore"/> root. Loading is tolerant — a missing or
/// corrupt file falls back to <see cref="CinematicProgress.Default"/> (a
/// progress marker must never block startup; worst case the cinematic
/// replays), and every write goes through the atomic store contract.
/// </summary>
public static class CinematicProgressStore
{
    /// <summary>Progress file path relative to the injected data root.</summary>
    public const string RelativePath = "cinematics/progress.json";

    /// <summary>
    /// Loads the progress; a missing, unparsable or member-incomplete file
    /// yields <see cref="CinematicProgress.Default"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static CinematicProgress Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return CinematicProgress.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            CinematicProgress? parsed = JsonSerializer.Deserialize(
                bytes, CinematicsJsonContext.Default.CinematicProgress);
            return parsed ?? CinematicProgress.Default;
        }
        catch (JsonException)
        {
            return CinematicProgress.Default; // corrupt progress file — replays, never blocks startup
        }
    }

    /// <summary>Round-trips the progress through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Save(IFileStore store, CinematicProgress progress)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(progress);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            progress, CinematicsJsonContext.Default.CinematicProgress);
        store.WriteAllBytes(RelativePath, bytes);
    }

    /// <summary>Whether the script completed at least once (不重复播放 gate).</summary>
    /// <exception cref="ArgumentNullException">An argument is null or blank.</exception>
    public static bool IsCompleted(CinematicProgress progress, string scriptId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            throw new ArgumentException("scriptId must not be blank.", nameof(scriptId));
        }

        return progress.CompletedIds.Contains(scriptId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the progress with the script marked complete (idempotent —
    /// the id keeps its first-completion position).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null or blank.</exception>
    public static CinematicProgress MarkCompleted(CinematicProgress progress, string scriptId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            throw new ArgumentException("scriptId must not be blank.", nameof(scriptId));
        }

        if (IsCompleted(progress, scriptId))
        {
            return progress;
        }

        var ids = new List<string>(progress.CompletedIds) { scriptId };
        return new CinematicProgress { CompletedIds = ids };
    }

    /// <summary>
    /// Returns the progress with the script's marker cleared (可回看 re-arm:
    /// the hooked beat fires again on the next qualifying run).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null or blank.</exception>
    public static CinematicProgress Forget(CinematicProgress progress, string scriptId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            throw new ArgumentException("scriptId must not be blank.", nameof(scriptId));
        }

        if (!IsCompleted(progress, scriptId))
        {
            return progress;
        }

        var ids = new List<string>(progress.CompletedIds);
        ids.RemoveAll(id => string.Equals(id, scriptId, StringComparison.Ordinal));
        return new CinematicProgress { CompletedIds = ids };
    }
}
