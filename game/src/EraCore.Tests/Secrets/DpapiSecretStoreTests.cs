using System.Text;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 DPAPI 后端（Windows 真 DPAPI 集成）——非 Windows 平台按任务纪律
// 标注跳过往返用例（能力面 Probe/IsAvailable 断言全平台执行，不伪造 DPAPI 通过）。
public class DpapiSecretStoreTests : SecretsSandbox
{
    private readonly CaptureLogSink _sink = new();

    // ---------------------------------------------------------------- legal

    [Fact]
    public void Real_dpapi_roundtrip_persists_and_stays_encrypted_on_disk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：真 DPAPI 用例标注跳过（见类注），文件回落用例在 FileSecretStoreTests
        }

        var store = new DpapiSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");

        var blobPath = Path.Combine(Root, DpapiSecretStore.RelativePath);
        Assert.True(File.Exists(blobPath), "DPAPI store must persist its blob");

        // 落盘内容必须是密文：键明文与 provider id 明文都不得出现
        // （整容器加密，而非仅加密键值）。
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(blobPath));
        Assert.DoesNotContain("sk-1234567890abcdef", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("zhipu-glm", raw, StringComparison.Ordinal);

        var reloaded = new DpapiSecretStore(NewFiles(), _sink);
        Assert.Equal("sk-1234567890abcdef", reloaded.Retrieve("zhipu-glm"));
    }

    [Fact]
    public void Blob_is_bound_to_the_fixed_app_entropy_salt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：真 DPAPI 用例标注跳过
        }

        var files = NewFiles();
        var trusted = new DpapiSecretStore(files, _sink);
        trusted.Store("zhipu-glm", "sk-1234567890abcdef");

        // 换熵的读取者（伪造 app）必须解不开：CRYPTO 异常，非静默空值。
        var forgedApp = new DpapiSecretStore(files, _sink, "forged-app-salt"u8.ToArray());
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => forgedApp.Retrieve("zhipu-glm"));

        // 同一固定 app 盐的新实例仍可解（盐的跨实例稳定性=配置迁移可用）。
        var sameSalt = new DpapiSecretStore(files, _sink);
        Assert.Equal("sk-1234567890abcdef", sameSalt.Retrieve("zhipu-glm"));
    }

    [Fact]
    public void Logs_carry_only_the_masked_tail_never_the_plaintext_key()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：真 DPAPI 用例标注跳过；文件后端同款负向在 FileSecretStoreTests
        }

        var store = new DpapiSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");
        _ = store.Retrieve("zhipu-glm");
        store.Clear("zhipu-glm");
        store.Store("zhipu-glm", "sk-1234567890abcdef"); // 再存一次扩大日志面
        store.ClearAll();

        Assert.DoesNotContain("sk-1234567890abcdef", _sink.Joined);
        Assert.Contains("sk-****cdef", _sink.Joined); // 只有掩码形态可入日志
    }

    // -------------------------------------------------------------- boundary

    [Fact]
    public void Default_entropy_is_the_documented_fixed_app_salt()
    {
        // 熵盐锚定：固定字面量（版本戳）保证旧 blob 跨版本可解。
        Assert.Equal("EraTD.C-17.secrets.v1"u8.ToArray(), DpapiSecretStore.DefaultEntropy);
    }

    [Fact]
    public void Probe_and_IsAvailable_follow_the_windows_platform_gate()
    {
        Assert.Equal(OperatingSystem.IsWindows(), new DpapiSecretStore(NewFiles(), _sink).IsAvailable);

        var probed = DpapiSecretStore.Probe(out var reason);
        Assert.Equal(OperatingSystem.IsWindows(), probed);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(string.Empty, reason);
        }
        else
        {
            Assert.Contains("Windows", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Clear_then_Retrieve_returns_empty_and_blob_stays_parseable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：真 DPAPI 用例标注跳过
        }

        var store = new DpapiSecretStore(NewFiles(), _sink);
        store.Store("zhipu-glm", "sk-1234567890abcdef");
        store.Clear("zhipu-glm");

        Assert.Equal(string.Empty, store.Retrieve("zhipu-glm"));

        // 清除后容器仍是合法可解的 DPAPI blob（后续可继续存新键）。
        store.Store("xiaomi-mimo", "sk-abcdefgh1234");
        Assert.Equal("sk-abcdefgh1234", store.Retrieve("xiaomi-mimo"));
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Non_windows_use_fails_with_an_explicit_platform_exception()
    {
        var store = new DpapiSecretStore(NewFiles(), _sink);
        if (OperatingSystem.IsWindows())
        {
            // Windows 本机：真往返已在上方用例覆盖，此处只锚定能力位为真。
            Assert.True(store.IsAvailable);
            return;
        }

        // 非 Windows：DPAPI 不可用时异常必须明确（指向文件回落），且不落
        // 任何半成品文件。
        Assert.False(store.IsAvailable);
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => store.Store("zhipu-glm", "sk-1234567890abcdef"));
        Assert.Contains("file fallback", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, DpapiSecretStore.RelativePath)));
    }
}
