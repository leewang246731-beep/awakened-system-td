using EraCore.IO;
using Xunit;

namespace EraCore.Tests.IO;

public class DevFileStoreTests : IDisposable
{
    // Scratch sandbox under the pinned dev root (D-drive only; data/ is gitignored runtime data).
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Roundtrip_write_then_read_returns_same_bytes()
    {
        var store = new DevFileStore(_root);
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        store.WriteAllBytes("saves/profile/write.bin", payload);

        Assert.True(store.Exists("saves/profile/write.bin"));
        Assert.Equal(payload, store.ReadAllBytes("saves/profile/write.bin"));
    }

    [Fact]
    public void Exists_is_false_before_write_and_does_not_create_directories()
    {
        var store = new DevFileStore(_root);

        Assert.False(store.Exists("missing.bin"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Relative_path_escaping_root_is_rejected()
    {
        var store = new DevFileStore(_root);

        Assert.ThrowsAny<ArgumentException>(() => store.Exists("../outside.bin"));
    }

    [Fact]
    public void Default_root_is_the_pinned_dev_root_from_D03()
    {
        // Dev machine registration (07-decided-plan env facts). Release builds inject their own root.
        Assert.Equal(@"D:\game\game\data", DevFileStore.DefaultDevRoot);
    }

    public void Dispose()
    {
        var sandbox = Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests");
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
