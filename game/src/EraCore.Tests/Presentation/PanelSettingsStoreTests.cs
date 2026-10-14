using EraCore.IO;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-097] T-075 面板设置持久化（关闭系统开关跨运行记忆）：缺省开、
// 缺文件/坏 JSON/缺 required 成员 → 工厂缺省（UI 偏好永不阻断启动、永不静默
// 关系统）、往返保存、null 拒绝。沙盒同 DevFileStoreTests（D 盘开发根下
// .tmp-tests，data/ 为 gitignore 运行时数据）。
// [F-097] [C-22]
public class PanelSettingsStoreTests : IDisposable
{
    // Scratch sandbox under the pinned dev root (D-drive only; data/ is gitignored runtime data).
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public PanelSettingsStoreTests()
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

    [Fact] // legal — 缺文件：出厂缺省=开（支柱 4：关闭是玩家的主动选择）
    public void Load_missing_file_yields_the_enabled_factory_default()
    {
        Assert.True(PanelSettingsStore.Load(_store).SystemEnabled);
        Assert.True(PanelSettings.Default.SystemEnabled);
        Assert.False(_store.Exists(PanelSettingsStore.RelativePath));
    }

    [Fact] // legal — 往返：关→保存→重载仍是关（跨运行记忆）
    public void Save_then_load_roundtrips_the_off_position()
    {
        PanelSettingsStore.Save(_store, new PanelSettings { SystemEnabled = false });

        Assert.False(PanelSettingsStore.Load(_store).SystemEnabled);
    }

    [Fact] // legal — 往返：开位置同样原样重载（重开恢复的记忆源）
    public void Save_then_load_roundtrips_the_on_position()
    {
        PanelSettingsStore.Save(_store, new PanelSettings { SystemEnabled = false });
        PanelSettingsStore.Save(_store, new PanelSettings { SystemEnabled = true });

        Assert.True(PanelSettingsStore.Load(_store).SystemEnabled);
    }

    [Fact] // legal — 线上格式：紧凑 JSON，成员名与记录参数对齐（可人工检视）
    public void Save_writes_compact_json_under_the_settings_relative_path()
    {
        PanelSettingsStore.Save(_store, new PanelSettings { SystemEnabled = false });

        Assert.True(_store.Exists(PanelSettingsStore.RelativePath));
        string json = System.Text.Encoding.UTF8.GetString(
            _store.ReadAllBytes(PanelSettingsStore.RelativePath));
        Assert.Equal("""{"SystemEnabled":false}""", json);
    }

    // ------------------------------------------------------------ boundary

    [Fact] // boundary — 坏 JSON：回出厂缺省，不抛（偏好损坏永不阻断启动）
    public void Load_corrupt_file_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(PanelSettingsStore.RelativePath, new byte[] { 0x7B, 0x00, 0xFF });

        Assert.True(PanelSettingsStore.Load(_store).SystemEnabled);
    }

    [Fact] // boundary — 缺 required 成员：解码抛 JsonException → 同样回缺省，
    // 且缺省是“开”——损坏文件永不静默关掉系统（关闭必须是玩家显式选择）
    public void Load_file_missing_the_required_member_falls_back_enabled()
    {
        _store.WriteAllBytes(
            PanelSettingsStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("{}"));

        Assert.True(PanelSettingsStore.Load(_store).SystemEnabled);
    }

    [Fact] // boundary — null 文档：回缺省（与坏 JSON 同道）
    public void Load_null_document_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(
            PanelSettingsStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("null"));

        Assert.True(PanelSettingsStore.Load(_store).SystemEnabled);
    }

    // ------------------------------------------------------------ illegal

    [Fact] // illegal — null store / null settings 拒绝
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => PanelSettingsStore.Load(null!));
        Assert.Throws<ArgumentNullException>(
            () => PanelSettingsStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => PanelSettingsStore.Save(null!, PanelSettings.Default));
    }
}
