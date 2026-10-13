using EraCore.IO;
using Xunit;

namespace EraCore.Tests.IO;

// [C-01] K-1 端口适配：IFileStore 开发机实现（D-03 根注入）
public class DevFileStoreTests : IDisposable
{
    // Scratch sandbox under the pinned dev root (D-drive only; data/ is gitignored runtime data).
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- legal

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
    public void Write_can_target_the_root_directory_itself()
    {
        // Root itself stays allowed by the containment check (only paths
        // outside root+separator are rejected); a flat file is the legal case.
        var store = new DevFileStore(_root);
        var payload = new byte[] { 9 };

        store.WriteAllBytes("flat.bin", payload);

        Assert.Equal(payload, store.ReadAllBytes("flat.bin"));
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Exists_is_false_before_write_and_does_not_create_directories()
    {
        var store = new DevFileStore(_root);

        Assert.False(store.Exists("missing.bin"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Default_root_follows_the_D03_injection_priority()
    {
        // D-03 root priority: explicit ctor arg > ERA_DATA_ROOT > pinned
        // DefaultDevRoot. Expected value is derived from the same inputs the
        // store uses (environment registry in 07-decided-plan env facts),
        // never a hard-coded workstation path (review round-1, REV-20261013).
        var expected = Environment.GetEnvironmentVariable("ERA_DATA_ROOT") is { Length: > 0 } envRoot
            ? Path.GetFullPath(envRoot)
            : DevFileStore.DefaultDevRoot;

        Assert.Equal(expected, Path.GetFullPath(new DevFileStore().Root));
        Assert.Equal(_root, Path.GetFullPath(new DevFileStore(_root).Root));
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Relative_path_escaping_root_is_rejected()
    {
        var store = new DevFileStore(_root);

        Assert.True(RejectsEscape(() => store.Exists("../outside.bin")),
            "'../outside.bin' must be rejected as a root escape");
        Assert.True(RejectsEscape(() => store.Exists("../data-evil/x.bin")),
            "'../data-evil/x.bin' must be rejected as a root escape");
        Assert.True(RejectsEscape(() => store.ReadAllBytes("../../escape.bin")),
            "'../../escape.bin' must be rejected as a root escape");
        Assert.True(RejectsEscape(() => store.WriteAllBytes("..\\data-evil\\x.bin", new byte[] { 1 })),
            "'..\\data-evil\\x.bin' must be rejected as a root escape");
    }

    [Fact]
    public void Same_drive_absolute_path_sharing_the_root_prefix_is_rejected()
    {
        // Negative control for the round-1 prefix fix: a sibling directory
        // whose path merely shares the root as a string prefix ("<root>X",
        // the dataX-next-to-data shape) must be rejected, not silently opened.
        var store = new DevFileStore(_root);
        var sibling = _root + "X";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllBytes(Path.Combine(sibling, "f.bin"), new byte[] { 1 });

            Assert.True(RejectsEscape(() => store.Exists(sibling)),
                "an absolute path into the '<root>X' sibling must be rejected");
            Assert.True(
                RejectsEscape(() => store.ReadAllBytes(sibling + Path.DirectorySeparatorChar + "f.bin")),
                "reading a real file inside the '<root>X' sibling must be rejected");
            Assert.True(RejectsEscape(() => store.Exists(Path.Combine(_root + "X", "f.bin"))),
                "a combined path into the '<root>X' sibling must be rejected");
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadAllBytes_of_missing_file_throws()
    {
        // Missing files must fail loudly — an IFileStore never fabricates
        // empty content for absent files. Missing parent directory throws
        // DirectoryNotFoundException, missing file FileNotFoundException;
        // both derive from IOException, which is the contract here.
        var store = new DevFileStore(_root);
        store.WriteAllBytes("present.bin", new byte[] { 7 });

        Assert.ThrowsAny<IOException>(() => store.ReadAllBytes("missing.bin"));
        Assert.ThrowsAny<IOException>(() => store.ReadAllBytes("no/such/dir/missing.bin"));
    }

    // Escape attempts must fail closed with IOException or
    // UnauthorizedAccessException — never a silent read/write outside the
    // injected root (review round-1 acceptance criteria).
    private static bool RejectsEscape(Action act)
    {
        var ex = Record.Exception(act);
        return ex is IOException or UnauthorizedAccessException;
    }

    public void Dispose()
    {
        // Only this fixture's own GUID sandbox — the shared .tmp-tests parent
        // may hold sibling GUIDs owned by concurrent runs (review round-1).
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
