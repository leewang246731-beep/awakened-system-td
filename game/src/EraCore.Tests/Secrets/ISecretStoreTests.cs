using EraCore.IO;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 ISecretStore 端口契约矩阵——两个后端（文件回落 / DPAPI）同套断言；
// DPAPI 用例在非 Windows 上按任务纪律标注跳过（不伪造 DPAPI 通过），文件后端全平台执行。
public class ISecretStoreTests : SecretsSandbox
{
    private readonly CaptureLogSink _sink = new();

    // 每个用例自备 root 的后端清单：文件后端全平台 + DPAPI 仅 Windows。
    private IEnumerable<Func<IFileStore, ISecretStore>> BackendFactories()
    {
        yield return files => new FileSecretStore(files, _sink);
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：DPAPI 用例标注跳过（见类注）；平台能力面
            // （IsAvailable/Probe=false、异常明确）在 DpapiSecretStoreTests 锚定。
            yield break;
        }

        yield return files => new DpapiSecretStore(files, _sink);
    }

    // ---------------------------------------------------------------- legal

    [Fact]
    public void Store_then_Retrieve_roundtrips_per_provider_id()
    {
        foreach (var backend in BackendFactories())
        {
            var store = backend(NewFiles());
            store.Store("zhipu-glm", "sk-1234567890abcdef");
            store.Store("xiaomi-mimo", "sk-abcdefgh1234");

            Assert.Equal("sk-1234567890abcdef", store.Retrieve("zhipu-glm"));
            Assert.Equal("sk-abcdefgh1234", store.Retrieve("xiaomi-mimo"));
        }
    }

    [Fact]
    public void Providers_are_isolated_and_overwrite_replaces_only_that_provider()
    {
        foreach (var backend in BackendFactories())
        {
            var store = backend(NewFiles());
            store.Store("zhipu-glm", "sk-1111aaaa");
            store.Store("xiaomi-mimo", "sk-2222bbbb");
            store.Store("zhipu-glm", "sk-3333cccc");

            Assert.Equal("sk-3333cccc", store.Retrieve("zhipu-glm"));
            Assert.Equal("sk-2222bbbb", store.Retrieve("xiaomi-mimo"));
        }
    }

    [Fact]
    public void Persistence_survives_a_new_store_instance_over_the_same_root()
    {
        foreach (var backend in BackendFactories())
        {
            var files = NewFiles();
            backend(files).Store("zhipu-glm", "sk-1234567890abcdef");

            var second = backend(files);
            Assert.Equal("sk-1234567890abcdef", second.Retrieve("zhipu-glm"));
        }
    }

    // -------------------------------------------------------------- boundary

    [Theory]
    [InlineData("zhipu-glm")]
    [InlineData("custom-relay")]
    [InlineData("unicode-provider-智谱")]
    public void Retrieve_of_a_missing_provider_returns_empty(string providerId)
    {
        foreach (var backend in BackendFactories())
        {
            Assert.Equal(string.Empty, backend(NewFiles()).Retrieve(providerId));
        }
    }

    [Fact]
    public void Storing_an_empty_value_removes_the_entry()
    {
        foreach (var backend in BackendFactories())
        {
            var store = backend(NewFiles());
            store.Store("zhipu-glm", "sk-1234567890abcdef");
            store.Store("zhipu-glm", string.Empty);

            Assert.Equal(string.Empty, store.Retrieve("zhipu-glm"));
        }
    }

    [Fact]
    public void Clear_is_idempotent_and_clear_all_empties_every_provider()
    {
        foreach (var backend in BackendFactories())
        {
            var store = backend(NewFiles());
            store.Store("zhipu-glm", "sk-1111aaaa");
            store.Store("xiaomi-mimo", "sk-2222bbbb");

            store.Clear("zhipu-glm");
            Assert.Equal(string.Empty, store.Retrieve("zhipu-glm"));
            Assert.Equal("sk-2222bbbb", store.Retrieve("xiaomi-mimo"));

            store.Clear("zhipu-glm"); // 幂等：清除不存在的条目不算错
            store.ClearAll();
            Assert.Equal(string.Empty, store.Retrieve("xiaomi-mimo"));
        }
    }

    [Fact]
    public void Backend_designations_are_stable_and_key_free()
    {
        var file = new FileSecretStore(NewFiles(), _sink);
        Assert.Equal("file-fallback", file.Backend);
        Assert.True(file.IsAvailable);

        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：DPAPI 契约由文件后端承载
        }

        var dpapi = new DpapiSecretStore(NewFiles(), _sink);
        Assert.Equal("dpapi-currentuser", dpapi.Backend);
        Assert.True(dpapi.IsAvailable);
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Blank_provider_ids_are_rejected_by_every_backend()
    {
        foreach (var backend in BackendFactories())
        {
            var store = backend(NewFiles());
            Assert.Throws<ArgumentException>(() => store.Store(string.Empty, "v"));
            Assert.Throws<ArgumentException>(() => store.Store("   ", "v"));
            Assert.Throws<ArgumentException>(() => store.Store(null!, "v"));
            Assert.Throws<ArgumentException>(() => store.Retrieve(string.Empty));
            Assert.Throws<ArgumentException>(() => store.Clear(string.Empty));
        }
    }
}
