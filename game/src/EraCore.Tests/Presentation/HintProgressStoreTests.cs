using EraCore.IO;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-110] T-093 提示已读持久化（附录 G.3「持久化」）：缺文件/坏 JSON/
// 缺 required 成员 → 新玩家缺省（提示偏好永不阻断启动）、往返保存、null 拒绝。
// 沙盒同 PanelSettingsStoreTests（D 盘开发根下 .tmp-tests）。
public class HintProgressStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public HintProgressStoreTests()
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

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 缺文件：新玩家缺省（零已读）
    public void Load_missing_file_yields_the_fresh_default()
    {
        HintProgress progress = HintProgressStore.Load(_store);

        Assert.Empty(HintProgress.Default.Read);
        Assert.Empty(progress.Read);
    }

    [Fact] // legal — 往返：已读集合跨"运行"记忆（once 提示不再弹的依据）
    public void Save_then_load_roundtrips_the_read_set()
    {
        HintProgressStore.Save(_store, new HintProgress { Read = new[] { "hint-a", "hint-b" } });

        HintProgress reloaded = HintProgressStore.Load(_store);
        Assert.Equal(new[] { "hint-a", "hint-b" }, reloaded.Read);
    }

    // ---------------------------------------------------------------- boundary

    [Fact] // boundary — 空已读集合往返合法（保存合法状态而非靠缺文件表达）
    public void Empty_read_set_roundtrips()
    {
        HintProgressStore.Save(_store, new HintProgress { Read = Array.Empty<string>() });

        Assert.Empty(HintProgressStore.Load(_store).Read);
    }

    // ---------------------------------------------------------------- illegal

    [Fact] // illegal — 坏 JSON 落新玩家缺省（宁可重放引导，不可阻断启动）
    public void Corrupt_file_falls_back_to_fresh_default()
    {
        _store.WriteAllBytes(HintProgressStore.RelativePath, "{ not json"u8.ToArray());

        Assert.Empty(HintProgressStore.Load(_store).Read);
    }

    [Fact] // illegal — 缺 required 成员（read）→ 反序列化抛→缺省
    public void Missing_required_member_falls_back_to_default()
    {
        _store.WriteAllBytes(HintProgressStore.RelativePath, """{"nonsense": true}"""u8.ToArray());

        Assert.Empty(HintProgressStore.Load(_store).Read);
    }

    [Fact] // illegal — null 入参拒绝
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => HintProgressStore.Load(null!));
        Assert.Throws<ArgumentNullException>(
            () => HintProgressStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => HintProgressStore.Save(null!, HintProgress.Default));
    }
}
