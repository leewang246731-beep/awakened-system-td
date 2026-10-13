using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 文件回落后端（明文契约 + 0600/ACL 尽力收紧 + 日志无 Key 负向）
public class FileSecretStoreTests : SecretsSandbox
{
    private readonly CaptureLogSink _sink = new();

    // ---------------------------------------------------------------- legal

    [Fact]
    public void Fallback_file_is_plaintext_under_the_injected_root()
    {
        var store = new FileSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");

        // 文档化回落契约（§11.6）：明文独立文件——正因如此权限必须收紧，
        // 且该形态仅在 DPAPI 不可用时由工厂选择。
        var path = Path.Combine(Root, FileSecretStore.RelativePath);
        Assert.True(File.Exists(path));
        Assert.Contains(
            "sk-1234567890abcdef",
            Encoding.UTF8.GetString(File.ReadAllBytes(path)),
            StringComparison.Ordinal);
        Assert.Equal("sk-1234567890abcdef", store.Retrieve("zhipu-glm"));
    }

    [Fact]
    public void Logs_carry_only_the_masked_tail_never_the_plaintext_key()
    {
        var store = new FileSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");
        _ = store.Retrieve("zhipu-glm");
        store.Store("zhipu-glm", "sk-abcdefgh1234");
        store.Clear("zhipu-glm");
        store.ClearAll();

        Assert.DoesNotContain("sk-1234567890abcdef", _sink.Joined);
        Assert.DoesNotContain("sk-abcdefgh1234", _sink.Joined);
        Assert.Contains("sk-****cdef", _sink.Joined); // 只有掩码形态可入日志
    }

    // -------------------------------------------------------------- boundary

    [Fact]
    public void Permissions_are_tightened_to_the_current_user_after_write()
    {
        var store = new FileSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");
        var path = Path.Combine(Root, FileSecretStore.RelativePath);

        if (OperatingSystem.IsWindows())
        {
            AssertWindowsAclTightened(path);
        }
        else
        {
            // 0600 尽力（Unix 形态）：属主读写，组/其他无任何位。
            // Inline in the else-branch so the platform analyzer sees the
            // !windows guard that GetUnixFileMode requires.
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(
                System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite,
                mode & (System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite
                    | System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupWrite
                    | System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherWrite));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertWindowsAclTightened(string path)
    {
        // 0600 尽力（Windows 形态）：继承切断 + 仅当前用户条目。
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected,
            "inheritance must be cut off for the fallback secrets file");
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        var currentSid = WindowsIdentity.GetCurrent().User;
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.Equal(currentSid, rule.IdentityReference);
        }

        Assert.Contains(rules, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.Read));
    }

    [Fact]
    public void Clear_then_Retrieve_returns_empty()
    {
        var store = new FileSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");

        store.Clear("zhipu-glm");

        Assert.Equal(string.Empty, store.Retrieve("zhipu-glm"));
        Assert.True(File.Exists(Path.Combine(Root, FileSecretStore.RelativePath)),
            "cleared store keeps a stable empty container on disk");
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Corrupted_fallback_file_fails_loud_on_retrieve()
    {
        var files = NewFiles();
        var store = new FileSecretStore(files, _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");

        // 非法面：篡改后必须抛 IOException（fail loud），不得静默返回空键。
        files.WriteAllBytes(FileSecretStore.RelativePath, "tampered!!"u8.ToArray());
        Assert.Throws<IOException>(() => store.Retrieve("zhipu-glm"));
    }
}
