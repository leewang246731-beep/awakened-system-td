using EraCore.IO;
using EraCore.Llm.L2;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [F-078] T-137 L2 档位设置持久化（settings/l2-tier.json）：缺省 L1、缺文件/
// 坏 JSON/未知档位 → 工厂缺省（偏好坏不阻断启动、不静默开 L2——L2/L3 均
// opt-in）、往返保存、覆盖路径与端口、路由枚举映射单源。
public class L2TierStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public L2TierStoreTests()
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

    [Fact] // 缺文件：出厂=L1（L2/L3 都是玩家主动选择——opt-in 纪律）
    public void Missing_file_yields_the_L1_factory_default()
    {
        L2TierSettings settings = L2TierStore.Load(_store);

        Assert.Equal(L2TierSelection.L1, settings.Selection);
        Assert.Equal(L2Facts.DefaultPort, settings.Port);
        Assert.Equal(string.Empty, settings.ModelPathOverride);
        Assert.Equal(string.Empty, settings.RuntimePathOverride);
    }

    [Fact] // 往返：L2+覆盖路径+端口 → 保存重载逐字段等值（dev 复用既有模型文件的指路面——值即下两行 ci-allow 登记的环境事实）
    public void Save_then_load_roundtrips_every_field()
    {
        var settings = new L2TierSettings
        {
            Selection = L2TierSelection.L2,
            ModelPathOverride = "D:/models/Qwen2.5-3B-Instruct-Q4_K_M.gguf", // ci-allow: R9 T-137 dev 复用既有模型文件的指路测试值（47-t129 §4.5 环境事实，运行时玩家数据非仓库配置）
            RuntimePathOverride = "D:/llama-cpp/b11714", // ci-allow: R9 T-137 dev 复用 spike 运行时目录的指路测试值（同上，环境事实非硬编码依赖）
            Port = 8024,
        };
        L2TierStore.Save(_store, settings);

        L2TierSettings reloaded = L2TierStore.Load(_store);
        Assert.Equal(settings, reloaded);
        Assert.Contains("l2-tier.json", L2TierStore.RelativePath);
    }

    [Fact] // 坏 JSON：回落工厂 L1（永不阻断启动）
    public void Corrupt_file_falls_back_to_factory()
    {
        _store.WriteAllBytes(L2TierStore.RelativePath, [0xDE, 0xAD, 0xBE, 0xEF]);

        Assert.Equal(L2TierSelection.L1, L2TierStore.Load(_store).Selection);
    }

    [Fact] // 未知档位字符串：解析抛出 → 载入回落（无静默缺省档——F-080 纪律）
    public void Unknown_tier_wire_string_falls_back_to_factory()
    {
        _store.WriteAllBytes(
            L2TierStore.RelativePath,
            System.Text.Encoding.UTF8.GetBytes("""{"selection":"l9"}"""));

        Assert.Equal(L2TierSelection.L1, L2TierStore.Load(_store).Selection);
    }

    [Fact] // null 拒绝（保存路径防御）
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => L2TierStore.Save(null!, new L2TierSettings { Selection = L2TierSelection.L1 }));
        Assert.Throws<ArgumentNullException>(() => L2TierStore.Save(_store, null!));
    }

    [Fact] // 路由枚举映射：持久化选择 ↔ 路由枚举（两个词汇表之间的唯一切换点）
    public void Routing_kind_maps_the_persisted_selection()
    {
        Assert.Equal(LlmTierKind.L1Grammar, L2TierStore.RoutingKind(new L2TierSettings { Selection = L2TierSelection.L1 }));
        Assert.Equal(LlmTierKind.L2Local, L2TierStore.RoutingKind(new L2TierSettings { Selection = L2TierSelection.L2 }));
        Assert.Equal(LlmTierKind.L3Cloud, L2TierStore.RoutingKind(new L2TierSettings { Selection = L2TierSelection.L3 }));
    }
}
