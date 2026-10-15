using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;

namespace EraCore.Cinematics;

/// <summary>
/// 记忆档案收藏状态（T-152, 31 号 §三 D 层插入点）：已观看的记忆段集合 +
/// 《零的真相》合成篇解锁标记。解锁状态只写本文件（global 域事实，P5 同 seed
/// 战报等价断言口径——收藏/演出装饰零接触数值）；纯数据，I/O 全在 store。
/// </summary>
public sealed record MemoryArchive
{
    /// <summary>已观看的记忆段目录 id（D-01…D-08，首看顺序）。</summary>
    public required IReadOnlyList<string> ViewedMemoryIds { get; init; }

    /// <summary>《零的真相》是否已解锁（集齐 8 段即解锁，31 号 ★行）。</summary>
    public bool ZeroTruthUnlocked { get; init; }

    /// <summary>Factory state: nothing collected yet.</summary>
    public static MemoryArchive Default { get; } =
        new() { ViewedMemoryIds = Array.Empty<string>(), ZeroTruthUnlocked = false };
}

/// <summary>Source-generated codec（AOT red line 07 D-05）。</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MemoryArchive))]
public sealed partial class MemoryArchiveJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the memory archive: one JSON file under the injected
/// <see cref="IFileStore"/> root. Tolerant load (missing/corrupt → Default——
/// 收藏进度永不阻断启动)；写入走原子 store 契约。解锁判定纯函数：
/// 集齐全部记忆段（目录 requires 全在已看集合）即解锁。
/// </summary>
public static class MemoryArchiveStore
{
    /// <summary>Archive file path relative to the injected data root.</summary>
    public const string RelativePath = "cinematics/memory-archive.json";

    /// <summary>Loads the archive; missing/unparsable yields <see cref="MemoryArchive.Default"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static MemoryArchive Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return MemoryArchive.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            MemoryArchive? parsed = JsonSerializer.Deserialize(
                bytes, MemoryArchiveJsonContext.Default.MemoryArchive);
            return parsed ?? MemoryArchive.Default;
        }
        catch (JsonException)
        {
            return MemoryArchive.Default; // corrupt archive — replays, never blocks startup
        }
    }

    /// <summary>Round-trips the archive through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Save(IFileStore store, MemoryArchive archive)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(archive);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            archive, MemoryArchiveJsonContext.Default.MemoryArchive);
        store.WriteAllBytes(RelativePath, bytes);
    }

    /// <summary>Whether one memory segment has been viewed (收藏判定).</summary>
    public static bool IsViewed(MemoryArchive archive, string memoryId)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.ViewedMemoryIds.Contains(memoryId, StringComparer.Ordinal);
    }

    /// <summary>
    /// 集齐判定：《零的真相》的解锁条件=目录 composite.requires 全部已看
    /// （31 号 §三「集齐 D-01~D-08 自动解锁」——requires 从目录来，零硬编码）。
    /// </summary>
    public static bool AllCollected(MemoryArchive archive, VisualAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Composite.Requires.All(
            required => archive.ViewedMemoryIds.Contains(required, StringComparer.Ordinal));
    }

    /// <summary>
    /// 记录一次记忆段观看并推导解锁位（幂等）：集齐时 ZeroTruthUnlocked 置位
    /// 且不再回落（解锁是持久事实，31 号 §三「解锁状态只写 global 域」）。
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// memoryId is blank, or not one of the catalog's memory-usage entries.
    /// </exception>
    public static MemoryArchive MarkViewed(
        MemoryArchive archive, string memoryId, VisualAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("memoryId must not be blank.", nameof(memoryId));
        }

        VisualVideoAsset? entry = catalog.VideoById(memoryId);
        if (entry is null || entry.Usage != "memory")
        {
            throw new ArgumentException(
                $"memoryId 未登记为记忆段（{memoryId}，目录 memory 用途位 D-01…D-08）", nameof(memoryId));
        }

        MemoryArchive next = archive;
        if (!IsViewed(archive, memoryId))
        {
            var ids = new List<string>(archive.ViewedMemoryIds) { memoryId };
            next = archive with { ViewedMemoryIds = ids };
        }

        return AllCollected(next, catalog)
            ? next with { ZeroTruthUnlocked = true }
            : next;
    }
}
