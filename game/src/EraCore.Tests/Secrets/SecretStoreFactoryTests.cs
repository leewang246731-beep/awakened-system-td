using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 后端选择（优先 DPAPI，失败回落文件并经 ILogSink 记录原因）
public class SecretStoreFactoryTests : SecretsSandbox
{
    // ---------------------------------------------------------------- legal

    [Fact]
    public void Factory_prefers_dpapi_when_the_real_probe_passes()
    {
        var sink = new CaptureLogSink();
        var store = SecretStoreFactory.Create(NewFiles(), sink);

        if (OperatingSystem.IsWindows())
        {
            // Windows 本机：真 DPAPI 探针通过 → 主后端，并带 Info 选择记录。
            Assert.IsType<DpapiSecretStore>(store);
            Assert.Contains(sink.Lines, line =>
                line.Contains("backend=dpapi-currentuser", StringComparison.Ordinal)
                && line.Contains("reason=dpapi-available", StringComparison.Ordinal));

            // 端到端可用性：工厂产物直接完成一次真实往返。
            store.Store("zhipu-glm", "sk-1234567890abcdef");
            Assert.Equal("sk-1234567890abcdef", store.Retrieve("zhipu-glm"));
        }
        else
        {
            // 非 Windows：无 DPAPI 可言 → 文件回落 + Warn 原因（平台事实）。
            Assert.IsType<FileSecretStore>(store);
            Assert.Contains(sink.Lines, line =>
                line.Contains("backend=file-fallback", StringComparison.Ordinal)
                && line.Contains("Windows", StringComparison.Ordinal));
        }
    }

    // -------------------------------------------------------------- boundary

    [Fact]
    public void Probe_failure_falls_back_to_file_with_a_logged_reason()
    {
        var sink = new CaptureLogSink();
        var store = SecretStoreFactory.Create(NewFiles(), sink, dpapiProbe: () => false);

        // 逻辑回落路径（全平台可跑）：DPAPI 探针失败 → 文件回落 + Warn。
        Assert.IsType<FileSecretStore>(store);
        Assert.Contains(sink.Lines, line =>
            line.Contains("backend=file-fallback", StringComparison.Ordinal)
            && line.Contains("reason=dpapi probe returned false", StringComparison.Ordinal));

        // 回落产物同样满足端口契约（存取可用）。
        store.Store("xiaomi-mimo", "sk-abcdefgh1234");
        Assert.Equal("sk-abcdefgh1234", store.Retrieve("xiaomi-mimo"));
    }

    [Fact]
    public void Fallback_selection_log_lines_never_carry_key_material()
    {
        // F-084 负向（工厂路径）：工厂+回落全流程的日志面不得出现键明文。
        var sink = new CaptureLogSink();
        var store = SecretStoreFactory.Create(NewFiles(), sink, dpapiProbe: () => false);
        store.Store("zhipu-glm", "sk-1234567890abcdef");

        Assert.DoesNotContain("sk-1234567890abcdef", sink.Joined);
        Assert.Contains("sk-****cdef", sink.Joined);
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var sink = new CaptureLogSink();
        Assert.Throws<ArgumentNullException>(() => SecretStoreFactory.Create(null!, sink));
        Assert.Throws<ArgumentNullException>(() =>
            SecretStoreFactory.Create(NewFiles(), null!));
    }

    [Fact]
    public void A_passing_injected_probe_still_cannot_select_dpapi_off_windows()
    {
        var sink = new CaptureLogSink();
        var store = SecretStoreFactory.Create(NewFiles(), sink, dpapiProbe: () => true);

        if (OperatingSystem.IsWindows())
        {
            // Windows 本机：注入探针通过 → 照常选 DPAPI 主后端。
            Assert.IsType<DpapiSecretStore>(store);
        }
        else
        {
            // 非法面：探针被伪造为真也骗不过平台门——仍回落文件并记原因。
            Assert.IsType<FileSecretStore>(store);
            Assert.Contains(sink.Lines, line =>
                line.Contains("unavailable on this platform", StringComparison.Ordinal));
        }
    }
}
