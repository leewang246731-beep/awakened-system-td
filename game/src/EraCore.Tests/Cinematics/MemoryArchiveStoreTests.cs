using EraCore.Cinematics;
using EraCore.IO;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-099] [F-113] [T-152·31 号 §三 D 层（视觉扩充批次不占特性序）] 记忆档案收藏链入存档：集齐 8 段（D-01~D-08）
// 恰在第 8 段解锁《零的真相》合成篇、7 段持锁、持久化往返（解锁=持久
// 事实）、幂等登记、缺文件/坏 JSON 回出厂缺省（收藏进度永不阻断启动）、
// 非记忆段/空白 id 拒绝。沙盒同 CinematicProgressStoreTests（.tmp-tests）。
public class MemoryArchiveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public MemoryArchiveStoreTests()
    {
        _store = new DevFileStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact] // legal — 7 段不解锁、第 8 段解锁；持久化往返；幂等
    public void Memory_archive_unlocks_exactly_on_the_eighth_segment()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();

        MemoryArchive archive = MemoryArchive.Default;
        foreach (string id in catalog.Composite.Requires.Take(7))
        {
            archive = MemoryArchiveStore.MarkViewed(archive, id, catalog);
            Assert.False(archive.ZeroTruthUnlocked);
        }

        archive = MemoryArchiveStore.MarkViewed(archive, catalog.Composite.Requires[7], catalog);
        Assert.True(archive.ZeroTruthUnlocked);
        Assert.True(MemoryArchiveStore.AllCollected(archive, catalog));

        // 幂等：重复观看同段不改变集合顺序
        archive = MemoryArchiveStore.MarkViewed(archive, catalog.Composite.Requires[0], catalog);
        Assert.Equal(8, archive.ViewedMemoryIds.Count);

        MemoryArchiveStore.Save(_store, archive);
        MemoryArchive reloaded = MemoryArchiveStore.Load(_store);
        Assert.True(reloaded.ZeroTruthUnlocked); // 解锁是持久事实（31 号 ★行）
    }

    [Fact] // legal — 缺文件/坏 JSON 回出厂缺省（收藏进度永不阻断启动）
    public void Memory_archive_tolerant_load_falls_back_to_default()
    {
        Assert.Equal(MemoryArchive.Default, MemoryArchiveStore.Load(_store));
        _store.WriteAllBytes(MemoryArchiveStore.RelativePath, new byte[] { 0xFF, 0xFE });
        Assert.False(MemoryArchiveStore.Load(_store).ZeroTruthUnlocked);
    }

    [Fact] // illegal — 非记忆段/空白 id 拒绝登记
    public void Memory_archive_rejects_non_memory_ids()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();

        Assert.Throws<ArgumentException>(() =>
            MemoryArchiveStore.MarkViewed(MemoryArchive.Default, "A-01", catalog));
        Assert.Throws<ArgumentException>(() =>
            MemoryArchiveStore.MarkViewed(MemoryArchive.Default, " ", catalog));
    }
}
